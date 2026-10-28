using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Scheduler.Timing;
using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>
/// Przegląd rundy 4 (it. 60, rola 11, SEC-3.3): losowe mutacje wejść parserów dodanych w rundzie 4 —
/// definicja zadania v1 (job_jobs.definition), nagłówek kobian.repo, plik klucza (wariant klucza odzyskiwania — bez KDF, tani)
/// i wyrażenie cron. Kryterium: jedynym wyjątkiem jest <see cref="KobianException"/> z kodem właściwym dla parsera
/// (brak wyjątków środowiska uruchomieniowego, zawieszeń i wycieku ścieżek do komunikatu). Ziarna stałe — wynik powtarzalny.
/// </summary>
public sealed class Round4FuzzTests
{
    private const int Rounds = 20_000;

    private static string Abs(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kb-fuzz", name));

    private static byte[] ValidDefinition() => Encoding.UTF8.GetBytes(
        "{\"version\":1,\"sources\":[" + Q(Abs("src")) + "],\"destination\":{\"scheme\":\"local\",\"root\":" + Q(Abs("repo")) +
        "},\"key\":{\"file\":" + Q(Abs("k.key")) + ",\"passwordFile\":" + Q(Abs("pw")) + ",\"repoHeader\":null},\"packTargetSize\":4194304}");

    private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static byte[] Mutate(Random rnd, byte[] good)
    {
        var b = (byte[])good.Clone();
        switch (rnd.Next(5))
        {
            case 0: // podmiana bajtów
                for (var k = rnd.Next(1, 4); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                return b;
            case 1: // obcięcie
                return b[..rnd.Next(b.Length)];
            case 2: // znaki strukturalne JSON
                for (var k = rnd.Next(1, 3); k > 0; k--) b[rnd.Next(b.Length)] = "{}[]\",:0-e\\"u8[rnd.Next(11)];
                return b;
            case 3: // wycięcie fragmentu
            {
                var i = rnd.Next(b.Length);
                var n = rnd.Next(1, Math.Min(16, b.Length - i) + 1);
                return [.. b[..i], .. b[(i + n)..]];
            }
            default: // duplikacja fragmentu (powtórzone klucze, zagnieżdżenie)
            {
                var i = rnd.Next(b.Length);
                var n = rnd.Next(1, Math.Min(32, b.Length - i) + 1);
                return [.. b[..(i + n)], .. b[i..]];
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-15")]
    public void Job_definition_parser_only_throws_JobDefinitionInvalid_without_paths()
    {
        var good = ValidDefinition();
        _ = JobDefinition.Parse("files", good); // punkt wyjścia jest poprawny
        var rnd = new Random(6001);
        var accepted = 0;
        for (var i = 0; i < Rounds; i++)
        {
            var b = Mutate(rnd, good);
            try
            {
                _ = JobDefinition.Parse("files", b);
                accepted++;
            }
            catch (KobianException ex)
            {
                Assert.Equal(KbCode.JobDefinitionInvalid, ex.Code);
                Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), ex.Message, StringComparison.Ordinal); // NFR-11
            }
        }
        Assert.True(accepted < Rounds, "fuzzer nie zmienił wejścia");
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-15")]
    public void Job_definition_parser_rejects_deep_nesting_and_oversize()
    {
        var deep = Encoding.UTF8.GetBytes("{\"version\":1,\"x\":" + new string('[', 100) + new string(']', 100) + "}");
        Assert.Equal(KbCode.JobDefinitionInvalid, Assert.Throws<KobianException>(() => JobDefinition.Parse("files", deep)).Code);
        var big = new byte[JobDefinition.MaxBytes + 1];
        Assert.Equal(KbCode.JobDefinitionInvalid, Assert.Throws<KobianException>(() => JobDefinition.Parse("files", big)).Code);
        var hugeNumber = Encoding.UTF8.GetBytes("{\"version\":1e400}");
        Assert.Equal(KbCode.JobDefinitionInvalid, Assert.Throws<KobianException>(() => JobDefinition.Parse("files", hugeNumber)).Code);
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "SEC-7.3"), Trait("BL", "BL-24")]
    public void Repo_header_mutations_never_verify_and_only_throw_RepoHeaderInvalid()
    {
        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(RepoKeys.KeySize), RandomNumberGenerator.GetBytes(RepoKeys.IdSize));
        var json = RepoHeader.Create(keys, DateTimeOffset.Parse("2026-10-14T07:00:00Z", CultureInfo.InvariantCulture), [RandomNumberGenerator.GetBytes(RepoKeys.IdSize)]);
        var good = Encoding.UTF8.GetBytes(json);
        var rnd = new Random(6002);
        for (var i = 0; i < Rounds; i++)
        {
            var b = Mutate(rnd, good);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(b); }
            catch (DecoderFallbackException) { continue; } // plik nie-UTF-8 odrzuca warstwa odczytu
            if (text == json) continue;
            try { _ = RepoHeader.PeekRepoId(text); }
            catch (KobianException ex) { Assert.Equal(KbCode.RepoHeaderInvalid, ex.Code); }
            // Zmieniony nagłówek może przejść tylko, gdy treść objęta MAC jest semantycznie ta sama (np. białe znaki poza body).
            try
            {
                var info = RepoHeader.Verify(text, keys);
                Assert.Equal(keys.RepoId.ToArray(), info.RepoId);
            }
            catch (KobianException ex)
            {
                Assert.True(ex.Code is KbCode.RepoHeaderInvalid or KbCode.UnsupportedFeature, ex.Code);
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "SEC-13.2"), Trait("BL", "BL-24")]
    public void Key_file_mutations_only_throw_KeyUnwrapFailed()
    {
        var mk = RandomNumberGenerator.GetBytes(RepoKeys.KeySize);
        var rk = RandomNumberGenerator.GetBytes(RepoKeys.KeySize);
        var json = KeyFiles.WrapWithRecoveryKey(mk, rk, RandomNumberGenerator.GetBytes(RepoKeys.IdSize), DateTimeOffset.UnixEpoch);
        Assert.Equal(mk, KeyFiles.UnwrapWithRecoveryKey(json, rk));
        var good = Encoding.UTF8.GetBytes(json);
        var rnd = new Random(6003);
        for (var i = 0; i < Rounds; i++)
        {
            var text = Encoding.UTF8.GetString(Mutate(rnd, good));
            if (text == json) continue;
            try
            {
                var got = KeyFiles.UnwrapWithRecoveryKey(text, rk);
                Assert.Equal(mk, got); // AEAD: przejść może wyłącznie plik o tym samym znaczeniu
            }
            catch (KobianException ex) { Assert.Equal(KbCode.KeyUnwrapFailed, ex.Code); }
            try { _ = KeyFiles.MarkRevoked(text); }
            catch (KobianException ex) { Assert.Equal(KbCode.KeyUnwrapFailed, ex.Code); }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-42")]
    public void Cron_parser_on_random_tokens_only_throws_ScheduleInvalid()
    {
        string[] atoms = ["*", "*/5", "0", "59", "60", "-1", "1-5", "5-1", "1,2,3", "*/0", "MON", "JAN", "?", "L", "#", "", " ", "\t", "99999999999", "1-", ",", "/"];
        string[] zones = ["Europe/Warsaw", "UTC", "", "Nie/Ma", "../../etc/passwd", "Europe/Warsaw\0"];
        var rnd = new Random(6004);
        for (var i = 0; i < Rounds; i++)
        {
            var n = rnd.Next(0, 8);
            var expr = string.Join(rnd.Next(4) == 0 ? "  " : " ", Enumerable.Range(0, n).Select(_ => atoms[rnd.Next(atoms.Length)]));
            var tz = zones[rnd.Next(zones.Length)];
            try { _ = CronSchedule.Parse(expr, tz); }
            catch (KobianException ex) { Assert.Equal(KbCode.ScheduleInvalid, ex.Code); }
        }
    }
}
