using System.Security.Cryptography;
using System.Text;
using Kobian.Notifications.Email;
using Kobian.Notifications.Events;
using MimeKit;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

/// <summary>
/// BL-45 — konfiguracja kanału e-mail (KOBIAN_SMTP_* + plik sekretu), walidacja adresów (SEC-9.1, CVE-2026-30227: CR/LF, składnia, limity) i budowa wiadomości
/// (nagłówki przez API MimeKit, bez sekretów, tryb minimalny SEC-9.6, temat ≤ 120 znaków). Dane i sekrety syntetyczne, losowane w teście.
/// </summary>
public sealed class EmailConfigTests : IDisposable
{
    private readonly string _secrets = Path.Combine(Path.GetTempPath(), "kobian-smtp-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal);

    public EmailConfigTests()
    {
        Directory.CreateDirectory(_secrets);
        _env[EmailConfig.HostVariable] = "smtp.example.com";
        _env[EmailConfig.FromVariable] = "kobian@example.com";
        _env[EmailConfig.ToVariable] = "admin@example.com; serwis@example.org";
    }

    public void Dispose()
    {
        try { Directory.Delete(_secrets, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private string? Env(string k) => _env.GetValueOrDefault(k);

    private EmailConfigResult Load() => EmailConfig.Load(Env, _secrets, "SRV-KSIEGOWOSC");

    private (string Login, string Secret) WriteCredentials()
    {
        var login = "kobian-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        File.WriteAllText(Path.Combine(_secrets, EmailConfig.CredentialsFile), login + "\n" + secret + "\n");
        return (login, secret);
    }

    [Fact, Trait("Plan", "BL-45")]
    public void Missing_host_disables_the_channel()
    {
        _env[EmailConfig.HostVariable] = null;
        Assert.Same(EmailConfigResult.Disabled, Load());
    }

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.4")]
    public void Valid_config_defaults_to_required_starttls_and_reads_credentials_only_from_the_secrets_file()
    {
        var (login, secret) = WriteCredentials();
        _env["KOBIAN_SMTP_PASSWORD"] = "ignored-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)); // zmienna nie jest źródłem hasła
        var r = Load();
        Assert.Null(r.Error);
        Assert.Null(r.Warning);
        var s = r.Settings!;
        Assert.Equal(("smtp.example.com", 587, SmtpTlsMode.StartTls), (s.Host, s.Port, s.Tls));
        Assert.Equal(["admin@example.com", "serwis@example.org"], s.To);
        Assert.Equal(login, s.Credential!.Login);
        Assert.Equal(secret, s.Credential.Secret);
        Assert.True(s.ProblemsOnly); // 9.7: domyślnie tylko Warning/Failed
        Assert.False(s.Minimal);
        Assert.Equal("pl", s.Language);
        Assert.Equal("SRV-KSIEGOWOSC", s.HostLabel);
        Assert.DoesNotContain(secret, s.ToString(), StringComparison.Ordinal); // rekord nie ujawnia hasła
        Assert.DoesNotContain(secret, s.Credential.ToString(), StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "BL-45")]
    public void Port_465_selects_implicit_tls_and_options_are_parsed()
    {
        _env[EmailConfig.PortVariable] = "465";
        _env[EmailConfig.LanguageVariable] = "EN";
        _env[EmailConfig.MinimalVariable] = "1";
        _env[EmailConfig.NotifyVariable] = "all";
        var s = Load().Settings!;
        Assert.Equal((465, SmtpTlsMode.Implicit, "en", true, false), (s.Port, s.Tls, s.Language, s.Minimal, s.ProblemsOnly));
        Assert.Null(s.Credential); // relay bez uwierzytelnienia
        _env[EmailConfig.TlsVariable] = "starttls";
        Assert.Equal(SmtpTlsMode.StartTls, Load().Settings!.Tls);
    }

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.1")]
    public void Plain_smtp_requires_the_insecure_flag_warns_and_never_carries_credentials()
    {
        _env[EmailConfig.TlsVariable] = "none";
        var r = Load();
        Assert.Null(r.Settings);
        Assert.Equal("KB-6207", r.ErrorCode);
        _env[EmailConfig.InsecureVariable] = "1";
        r = Load();
        Assert.NotNull(r.Settings);
        Assert.Equal(25, r.Settings.Port);
        Assert.Contains("KB-6207", r.Warning!, StringComparison.Ordinal);
        WriteCredentials();
        r = Load();
        Assert.Null(r.Settings); // hasło nie pójdzie otwartym tekstem
        Assert.Equal("KB-6207", r.ErrorCode);
    }

    [Theory, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.1")]
    [InlineData("KOBIAN_SMTP_TO", "admin@example.com\r\nRCPT TO:<x@evil.example>", "KB-6205")]
    [InlineData("KOBIAN_SMTP_TO", "admin@example.com\nBcc: x@evil.example", "KB-6205")]
    [InlineData("KOBIAN_SMTP_FROM", "kobian@example.com\r\nBcc: x@evil.example", "KB-6205")]
    [InlineData("KOBIAN_SMTP_FROM", "Kobian <kobian@example.com>", "KB-6205")]
    [InlineData("KOBIAN_SMTP_FROM", "kobian@example.com, x@evil.example", "KB-6205")]
    [InlineData("KOBIAN_SMTP_TO", "admin@example.com,,", "KB-6205")]
    [InlineData("KOBIAN_SMTP_FROM", "", "KB-6204")]
    [InlineData("KOBIAN_SMTP_HOST", "smtp.example.com\r\nX", "KB-6204")]
    [InlineData("KOBIAN_SMTP_HOST", "smtp example.com", "KB-6204")]
    [InlineData("KOBIAN_SMTP_PORT", "0", "KB-6204")]
    [InlineData("KOBIAN_SMTP_PORT", "70000", "KB-6204")]
    [InlineData("KOBIAN_SMTP_PORT", "+25", "KB-6204")]
    [InlineData("KOBIAN_SMTP_TLS", "opportunistic", "KB-6204")]
    [InlineData("KOBIAN_SMTP_LANG", "de", "KB-6204")]
    [InlineData("KOBIAN_SMTP_NOTIFY", "never", "KB-6204")]
    public void Invalid_values_are_rejected_with_a_kb_code_and_without_echoing_the_value(string variable, string value, string code)
    {
        _env[variable] = value;
        var r = Load();
        Assert.Null(r.Settings);
        Assert.Equal(code, r.ErrorCode);
        Assert.Contains(code, r.Error!, StringComparison.Ordinal);
        if (value.Length > 3) Assert.DoesNotContain(value, r.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", r.Error!, StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "BL-45")]
    public void Recipient_list_is_limited_and_deduplicated()
    {
        _env[EmailConfig.ToVariable] = string.Join(',', Enumerable.Range(0, EmailConfig.MaxRecipients + 1).Select(i => $"u{i}@example.com"));
        Assert.Equal("KB-6204", Load().ErrorCode);
        _env[EmailConfig.ToVariable] = "a@example.com, A@example.com ,b@example.com";
        Assert.Equal(["a@example.com", "b@example.com"], Load().Settings!.To);
    }

    [Theory, Trait("Plan", "BL-45")]
    [InlineData("tylko-login\n")]
    [InlineData("login\nhaslo\ntrzeci\n")]
    [InlineData("log\u0007in\nx\n")]
    public void Malformed_credentials_file_is_a_configuration_error(string content)
    {
        File.WriteAllText(Path.Combine(_secrets, EmailConfig.CredentialsFile), content);
        var r = Load();
        Assert.Equal("KB-6204", r.ErrorCode);
        Assert.Contains(EmailConfig.CredentialsFile, r.Error!, StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "BL-45")]
    public void Oversized_credentials_file_is_rejected()
    {
        File.WriteAllText(Path.Combine(_secrets, EmailConfig.CredentialsFile), "a\n" + new string('x', EmailConfig.MaxCredentialsFileBytes));
        Assert.Equal("KB-6204", Load().ErrorCode);
    }

    [Theory, Trait("SEC", "SEC-9.1"), Trait("Plan", "CVE-2026-30227")]
    [InlineData("admin@example.com", true)]
    [InlineData("a.b-c+tag@sub.example.co.uk", true)]
    [InlineData("admin@localhost", true)]
    [InlineData("admin@example.com\r\n", false)]
    [InlineData("adm\rin@example.com", false)]
    [InlineData("adm\nin@example.com", false)]
    [InlineData("\"adm\r\nin\"@example.com", false)] // CVE-2026-30227: CR/LF w części lokalnej w cudzysłowie
    [InlineData("adm\0in@example.com", false)]
    [InlineData("Admin <admin@example.com>", false)]
    [InlineData("admin@example.com,b@example.com", false)]
    [InlineData("admin@@example.com", false)]
    [InlineData("admin@", false)]
    [InlineData("@example.com", false)]
    [InlineData("admin", false)]
    [InlineData("admin@[127.0.0.1]", false)]
    [InlineData("admin@exa mple.com", false)]
    [InlineData("admin@-example.com", false)]
    [InlineData("zażółć@example.com", false)] // PROWIZORYCZNIE Qb65
    [InlineData("@route:admin@example.com", false)]
    [InlineData("admin(comment)@example.com", false)]
    public void Address_policy_accepts_only_plain_ascii_addr_spec(string address, bool valid) =>
        Assert.Equal(valid, EmailAddressPolicy.IsValid(address));

    [Fact, Trait("SEC", "SEC-9.1")]
    public void Address_length_limits_follow_rfc_5321()
    {
        Assert.True(EmailAddressPolicy.IsValid(new string('a', 64) + "@example.com"));
        Assert.False(EmailAddressPolicy.IsValid(new string('a', 65) + "@example.com"));
        var label = new string('b', 60);
        Assert.False(EmailAddressPolicy.IsValid("a@" + string.Join('.', Enumerable.Repeat(label, 5)) + ".com")); // > 254
    }

    // ---------- budowa wiadomości ----------

    private static EmailSettings Settings(bool minimal = false, string lang = "pl", SmtpCredential? cred = null) =>
        new("smtp.example.com", 587, SmtpTlsMode.StartTls, "kobian@example.com", ["admin@example.com", "serwis@example.org"], cred, minimal, true, lang, "SRV-01");

    internal static byte[] RunPayload(string eventId, string jobName, string status = "failed", string? kb = "KB-2104") =>
        RunFinishedEvent.Build(eventId, "00112233445566778899aabbccddeeff",
            new RunFinished("aa11bb22cc33dd44", "7a2e0011", jobName, status, kb, 2, "schedule",
                new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 17, 1, 2, 5, TimeSpan.Zero), 1234, 987654, 4321, 0, "5n4p"), minimal: false);

    private static string Serialize(MimeMessage m)
    {
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact, Trait("Plan", "BL-45"), Trait("Plan", "9.7"), Trait("SEC", "SEC-9.6")]
    public void Run_finished_message_has_safe_headers_subject_and_no_secrets()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var login = "user-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var eventId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        using var m = EmailMessageBuilder.Build(Settings(cred: new SmtpCredential(login, secret)), RunPayload(eventId, "Faktury-2026"));
        Assert.Equal("[Kobian] Kopia nieudana KB-2104: Faktury-2026 (SRV-01)", m.Subject);
        Assert.Equal(eventId + "@example.com", m.MessageId);
        Assert.Equal("auto-generated", m.Headers["Auto-Submitted"]);
        Assert.Equal(eventId, m.Headers["X-Kobian-Event-Id"]);
        Assert.Equal(["admin@example.com", "serwis@example.org"], m.To.Mailboxes.Select(x => x.Address));
        Assert.Empty(m.Cc);
        Assert.Empty(m.Bcc);
        Assert.Contains("Pliki: 1234", m.TextBody!, StringComparison.Ordinal);
        Assert.Contains("KB-2104", m.HtmlBody, StringComparison.Ordinal);
        var raw = Serialize(m);
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(login, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("00112233445566778899aabbccddeeff", raw, StringComparison.Ordinal); // identyfikator hosta z koperty nie jest potrzebny
    }

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.6")]
    public void Minimal_mode_drops_job_name_and_statistics()
    {
        using var m = EmailMessageBuilder.Build(Settings(minimal: true, lang: "en"), RunPayload("e1", "Kadry-Tajne", "warning", null));
        Assert.Equal("[Kobian] Backup completed with warnings: job 7a2e0011 (SRV-01)", m.Subject);
        var raw = Serialize(m);
        Assert.DoesNotContain("Kadry-Tajne", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("1234", m.TextBody, StringComparison.Ordinal);
    }

    [Fact, Trait("SEC", "SEC-9.1")]
    public void Control_characters_in_job_name_cannot_inject_headers_and_subject_is_bounded()
    {
        var name = "Faktury\r\nBcc: x@evil.example\r\n\r\n<script>" + new string('Z', 200) + "‮";
        using var m = EmailMessageBuilder.Build(Settings(), RunPayload("e2", name));
        Assert.Empty(m.Bcc);
        Assert.True(m.Subject!.Length <= EmailMessageBuilder.MaxSubjectLength);
        Assert.DoesNotContain('\r', m.Subject);
        Assert.DoesNotContain('\n', m.Subject);
        Assert.DoesNotContain('‮', m.Subject);
        var raw = Serialize(m);
        Assert.DoesNotContain("\nBcc:", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", m.HtmlBody, StringComparison.Ordinal); // encje HTML
        Assert.All(raw.Split('\n').Select(l => l.TrimEnd('\r')).TakeWhile(l => l.Length > 0), l => Assert.True(l.Length <= 998)); // RFC 5322
    }

    [Fact, Trait("Plan", "BL-45"), Trait("RD", "RD-61")]
    public void Daily_report_message_carries_counts_and_the_audit_anchor()
    {
        var anchor = "18422:" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "daily", ["fromUtc"] = "2026-10-16T05:00:00Z", ["toUtc"] = "2026-10-17T05:00:00Z",
            ["results"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["succeeded"] = 5L, ["failed"] = 1L, ["warning"] = 0L },
            ["failedJobIds"] = "7a2e0011,bad id;x", ["deadNotifications"] = 2L,
            ["auditAnchor"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = anchor, ["atUtc"] = "2026-10-17T04:55:00Z" },
        };
        var payload = CloudEvent.Serialize("r1", "00ff", "pl.kobian.report.ready.v1", new DateTimeOffset(2026, 10, 17, 5, 0, 0, TimeSpan.Zero), "report:daily", data, minimal: true);
        using var m = EmailMessageBuilder.Build(Settings(), payload);
        Assert.Equal("[Kobian] Raport dzienny SRV-01 2026-10-17: udane 5, nieudane 1", m.Subject);
        Assert.Contains("Kotwica audytu: " + anchor, m.TextBody, StringComparison.Ordinal);
        Assert.Contains("kobian audit verify --anchor " + anchor, m.TextBody, StringComparison.Ordinal);
        Assert.Contains("Zadania z błędem (identyfikatory): 7a2e0011", m.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("bad id", m.TextBody, StringComparison.Ordinal); // wartość spoza wzorca pominięta
        Assert.Contains("Niedoręczone powiadomienia: 2", m.TextBody, StringComparison.Ordinal);
    }

    [Theory, Trait("Plan", "BL-45")]
    [InlineData("{\"type\":\"pl.kobian.heartbeat.v1\",\"data\":{}}")]
    [InlineData("{\"type\":\"pl.kobian.run.finished.v1\"}")]
    [InlineData("nie json")]
    [InlineData("[]")]
    public void Unsupported_or_malformed_payload_is_rejected(string payload) =>
        Assert.Throws<FormatException>(() => EmailMessageBuilder.Build(Settings(), Encoding.UTF8.GetBytes(payload)));

    [Fact, Trait("Plan", "BL-45")]
    public void Templates_have_the_same_keys_in_both_languages() =>
        Assert.Equal(EmailTemplates.Keys("pl").Order(StringComparer.Ordinal), EmailTemplates.Keys("en").Order(StringComparer.Ordinal));
}
