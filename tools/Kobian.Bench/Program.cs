using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Kobian.Crypto;
using Kobian.Engine.Compression;
using Kobian.Format.Pack;

// RD-22 (it. 87, it. 102): `--dedup [N]` — pamięć i szybkość struktur dedup dla N bloków bazowych + N/10 nowych (wzorzec potoku: najpierw baza, potem odwołania).
// Warianty: słownik flag (warstwa 1 z it. 87: Dictionary<ChunkKey,byte>), tablica posortowana (it. 102), tablica + filtr Blooma (it. 102).
if (args.Length > 0 && args[0] == "--dedup")
{
    var n = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1_000_000;
    var ids = new byte[n][];
    var fresh = new byte[n / 10][];
    var rng = new Random(87);
    for (var i = 0; i < n; i++) { ids[i] = new byte[32]; rng.NextBytes(ids[i]); }
    for (var i = 0; i < fresh.Length; i++) { fresh[i] = new byte[32]; rng.NextBytes(fresh[i]); }
    long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
    Console.WriteLine($"Środowisko: {RuntimeInformation.OSDescription}; .NET {Environment.Version}; rdzeni {Environment.ProcessorCount}");

    var m0 = Mem();
    var dict = new Dictionary<Kobian.Engine.Backup.ChunkKey, byte>();
    foreach (var id in ids) dict[new Kobian.Engine.Backup.ChunkKey(id)] = 3;
    var dictBytes = Mem() - m0;
    var sw = Stopwatch.StartNew();
    var h0 = 0;
    foreach (var id in ids) if (dict.ContainsKey(new Kobian.Engine.Backup.ChunkKey(id))) h0++;
    var dictHit = sw.Elapsed.TotalSeconds;
    sw.Restart();
    foreach (var id in fresh) if (dict.ContainsKey(new Kobian.Engine.Backup.ChunkKey(id))) h0++;
    var dictMiss = sw.Elapsed.TotalSeconds;
    GC.KeepAlive(dict);
    dict = null!;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"słownik flag (warstwa 1): {dictBytes / 1048576.0:F1} MiB ({(double)dictBytes / n:F0} B/blok); trafienia {n / dictHit / 1e6:F1} mln/s; nowe {fresh.Length / dictMiss / 1e6:F1} mln/s"));

    foreach (var bloom in new[] { false, true })
    {
        var mA = Mem();
        var t = new Kobian.Engine.Backup.DedupTable(n, bloom ? 0 : int.MaxValue);
        foreach (var id in ids) t.AddConfirmed(id);
        sw.Restart();
        t.Reference(ids[0]); // zamrożenie (sortowanie)
        var freeze = sw.Elapsed.TotalSeconds;
        var mB = Mem();
        sw.Restart();
        var hits = 0;
        foreach (var id in ids) if (!t.Reference(id)) hits++;
        var hit = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var news = 0;
        foreach (var id in fresh) if (t.Reference(id)) news++;
        var miss = sw.Elapsed.TotalSeconds;
        t.ConfirmPending();
        var ok = t.AllReferencedConfirmed();
        GC.KeepAlive(t);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"DedupTable {(bloom ? "tablica + Bloom" : "tablica posortowana")}: {(mB - mA) / 1048576.0:F1} MiB ({(double)(mB - mA) / n:F0} B/blok, przed odwołaniami nowych); zamrożenie {freeze:F2} s; trafienia {n / hit / 1e6:F1} mln/s ({hits:N0}); nowe {fresh.Length / miss / 1e6:F1} mln/s ({news:N0}); I-3 {ok}"));
    }

    // FP filtra Blooma zmierzony (nie z wzoru): odsetek nowych bloków, dla których filtr mówi „może być”.
    var bf = new Kobian.Engine.Backup.ChunkBloomFilter(n);
    foreach (var id in ids) bf.Add(new Kobian.Engine.Backup.ChunkKey(id));
    var fp = fresh.Count(id => bf.MayContain(new Kobian.Engine.Backup.ChunkKey(id)));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Bloom {Kobian.Engine.Backup.ChunkBloomFilter.BitsPerEntry} bit/wpis: {bf.SizeBytes / 1048576.0:F1} MiB; FP zmierzony {100.0 * fp / fresh.Length:F2}% ({fp:N0}/{fresh.Length:N0})"));
    return 0;
}

