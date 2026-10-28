using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Domain.Serialization;
using Kobian.Notifications.Alerts;
using Kobian.Notifications.Audit;
using Kobian.Notifications.Events;
using Kobian.Notifications.Webhooks;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

public sealed class JcsTests
{
    [Fact]
    public void Rfc8785_property_sorting_example()
    {
        var input = "{\"\\u20ac\":\"Euro Sign\",\"\\r\":\"Carriage Return\",\"\\ufb33\":\"Hebrew\",\"1\":\"One\",\"\\ud83d\\ude00\":\"Emoji\",\"\\u0080\":\"Control\",\"\\u00f6\":\"o\"}";
        var expected = "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\",\"ö\":\"o\",\"€\":\"Euro Sign\",\"😀\":\"Emoji\",\"\ufb33\":\"Hebrew\"}";
        Assert.Equal(expected, Encoding.UTF8.GetString(Jcs.Canonicalize(input)));
    }

    [Fact]
    public void Whitespace_and_order_do_not_change_canonical_form() =>
        Assert.Equal(Jcs.Canonicalize("{\"b\":[1,2,{\"z\":null,\"a\":true}],\"a\":\"x/y\"}"), Jcs.Canonicalize(" { \"a\" : \"x/y\" , \"b\" : [ 1 , 2 , { \"a\" : true , \"z\" : null } ] } "));

    [Theory]
    [InlineData("{\"a\":1.5}")]
    [InlineData("{\"a\":1e3}")]
    [InlineData("{\"a\":9007199254740993}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    public void Unsupported_numbers_and_duplicates_rejected(string json) =>
        Assert.Throws<FormatException>(() => Jcs.Canonicalize(json));

    [Fact]
    public void Control_characters_escaped_lowercase() =>
        Assert.Equal("[\"\\u001f\\n\"]", Encoding.UTF8.GetString(Jcs.Canonicalize("[\"\\u001F\\n\"]")));
}

public sealed class AuditChainTests
{
    private static AuditEntry E(long seq, string op = "JobService.UpsertJob") =>
        new(seq, "2026-10-12T20:00:00Z", "S-1-5-21-1-2-3-1001", "Admin", "ui", op, "job:1", "{\"name\":\"ERP\",\"password\":\"***\"}", "allowed", null);

    private static List<ChainedAuditEntry> Build(int n)
    {
        var list = new List<ChainedAuditEntry>();
        var prev = AuditChain.Genesis;
        for (var i = 1; i <= n; i++)
        {
            var c = AuditChain.Append(prev, E(i));
            list.Add(c);
            prev = c.Hash;
        }
        return list;
    }

    [Fact, Trait("D", "D9.9")]
    public void Intact_chain_verifies_and_matches_anchor()
    {
        var chain = Build(10);
        Assert.Null(AuditChain.Verify(chain, (5, chain[4].Hash)));
    }

    [Fact, Trait("D", "D9.9")]
    public void Modified_entry_is_detected_at_its_seq()
    {
        var chain = Build(10);
        chain[6] = chain[6] with { Entry = chain[6].Entry with { Outcome = "denied" } };
        Assert.Equal(7, AuditChain.Verify(chain));
    }

    [Fact, Trait("D", "D9.9")]
    public void Deleted_entry_is_detected()
    {
        var chain = Build(10);
        chain.RemoveAt(3);
        Assert.Equal(5, AuditChain.Verify(chain));
    }

    [Fact, Trait("D", "D9.9")]
    public void Rewritten_tail_is_detected_by_anchor()
    {
        var chain = Build(10);
        var anchor = (Seq: 8L, Hash: chain[7].Hash);
        // napastnik przepisuje wpisy 6–10 i przelicza hashe — spójny łańcuch, ale niezgodny z kotwicą
        var prev = chain[4].Hash;
        for (var i = 5; i < 10; i++)
        {
            chain[i] = AuditChain.Append(prev, E(i + 1, "Forged"));
            prev = chain[i].Hash;
        }
        Assert.Null(AuditChain.Verify(chain));
        Assert.Equal(8, AuditChain.Verify(chain, anchor));
    }
}

public sealed class WebhookTests
{
    [Fact, Trait("D", "D9.10")]
    public void Signature_roundtrip_and_replay_window()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var body = "{\"id\":\"1\"}"u8.ToArray();
        var sig = WebhookSignature.Sign(secret, 1_790_000_000, body);
        Assert.StartsWith("v1=", sig, StringComparison.Ordinal);
        Assert.True(WebhookSignature.Verify(secret, "1790000000", sig, body, 1_790_000_100));
        Assert.False(WebhookSignature.Verify(secret, "1790000000", sig, body, 1_790_000_301));
        Assert.False(WebhookSignature.Verify(secret, "1790000001", sig, body, 1_790_000_100)); // znacznik podmieniony
        Assert.False(WebhookSignature.Verify(secret, "1790000000", sig, "{\"id\":\"2\"}"u8.ToArray(), 1_790_000_100));
        Assert.True(WebhookSignature.Verify(secret, "1790000000", "v1=00, " + sig, body, 1_790_000_100)); // rotacja: dwa podpisy
    }

    [Theory, Trait("SEC", "SEC-9.3")]
    [InlineData("http://hooks.example.com/x", false)]
    [InlineData("https://user:pw@hooks.example.com/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://10.1.2.3/x", false)]
    [InlineData("https://192.168.1.10/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://[::1]/x", false)]
    [InlineData("https://[fd00::1]/x", false)]
    [InlineData("https://[::ffff:10.0.0.1]/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://nas.local/x", false)]
    [InlineData("https://intranet/x", false)]
    [InlineData("https://hooks.example.com:8443/x", false)]
    [InlineData("https://hooks.example.com/x", true)]
    [InlineData("https://8.8.8.8/x", true)]
    public void Webhook_url_policy(string url, bool allowed) => Assert.Equal(allowed, WebhookUrlPolicy.IsAllowed(url));

    [Fact]
    public void Cgnat_and_multicast_are_not_public()
    {
        Assert.False(WebhookUrlPolicy.IsPublic(IPAddress.Parse("100.64.0.1")));
        Assert.False(WebhookUrlPolicy.IsPublic(IPAddress.Parse("224.0.0.1")));
    }
}

public sealed class AlertStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);

    [Fact, Trait("AC", "AC-05.1")]
    public void One_notification_then_reminder_after_24h_then_resolved()
    {
        var (a, o) = AlertStateMachine.Tick(null, "A01", "job:1", AlertSeverity.Critical, true, T0, false);
        Assert.Equal(AlertOutbox.Opened, o);
        (a, o) = AlertStateMachine.Tick(a, "A01", "job:1", AlertSeverity.Critical, true, T0.AddHours(1), false);
        Assert.Equal(AlertOutbox.None, o);
        (a, o) = AlertStateMachine.Tick(a, "A01", "job:1", AlertSeverity.Critical, true, T0.AddHours(24), false);
        Assert.Equal(AlertOutbox.Reminder, o);
        Assert.Equal(1, a!.Reminders);
        (a, o) = AlertStateMachine.Tick(a, "A01", "job:1", AlertSeverity.Critical, false, T0.AddHours(25), false);
        Assert.Equal(AlertOutbox.Resolved, o);
        Assert.Equal(AlertState.Resolved, a!.State);
    }

    [Fact]
    public void Quiet_hours_delay_warnings_but_not_critical()
    {
        var (w, ow) = AlertStateMachine.Tick(null, "A06", "dest:1", AlertSeverity.Warning, true, T0, quietHours: true);
        Assert.Equal((AlertState.Open, AlertOutbox.None), (w!.State, ow));
        var (_, oc) = AlertStateMachine.Tick(null, "A03", "job:1", AlertSeverity.Critical, true, T0, quietHours: true);
        Assert.Equal(AlertOutbox.Opened, oc);
        // ostrzeżenie rozwiązane przed wysłaniem → brak jakiejkolwiek wiadomości
        var (r, or) = AlertStateMachine.Tick(w, "A06", "dest:1", AlertSeverity.Warning, false, T0.AddMinutes(5), true);
        Assert.Equal((AlertState.Resolved, AlertOutbox.None), (r!.State, or));
    }

    [Fact]
    public void Security_alert_silence_capped_at_24h_and_ack_reopens_after_72h()
    {
        var (a, _) = AlertStateMachine.Tick(null, "A17", "host", AlertSeverity.Security, true, T0, false);
        var s = AlertStateMachine.Silence(a!, T0, TimeSpan.FromDays(7));
        Assert.Equal(T0.AddHours(24), s.SilencedUntil);
        var ack = AlertStateMachine.Acknowledge(a!, T0);
        var (b, o) = AlertStateMachine.Tick(ack, "A17", "host", AlertSeverity.Security, true, T0.AddHours(71), false);
        Assert.Equal(AlertOutbox.None, o);
        (b, o) = AlertStateMachine.Tick(b, "A17", "host", AlertSeverity.Security, true, T0.AddHours(72), false);
        Assert.Equal((AlertState.Notified, AlertOutbox.Reminder), (b!.State, o));
    }
}

