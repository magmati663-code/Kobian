using System.Text;
using System.Text.RegularExpressions;
using Kobian.Agent.Audit;
using Kobian.Agent.Hosting;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Engine.Restore;
using Kobian.Providers.Sources.MsSql;
using Kobian.UI.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>
/// Przegląd rundy 6 (it. 90, rola 11, SEC-3.3): losowe wejścia parserów i walidatorów dodanych w rundzie 6 —
/// ścieżka przeglądania kopii z klienta (SnapshotBrowser.SplitPath, BL-63), wiersze RESTORE FILELISTONLY z serwera SQL (PlanMoves, BL-68),
/// konfiguracja listy dozwolonych serwerów SQL (SqlServerAllowList, RD-39), plik ui-settings.json (FileUiSettingsStore, BL-66)
/// oraz weryfikacja łańcucha audytu po ręcznej zmianie wiersza (AuditLogService.Verify, RD-42).
/// Kryterium: tylko oczekiwany typ/kod wyjątku, zachowane niezmienniki wyniku, brak zawieszeń. Ziarna stałe.
/// </summary>
public sealed class Round6FuzzTests
{
    private const int Rounds = 20_000;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private const string Alphabet = "ab./\\:.\0\u0001 _-*,;[]()\u00e9\ud83d\ude00\ud800\udc00\u0085..~$%";