// RD-74 (it. 117): `--secure-delete N` — koszt PRAGMA secure_delete: N wierszy (ok. 300 B) wstawionych i usuniętych partiami po 200, synchronous=FULL, WAL; mediana z 5.
if (args.Length > 0 && args[0] == "--secure-delete")
{
    var n = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 20_000;
    Console.WriteLine($"Środowisko: {RuntimeInformation.OSDescription}; .NET {Environment.Version}; rdzeni {Environment.ProcessorCount}");
    var dir = Path.Combine(Path.GetTempPath(), "kobian-bench-sd-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        foreach (var on in new[] { false, true })
        {
            var times = new List<double>();
            for (var rep = 0; rep < 6; rep++)
            {
                var path = Path.Combine(dir, $"c{on}{rep}.db");
                Kobian.Catalog.Sqlite.CatalogBootstrap.Bootstrap(path, TimeProvider.System);
                using var c = Kobian.Catalog.Sqlite.CatalogBootstrap.Open(path);
                using (var p0 = c.CreateCommand()) { p0.CommandText = $"PRAGMA secure_delete = {(on ? "ON" : "OFF")};"; p0.ExecuteNonQuery(); }
                var payload = new byte[300];
                for (var i = 0; i < n; i += 200)
                {
                    using var tx = c.BeginTransaction();
                    using var ins = c.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO core_outbox(channel,payload,attempts,next_attempt_at,state) VALUES ('webhook',$p,1,'2026-01-01T00:00:00.000Z','sent');";
                    ins.Parameters.AddWithValue("$p", payload);
                    for (var j = 0; j < 200; j++) ins.ExecuteNonQuery();
                    tx.Commit();
                }
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < n; i += 200)
                {
                    using var del = c.CreateCommand();
                    del.CommandText = "DELETE FROM core_outbox WHERE id IN (SELECT id FROM core_outbox ORDER BY id LIMIT 200);";
                    del.ExecuteNonQuery();
                }
                using (var cp = c.CreateCommand()) { cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; cp.ExecuteNonQuery(); }
                if (rep > 0) times.Add(sw.Elapsed.TotalSeconds); // pierwszy = rozgrzewka
            }
            times.Sort();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"secure_delete {(on ? "ON " : "OFF")}: usunięcie {n:N0} wierszy w partiach po 200 + checkpoint — mediana {times[2]:F3} s"));
        }
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }
    return 0;
}

// BL-83 (it. 113, RD-64): `--baseline N list|stream` — pamięć bazy przyrostu: N bloków w 64 indeksach (po ~1 000 bloków na pack).
// list = obiekty IndexObject trzymane przez całe uruchomienie (stan przed it. 113); stream = BaselineSet (indeks po indeksie, obiekty porzucane).
// Uruchamiać każdy wariant w OSOBNYM procesie (szczyt zestawu roboczego procesu jest wtedy porównywalny).
if (args.Length > 0 && args[0] == "--baseline")
{
    var n = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1_000_000;
    var stream = args.Length > 2 && args[2] == "stream";
    const int Indexes = 64, PerPack = 1000;
    long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
    Kobian.Format.Metadata.IndexObject Make(int k)
    {
        var rng = new Random(113 + k);
        var count = n / Indexes + (k < n % Indexes ? 1 : 0);
        var packs = new List<Kobian.Format.Metadata.IndexPack>();
        for (var done = 0; done < count; done += PerPack)
        {
            var entries = new List<Kobian.Format.Pack.PackEntry>(Math.Min(PerPack, count - done));
            for (var j = 0; j < Math.Min(PerPack, count - done); j++)
            {
                var id = new byte[32];
                rng.NextBytes(id);
                entries.Add(new Kobian.Format.Pack.PackEntry(id, j * 65_536L, 65_000, Kobian.Format.Pack.BlobType.Data, 65_536));
            }
            var pid = new byte[16];
            rng.NextBytes(pid);
            packs.Add(new Kobian.Format.Metadata.IndexPack(pid, new byte[32], 64L * 1024 * 1024, entries));
        }
        return new Kobian.Format.Metadata.IndexObject(packs, [], new byte[16], new byte[16]);
    }
    Console.WriteLine($"Środowisko: {RuntimeInformation.OSDescription}; .NET {Environment.Version}; rdzeni {Environment.ProcessorCount}");
    var m0 = Mem();
    var sw = Stopwatch.StartNew();
    object keep;
    if (stream)
    {
        var ids = Enumerable.Range(0, Indexes).Select(k => BitConverter.GetBytes(k)).ToList();
        keep = await Kobian.Engine.Backup.BaselineSet.LoadAsync(ids, (id, _) => Task.FromResult((Make(BitConverter.ToInt32(id)), new byte[32])), CancellationToken.None);
    }
    else
    {
        var list = new List<Kobian.Engine.Backup.BaselineIndex>();
        for (var k = 0; k < Indexes; k++) list.Add(new Kobian.Engine.Backup.BaselineIndex(BitConverter.GetBytes(k), new byte[32], Make(k)));
        var t = new Kobian.Engine.Backup.DedupTable(list.Sum(b => b.Index.Packs.Sum(pk => pk.Entries.Count)));
        foreach (var b in list) foreach (var pk in b.Index.Packs) foreach (var e in pk.Entries) t.AddConfirmed(e.ChunkId);
        keep = (list, t);
    }
    var load = sw.Elapsed.TotalSeconds;
    var retained = Mem() - m0;
    GC.KeepAlive(keep);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"baza {(stream ? "strumieniowa (BaselineSet)" : "lista BaselineIndex")}: N={n:N0}; trzymane w trakcie uruchomienia {retained / 1048576.0:F1} MiB ({(double)retained / n:F0} B/blok); ładowanie {load:F2} s; szczyt procesu {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB"));
    return 0;
}

