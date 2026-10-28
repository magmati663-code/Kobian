using System.Globalization;
using Kobian.Domain;

namespace Kobian.Notifications.Email;

/// <summary>Tryb szyfrowania połączenia SMTP (9.6): STARTTLS (wymagany, nie „gdy dostępny”), TLS od początku (465) albo jawne SMTP (tylko z flagą).</summary>
public enum SmtpTlsMode
{
    StartTls,
    Implicit,
    None,
}

/// <summary>Login i hasło SMTP z pliku w katalogu sekretów. <see cref="ToString"/> nie ujawnia wartości (NFR-11, SEC-9.4).</summary>
public sealed class SmtpCredential(string login, string secret)
{
    public string Login { get; } = login;

    /// <summary>Hasło; puste przy OAuth2 (wtedy ustawione <see cref="TokenProvider"/>).</summary>
    public string Secret { get; } = secret;

    /// <summary>Gdy ustawiony — uwierzytelnienie SASL XOAUTH2 tokenem z dostawcy (Microsoft 365, KB-6208), a nie hasłem.</summary>
    public IOAuth2TokenProvider? TokenProvider { get; init; }

    public static SmtpCredential OAuth2(string mailbox, IOAuth2TokenProvider provider) => new(mailbox, "") { TokenProvider = provider };

    public override string ToString() => "SmtpCredential(***)";
}

/// <summary>Ustawienia kanału e-mail. Rekord nie zawiera sekretu w jawnej postaci w <c>ToString</c> (poświadczenie maskowane).</summary>
public sealed record EmailSettings(
    string Host,
    int Port,
    SmtpTlsMode Tls,
    string From,
    IReadOnlyList<string> To,
    SmtpCredential? Credential,
    bool Minimal,
    bool ProblemsOnly,
    string Language,
    string HostLabel);

/// <summary>Wynik wczytania: <c>Settings = null, Error = null</c> — kanał wyłączony; <c>Error</c> — konfiguracja niepoprawna (kod KB w <c>ErrorCode</c>); <c>Warning</c> — np. jawne SMTP.</summary>
public sealed record EmailConfigResult(EmailSettings? Settings, string? ErrorCode, string? Error, string? Warning)
{
    public static readonly EmailConfigResult Disabled = new(null, null, null, null);
}

/// <summary>
/// Konfiguracja powiadomień e-mail (BL-45, 9.6; wzorem <c>WebhookConfig</c>, PROWIZORYCZNIE Qb64 — docelowo <c>alert_channels</c> + magazyn DPAPI, SEC-9.4):
/// zmienne <c>KOBIAN_SMTP_*</c> i opcjonalny plik <c>&lt;sekrety&gt;/smtp.credentials</c> (wiersz 1 — login, wiersz 2 — hasło; nigdy w zmiennych środowiskowych).
/// Brak <c>KOBIAN_SMTP_HOST</c> = kanał wyłączony. Jawne SMTP wyłącznie z <c>KOBIAN_SMTP_INSECURE=1</c> i bez poświadczeń (hasło nie idzie otwartym tekstem).
/// Komunikaty błędów zawierają tylko nazwy zmiennych/plików i kod KB — bez wartości.
/// </summary>
public static class EmailConfig
{
    public const string HostVariable = "KOBIAN_SMTP_HOST";
    public const string PortVariable = "KOBIAN_SMTP_PORT";
    public const string TlsVariable = "KOBIAN_SMTP_TLS";
    public const string InsecureVariable = "KOBIAN_SMTP_INSECURE";
    public const string FromVariable = "KOBIAN_SMTP_FROM";
    public const string ToVariable = "KOBIAN_SMTP_TO";
    public const string LanguageVariable = "KOBIAN_SMTP_LANG";
    public const string MinimalVariable = "KOBIAN_SMTP_MINIMAL";
    public const string NotifyVariable = "KOBIAN_SMTP_NOTIFY";
    public const string CredentialsFile = "smtp.credentials";
    public const string AuthVariable = "KOBIAN_SMTP_AUTH";
    public const string OAuthTenantVariable = "KOBIAN_SMTP_OAUTH_TENANT";
    public const string OAuthClientIdVariable = "KOBIAN_SMTP_OAUTH_CLIENT_ID";
    public const string OAuthUserVariable = "KOBIAN_SMTP_OAUTH_USER";
    public const string OAuthScopeVariable = "KOBIAN_SMTP_OAUTH_SCOPE";
    public const string OAuthSecretFile = "smtp.oauth";
    public const int MaxClientIdLength = 128;

    public const int MaxRecipients = 10;
    public const int MaxHostLength = 253;
    public const int MaxLoginLength = 256;
    public const int MaxSecretLength = 1024;
    public const int MaxCredentialsFileBytes = 4096;

