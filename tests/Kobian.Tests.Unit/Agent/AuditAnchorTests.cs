using Kobian.Agent.Audit;
using Kobian.Agent.Hosting;
using Kobian.Api.V1;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Notifications.Audit;
using Kobian.Tests.Unit.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// it. 99 — D9.9 kotwice dzienne łańcucha audytu i RD-51 kontrola schematu: przepisanie historii z przeliczeniem łańcucha (wykrywa kotwica),
/// skrócenie dziennika, kotwica zewnętrzna z raportu, odbudowana tabela / zdjęty wyzwalacz (KB-6502), termin 06:55 czasu lokalnego.
/// </summary>
public sealed class AuditAnchorTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-anchor-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private AuditLogService _audit = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _audit = new AuditLogService(Catalog, _writer, TimeProvider.System);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private Task Append(int i) =>
        _audit.AppendAsync(new AuditRecord(AgentCallAudit.LocalActor, "Operator", "api", "Agent/Op" + i, "cel", null, "allowed", null), _ct);

    /// <summary>„Atakujący z dostępem do pliku”: zdejmuje wyzwalacze, wykonuje SQL, odtwarza wyzwalacze IDENTYCZNIE (schemat wygląda na nietknięty).</summary>
    private async Task Tamper(params string[] sql)
    {
        await _writer.FlushAsync(_ct);
        var schema = CatalogBootstrap.MigrationSchema("core", 3);
        using var c = new SqliteConnection($"Data Source={Catalog};Pooling=False");
        c.Open();
        void X(string s)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = s;
            cmd.ExecuteNonQuery();
        }
        X("PRAGMA foreign_keys = OFF;");
        foreach (var t in new[] { "audit_no_update", "audit_no_delete", "audit_seq_contiguous" }) X($"DROP TRIGGER {t};");
        foreach (var s in sql) X(s);
        foreach (var t in new[] { "audit_no_update", "audit_no_delete", "audit_seq_contiguous" }) X(schema["trigger:" + t] + ";");
    }

    /// <summary>Przepisuje wpis <paramref name="seq"/> i PRZELICZA cały łańcuch od niego — weryfikacja samego łańcucha tego nie wykryje.</summary>
    private async Task RewriteWithRecomputedChain(long seq, string newOperation)
    {
        await _writer.FlushAsync(_ct);
        var rows = new List<ChainedAuditEntry>();
        using (var c = CatalogBootstrap.Open(Catalog, readOnly: true))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT seq,at_utc,actor_sid,role,channel,operation,target,outcome,kb_code,prev_hash,hash FROM audit_log ORDER BY seq;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new ChainedAuditEntry(new AuditEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), null, r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)), (byte[])r[9], (byte[])r[10]));
        }
        var prev = AuditChain.Genesis;
        var sql = new List<string>();
        foreach (var row in rows)
        {
            var e = row.Entry.Seq == seq ? row.Entry with { Operation = newOperation } : row.Entry;
            var ch = AuditChain.Append(prev, e);
            sql.Add($"UPDATE audit_log SET operation='{e.Operation}', prev_hash=x'{Convert.ToHexString(ch.PrevHash)}', hash=x'{Convert.ToHexString(ch.Hash)}' WHERE seq={e.Seq};");
            prev = ch.Hash;
        }
        await Tamper([.. sql]);
    }

    [Fact, Trait("Plan", "D9.9"), Trait("SEC", "SEC-19.1")]
    public async Task Anchor_is_idempotent_per_seq_and_reported_by_verify()
    {
        Assert.Null(await _audit.CreateAnchorAsync(_ct)); // pusty dziennik
        for (var i = 0; i < 3; i++) await Append(i);
        var a = await _audit.CreateAnchorAsync(_ct);
        Assert.Equal(3, a!.Seq);
        Assert.Equal(a.AtUtc, (await _audit.CreateAnchorAsync(_ct))!.AtUtc); // ten sam seq — bez nowego wiersza
        await Append(3);
        await Append(4);
        var b = await _audit.CreateAnchorAsync(_ct);
        Assert.Equal(5, b!.Seq);
        var v = _audit.Verify();
        Assert.True(v.Ok);
        Assert.Equal((5L, 2), (v.Entries, v.AnchorsChecked));
        Assert.Equal(b.Text, v.LastAnchor!.Text);
        Assert.Matches("^5:[0-9a-f]{64}$", b.Text);
        var parsed = AuditAnchor.TryParse(b.Text)!.Value;
        Assert.Equal(5L, parsed.Seq);
        Assert.Equal(b.Hash, parsed.Hash);
    }

    [Fact, Trait("Plan", "D9.9"), Trait("SEC", "SEC-19.1")]
    public async Task History_rewritten_with_recomputed_chain_is_caught_only_by_the_anchor()
    {
        for (var i = 0; i < 5; i++) await Append(i);
        var anchor = await _audit.CreateAnchorAsync(_ct);
        await RewriteWithRecomputedChain(2, "Agent/Ukryte");
        var v = _audit.Verify();
        Assert.Equal(KbCode.AuditAnchorMismatch, v.Code); // łańcuch sam w sobie spójny, schemat identyczny — rozjazd tylko z kotwicą
        Assert.Equal(anchor!.Seq, v.BrokenSeq);
    }

    [Fact, Trait("Plan", "D9.9"), Trait("SEC", "SEC-19.1")]
    public async Task Truncated_log_is_caught_by_the_anchor_and_by_an_external_anchor()
    {
        for (var i = 0; i < 5; i++) await Append(i);
        var full = _audit.Verify();
        var external = $"5:{Convert.ToHexStringLower(full.LastHash!)}";
        Assert.True(_audit.Verify(AuditAnchor.TryParse(external)).Ok);
        Assert.Equal(1, _audit.Verify(AuditAnchor.TryParse(external)).AnchorsChecked);

        await Tamper("DELETE FROM audit_log WHERE seq >= 4;"); // bez kotwicy w katalogu: skrócony dziennik wygląda poprawnie …
        Assert.True(_audit.Verify().Ok);
        var v = _audit.Verify(AuditAnchor.TryParse(external)); // … ale kotwica zapisana poza serwerem go wykrywa
        Assert.Equal((KbCode.AuditAnchorMismatch, (long?)5), (v.Code, v.BrokenSeq));
        Assert.Equal(KbCode.AuditAnchorMismatch, _audit.Verify((2, new byte[32])).Code); // zły hash
    }

    [Theory, Trait("RD", "RD-51"), Trait("SEC", "SEC-19.1")]
    [InlineData("DROP TRIGGER audit_no_update;")]
    [InlineData("CREATE TRIGGER audit_extra AFTER INSERT ON audit_log BEGIN SELECT 1; END;")]
    [InlineData("ALTER TABLE audit_log RENAME TO audit_old; CREATE TABLE audit_log (seq INTEGER PRIMARY KEY, at_utc TEXT, actor_sid TEXT, actor_name TEXT, role TEXT, channel TEXT, operation TEXT, target TEXT, params BLOB, outcome TEXT, kb_code TEXT, prev_hash BLOB, hash BLOB); INSERT INTO audit_log SELECT * FROM audit_old; DROP TABLE audit_old;")]
    public async Task Changed_audit_schema_is_reported_as_KB6502_not_an_exception(string sql)
    {
        for (var i = 0; i < 3; i++) await Append(i);
        Assert.True(_audit.Verify().Ok);
        await _writer.FlushAsync(_ct);
        using (var c = new SqliteConnection($"Data Source={Catalog};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA legacy_alter_table = ON; " + sql;
            cmd.ExecuteNonQuery();
        }
        var v = _audit.Verify();
        Assert.Equal(KbCode.AuditSchemaChanged, v.Code);
        Assert.False(v.Ok);
    }

    [Fact, Trait("Plan", "D9.9")]
    public async Task Backend_passes_the_external_anchor_and_rejects_a_malformed_one()
    {
        for (var i = 0; i < 2; i++) await Append(i);
        var anchor = await _audit.CreateAnchorAsync(_ct);
        var backend = new CatalogAgentBackend(Catalog, "test") { Audit = _audit };
        var ok = await backend.VerifyAuditAsync(new VerifyAuditArgs { Anchor = anchor!.Text }, _ct);
        Assert.Equal((OverallState.Ok, 2, anchor.Text), (ok.Result, ok.AnchorsChecked, ok.LastAnchor));
        Assert.Equal(anchor.AtUtc, ok.LastAnchorAtUtc);
        var bad = await backend.VerifyAuditAsync(new VerifyAuditArgs { Anchor = "x:zz" }, _ct);
        Assert.Equal((OverallState.Failed, KbCode.JobDefinitionInvalid), (bad.Result, bad.Code));
        var wrong = await backend.VerifyAuditAsync(new VerifyAuditArgs { Anchor = "1:" + new string('0', 64) }, _ct);
        Assert.Equal((OverallState.Failed, KbCode.AuditAnchorMismatch, 1L), (wrong.Result, wrong.Code, wrong.BrokenSeq));
        foreach (var t in new[] { "", "0:" + new string('a', 64), "1:" + new string('a', 63), "-1:" + new string('a', 64), "1:" + new string('g', 64), "1:2:3" })
            Assert.Null(AuditAnchor.TryParse(t));
    }

    [Fact, Trait("Plan", "D9.9")]
    public void Daily_slot_is_0655_local_time_and_one_missed_slot_gives_one_anchor()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central European Standard Time" : "Europe/Warsaw");
        var morning = new DateTimeOffset(2026, 10, 17, 5, 0, 0, TimeSpan.Zero); // 07:00 CEST
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 4, 55, 0, TimeSpan.Zero), AuditAnchorSchedule.LastSlotUtc(morning, zone));
        var early = new DateTimeOffset(2026, 10, 17, 4, 0, 0, TimeSpan.Zero); // 06:00 CEST → wczorajszy termin
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 4, 55, 0, TimeSpan.Zero), AuditAnchorSchedule.LastSlotUtc(early, zone));
        var winter = new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero); // 07:00 CET
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 55, 0, TimeSpan.Zero), AuditAnchorSchedule.LastSlotUtc(winter, zone));
        Assert.True(AuditAnchorSchedule.IsDue(null, morning, zone));
        Assert.False(AuditAnchorSchedule.IsDue(morning.AddMinutes(-4), morning, zone)); // 04:56Z — po terminie
        Assert.True(AuditAnchorSchedule.IsDue(morning.AddDays(-3), morning, zone)); // po przerwie: należna (jedna)
    }

    [Fact, Trait("Plan", "D9.9")]
    public async Task Loop_tick_creates_at_most_one_anchor_per_day()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 17, 7, 0, 0, TimeSpan.Zero)); // po terminie 06:55 (strefa UTC)
        var audit = new AuditLogService(Catalog, _writer, clock);
        var loop = new AuditAnchorLoop(audit, clock, TimeZoneInfo.Utc);
        Assert.Null(await loop.TickAsync(_ct)); // pusty dziennik — nic do zakotwiczenia
        await audit.AppendAsync(new AuditRecord("S-1-5-18", "Administrator", "api", "Agent/X", null, null, "allowed", null), _ct);
        Assert.NotNull(await loop.TickAsync(_ct));
        await audit.AppendAsync(new AuditRecord("S-1-5-18", "Administrator", "api", "Agent/Y", null, null, "allowed", null), _ct);
        await _writer.FlushAsync(_ct);
        clock.Now = clock.Now.AddHours(3);
        Assert.Null(await loop.TickAsync(_ct)); // ten sam dzień
        clock.Now = new DateTimeOffset(2026, 10, 18, 7, 0, 0, TimeSpan.Zero);
        Assert.Equal(2, (await loop.TickAsync(_ct))!.Seq);
        Assert.Equal(2, audit.Verify().AnchorsChecked);
    }
}