// it. 88 (rola 13): `--e2e [MiB] [sesje] [pack MiB] [budżet MiB]` — pełny cykl na dostawcy Local: korpus syntetyczny → kopia pełna → zmiana ~5% plików → przyrost → restore (pomiar informacyjny).
if (args.Length > 0 && args[0] == "--e2e")
{
    var targetMb = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 200;
    var sessions = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1; // it. 119 (RD-73 cz. 2): liczba sesji wysyłki packów
    long? packMb = args.Length > 3 ? long.Parse(args[3], CultureInfo.InvariantCulture) : null; // it. 120 (RD-75): rozmiar packa (MiB), domyślnie z dostawcy (16 MiB)
    long? budgetMb = args.Length > 4 ? long.Parse(args[4], CultureInfo.InvariantCulture) : null; // it. 120 (RD-75): budżet buforów packów (MiB)
    return await E2E.RunAsync(targetMb, sessions, packMb, budgetMb);
}

// Dane: 64 bloki × 4 MiB (256 MiB) mieszane jak korpus D10.9: segmenty 4 KiB pseudolosowe, co ~4. wyzerowany, część tekstowa.
const int BlockSize = 4 * 1024 * 1024;
var blocks = Enumerable.Range(0, 64).Select(i => MakeBlock(i)).ToArray();
var totalMb = blocks.Length * (double)BlockSize / (1024 * 1024);
var mk = RandomNumberGenerator.GetBytes(32);
using var keys = RepoKeys.Derive(mk, RandomNumberGenerator.GetBytes(16));
var packId = RandomNumberGenerator.GetBytes(16);
var packKey = keys.DerivePackKey(packId);

Console.WriteLine($"Środowisko: {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; rdzenie logiczne: {Environment.ProcessorCount}; .NET {Environment.Version}");
Console.WriteLine($"Dane: {totalMb:F0} MiB w blokach 4 MiB (mieszane)");
long compressedBytes = 0;
Measure("HMAC-SHA256 chunkId", () => { foreach (var b in blocks) keys.ChunkId(b); });
Measure("zstd poziom 3", () => { using var c = new BlockCompressor(); compressedBytes = 0; foreach (var b in blocks) compressedBytes += c.Compress(b).Payload.Length; });
Console.WriteLine($"  współczynnik kompresji: {compressedBytes / (totalMb * 1024 * 1024):P1} rozmiaru");
Measure("AES-256-GCM (seal)", () => { using var a = new AesGcmCipher(packKey); var o = new byte[BlockSize + 16]; var n = new byte[12]; foreach (var b in blocks) a.Seal(n, b, ReadOnlySpan<byte>.Empty, o); });
Measure("Etap pełny 1 wątek (zstd3 + HMAC + PackWriter/AEAD)", () => Pipeline(blocks));
var halves = new[] { blocks[..32], blocks[32..] };
Measure("Etap pełny 2 wątki (2 packi równolegle)", () => Parallel.ForEach(halves, h => Pipeline(h)));
return 0;