    public static EmailConfigResult Load(Func<string, string?> env, string secretsDir, string? hostLabel = null)
    {
        ArgumentNullException.ThrowIfNull(env);
        var host = env(HostVariable)?.Trim();
        if (string.IsNullOrEmpty(host)) return EmailConfigResult.Disabled;
        if (!IsValidHost(host)) return Fail(KbCode.SmtpConfigInvalid, $"{HostVariable} is not a valid host name or IP address");

        var tlsText = env(TlsVariable)?.Trim().ToLowerInvariant();
        var portText = env(PortVariable)?.Trim();
        int? port = null;
        if (!string.IsNullOrEmpty(portText))
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                return Fail(KbCode.SmtpConfigInvalid, $"{PortVariable} must be a number 1-65535");
            port = p;
        }
        SmtpTlsMode tls;
        switch (tlsText)
        {
            case null or "": tls = port == 465 ? SmtpTlsMode.Implicit : SmtpTlsMode.StartTls; break;
            case "starttls": tls = SmtpTlsMode.StartTls; break;
            case "tls" or "ssl" or "implicit": tls = SmtpTlsMode.Implicit; break;
            case "none" or "plain": tls = SmtpTlsMode.None; break;
            default: return Fail(KbCode.SmtpConfigInvalid, $"{TlsVariable} must be starttls, tls or none");
        }
        string? warning = null;
        if (tls == SmtpTlsMode.None)
        {
            if (env(InsecureVariable)?.Trim() != "1")
                return Fail(KbCode.SmtpInsecureEnabled, $"{TlsVariable}=none requires {InsecureVariable}=1 (plain SMTP is not encrypted)");
            warning = $"{KbCode.SmtpInsecureEnabled}: e-mail notifications are sent over plain SMTP without encryption ({InsecureVariable}=1).";
        }
        port ??= tls switch { SmtpTlsMode.Implicit => 465, SmtpTlsMode.None => 25, _ => 587 };

        var from = env(FromVariable)?.Trim();
        if (string.IsNullOrEmpty(from)) return Fail(KbCode.SmtpConfigInvalid, $"{FromVariable} is required");
        if (!EmailAddressPolicy.IsValid(from)) return Fail(KbCode.EmailAddressInvalid, $"{FromVariable} is not a valid single e-mail address");

        var toText = env(ToVariable);
        if (string.IsNullOrWhiteSpace(toText)) return Fail(KbCode.SmtpConfigInvalid, $"{ToVariable} is required");
        if (toText.Length > MaxRecipients * (EmailAddressPolicy.MaxLength + 2)) return Fail(KbCode.SmtpConfigInvalid, $"{ToVariable} is too long");
        var to = new List<string>();
        foreach (var raw in toText.Split([',', ';']))
        {
            var a = raw.Trim(' ', '\t');
            if (!EmailAddressPolicy.IsValid(a)) return Fail(KbCode.EmailAddressInvalid, $"{ToVariable} contains an invalid e-mail address (entry {to.Count + 1})");
            if (!to.Contains(a, StringComparer.OrdinalIgnoreCase)) to.Add(a);
        }
        if (to.Count > MaxRecipients) return Fail(KbCode.SmtpConfigInvalid, $"{ToVariable} allows at most {MaxRecipients} recipients");

        var lang = env(LanguageVariable)?.Trim().ToLowerInvariant();
        lang = string.IsNullOrEmpty(lang) ? "pl" : lang;
        if (lang is not ("pl" or "en")) return Fail(KbCode.SmtpConfigInvalid, $"{LanguageVariable} must be pl or en");

        var notify = env(NotifyVariable)?.Trim().ToLowerInvariant();
        if (notify is not (null or "" or "problems" or "all")) return Fail(KbCode.SmtpConfigInvalid, $"{NotifyVariable} must be problems or all");

        var auth = env(AuthVariable)?.Trim().ToLowerInvariant();
        if (auth is not (null or "" or "password" or "oauth2")) return Fail(KbCode.SmtpConfigInvalid, $"{AuthVariable} must be password or oauth2");
        SmtpCredential? credential;
        if (auth == "oauth2")
        {
            if (tls == SmtpTlsMode.None) return Fail(KbCode.SmtpInsecureEnabled, $"{AuthVariable}=oauth2 cannot be used with plain SMTP ({TlsVariable}=none)");
            var (oauth, oauthError) = ReadOAuth(env, secretsDir, from);
            if (oauthError is not null) return Fail(KbCode.OAuth2ConfigInvalid, oauthError);
            credential = oauth;
        }
        else
        {
            var (pw, credError) = ReadCredential(secretsDir);
            if (credError is not null) return Fail(KbCode.SmtpConfigInvalid, credError);
            credential = pw;
        }
        if (credential is not null && tls == SmtpTlsMode.None)
            return Fail(KbCode.SmtpInsecureEnabled, $"{CredentialsFile} cannot be used with plain SMTP ({TlsVariable}=none) — the password would be sent unencrypted");