public sealed class CloudEventTests
{
    [Fact, Trait("D", "D9.6"), Trait("SEC", "SEC-9.6")]
    public void Envelope_has_required_attributes_and_minimal_mode_drops_job_name()
    {
        var data = new Dictionary<string, object?>
        {
            ["code"] = "A03",
            ["kb"] = "KB-6103",
            ["job"] = new Dictionary<string, object?> { ["id"] = "7a2e", ["name"] = "ERP-noc" },
            ["rpoSeconds"] = 93600L,
        };
        var full = JsonDocument.Parse(CloudEvent.Serialize("01J", "6f1c", "pl.kobian.alert.opened.v1", DateTimeOffset.UnixEpoch, "job:7a2e", data, minimal: false)).RootElement;
        Assert.Equal("1.0", full.GetProperty("specversion").GetString());
        Assert.Equal("urn:kobian:host:6f1c", full.GetProperty("source").GetString());
        Assert.Equal("1970-01-01T00:00:00Z", full.GetProperty("time").GetString());
        Assert.Equal("ERP-noc", full.GetProperty("data").GetProperty("job").GetProperty("name").GetString());
        var min = JsonDocument.Parse(CloudEvent.Serialize("01J", "6f1c", "pl.kobian.alert.opened.v1", DateTimeOffset.UnixEpoch, null, data, minimal: true)).RootElement;
        Assert.False(min.GetProperty("data").GetProperty("job").TryGetProperty("name", out _));
    }

    [Fact]
    public void Unknown_type_rejected() =>
        Assert.Throws<ArgumentException>(() => CloudEvent.Serialize("1", "h", "pl.kobian.evil.v1", DateTimeOffset.UnixEpoch, null, new Dictionary<string, object?>(), false));
}