void Pipeline(byte[][] bs)
{
    using var c = new BlockCompressor();
    using var a = new AesGcmCipher(packKey);
    using var ms = new MemoryStream(capacity: 300 * 1024 * 1024);
    var w = new PackWriter(ms, a, keys.RepoId, packId, keys.CommitTag(packId));
    foreach (var b in bs)
    {
        var id = keys.ChunkId(b);
        var (algo, payload) = c.Compress(b);
        w.AddBlob(id, BlobType.Data, algo, b.Length, payload);
    }
    w.Finish();
}

void Measure(string name, Action action)
{
    action(); // rozgrzewka (JIT)
    var times = new List<double>();
    for (var i = 0; i < 5; i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        times.Add(sw.Elapsed.TotalSeconds);
    }
    times.Sort();
    var median = times[2];
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-55} mediana {totalMb / median,8:F1} MB/s (min {totalMb / times[^1]:F1}, max {totalMb / times[0]:F1}; 5 przebiegów)"));
}

static byte[] MakeBlock(int seed)
{
    var b = new byte[BlockSize];
    var rnd = new Random(seed);
    for (var off = 0; off < BlockSize; off += 4096)
    {
        var kind = rnd.Next(4);
        if (kind == 0) continue; // zera
        if (kind == 1)
        {
            var text = "Faktura VAT nr FV/2026/10/0001 Kontrahent Przykładowy sp. z o.o. Kwota netto 1234,56 PLN; "u8;
            for (var i = 0; i < 4096; i++) b[off + i] = text[(i + seed) % text.Length];
            continue;
        }
        rnd.NextBytes(b.AsSpan(off, 4096));
    }
    return b;
}