        var settings = new EmailSettings(host, port.Value, tls, from, to, credential, env(MinimalVariable)?.Trim() == "1", notify != "all", lang, SanitizeHostLabel(hostLabel));
        return new EmailConfigResult(settings, null, null, warning);
    }

    /// <summary>Etykieta hosta w temacie/treści: tylko [A-Za-z0-9.-_], ≤ 63 znaki; pusta → „host”.</summary>
    public static string SanitizeHostLabel(string? label)
    {
        var chars = (label ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_').Take(63).ToArray();
        return chars.Length == 0 ? "host" : new string(chars);
    }

    internal static bool IsValidHost(string host) =>
        host.Length <= MaxHostLength && host.All(c => c is > (char)0x20 and < (char)0x7F) && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static (SmtpCredential? Credential, string? Error) ReadOAuth(Func<string, string?> env, string secretsDir, string from)
    {
        var tenant = env(OAuthTenantVariable)?.Trim();
        if (string.IsNullOrEmpty(tenant) || !ClientCredentialsTokenProvider.IsValidTenant(tenant)) return (null, $"{OAuthTenantVariable} must be a tenant id or domain name");
        var clientId = env(OAuthClientIdVariable)?.Trim();
        if (string.IsNullOrEmpty(clientId) || clientId.Length > MaxClientIdLength || !clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
            return (null, $"{OAuthClientIdVariable} must be an application (client) id");
        var mailbox = env(OAuthUserVariable)?.Trim();
        if (string.IsNullOrEmpty(mailbox)) mailbox = from;
        if (!EmailAddressPolicy.IsValid(mailbox)) return (null, $"{OAuthUserVariable} is not a valid single e-mail address");
        var scope = env(OAuthScopeVariable)?.Trim();
        if (string.IsNullOrEmpty(scope)) scope = ClientCredentialsTokenProvider.DefaultScope;
        if (scope.Length > 256 || scope.Any(c => c <= ' ' || c > '~') || !scope.StartsWith("https://", StringComparison.Ordinal))
            return (null, $"{OAuthScopeVariable} must be an https:// scope");

        var path = Path.Combine(secretsDir, OAuthSecretFile);
        string secret;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return (null, $"{OAuthSecretFile} is required for {AuthVariable}=oauth2");
            if (fi.LinkTarget is not null) return (null, $"{OAuthSecretFile} must not be a link");
            if (fi.Length > MaxCredentialsFileBytes) return (null, $"{OAuthSecretFile} is too large");
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
            if (lines.Length != 1) return (null, $"{OAuthSecretFile} must contain exactly one line: the client secret");
            secret = lines[0].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{OAuthSecretFile} cannot be read");
        }
        if (secret.Length is 0 or > MaxSecretLength || secret.Any(char.IsControl)) return (null, $"{OAuthSecretFile}: invalid client secret");
        var provider = new ClientCredentialsTokenProvider(ClientCredentialsTokenProvider.EndpointFor(tenant), clientId, secret, scope);
        return (SmtpCredential.OAuth2(mailbox, provider), null);
    }

    private static (SmtpCredential? Credential, string? Error) ReadCredential(string secretsDir)
    {
        var path = Path.Combine(secretsDir, CredentialsFile);
        string[] lines;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return (null, null); // relay bez uwierzytelnienia
            if (fi.LinkTarget is not null) return (null, $"{CredentialsFile} must not be a link");
            if (fi.Length > MaxCredentialsFileBytes) return (null, $"{CredentialsFile} is too large");
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{CredentialsFile} cannot be read");
        }
        var content = lines.Where(l => l.Length > 0).ToArray();
        if (content.Length != 2) return (null, $"{CredentialsFile} must contain exactly two lines: login and password");
        var login = content[0].Trim();
        var secret = content[1];
        if (login.Length is 0 or > MaxLoginLength || login.Any(char.IsControl)) return (null, $"{CredentialsFile}: invalid login (line 1)");
        if (secret.Length is 0 or > MaxSecretLength || secret.Any(char.IsControl)) return (null, $"{CredentialsFile}: invalid password (line 2)");
        return (new SmtpCredential(login, secret), null);
    }

    private static EmailConfigResult Fail(string code, string message) => new(null, code, $"{message} ({code}).", null);
}
