using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kobian.Domain;
using Kobian.Notifications.Email;
using Kobian.Notifications.Outbox;
using Kobian.Notifications.Webhooks;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

/// <summary>
/// BL-45 — OAuth2 (client credentials) dla SMTP Microsoft 365: dostawca tokenu na atrapie HTTP (bez sieci), pamięć podręczna i wygasanie,
/// klasyfikacja błędów (trwałe/przejściowe), konfiguracja KOBIAN_SMTP_AUTH=oauth2 i brak wycieku sekretu/tokenu. Dane syntetyczne, losowe sekrety.
/// </summary>
public sealed class OAuth2Tests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-oauth-" + Guid.NewGuid().ToString("N"));

    public OAuth2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string TokenJson(string token, int expires = 3600) => $$"""{"token_type":"Bearer","expires_in":{{expires}},"access_token":"{{token}}"}""";

    private static string RandomToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    private static ClientCredentialsTokenProvider Provider(StubHandler h, string secret, TimeProvider? time = null) =>
        new(ClientCredentialsTokenProvider.EndpointFor("contoso.onmicrosoft.com"), "11111111-2222-3333-4444-555555555555", secret, ClientCredentialsTokenProvider.DefaultScope, h, time);

    [Fact, Trait("Plan", "BL-45")]
    public async Task Token_is_requested_with_client_credentials_over_https_and_cached_until_expiry()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var t1 = RandomToken();
        var t2 = RandomToken();
        var n = 0;
        var h = new StubHandler((_, _) => Json(HttpStatusCode.OK, TokenJson(n++ == 0 ? t1 : t2, 3600)));
        var time = new ManualTime();
        using var p = Provider(h, secret, time);

        Assert.Equal(t1, await p.GetAccessTokenAsync(_ct));
        Assert.Equal(t1, await p.GetAccessTokenAsync(_ct));
        Assert.Single(h.Requests);
        var (uri, body) = h.Requests[0];
        Assert.Equal("https", uri!.Scheme);
        Assert.Equal("login.microsoftonline.com", uri.Host);
        Assert.Equal("/contoso.onmicrosoft.com/oauth2/v2.0/token", uri.AbsolutePath);
        Assert.Contains("grant_type=client_credentials", body, StringComparison.Ordinal);
        Assert.Contains("scope=https%3A%2F%2Foutlook.office365.com%2F.default", body, StringComparison.Ordinal);

        time.Now += TimeSpan.FromMinutes(54); // 60 min − margines 5 min = 55 min ważności
        Assert.Equal(t1, await p.GetAccessTokenAsync(_ct));
        time.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(t2, await p.GetAccessTokenAsync(_ct));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact, Trait("Plan", "BL-45")]
    public async Task Invalidate_forces_a_fresh_token()
    {
        var n = 0;
        var h = new StubHandler((_, _) => Json(HttpStatusCode.OK, TokenJson(RandomToken() + (n++).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        using var p = Provider(h, "s3cret-value");
        var a = await p.GetAccessTokenAsync(_ct);
        p.Invalidate();
        var b = await p.GetAccessTokenAsync(_ct);
        Assert.NotEqual(a, b);
        Assert.Equal(2, h.Requests.Count);
    }

    [Theory, Trait("Plan", "BL-45")]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(302, true)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task Http_status_is_classified_as_permanent_or_transient_without_leaking_secret(int status, bool permanent)
    {
        var secret = "S-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
        var h = new StubHandler((_, _) => Json((HttpStatusCode)status, $$"""{"error":"invalid_client","error_description":"{{secret}}"}"""));
        using var p = Provider(h, secret);
        var ex = await Assert.ThrowsAsync<OAuth2TokenException>(() => p.GetAccessTokenAsync(_ct));
        Assert.Equal(permanent, ex.Permanent);
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "BL-45")]
    public async Task Network_failure_is_transient()
    {
        var h = new StubHandler((_, _) => throw new HttpRequestException("boom"));
        using var p = Provider(h, "s3cret-value");
        var ex = await Assert.ThrowsAsync<OAuth2TokenException>(() => p.GetAccessTokenAsync(_ct));
        Assert.False(ex.Permanent);
    }

    [Theory, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.4")]
    [InlineData("""{"token_type":"mac","expires_in":3600,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3600}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3600,"access_token":""}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3600,"access_token":"a b"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3600,"access_token":"a\r\nb"}""")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public async Task Malformed_or_unsafe_token_response_is_rejected_permanently(string json)
    {
        var h = new StubHandler((_, _) => Json(HttpStatusCode.OK, json));
        using var p = Provider(h, "s3cret-value");
        var ex = await Assert.ThrowsAsync<OAuth2TokenException>(() => p.GetAccessTokenAsync(_ct));
        Assert.True(ex.Permanent);
    }

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.4")]
    public async Task Oversized_response_and_overlong_token_are_rejected()
    {
        var big = new StubHandler((_, _) => Json(HttpStatusCode.OK, TokenJson(new string('a', ClientCredentialsTokenProvider.MaxResponseBytes))));
        using var p1 = Provider(big, "s3cret-value");
        Assert.True((await Assert.ThrowsAsync<OAuth2TokenException>(() => p1.GetAccessTokenAsync(_ct))).Permanent);

        var longTok = new StubHandler((_, _) => Json(HttpStatusCode.OK, TokenJson(new string('a', ClientCredentialsTokenProvider.MaxTokenLength + 1))));
        using var p2 = Provider(longTok, "s3cret-value");
        Assert.True((await Assert.ThrowsAsync<OAuth2TokenException>(() => p2.GetAccessTokenAsync(_ct))).Permanent);
    }

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.4")]
    public void Lifetime_is_clamped_and_endpoint_must_be_https()
    {
        var (_, short1) = ClientCredentialsTokenProvider.Parse(Encoding.UTF8.GetBytes(TokenJson("abc", 1)));
        var (_, long1) = ClientCredentialsTokenProvider.Parse(Encoding.UTF8.GetBytes(TokenJson("abc", 999999)));
        Assert.Equal(TimeSpan.FromSeconds(60), short1);
        Assert.Equal(TimeSpan.FromDays(1), long1);
        Assert.Throws<ArgumentException>(() => new ClientCredentialsTokenProvider(new Uri("http://login.example/token"), "id", "s", "https://x/.default"));
    }

    [Theory, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.4")]
    [InlineData("contoso.onmicrosoft.com", true)]
    [InlineData("11111111-2222-3333-4444-555555555555", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("a?b=c", false)]
    [InlineData("a b", false)]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData(".a", false)]
    [InlineData("a@evil.example", false)]
    public void Tenant_is_restricted_to_url_safe_characters(string tenant, bool ok) =>
        Assert.Equal(ok, ClientCredentialsTokenProvider.IsValidTenant(tenant));

    [Fact, Trait("Plan", "BL-45")]
    public void Classification_maps_token_errors()
    {
        var perm = EmailSender.Classify(new OAuth2TokenException(true, "x"))!.Value;
        Assert.Equal(DeliveryResult.Permanent, perm.Result);
        Assert.Equal(KbCode.OAuth2TokenRejected, perm.KbCode);
        var transient = EmailSender.Classify(new OAuth2TokenException(false, "x"))!.Value;
        Assert.Equal(DeliveryResult.Retry, transient.Result);
        Assert.Equal(KbCode.EmailDeliveryFailed, transient.KbCode);
    }

    // ---------- konfiguracja ----------

    private static Func<string, string?> Env(Dictionary<string, string> vars) => k => vars.GetValueOrDefault(k);

    private static Dictionary<string, string> OAuthEnv() => new()
    {
        [EmailConfig.HostVariable] = "smtp.office365.com",
        [EmailConfig.FromVariable] = "kobian@contoso.example",
        [EmailConfig.ToVariable] = "admin@contoso.example",
        [EmailConfig.AuthVariable] = "oauth2",
        [EmailConfig.OAuthTenantVariable] = "contoso.onmicrosoft.com",
        [EmailConfig.OAuthClientIdVariable] = "11111111-2222-3333-4444-555555555555",
    };

    private void WriteSecret(string content) => File.WriteAllText(Path.Combine(_dir, EmailConfig.OAuthSecretFile), content);

    [Fact, Trait("Plan", "BL-45")]
    public void Oauth2_configuration_builds_a_token_credential_for_the_sender_mailbox()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        WriteSecret(secret + "\n");
        var r = EmailConfig.Load(Env(OAuthEnv()), _dir);
        Assert.Null(r.Error);
        var cred = r.Settings!.Credential!;
        Assert.NotNull(cred.TokenProvider);
        Assert.Equal("kobian@contoso.example", cred.Login);
        Assert.Equal("", cred.Secret);
        Assert.DoesNotContain(secret, cred.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, r.Settings.ToString(), StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "BL-45")]
    public void Oauth2_mailbox_can_differ_from_sender()
    {
        WriteSecret("abc123secret");
        var env = OAuthEnv();
        env[EmailConfig.OAuthUserVariable] = "relay@contoso.example";
        Assert.Equal("relay@contoso.example", EmailConfig.Load(Env(env), _dir).Settings!.Credential!.Login);
    }

    [Theory, Trait("Plan", "BL-45")]
    [InlineData("tenant", "bad/tenant")]
    [InlineData("tenant", "")]
    [InlineData("client", "id with space")]
    [InlineData("client", "")]
    [InlineData("user", "not-an-address")]
    [InlineData("scope", "http://insecure.example/.default")]
    [InlineData("scope", "outlook scope")]
    public void Invalid_oauth_settings_are_rejected_with_kb_6209(string what, string value)
    {
        WriteSecret("abc123secret");
        var env = OAuthEnv();
        var key = what switch
        {
            "tenant" => EmailConfig.OAuthTenantVariable,
            "client" => EmailConfig.OAuthClientIdVariable,
            "user" => EmailConfig.OAuthUserVariable,
            _ => EmailConfig.OAuthScopeVariable,
        };
        env[key] = value;
        var r = EmailConfig.Load(Env(env), _dir);
        Assert.Null(r.Settings);
        Assert.Equal(KbCode.OAuth2ConfigInvalid, r.ErrorCode);
    }

    [Theory, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.4")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("line1\nline2\n")]
    public void Missing_or_malformed_secret_file_is_rejected(string? content)
    {
        if (content is not null) WriteSecret(content);
        var r = EmailConfig.Load(Env(OAuthEnv()), _dir);
        Assert.Null(r.Settings);
        Assert.Equal(KbCode.OAuth2ConfigInvalid, r.ErrorCode);
    }

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.4")]
    public void Oauth2_is_refused_over_plain_smtp_and_unknown_modes_are_rejected()
    {
        WriteSecret("abc123secret");
        var plain = OAuthEnv();
        plain[EmailConfig.TlsVariable] = "none";
        plain[EmailConfig.InsecureVariable] = "1";
        Assert.Equal(KbCode.SmtpInsecureEnabled, EmailConfig.Load(Env(plain), _dir).ErrorCode);

        var bad = OAuthEnv();
        bad[EmailConfig.AuthVariable] = "kerberos";
        Assert.Equal(KbCode.SmtpConfigInvalid, EmailConfig.Load(Env(bad), _dir).ErrorCode);
    }

    [Fact, Trait("Plan", "BL-45")]
    public void Password_mode_stays_the_default_and_ignores_oauth_variables()
    {
        var env = OAuthEnv();
        env.Remove(EmailConfig.AuthVariable);
        var r = EmailConfig.Load(Env(env), _dir); // brak smtp.credentials = relay bez uwierzytelnienia
        Assert.Null(r.Error);
        Assert.Null(r.Settings!.Credential);
    }
}
