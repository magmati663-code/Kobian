using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Metadata;

namespace Kobian.Engine.Repo;

/// <summary>Klasyfikacja szkód snapshotu (D8.10): pełny, częściowo uszkodzony (lista plików), nieodtwarzalny (brak snapshotu, indeksów albo korzenia).</summary>
public enum SnapshotDamageState
{
    Intact = 0,
    Partial = 1,
    Unrecoverable = 2,
}

/// <summary>Szkody jednego snapshotu: pliki i foldery, których nie da się odtworzyć; <see cref="Sample"/> — do 20 ścieżek WZGLĘDNYCH (raport lokalny administratora, nie logi — NFR-11).</summary>
public sealed record SnapshotDamage(string SnapshotId, SnapshotDamageState State, long DamagedFiles, long DamagedDirectories, long IntactFiles, IReadOnlyList<string> Sample,
    bool IndexRecovered = false)
{
    public const int MaxSample = 20;
}

/// <summary>
/// Ocena zakresu strat dla <c>check --repair</c> (D8.10 „affected(s) ← files(s) ∩ users(damagedChunks) — przez drzewa”; it. 109): dla snapshotów dotkniętych uszkodzonymi
/// packami (z <see cref="RepoChecker"/>) przechodzi drzewa i liczy pliki, których bloki leżą w uszkodzonych packach albo nie są w indeksach. Drzewo w uszkodzonym packu
/// nie jest czytane (cały folder = uszkodzony); korzeń/snapshot/indeks nieczytelny = nieodtwarzalny. Tylko odczyt — niczego nie zapisuje ani nie usuwa (SEC-18.5).
/// </summary>
public sealed class RepoDamageAssessor(IStorageSession session, RepoKeys keys)
{
    private readonly IStorageSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly RepoKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));

    /// <summary>Uszkodzone packi = cele „pack:&lt;hex&gt;” znalezisk check (brak, zły rozmiar, błąd V3).</summary>
    public static IReadOnlySet<string> BadPacks(CheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var bad = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in report.Issues)
            if (i.Code != KbCode.CheckOrphanObject && i.Target.StartsWith("pack:", StringComparison.Ordinal)) bad.Add(i.Target[5..]);
        return bad;
    }

    public async Task<SnapshotDamage> AssessAsync(string snapshotHex, IReadOnlySet<string> badPacks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(badPacks);
        SnapshotObject snap;
        RepoReader reader;
        bool recovered;
        try
        {
            snap = await MetadataStore.LoadSnapshotAsync(_session, _keys, Convert.FromHexString(snapshotHex), ct).ConfigureAwait(false);
            reader = new RepoReader(_session, _keys);
            recovered = await reader.LoadIndexesWithFallbackAsync(snap, ct).ConfigureAwait(false); // it. 110: brak indeksu → indeksy odtworzone z packów
        }
        catch (Exception ex) when (ex is KobianException or StorageException or FormatException)
        {
            return new SnapshotDamage(snapshotHex, SnapshotDamageState.Unrecoverable, 0, 0, 0, []);
        }
        using (reader)
        {
            var result = await AssessTreesAsync(snapshotHex, snap, reader, badPacks, ct).ConfigureAwait(false);
            return result with { IndexRecovered = recovered };
        }
    }

    private static async Task<SnapshotDamage> AssessTreesAsync(string snapshotHex, SnapshotObject snap, RepoReader reader, IReadOnlySet<string> badPacks, CancellationToken ct)
    {
        {
            var acc = new Acc();
            if (!Readable(reader, snap.RootTreeId, badPacks))
                return new SnapshotDamage(snapshotHex, SnapshotDamageState.Unrecoverable, 0, 1, 0, []);
            try
            {
                await WalkAsync(reader, snap.RootTreeId, "", badPacks, acc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                return new SnapshotDamage(snapshotHex, SnapshotDamageState.Unrecoverable, acc.Damaged, acc.DamagedDirs + 1, acc.Intact, acc.Sample);
            }
            var state = acc.Damaged + acc.DamagedDirs == 0 ? SnapshotDamageState.Intact : SnapshotDamageState.Partial;
            return new SnapshotDamage(snapshotHex, state, acc.Damaged, acc.DamagedDirs, acc.Intact, acc.Sample);
        }
    }

    private sealed class Acc
    {
        public long Damaged, DamagedDirs, Intact;
        public List<string> Sample { get; } = [];

        public void Add(string rel)
        {
            if (Sample.Count < SnapshotDamage.MaxSample) Sample.Add(rel);
        }
    }

    private static bool Readable(RepoReader reader, byte[] id, IReadOnlySet<string> bad) =>
        reader.TryLocate(id, out var loc) && loc is not null && !bad.Contains(Convert.ToHexStringLower(loc.PackId));

    private static async Task WalkAsync(RepoReader reader, byte[] treeId, string rel, IReadOnlySet<string> bad, Acc acc, CancellationToken ct)
    {
        foreach (var e in await reader.ReadTreeAsync(treeId, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var child = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
            switch (e.Kind)
            {
                case EntryKind.Directory when e.SubtreeId is not null:
                    if (!Readable(reader, e.SubtreeId, bad))
                    {
                        acc.DamagedDirs++;
                        acc.Add(child + "/");
                        continue;
                    }
                    try
                    {
                        await WalkAsync(reader, e.SubtreeId, child, bad, acc, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is KobianException or StorageException)
                    {
                        acc.DamagedDirs++; // uszkodzone drzewo mimo poprawnego packa (np. błąd AEAD) — folder nieodtwarzalny, reszta snapshotu dalej
                        acc.Add(child + "/");
                    }
                    break;
                case EntryKind.File:
                    if ((e.Chunks ?? []).All(c => Readable(reader, c, bad))) acc.Intact++;
                    else
                    {
                        acc.Damaged++;
                        acc.Add(child);
                    }
                    break;
            }
        }
    }
}