internal static class E2E
{
    public static async Task<int> RunAsync(int targetMb, int sessions, long? packMb = null, long? budgetMb = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "kobian-bench-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var src = Path.Combine(root, "src");
        var repo = Path.Combine(root, "repo");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(src); Directory.CreateDirectory(repo); Directory.CreateDirectory(outDir);
        try
        {
            var rnd = new Random(88);
            long total = 0;
            var files = new List<string>();
            var target = (long)targetMb * 1024 * 1024;
            var i = 0;
            while (total < target)
            {
                // rozkład jak korpus D10.9 (uproszczony): 85% małych (1–64 KiB), 13% średnich (0,5–4 MiB), 2% dużych (16–32 MiB); połowa danych kompresowalna
                var pick = rnd.Next(100);
                var size = pick < 85 ? rnd.Next(1024, 64 * 1024) : pick < 98 ? rnd.Next(512 * 1024, 4 * 1024 * 1024) : rnd.Next(16 * 1024 * 1024, 32 * 1024 * 1024);
                var dir = Path.Combine(src, "d" + (i % 40).ToString(CultureInfo.InvariantCulture), "s" + (i % 7).ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "f" + i.ToString(CultureInfo.InvariantCulture) + ".dat");
                var data = new byte[size];
                if (i % 2 == 0) rnd.NextBytes(data);
                else for (var k = 0; k < size; k++) data[k] = (byte)('a' + (k * 7 + i) % 26);
                File.WriteAllBytes(path, data);
                files.Add(path);
                total += size;
                i++;
            }
            Console.WriteLine($"Środowisko: {RuntimeInformation.OSDescription}; rdzenie {Environment.ProcessorCount}; .NET {Environment.Version}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Korpus: {files.Count:N0} plików, {total / 1048576.0:F0} MiB"));
            using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
            await using var s = await new Kobian.Providers.Storage.Local.LocalStorageProviderFactory().OpenAsync(new Kobian.Application.Storage.DestinationConfig("b", "local", repo), Kobian.Application.Storage.CredentialRole.Backup, CancellationToken.None);
            var main = new Kobian.Engine.Repo.VerifiedUploader(s);
            var extra = new List<Kobian.Application.Storage.IStorageSession>();
            for (var e = 1; e < sessions; e++)
                extra.Add(await new Kobian.Providers.Storage.Local.LocalStorageProviderFactory().OpenAsync(new Kobian.Application.Storage.DestinationConfig("b", "local", repo), Kobian.Application.Storage.CredentialRole.Backup, CancellationToken.None));
            var pipeline = new Kobian.Engine.Backup.BackupPipeline(keys, main)
            {
                PackUploaders = sessions > 1 ? [main, .. extra.Select(x => new Kobian.Engine.Repo.VerifiedUploader(x))] : [],
                PackBufferBudget = budgetMb is { } bm ? bm << 20 : Kobian.Engine.Backup.BackupPipeline.DefaultPackBufferBudget,
            };
            var packTarget = packMb is { } pm ? pm << 20 : (long?)null;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Sesje wysyłki packów: {Math.Max(1, sessions)}; pack {(packTarget ?? s.Capabilities.RecommendedPackSize) / 1048576.0:F0} MiB; budżet buforów {pipeline.PackBufferBudget / 1048576.0:F0} MiB"));
            var host = new byte[16];
            var sw = Stopwatch.StartNew();
            var full = await pipeline.RunAsync(new Kobian.Engine.Backup.BackupRequest(new Kobian.Engine.Backup.FileSystemSource(src), "bench", host, PackTargetSize: packTarget), CancellationToken.None);
            var tFull = sw.Elapsed.TotalSeconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Po kopii pełnej: szczyt zestawu roboczego {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB; bufory packów naraz (szczyt) {pipeline.PeakPackBuffers}"));
            var repoMb = Directory.GetFiles(repo, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1048576.0;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Kopia pełna: {full.Status}, {tFull:F1} s, {total / 1048576.0 / tFull:F1} MiB/s; repozytorium {repoMb:F0} MiB"));

            var changed = 0;
            foreach (var f in files.Where((_, k) => k % 20 == 0)) { await File.AppendAllTextAsync(f, "zmiana"); changed++; }
            var snap1 = await Kobian.Engine.Repo.MetadataStore.LoadSnapshotAsync(s, keys, full.SnapId!, CancellationToken.None);
            var baseline = new List<Kobian.Engine.Backup.BaselineIndex>();
            foreach (var (id, _) in snap1.Indexes)
            {
                var (idx, sha) = await Kobian.Engine.Repo.MetadataStore.LoadIndexAsync(s, keys, id, CancellationToken.None);
                baseline.Add(new Kobian.Engine.Backup.BaselineIndex(id, sha, idx));
            }
            sw.Restart();
            var inc = await pipeline.RunAsync(new Kobian.Engine.Backup.BackupRequest(new Kobian.Engine.Backup.FileSystemSource(src), "bench", host, Kobian.Format.Metadata.SnapshotMode.Incremental, baseline, full.SnapId, packTarget), CancellationToken.None);
            var tInc = sw.Elapsed.TotalSeconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Przyrost ({changed} zmienionych plików): {inc.Status}, {tInc:F1} s, nowe dane {inc.Stats.BytesNew / 1048576.0:F1} MiB"));

            var snap2 = await Kobian.Engine.Repo.MetadataStore.LoadSnapshotAsync(s, keys, inc.SnapId!, CancellationToken.None);
            using var reader = new Kobian.Engine.Repo.RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap2, CancellationToken.None);
            sw.Restart();
            var r = await new Kobian.Engine.Restore.RestoreExecutor(reader).RestoreAsync(snap2, outDir, null, CancellationToken.None);
            var tRes = sw.Elapsed.TotalSeconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Restore: {r.Status}, {r.Files:N0} plików, {tRes:F1} s, {r.Bytes / 1048576.0 / tRes:F1} MiB/s"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Szczytowy zestaw roboczy procesu: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB"));
            foreach (var x in extra) await x.DisposeAsync();
            return full.Status == Kobian.Engine.Backup.BackupStatus.Succeeded && inc.Status == Kobian.Engine.Backup.BackupStatus.Succeeded && r.Status == Kobian.Engine.Restore.RestoreStatus.Succeeded ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
