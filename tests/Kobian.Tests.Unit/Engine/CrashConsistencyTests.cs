using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>
/// SP-18 / NFR-26 na rzeczywistym potoku (RD-21), in-proc: awaria w każdym kolejnym zapisie (pack, indeks, snapshot) × 4 rodzaje
/// × 2 tryby zapisu. Niezależny weryfikator: każdy CZYTELNY snapshot ma wszystkie bloki w istniejących, nieuszkodzonych packach.
/// To nie zastępuje części SP-18 z prawdziwym ENOSPC (VHD) i SFTP — te pozostają „wymaga Windows / serwera”.
/// </summary>
public sealed class CrashConsistencyTests : IDisposable
{
    private readonly string _src = Path.Combine(Path.GetTempPath(), "kobian-cc-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly byte[] _mk = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _repoId = RandomNumberGenerator.GetBytes(16);

    public CrashConsistencyTests()
    {
        var rnd = new Random(34);
        Directory.CreateDirectory(Path.Combine(_src, "d1", "d2"));
        for (var i = 0; i < 12; i++)
        {
            var b = new byte[rnd.Next(1, 300_000)];
            rnd.NextBytes(b);
            File.WriteAllBytes(Path.Combine(_src, i % 3 == 0 ? "d1" : i % 3 == 1 ? Path.Combine("d1", "d2") : "", $"f{i}.bin"), b);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_src)) Directory.Delete(_src, recursive: true);
    }

    [Theory, Trait("SP", "SP-18"), Trait("NFR", "NFR-26"), Trait("D", "D3.14")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_readable_snapshot_ever_references_missing_block(bool atomicRename)
    {
        using var keys = RepoKeys.Derive(_mk, _repoId);
        var host = RandomNumberGenerator.GetBytes(16);
        // liczba zapisów w przebiegu bez awarii
        var probe = new MemoryStorage(atomicRename);
        var ok = await Run(keys, host, probe.Open());
        Assert.Equal(BackupStatus.Succeeded, ok.Status);
        var writes = probe.Objects.Count;
        Assert.True(writes >= 4);

        int runs = 0, failed = 0, dangling = 0, partialSnap = 0;
        foreach (var fault in Enum.GetValues<FaultKind>())
        {
            for (var k = 1; k <= writes; k++)
            {
                var store = new MemoryStorage(atomicRename);
                BackupResult? r = null;
                try { r = await Run(keys, host, store.Open(faultAt: k, fault: fault)); }
                catch (SimulatedProcessKillException) { }
                runs++;
                if (r is null || r.Status == BackupStatus.Failed) failed++;
                var v = await Verify(keys, store);
                dangling += v.Dangling;
                if (fault != FaultKind.SilentTruncate) partialSnap += v.Unreadable; // obcięty przez cel snapshot zostaje (Backup nie usuwa), czytnik go odrzuca
                if (r is { Status: not BackupStatus.Failed }) Assert.Equal(1, v.Readable); // sukces ⇒ snapshot czytelny
                // Kolejny przebieg po „naprawie” celu (D3.14, AC-08.4): sukces i spójność
                var again = await Run(keys, host, store.Open());
                Assert.NotEqual(BackupStatus.Failed, again.Status);
                Assert.Equal(0, (await Verify(keys, store)).Dangling);
            }
        }
        Assert.Equal(0, dangling);
        Assert.True(failed > 0);
        Assert.True(runs >= 16);
        if (atomicRename) Assert.Equal(0, partialSnap); // przy StagedRename awarie w locie nie zostawiają częściowych snapshotów
    }

    private Task<BackupResult> Run(RepoKeys keys, byte[] host, IStorageSession s) =>
        new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(_src), "job", host, PackTargetSize: 256 * 1024), _ct);

    /// <summary>Niezależny weryfikator pełnej spójności: tylko dane z celu i klucze.</summary>
    private async Task<(int Readable, int Unreadable, int Dangling)> Verify(RepoKeys keys, MemoryStorage store)
    {
        var s = store.Open();
        int readable = 0, unreadable = 0, dangling = 0;
        foreach (var path in store.Objects.Keys.Where(p => p.StartsWith("snapshots/", StringComparison.Ordinal)).ToList())
        {
            var id = RepoPaths.TryParseId(path, RepoPaths.SnapshotsDir, ".snap");
            if (id is null) continue;
            SnapshotObject snap;
            try { snap = await MetadataStore.LoadSnapshotAsync(s, keys, id, _ct); }
            catch (KobianException) { unreadable++; continue; }
            readable++;
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            dangling += await Walk(reader, store, snap.RootTreeId);
        }
        return (readable, unreadable, dangling);
    }

    private async Task<int> Walk(RepoReader reader, MemoryStorage store, byte[] treeId)
    {
        if (!Present(reader, store, treeId)) return 1;
        var missing = 0;
        foreach (var e in await reader.ReadTreeAsync(treeId, _ct))
        {
            if (e.SubtreeId is not null) missing += await Walk(reader, store, e.SubtreeId);
            foreach (var c in e.Chunks ?? []) if (!Present(reader, store, c)) missing++;
        }
        return missing;
    }

    private static bool Present(RepoReader reader, MemoryStorage store, byte[] id) =>
        reader.TryLocate(id, out var loc) && loc is not null
        && store.Objects.TryGetValue(RepoPaths.Pack(loc.PackId), out var bytes)
        && bytes.Length == loc.PackSize
        && SHA256.HashData(bytes).AsSpan().SequenceEqual(loc.PackSha256);
}
