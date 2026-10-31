using System.Text;
using Kobian.Agent.Audit;
using Kobian.Agent.Hosting;
using Kobian.Application.Storage;
using Kobian.Application.Worker;
using Kobian.Domain;
using Kobian.Engine.Restore;
using Kobian.Providers.Storage.Smb;
using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>
/// Przegląd rundy 7 (it. 105, rola 11, SEC-3.3): losowe wejścia parserów i walidatorów dodanych w rundzie 7 —
/// korzeń SMB (UncPath, BL-73), ścieżki stanu usługi z rejestru usługi (AgentPaths, BL-72), lista dokładnych ścieżek odtwarzania
/// (ExactPathSelection, PlanRestore — BL-64), kotwica audytu z wiersza poleceń (AuditAnchor.TryParse, D9.9), komunikaty Worker (WorkerJson, D2.8),
/// katalog ProgramData z instalatora (AgentInstallPlan.DataDir). Kryterium: tylko oczekiwany typ/kod wyjątku i zachowane niezmienniki. Ziarna stałe.
/// </summary>
public sealed class Round7FuzzTests
{
    private const int Rounds = 20_000;

    private const string Alphabet = "ab./\\:.\0\u0001 _-*,;[]()$?\"<>|\u00e9\ud83d\ude00\ud800\u0085..~%{}0123456789CDcd";

