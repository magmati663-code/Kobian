using System.Text;
using System.Text.Json;
using Kobian.Domain;
using Kobian.Notifications.Events;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

/// <summary>BL-45 / D9.6 / SEC-9.6: zdarzenie zakończenia uruchomienia bez danych klienta.</summary>
public sealed class RunFinishedEventTests
{
    private static RunFinished Sample(string? kb = null, string status = "failed") => new(
        "0123456789abcdef0123456789abcdef", "7a2e0000", "ERP-noc", status, kb ?? KbCode.RepoWriteFailed, 1, "retry",
        DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), DateTimeOffset.FromUnixTimeSeconds(1_760_000_125), 10, 1000, 200, 0, null);

    [Fact, Trait("D", "D9.6"), Trait("AC", "AC-08.4")]
    public void Failed_run_event_has_cloudevents_envelope_and_kb()
    {
        var json = JsonDocument.Parse(RunFinishedEvent.Build("01J000000000000000000000", "6f1c", Sample(), minimal: false)).RootElement;
        Assert.Equal("1.0", json.GetProperty("specversion").GetString());
        Assert.Equal(RunFinishedEvent.Type, json.GetProperty("type").GetString());
        Assert.Equal("job:7a2e0000", json.GetProperty("subject").GetString());
        var d = json.GetProperty("data");
        Assert.Equal("failed", d.GetProperty("status").GetString());
        Assert.Equal("KB-1402", d.GetProperty("kb").GetString());
        Assert.Equal(125, d.GetProperty("durationSeconds").GetInt64());
        Assert.Equal("ERP-noc", d.GetProperty("job").GetProperty("name").GetString());
    }

    [Fact, Trait("SEC", "SEC-9.6")]
    public void Minimal_mode_drops_job_name_and_data_keys_are_a_closed_list()
    {
        var bytes = RunFinishedEvent.Build("01J000000000000000000000", "6f1c", Sample(), minimal: true);
        var d = JsonDocument.Parse(bytes).RootElement.GetProperty("data");
        Assert.False(d.GetProperty("job").TryGetProperty("name", out _));
        Assert.Equal(["runId", "status", "kb", "attempt", "cause", "job", "startedUtc", "durationSeconds", "stats", "snapshotId"],
            d.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("ERP-noc", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Theory, Trait("SEC", "SEC-9.6")]
    [InlineData("C:\\Dane\\x")]
    [InlineData("../etc")]
    [InlineData("")]
    public void Identifiers_that_could_carry_paths_are_rejected(string bad)
    {
        Assert.Throws<ArgumentException>(() => RunFinishedEvent.Build("e", "h", Sample() with { RunId = bad }, false));
        Assert.Throws<ArgumentException>(() => RunFinishedEvent.Build("e", "h", Sample() with { SnapshotId = bad.Length == 0 ? "x y" : bad }, false));
    }

    [Fact, Trait("D", "D9.6")]
    public void Invalid_status_kb_or_times_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => RunFinishedEvent.Build("e", "h", Sample(status: "ok"), false));
        Assert.Throws<ArgumentException>(() => RunFinishedEvent.Build("e", "h", Sample(kb: "błąd: dysk D:\\ pełny"), false));
        Assert.Throws<ArgumentException>(() => RunFinishedEvent.Build("e", "h", Sample() with { FinishedUtc = DateTimeOffset.UnixEpoch }, false));
    }
}
