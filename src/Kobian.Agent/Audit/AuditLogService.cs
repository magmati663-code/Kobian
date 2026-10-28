using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Kobian.Catalog.Sqlite;
using Kobian.Notifications.Audit;
using KbCodes = Kobian.Domain.KbCode;

namespace Kobian.Agent.Audit;

/// <summary>Zdarzenie audytu przed nadaniem numeru: kto (SID/rola/kanał), co (operacja, cel), parametry zamaskowane (SEC-11.5), wynik, kod KB.</summary>
public sealed record AuditRecord(string ActorSid, string Role, string Channel, string Operation, string? Target, IReadOnlyDictionary<string, string>? Params, string Outcome, string? KbCode);

/// <summary>
/// Wynik weryfikacji łańcucha: liczba wpisów, pierwszy niezgodny seq albo null, ostatni hash, kod (KB-6501 łańcuch, KB-6502 schemat zmieniony — RD-51,
/// KB-6503 kotwica niezgodna/wskazuje wpis, którego nie ma — D9.9), liczba sprawdzonych kotwic i ostatnia kotwica z katalogu.
/// </summary>
public sealed record AuditVerification(long Entries, long? BrokenSeq, byte[]? LastHash, string? Code = null, int AnchorsChecked = 0, AuditAnchor? LastAnchor = null)
{
    public bool Ok => Code is null;
}

/// <summary>Kotwica łańcucha (D9.9): seq i hash wpisu w chwili <see cref="AtUtc"/>.</summary>
public sealed record AuditAnchor(long Seq, byte[] Hash, string AtUtc)
{
    /// <summary>Tekst do raportu/e-maila i do ręcznego zapisania: „seq:sha256hex” (wejście `kobian audit verify --anchor`).</summary>
    public string Text => Seq.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexStringLower(Hash);

    /// <summary>Parsowanie „seq:hex64”; null przy złym formacie.</summary>
    public static (long Seq, byte[] Hash)? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seq) || seq < 1) return null;
        if (parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit)) return null;
        return (seq, Convert.FromHexString(parts[1]));
    }
}

/// <summary>Port zapisu audytu dla warstwy API (interceptor). Brak audytu = <see cref="NullAuditSink"/> (testy, host tylko do odczytu).</summary>
public interface IAuditSink
{
    Task AppendAsync(AuditRecord record, CancellationToken ct);
}

public sealed class NullAuditSink : IAuditSink
{
    public static NullAuditSink Instance { get; } = new();