    private static string RandomText(Random rnd, int max)
    {
        var n = rnd.Next(max + 1);
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++)
            sb.Append(rnd.Next(10) == 0 ? (char)rnd.Next(0, 0x10000) : Alphabet[rnd.Next(Alphabet.Length)]);
        return sb.ToString();
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "D8.9"), Trait("BL", "BL-73")]
    public void Smb_root_parses_to_safe_parts_or_refuses_with_KB()
    {
        var rnd = new Random(1051);
        string[] seeds = [@"\\srv\kopie", @"\\srv\kopie\a\b", @"\\10.0.0.5\x", @"\\srv\C$", @"\\?\UNC\srv\x", @"\\srv\..\x", @"//srv/kopie"];
        for (var i = 0; i < Rounds; i++)
        {
            var s = i % 3 == 0 ? seeds[rnd.Next(seeds.Length)] + RandomText(rnd, 12) : @"\\" + RandomText(rnd, 40);
            try
            {
                var share = UncPath.Parse(s);
                Assert.False(string.IsNullOrEmpty(share.Server));
                Assert.DoesNotContain(share.Share, ['\\', '/', ':', '\0']);
                Assert.DoesNotContain("..", share.RelativePath.Split('\\'));
                Assert.DoesNotMatch("^([A-Za-z]\\$|IPC\\$|ADMIN\\$)$", share.Share);
                Assert.DoesNotContain(share.RelativePath, char.IsControl);
            }
            catch (StorageException ex)
            {
                Assert.Contains(ex.Code, new[] { KbCode.DestinationNotFound, KbCode.InvalidObjectName });
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-72")]
    public void Service_paths_from_environment_are_absolute_local_or_rejected()
    {
        var rnd = new Random(1052);
        for (var i = 0; i < Rounds; i++)
        {
            var env = new Dictionary<string, string?>
            {
                [AgentPaths.CatalogVariable] = rnd.Next(3) == 0 ? null : (rnd.Next(2) == 0 ? "/var/lib/kobian/" : "") + RandomText(rnd, 30),
                [AgentPaths.StateVariable] = rnd.Next(2) == 0 ? null : RandomText(rnd, 30),
                [AgentPaths.SecretsVariable] = rnd.Next(2) == 0 ? null : @"\\" + RandomText(rnd, 20),
            };
            var service = rnd.Next(2) == 0;
            (AgentPaths? p, string? err) r;
            try
            {
                r = AgentPaths.Resolve(k => env.GetValueOrDefault(k), service, rnd.Next(2) == 0 ? "/srv/programdata" : null);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue; // Path.GetFullPath na nietypowym wejściu — wyjątek typowy dla ścieżek, nie awaria hosta
            }
            if (r.p is null)
            {
                Assert.False(string.IsNullOrEmpty(r.err));
                continue;
            }
            foreach (var path in new[] { r.p.Catalog, r.p.StateDir, r.p.SecretsDir })
            {
                Assert.True(Path.IsPathFullyQualified(path), $"{service} [{path}] env={string.Join("|", env.Values)}");
                Assert.False(path.StartsWith(@"\\", StringComparison.Ordinal));
                Assert.DoesNotContain('\0', path);
            }
        }
    }

    [Theory, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-72")]
    [InlineData("/")]
    [InlineData("/var/lib/kobian/")]
    public void Catalog_must_be_a_file_not_a_root_or_directory(string catalog)
    {
        // Regresja z fuzzingu it. 105 (Niskie): KOBIAN_CATALOG="/" (Windows: „C:\”) dawał katalog stanu „.” — sekrety w katalogu bieżącym usługi.
        foreach (var service in new[] { false, true })
        {
            var (p, e) = AgentPaths.Resolve(k => k == AgentPaths.CatalogVariable ? catalog : null, service, "/srv/pd");
            Assert.Null(p);
            Assert.Contains(AgentPaths.CatalogVariable, e!, StringComparison.Ordinal);
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "D8.9"), Trait("BL", "BL-64")]
    public void Exact_restore_paths_normalize_to_safe_segments_or_KB_5003()
    {
        var rnd = new Random(1053);
        for (var i = 0; i < Rounds; i++)
        {
            var list = Enumerable.Range(0, rnd.Next(0, 4)).Select(_ => RandomText(rnd, 24)).ToList();
            try
            {
                var set = ExactPathSelection.Normalize(list);
                Assert.InRange(set.Count, 1, ExactPathSelection.MaxPaths);
                foreach (var p in set)
                {
                    var segs = p.Split('/');
                    Assert.All(segs, s =>
                    {
                        Assert.False(string.IsNullOrEmpty(s));
                        Assert.NotEqual("..", s);
                        Assert.NotEqual(".", s);
                        Assert.DoesNotContain('\0', s);
                        Assert.DoesNotContain('\\', s);
                    });
                }
            }
            catch (KobianException ex)
            {
                Assert.Equal(KbCode.SnapshotPathNotFound, ex.Code);
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D9.9")]
    public void Anchor_text_parses_only_to_seq_and_32_byte_hash()
    {
        var rnd = new Random(1054);
        for (var i = 0; i < Rounds; i++)
        {
            var s = i % 4 == 0
                ? $"{rnd.Next(-5, 100000)}:{Convert.ToHexString(Guid.NewGuid().ToByteArray())}{(rnd.Next(2) == 0 ? Convert.ToHexString(Guid.NewGuid().ToByteArray()) : RandomText(rnd, 4))}"
                : RandomText(rnd, 80);
            var r = AuditAnchor.TryParse(s);
            if (r is { } a)
            {
                Assert.True(a.Seq >= 1);
                Assert.Equal(32, a.Hash.Length);
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "SEC-2.5"), Trait("Plan", "D2.8")]
    public void Worker_messages_fail_only_as_protocol_violations()
    {
        var rnd = new Random(1055);
        string[] keys = ["id", "op", "volumes", "ok", "code", "devices", "snapshotSetId", "x"];
        string[] vals = ["1", "0", "-1", "2147483648", "\"ping\"", "\"vss.create\"", "[\"C:\\\\\"]", "[]", "null", "true", "{}", "{\"C:\\\\\":\"\\\\\\\\?\\\\GLOBALROOT\\\\Device\\\\X\\\\\"}", "\"KB-1406\"", "\"KB-9999\""];
        for (var i = 0; i < Rounds; i++)
        {
            string json;
            if (i % 2 == 0)
            {
                var props = Enumerable.Range(0, rnd.Next(0, 6)).Select(_ => $"\"{keys[rnd.Next(keys.Length)]}\":{vals[rnd.Next(vals.Length)]}");
                json = "{" + string.Join(",", props) + "}";
            }
            else
            {
                json = RandomText(rnd, 60);
            }
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                var req = WorkerJson.Validate(WorkerJson.Read<WorkerRequest>(bytes));
                Assert.True(WorkerOps.IsKnown(req.Op));
                Assert.True(req.Id >= 1);
            }
            catch (WorkerProtocolException)
            {
            }
            try
            {
                var resp = WorkerJson.Validate(WorkerJson.Read<WorkerResponse>(bytes), 1);
                Assert.Equal(1, resp.Id);
                if (!resp.Ok) Assert.True(KbCode.IsKnown(resp.Code));
            }
            catch (WorkerProtocolException)
            {
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D2.12")]
    public void Installer_data_dir_is_local_absolute_or_rejected()
    {
        var rnd = new Random(1056);
        for (var i = 0; i < Rounds; i++)
        {
            var s = (rnd.Next(3) == 0 ? "C:\\" : "") + RandomText(rnd, 30);
            try
            {
                var d = AgentInstallPlan.DataDir(s);
                Assert.Matches("^[A-Za-z]:[\\\\/]", d);
                Assert.EndsWith(@"\Kobian", d, StringComparison.Ordinal);
                Assert.DoesNotContain('\0', d);
                Assert.DoesNotContain('\n', d);
            }
            catch (ArgumentException)
            {
            }
        }
    }
}
