using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Kobian.Notifications.Webhooks;

/// <summary>Wynik jednej próby dostarczenia (D9.10): dostarczono, ponów później, trwała odmowa (np. 410 Gone — odbiorca zrezygnował).</summary>
public enum DeliveryResult
{
    Delivered,
    Retry,
    Permanent,
}

/// <summary>Port wysyłki (testy: atrapa; produkcja: <see cref="WebhookSender"/>).</summary>
public interface IWebhookSender
{
    Task<DeliveryResult> SendAsync(string eventId, ReadOnlyMemory<byte> body, CancellationToken ct);
}

/// <summary>
/// Nadawca webhooka D9.10 (it. 111; SEC-9.3): POST JSON (CloudEvents) z nagłówkami <c>Kobian-Webhook-Id/Timestamp/Signature</c>,
/// <c>AllowAutoRedirect = false</c>, połączenie ≤ 10 s, całość ≤ 30 s, odpowiedź czytana najwyżej 64 KiB i porzucana; ponowna kontrola SSRF przy KAŻDYM
/// połączeniu (ConnectCallback: tylko publiczne adresy po rozwiązaniu DNS — ochrona przed DNS rebinding). URL sprawdzany statycznie (<see cref="WebhookUrlPolicy"/>).
/// </summary>
public sealed class WebhookSender : IWebhookSender, IDisposable
{
    public const int MaxResponseBytes = 64 * 1024;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);

    private readonly Uri _url;
    private readonly byte[] _secret;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public WebhookSender(string url, byte[] secret, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        if (!WebhookUrlPolicy.IsAllowed(url)) throw new ArgumentException("Webhook URL is not allowed (KB-6302).", nameof(url));
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length < 32) throw new ArgumentException("Webhook secret must have at least 32 bytes.", nameof(secret));
        _url = new Uri(url);
        _secret = (byte[])secret.Clone();
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true) { Timeout = TotalTimeout };
    }

    /// <summary>Handler produkcyjny: bez przekierowań, bez proxy z ustawień użytkownika (konto usługi), tylko publiczne adresy.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = ConnectTimeout,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var pub = PublicOnly(addresses);
            if (pub.Count == 0) throw new HttpRequestException("Webhook host resolves only to non-public addresses (KB-6302).");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync([.. pub], ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Adresy publiczne spośród rozwiązanych (SEC-9.3); prywatne/lokalne/link-local odrzucane.</summary>
    public static IReadOnlyList<IPAddress> PublicOnly(IEnumerable<IPAddress> addresses) => [.. addresses.Where(WebhookUrlPolicy.IsPublic)];

    public async Task<DeliveryResult> SendAsync(string eventId, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        var ts = _time.GetUtcNow().ToUnixTimeSeconds();
        using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new ReadOnlyMemoryContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/cloudevents+json") { CharSet = "utf-8" };
        req.Headers.TryAddWithoutValidation("Kobian-Webhook-Id", eventId);
        req.Headers.TryAddWithoutValidation("Kobian-Webhook-Timestamp", ts.ToString(CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("Kobian-Webhook-Signature", WebhookSignature.Sign(_secret, ts, body.Span));
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await DrainAsync(resp, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            return code is >= 200 and < 300 ? DeliveryResult.Delivered : code == 410 ? DeliveryResult.Permanent : DeliveryResult.Retry;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return DeliveryResult.Retry;
        }
    }

    private static async Task DrainAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[8192];
        var total = 0;
        while (total < MaxResponseBytes)
        {
            var n = await s.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, MaxResponseBytes - total)), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_secret);
    }
}