    public Task AppendAsync(AuditRecord record, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Łańcuch audytu w katalogu (RD-42, D9.9): <c>audit_log</c> append-only; seq i hash nadawane w JEDNEJ transakcji pisarza katalogu
/// (odczyt ostatniego hash → <see cref="AuditChain.Append"/> → INSERT), więc równoległe wywołania nie rozwidlają łańcucha.
/// Parametry: tylko jawnie przekazane pary klucz–wartość, wartości obcinane do 256 znaków (SEC-11.5: bez sekretów — wywołujący nie przekazuje haseł).
/// </summary>
public sealed class AuditLogService(string catalogPath, CatalogWriter writer, TimeProvider time) : IAuditSink
{
    public const int MaxParamLength = 256;

    public Task AppendAsync(AuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var at = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var paramsJson = ParamsJson(record.Params);
        return writer.ExecuteControlAsync((c, tx) =>
        {
            long seq = 1;
            var prev = AuditChain.Genesis;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT seq, hash FROM audit_log ORDER BY seq DESC LIMIT 1;";
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    seq = r.GetInt64(0) + 1;
                    prev = (byte[])r[1];
                }
            }
            var entry = new AuditEntry(seq, at, record.ActorSid, record.Role, record.Channel, record.Operation, record.Target, paramsJson, record.Outcome, record.KbCode);
            var chained = AuditChain.Append(prev, entry);
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO audit_log(seq,at_utc,actor_sid,role,channel,operation,target,params,outcome,kb_code,prev_hash,hash)
                VALUES ($s,$a,$sid,$r,$ch,$op,$t,$p,$o,$k,$ph,$h);
                """;
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.Parameters.AddWithValue("$a", at);
            cmd.Parameters.AddWithValue("$sid", record.ActorSid);
            cmd.Parameters.AddWithValue("$r", record.Role);
            cmd.Parameters.AddWithValue("$ch", record.Channel);
            cmd.Parameters.AddWithValue("$op", record.Operation);
            cmd.Parameters.AddWithValue("$t", (object?)record.Target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", paramsJson is null ? DBNull.Value : System.Text.Encoding.UTF8.GetBytes(paramsJson));
            cmd.Parameters.AddWithValue("$o", record.Outcome);
            cmd.Parameters.AddWithValue("$k", (object?)record.KbCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ph", chained.PrevHash);
            cmd.Parameters.AddWithValue("$h", chained.Hash);
            cmd.ExecuteNonQuery();
            return 0;
        }, ct);
    }

    /// <summary>Tabele audytu objęte kontrolą schematu (RD-51).</summary>
    private static readonly string[] AuditTables = ["audit_log", "audit_anchors"];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ExpectedSchema =
        new(() => CatalogBootstrap.MigrationSchema("core", 3));

    /// <summary>
    /// Przeliczenie całego łańcucha z katalogu (SEC-19.1) + kontrola schematu (RD-51: odbudowana tabela bez CHECK/wyzwalaczy append-only, dodatkowy wyzwalacz)
    /// + zgodność z kotwicami z katalogu i opcjonalną kotwicą zewnętrzną (D9.9: z raportu/e-maila — niezależna od katalogu).
    /// Kolejność kodów: schemat (KB-6502) → łańcuch (KB-6501) → kotwice (KB-6503).
    /// </summary>
    public AuditVerification Verify((long Seq, byte[] Hash)? externalAnchor = null)
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        var schemaOk = SchemaMatches(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq,at_utc,actor_sid,role,channel,operation,target,params,outcome,kb_code,prev_hash,hash FROM audit_log ORDER BY seq;";
        using var r = cmd.ExecuteReader();
        var chain = new List<ChainedAuditEntry>();
        while (r.Read())
        {
            string? p = r.IsDBNull(7) ? null : System.Text.Encoding.UTF8.GetString((byte[])r[7]);
            var e = new AuditEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), p, r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));
            chain.Add(new ChainedAuditEntry(e, (byte[])r[10], (byte[])r[11]));
        }
        long? broken;
        try
        {
            broken = AuditChain.Verify(chain);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            broken = chain.Count == 0 ? 1 : chain[0].Entry.Seq; // nieczytelne params/kanał — łańcuch niewiarygodny od początku
        }
        var anchors = ReadAnchors(c);
        if (externalAnchor is { } ext) anchors.Add(new AuditAnchor(ext.Seq, ext.Hash, ""));
        long? anchorBroken = null;
        var bySeq = chain.ToDictionary(x => x.Entry.Seq, x => x.Hash);
        foreach (var a in anchors)
        {
            // Kotwica wskazuje wpis, którego nie ma (dziennik skrócony) albo hash się nie zgadza (historia przepisana z przeliczeniem łańcucha).
            if (!bySeq.TryGetValue(a.Seq, out var h) || a.Hash.Length != 32 || !CryptographicOperations.FixedTimeEquals(h, a.Hash))
            {
                anchorBroken = anchorBroken is null ? a.Seq : Math.Min(anchorBroken.Value, a.Seq);
            }
        }
        var code = !schemaOk ? KbCodes.AuditSchemaChanged
            : broken is not null ? KbCodes.AuditChainBroken
            : anchorBroken is not null ? KbCodes.AuditAnchorMismatch
            : null;
        var catalogAnchors = anchors.Where(a => a.AtUtc.Length > 0).ToList();
        return new AuditVerification(chain.Count, broken ?? anchorBroken, chain.Count == 0 ? null : chain[^1].Hash, code, anchors.Count,
            catalogAnchors.Count == 0 ? null : catalogAnchors.MaxBy(a => a.Seq));
    }

    private static bool SchemaMatches(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        var actual = CatalogBootstrap.ReadSchema(c, AuditTables);
        var expected = ExpectedSchema.Value;
        return actual.Count == expected.Count && expected.All(kv => actual.TryGetValue(kv.Key, out var sql) && string.Equals(sql, kv.Value, StringComparison.Ordinal));
    }

    private static List<AuditAnchor> ReadAnchors(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        var list = new List<AuditAnchor>();
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT anchor_seq, hash, at_utc FROM audit_anchors ORDER BY anchor_seq;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new AuditAnchor(r.GetInt64(0), (byte[])r[1], r.GetString(2)));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // tabela usunięta/odbudowana — zgłasza to kontrola schematu (KB-6502)
        }
        catch (InvalidCastException)
        {
        }
        return list;
    }

    /// <summary>
    /// Kotwica dzienna (D9.9): ostatni wpis (seq, hash) → audit_anchors w transakcji pisarza; idempotentna (ten sam seq = istniejąca kotwica).
    /// Pusty dziennik → null. Kotwice zewnętrzne (raport e-mail, `journal/` w repo — format v1.1, webhook) = później (Qb49).
    /// </summary>
    public Task<AuditAnchor?> CreateAnchorAsync(CancellationToken ct)
    {
        var at = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return writer.ExecuteControlAsync<AuditAnchor?>((c, tx) =>
        {
            long seq;
            byte[] hash;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT seq, hash FROM audit_log ORDER BY seq DESC LIMIT 1;";
                using var r = q.ExecuteReader();
                if (!r.Read()) return null;
                (seq, hash) = (r.GetInt64(0), (byte[])r[1]);
            }
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT hash, at_utc FROM audit_anchors WHERE anchor_seq = $s;";
                q.Parameters.AddWithValue("$s", seq);
                using var r = q.ExecuteReader();
                if (r.Read()) return new AuditAnchor(seq, (byte[])r[0], r.GetString(1));
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO audit_anchors(anchor_seq, at_utc, hash) VALUES ($s, $a, $h);";
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.Parameters.AddWithValue("$a", at);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.ExecuteNonQuery();
            return new AuditAnchor(seq, hash, at);
        }, ct);
    }

    internal static string? ParamsJson(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return null;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
                w.WriteString(k, v.Length > MaxParamLength ? v[..MaxParamLength] : v);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Czas ostatniej kotwicy w katalogu (UTC) albo null.</summary>
    public DateTimeOffset? LastAnchorUtc()
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(at_utc) FROM audit_anchors;";
        return cmd.ExecuteScalar() is string s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) : null;
    }
}

/// <summary>
/// Termin kotwicy dziennej (D9.9: codziennie 06:55 czasu lokalnego hosta, przed raportem 07:00). Czysta funkcja — pętla w <see cref="AuditAnchorLoop"/>.
/// Należna, gdy od ostatniego terminu (dziś 06:55 albo wczoraj, jeśli jeszcze przed 06:55) nie powstała kotwica. Zaległa po wyłączeniu = jedna, nie wiele.
/// </summary>
public static class AuditAnchorSchedule
{
    public static readonly TimeSpan LocalTime = new(6, 55, 0);

    public static DateTimeOffset LastSlotUtc(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var day = local.TimeOfDay >= LocalTime ? local.Date : local.Date.AddDays(-1);
        var slotLocal = day + LocalTime;
        if (zone.IsInvalidTime(slotLocal)) slotLocal = slotLocal.AddHours(1); // przejście na czas letni
        return new DateTimeOffset(slotLocal, zone.GetUtcOffset(slotLocal)).ToUniversalTime();
    }

    public static bool IsDue(DateTimeOffset? lastAnchorUtc, DateTimeOffset nowUtc, TimeZoneInfo zone) =>
        lastAnchorUtc is null || lastAnchorUtc < LastSlotUtc(nowUtc, zone);
}

/// <summary>Pętla kotwic w Agencie (jak CatalogScheduler: RunAsync do zatrzymania hosta): co minutę sprawdza termin; błąd zapisu nie zatrzymuje usługi.</summary>
public sealed class AuditAnchorLoop(AuditLogService audit, TimeProvider time, TimeZoneInfo zone)
{
    /// <summary>Jeden krok (testy): kotwica, gdy należna.</summary>
    public async Task<AuditAnchor?> TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!AuditAnchorSchedule.IsDue(audit.LastAnchorUtc(), now, zone)) return null;
        return await audit.CreateAnchorAsync(ct).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        try
        {
            do
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
                {
                    // katalog chwilowo niedostępny — następna próba za minutę
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
