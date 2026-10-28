using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Kobian.Notifications.Email;

/// <summary>Dostawca tokenu dostępu OAuth2 do SMTP (Microsoft 365 — KB-6208). Token nigdy nie trafia do logów ani wyjątków.</summary>
public interface IOAuth2TokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);

    /// <summary>Odrzuca token z pamięci podręcznej (serwer SMTP odrzucił uwierzytelnienie).</summary>
    void Invalidate();
}

/// <summary>Błąd pobrania tokenu. <see cref="Permanent"/> = odrzucone poświadczenia/konfiguracja (bez ponowień); inaczej sieć/serwer (ponowienie).</summary>
public sealed class OAuth2TokenException(bool permanent, string message) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}

/// <summary>
/// Przepływ „client credentials” (aplikacja bez użytkownika) do <c>login.microsoftonline.com</c>, bez dodatkowych pakietów (HttpClient + System.Text.Json).
/// Wymagania bezpieczeństwa: tylko HTTPS, bez przekierowań, odpowiedź ≤ <see cref="MaxResponseBytes"/>, limit czasu <see cref="RequestTimeout"/>,
/// token w pamięci do <c>expires_in</c> − <see cref="ExpiryMargin"/>, komunikaty błędów bez treści odpowiedzi i bez sekretu (NFR-11).
/// PROWIZORYCZNIE Qb105: przepływ interaktywny (delegowany) odłożony; sekret klienta w pliku <c>smtp.oauth</c> (docelowo magazyn DPAPI, SEC-9.4).
/// </summary>
public sealed class ClientCredentialsTokenProvider : IOAuth2TokenProvider, IDisposable
{
    public const string DefaultScope = "https://outlook.office365.com/.default";
    public const int MaxResponseBytes = 16 * 1024;
    public const int MaxTokenLength = 8192;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly Uri _endpoint;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scope;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _validUntil;

    public ClientCredentialsTokenProvider(Uri endpoint, string clientId, string clientSecret, string scope, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The token endpoint must use HTTPS.", nameof(endpoint));
        _endpoint = endpoint;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scope = scope;
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: true) { Timeout = RequestTimeout };
    }

    public static Uri EndpointFor(string tenant) => new($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token");

    /// <summary>Tenant: GUID albo nazwa domeny (litery, cyfry, kropka, myślnik) ≤ 253 znaki — wstawiany do ścieżki URL, więc bez innych znaków.</summary>
    public static bool IsValidTenant(string tenant) =>
        tenant.Length is > 0 and <= 253 && tenant.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') && !tenant.StartsWith('.') && !tenant.Contains("..", StringComparison.Ordinal);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _time.GetUtcNow() < _validUntil) return _token;
            var (token, lifetime) = await RequestAsync(ct).ConfigureAwait(false);
            _token = token;
            _validUntil = _time.GetUtcNow() + lifetime - ExpiryMargin;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _gate.Wait();
        try { _token = null; }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private async Task<(string Token, TimeSpan Lifetime)> RequestAsync(CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["scope"] = _scope,
        });
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_endpoint, body, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new OAuth2TokenException(false, "The token endpoint is unreachable.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OAuth2TokenException(false, "The token request timed out.");
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is 429 or 408 or >= 500) throw new OAuth2TokenException(false, $"The token endpoint is temporarily unavailable (HTTP {status.ToString(CultureInfo.InvariantCulture)}).");
            if (status is >= 300 and < 400) throw new OAuth2TokenException(true, "The token endpoint answered with a redirect, which is refused.");
            if (response.StatusCode != HttpStatusCode.OK) throw new OAuth2TokenException(true, $"The token endpoint rejected the credentials (HTTP {status.ToString(CultureInfo.InvariantCulture)}).");
            var bytes = await ReadLimitedAsync(response, ct).ConfigureAwait(false);
            return Parse(bytes);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxResponseBytes) throw new OAuth2TokenException(true, "The token response is too large.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    internal static (string Token, TimeSpan Lifetime) Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new OAuth2TokenException(true, "The token response is malformed.");
            var type = root.TryGetProperty("token_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (!string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase)) throw new OAuth2TokenException(true, "The token response has an unsupported token type.");
            var token = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength || token.Any(c => c <= ' ' || c > '~'))
                throw new OAuth2TokenException(true, "The token response contains no valid access token.");
            var seconds = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var s) ? s : 0;
            if (seconds < 60) seconds = 60;
            if (seconds > 86400) seconds = 86400;
            return (token, TimeSpan.FromSeconds(seconds));
        }
        catch (JsonException)
        {
            throw new OAuth2TokenException(true, "The token response is malformed.");
        }
    }
}