    private static string RandomText(Random rnd, int max)
    {
        var n = rnd.Next(max + 1);
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++)
            sb.Append(rnd.Next(8) == 0 ? (char)rnd.Next(0, 0x10000) : Alphabet[rnd.Next(Alphabet.Length)]);
        return sb.ToString();
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "D8.9"), Trait("BL", "BL-63")]
    public void Browse_path_from_client_yields_only_valid_segments_or_KB_5003()
    {
        var rnd = new Random(9001);
        var accepted = 0;
        for (var i = 0; i < Rounds; i++)
        {
            var path = i % 50 == 0 ? new string('a', SnapshotBrowser.MaxPathChars + rnd.Next(2)) : RandomText(rnd, 40);
            try
            {
                var parts = SnapshotBrowser.SplitPath(path);
                accepted++;
                Assert.InRange(parts.Count, 0, SnapshotBrowser.MaxDepth);
                foreach (var p in parts)
                {
                    Assert.False(p is "" or "." or "..", "segment nawigacyjny przepuszczony");
                    Assert.DoesNotContain('/', p);
                    Assert.DoesNotContain('\\', p);
                    Assert.DoesNotContain('\0', p);
                }
            }
            catch (KobianException ex)
            {
                Assert.Equal(KbCode.SnapshotPathNotFound, ex.Code);
                if (path.Length >= 4) Assert.DoesNotContain(path, ex.Message, StringComparison.Ordinal); // komunikat bez ścieżki z klienta
            }
        }
        Assert.InRange(accepted, 1, Rounds - 1); // obie gałęzie przećwiczone
    }

    private static object? RandomCell(Random rnd, string[] good) => rnd.Next(10) switch
    {
        0 => null,
        1 => rnd.Next(),
        2 => RandomText(rnd, 140),
        _ => good[rnd.Next(good.Length)],
    };

    private static readonly Regex MoveFile = new(@"^[A-Za-z0-9_-]+(\.mdf|_[0-9]+\.ndf|_log\.ldf|_log_[0-9]+\.ldf)$", RegexOptions.CultureInvariant);

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "SEC-5.1"), Trait("BL", "BL-68")]
    public void Filelistonly_rows_from_server_plan_moves_only_inside_target_directories()
    {
        const string dataDir = @"D:\SQLData\", logDir = @"E:\SQLLog";
        var rnd = new Random(9002);
        string[] names = ["Firma_Data", "Firma_Log", "Firma_2", @"..\..\Windows\x", "a:b", "C:\\x.mdf", "dane\u0007", new string('n', 129)];
        string[] types = ["D", "L", "D ", " L", "S", "F", "X", "", "d"];
        string[] targets = ["Firma_restore_20261014", "Firma 1", @"..\evil", "a:b", "Ząb", "x;DROP TABLE t--", "\u202ertl"];
        var accepted = 0;
        for (var i = 0; i < Rounds / 4; i++)
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            for (var k = rnd.Next(0, 6); k > 0; k--)
                rows.Add(new Dictionary<string, object?> { ["LogicalName"] = RandomCell(rnd, names), ["Type"] = RandomCell(rnd, types), ["PhysicalName"] = RandomText(rnd, 30) });
            var target = targets[rnd.Next(targets.Length)];
            try
            {
                var moves = MsSqlRestoreService.PlanMoves(rows, target, dataDir, logDir);
                accepted++;
                Assert.Equal(rows.Count, moves.Count);
                for (var k = 0; k < moves.Count; k++)
                {
                    var m = moves[k];
                    Assert.Equal(rows[k]["LogicalName"], m.LogicalName); // nazwa logiczna idzie parametrem, bez zmian
                    var dir = m.Type == 'D' ? @"D:\SQLData\" : @"E:\SQLLog\";
                    Assert.StartsWith(dir, m.PhysicalPath, StringComparison.Ordinal);
                    Assert.Matches(MoveFile, m.PhysicalPath[dir.Length..]); // nazwa pliku wyłącznie z bezpiecznych znaków — nic ze ścieżek z kopii
                }
                Assert.Equal(moves.Count, moves.Select(m => m.PhysicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Contains(moves, m => m.Type == 'D');
                Assert.Contains(moves, m => m.Type == 'L');
            }
            catch (KobianException ex)
            {
                Assert.Contains(ex.Code, new[] { KbCode.SqlVerifyFailed, KbCode.SqlRestoreUnsupportedFile });
            }
        }
        Assert.True(accepted > 0);
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("RD", "RD-39")]
    public void Sql_allow_list_config_and_server_names_never_escape_the_list()
    {
        var rnd = new Random(9003);
        string[] hosts = ["srv01", "SRV01", "localhost", ".", "(local)", "127.0.0.1", "[::1]", "srv01.firma.local", "10.0.0.5", "evil", "srv01\\SQLEXPRESS", "srv01,1433", "srv01:1433", "*"];
        string Pick() => rnd.Next(4) == 0 ? RandomText(rnd, 20) : hosts[rnd.Next(hosts.Length)];
        string[] localAliases = ["localhost", "LOCALHOST", ".", "127.0.0.1", "[::1]"]; // „(local)” odrzuca już walidacja adresu (ValidateServer)
        var parsed = 0;
        for (var i = 0; i < Rounds / 4; i++)
        {
            var cfg = string.Join(rnd.Next(2) == 0 ? ";" : ",", Enumerable.Range(0, rnd.Next(0, 4)).Select(_ => Pick()));
            SqlServerAllowList list;
            try
            {
                list = SqlServerAllowList.Parse(cfg);
                parsed++;
            }
            catch (KobianException ex)
            {
                Assert.Equal(KbCode.JobDefinitionInvalid, ex.Code); // niepoprawny wpis = błąd przy starcie, nie ciche pominięcie
                continue;
            }
            var server = Pick();
            var allowed = list.IsAllowed(server); // nigdy nie rzuca
            if (allowed && !list.AllowsAny)
            {
                var entries = cfg.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (entries.Length == 0) Assert.True(IsLocal(server), "pusta konfiguracja przepuściła serwer zdalny");
                else Assert.Contains(entries, e => SameHost(e, server) || IsLocal(e) && IsLocal(server));
            }
        }
        Assert.True(parsed > 0);
        // domyślnie (brak konfiguracji) wyłącznie ten komputer
        foreach (var h in localAliases) Assert.True(SqlServerAllowList.LocalOnly.IsAllowed(h), h);
        for (var i = 0; i < 2000; i++)
        {
            var s = RandomText(rnd, 16);
            if (SqlServerAllowList.LocalOnly.IsAllowed(s)) Assert.True(IsLocal(s), "LocalOnly przepuścił: " + s.Length);
        }

        static string Host(string s)
        {
            s = s.Trim();
            if (s.StartsWith('[')) return s.IndexOf(']', StringComparison.Ordinal) is var e and > 0 ? s[..(e + 1)] : s;
            return s.Split(',', ':')[0].Split('\\')[0];
        }
        static bool SameHost(string entry, string server) => string.Equals(Host(entry), Host(server), StringComparison.OrdinalIgnoreCase);
        static bool IsLocal(string s) => Host(s) is "." or "127.0.0.1" or "[::1]" or "::1" || string.Equals(Host(s), "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(Host(s), "(local)", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-66")]
    public void Ui_settings_file_of_any_content_loads_as_allowed_values_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kb-fuzz6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ui-settings.json");
            var store = new FileUiSettingsStore(path);
            store.Save(new UiSettings("en", "dark"));
            var good = File.ReadAllBytes(path);
            Assert.Equal(new UiSettings("en", "dark"), store.Load());
            var rnd = new Random(9004);
            for (var i = 0; i < Rounds / 4; i++)
            {
                byte[] b = (i % 100) switch
                {
                    0 => Encoding.UTF8.GetBytes(new string('[', 5000)),
                    1 => Encoding.UTF8.GetBytes("{\"language\":\"" + new string('x', FileUiSettingsStore.MaxBytes) + "\"}"),
                    2 => Encoding.UTF8.GetBytes("{\"language\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}}}"),
                    _ => MutateBytes(rnd, good),
                };
                File.WriteAllBytes(path, b);
                var s = store.Load(); // nigdy nie rzuca
                Assert.Contains(s.Language, UiSettings.Languages);
                Assert.Contains(s.Theme, UiSettings.Themes);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static byte[] MutateBytes(Random rnd, byte[] good)
    {
        var b = (byte[])good.Clone();
        switch (rnd.Next(4))
        {
            case 0:
                for (var k = rnd.Next(1, 4); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                return b;
            case 1:
                return b[..rnd.Next(b.Length)];
            case 2:
                for (var k = rnd.Next(1, 3); k > 0; k--) b[rnd.Next(b.Length)] = (byte)"{}[]\",:0-elnt"[rnd.Next(13)];
                return b;
            default:
            {
                var i = rnd.Next(b.Length);
                var n = rnd.Next(1, Math.Min(24, b.Length - i) + 1);
                return [.. b[..(i + n)], .. b[i..]];
            }
        }
    }

    [Fact, Trait("SEC", "SEC-19.1"), Trait("RD", "RD-42"), Trait("Category", "Integration")]
    public async Task Audit_verify_reports_the_tampered_row_and_never_throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kb-fuzz6a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var catalog = Path.Combine(dir, "catalog.db");
        try
        {
            CatalogBootstrap.Bootstrap(catalog, TimeProvider.System);
            await using (var writer = new CatalogWriter(catalog))
            {
                var audit = new AuditLogService(catalog, writer, TimeProvider.System);
                for (var i = 0; i < 8; i++)
                    await audit.AppendAsync(new AuditRecord(AgentCallAudit.LocalActor, "Operator", "api", "Agent/Op" + i, i % 2 == 0 ? "cel" + i : null,
                        i % 3 == 0 ? null : new Dictionary<string, string> { ["k"] = "v" + i, ["n"] = "1" }, i % 4 == 0 ? "denied" : "allowed", i % 4 == 0 ? "KB-7001" : null), _ct);
                Assert.Null(audit.Verify().BrokenSeq);

                using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = catalog, Pooling = false }.ToString());
                c.Open();
                Exec(c, "DROP TRIGGER audit_no_update;");
                string[] textCols = ["at_utc", "actor_sid", "role", "operation", "target", "params", "kb_code", "outcome", "channel"];
                var rnd = new Random(9005);
                var detected = 0;
                for (var round = 0; round < 300; round++)
                {
                    var seq = rnd.Next(1, 9);
                    var col = rnd.Next(11) switch { 9 => "prev_hash", 10 => "hash", var x => textCols[x] };
                    var original = Read(c, col, seq);
                    object? tampered;
                    if (col is "prev_hash" or "hash")
                    {
                        var h = (byte[])original!;
                        var t = (byte[])h.Clone();
                        t[rnd.Next(32)] ^= (byte)(1 << rnd.Next(8));
                        tampered = t;
                    }
                    else if (col == "params")
                    {
                        var text = original is byte[] p ? Encoding.UTF8.GetString(p) : "{\"k\":\"v\"}";
                        tampered = MutateBytes(rnd, Encoding.UTF8.GetBytes(text));
                    }
                    else if (col == "outcome") tampered = original as string == "denied" ? "failed" : "denied";
                    else if (col == "channel") tampered = "cli";
                    else if (col == "kb_code") tampered = original is null ? "KB-0001" : null;
                    else tampered = (original as string ?? "") + RandomText(rnd, 4).Replace("\0", "x", StringComparison.Ordinal) + "!";

                    if (!TryUpdate(c, col, seq, tampered)) continue; // ograniczenie CHECK/STRICT odrzuciło zmianę — nie ma czego wykrywać
                    var v = audit.Verify(); // nigdy nie rzuca (wynik RPC VerifyAudit zamiast Unknown)
                    Assert.Equal(8, v.Entries);
                    if (col == "params")
                        Assert.True(v.BrokenSeq is null || v.BrokenSeq == seq || v.BrokenSeq == 1, $"params: {v.BrokenSeq} dla {seq}"); // równoważny JSON po JCS = brak zmiany; nieczytelny = cały łańcuch
                    else
                        Assert.Equal(seq, v.BrokenSeq);
                    if (v.BrokenSeq is not null) detected++;
                    Assert.True(TryUpdate(c, col, seq, original));
                    Assert.Null(audit.Verify().BrokenSeq);
                }
                Assert.True(detected > 100, "wykryto " + detected);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
        }

        static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        static object? Read(SqliteConnection c, string col, int seq)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {col} FROM audit_log WHERE seq=$s;";
            cmd.Parameters.AddWithValue("$s", seq);
            var r = cmd.ExecuteScalar();
            return r is DBNull ? null : r;
        }

        static bool TryUpdate(SqliteConnection c, string col, int seq, object? value)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE audit_log SET {col}=$v WHERE seq=$s;";
            cmd.Parameters.AddWithValue("$v", value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", seq);
            try
            {
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException)
            {
                return false;
            }
        }
    }
}
