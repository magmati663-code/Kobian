using System.IO.Enumeration;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;

namespace Kobian.Engine.Restore;

/// <summary>
/// Podgląd odtworzenia (R2.8.1 „PlanRestore … dry-run (8.3): liczby, konflikty — bez zapisu danych”; SCR-07, BL-64; it. 98).
/// <see cref="BlockingCode"/> ≠ null = odtworzenie nie powinno ruszyć: KB-5001 (brak prawa zapisu — AC-07.2: odmowa PRZED odczytem danych),
/// KB-5106 (za mało miejsca), KB-5202 (brak bloków w indeksach — kopia uszkodzona), KB-5003 (wybrana ścieżka nie istnieje w kopii).
/// Konflikty (plik już istnieje → zostanie POMINIĘTY, KB-5105), niedozwolone nazwy i dowiązania to ostrzeżenia.
/// </summary>
public sealed record RestorePlan(
    long Files,
    long Bytes,
    long NewDirectories,
    long Conflicts,
    long ConflictBytes,
    IReadOnlyList<string> ConflictSample,
    long InvalidNames,
    long Links,
    long MissingBlocks,
    int Packs,
    long ReadBytes,
    long? FreeBytes,
    string? BlockingCode,
    long Resumable = 0,
    long ResumableBytes = 0)
{
    /// <summary>Bajty, które faktycznie zostaną zapisane (pliki bez konfliktów i bez już odtworzonych w przerwanej próbie).</summary>
    public long WriteBytes => Bytes - ConflictBytes - ResumableBytes;

    /// <summary>Maks. liczba przykładowych ścieżek konfliktów w odpowiedzi (lista do UI, nie raport).</summary>
    public const int MaxConflictSample = 20;
}

/// <summary>
/// Planer odtwarzania bez zapisu danych: przechodzi drzewa snapshotu z TYM SAMYM wyborem co <see cref="RestoreExecutor"/>
/// (filtr albo <see cref="ExactPathSelection"/>, <see cref="RestoreTarget"/>), liczy pliki/bajty, konflikty z istniejącymi plikami,
/// packi i bajty do odczytu (z indeksów — bez pobierania packów), sprawdza wolne miejsce i prawo zapisu (plik próbny usuwany przy zamknięciu).
/// Nie tworzy folderów. Nazwy z kopii są niezaufane — te same reguły co wykonawca (KB-5101).
/// </summary>
public sealed class RestorePlanner(RepoReader reader)
{
    private readonly RepoReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>Wolne miejsce wolumenu folderu docelowego (null = nieznane — bez blokady). Domyślnie DriveInfo.</summary>
    public Func<string, long?> FreeBytes { get; init; } = DriveFreeBytes;

    /// <summary>RD-68 (it. 108): dziennik przerwanej próby tego samego planu (tylko odczyt) — pliki już odtworzone nie są konfliktami.</summary>
    public IRestoreJournal Journal { get; init; } = NullRestoreJournal.Instance;

    /// <summary>Kontrola prawa zapisu plikiem próbnym (AC-07.2). Na Windows w usłudze sprawdza konto USŁUGI — kontrola tokenem klienta (SEC-6.3) = W-40.</summary>
    public Func<string, bool> CanWrite { get; init; } = ProbeWritable;

    public Task<RestorePlan> PlanAsync(SnapshotObject snapshot, string targetRoot, string? pathFilter, CancellationToken ct) =>
        PlanCoreAsync(snapshot, targetRoot, pathFilter, null, ct);

    public Task<RestorePlan> PlanExactAsync(SnapshotObject snapshot, string targetRoot, IReadOnlyCollection<string> exactPaths, CancellationToken ct) =>
        PlanCoreAsync(snapshot, targetRoot, null, new ExactPathSelection(ExactPathSelection.Normalize(exactPaths)), ct);

    private sealed class Acc(string root, string? filter, ExactPathSelection? exact)
    {
        public string Root { get; } = root;
        public string? Filter { get; } = filter;
        public ExactPathSelection? Exact { get; } = exact;
        public long Files, Bytes, NewDirs, Conflicts, ConflictBytes, Invalid, Links, Missing, ReadBytes, Resumable, ResumableBytes;
        public List<string> Sample { get; } = [];
        public HashSet<ChunkKey> Chunks { get; } = []; // 32 B/blok zamiast ciągu hex (jak DedupTable, RD-22)
        public HashSet<string> Packs { get; } = new(StringComparer.Ordinal);
    }

    private async Task<RestorePlan> PlanCoreAsync(SnapshotObject snapshot, string targetRoot, string? pathFilter, ExactPathSelection? exact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = RestoreTarget.OpenRoot(targetRoot);
        var acc = new Acc(root, string.IsNullOrEmpty(pathFilter) ? null : pathFilter, exact);
        await DirAsync(snapshot.RootTreeId, "", root, existsOnDisk: true, acc, ct).ConfigureAwait(false);
        var free = SafeFree(root);
        string? blocking = null;
        if (exact is not null && exact.Matched.Count == 0) blocking = KbCode.SnapshotPathNotFound;
        else if (acc.Missing > 0) blocking = KbCode.RestorePackMissing;
        else if (!CanWrite(root)) blocking = KbCode.RestoreTargetAccessDenied;
        else if (free is { } f && f < acc.Bytes - acc.ConflictBytes - acc.ResumableBytes) blocking = KbCode.RestoreTargetSpace;
        return new RestorePlan(acc.Files, acc.Bytes, acc.NewDirs, acc.Conflicts, acc.ConflictBytes, acc.Sample, acc.Invalid, acc.Links, acc.Missing,
            acc.Packs.Count, acc.ReadBytes, free, blocking, acc.Resumable, acc.ResumableBytes);
    }

    private long? SafeFree(string root)
    {
        try
        {
            return FreeBytes(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private async Task DirAsync(byte[] treeId, string rel, string dir, bool existsOnDisk, Acc acc, CancellationToken ct)
    {
        foreach (var e in await _reader.ReadTreeAsync(treeId, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var childRel = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
            var covered = acc.Exact is null || acc.Exact.Covered(childRel);
            var onPath = acc.Exact is not null && e.Kind == EntryKind.Directory && acc.Exact.OnPath(childRel);
            if (!covered && !onPath) continue; // poza wyborem: bez liczenia
            if (!RestoreTarget.TryChild(acc.Root, dir, e.Name, out var target))
            {
                acc.Invalid++;
                continue;
            }
            switch (e.Kind)
            {
                case EntryKind.Directory when e.SubtreeId is not null:
                    var dirExists = existsOnDisk && Directory.Exists(target);
                    if (existsOnDisk && File.Exists(target)) { acc.Conflicts++; AddSample(acc, childRel); continue; } // plik w miejscu folderu — wykonawca pominie
                    var willCreate = acc.Exact is not null ? covered : acc.Filter is null || RestoreExecutor.DirectoryCouldMatch(acc.Filter, childRel);
                    if (!dirExists && willCreate) acc.NewDirs++;
                    await DirAsync(e.SubtreeId, childRel, target, dirExists, acc, ct).ConfigureAwait(false);
                    break;
                case EntryKind.File:
                    if (acc.Filter is not null && !FileSystemName.MatchesSimpleExpression(acc.Filter, childRel, ignoreCase: true)) continue;
                    acc.Files++;
                    acc.Bytes += e.Size;
                    if (existsOnDisk && File.Exists(target)
                        && (Journal.IsRestored(childRel) || (Journal.WasStarted(childRel) && await RestoreExecutor.SameContentAsync(target, e, ct).ConfigureAwait(false))))
                    {
                        acc.Resumable++; // D8.8: odtworzony w przerwanej próbie — wznowienie go pominie bez konfliktu
                        acc.ResumableBytes += e.Size;
                        continue;
                    }
                    if (existsOnDisk && (File.Exists(target) || Directory.Exists(target)))
                    {
                        acc.Conflicts++;
                        acc.ConflictBytes += e.Size;
                        AddSample(acc, childRel);
                        continue; // nie będzie czytany
                    }
                    CountChunks(e, acc);
                    break;
                default:
                    acc.Links++; // dowiązania nie są odtwarzane (D8.9 R8)
                    break;
            }
        }
    }

    private void CountChunks(TreeEntry e, Acc acc)
    {
        foreach (var c in e.Chunks ?? [])
        {
            if (c.Length != 32)
            {
                acc.Missing++; // niepoprawny identyfikator bloku z niezaufanego drzewa — odtworzenie go nie odczyta
                continue;
            }
            if (!acc.Chunks.Add(new ChunkKey(c))) continue; // blok współdzielony — czytany raz
            if (!_reader.TryLocate(c, out var loc) || loc is null)
            {
                acc.Missing++;
                continue;
            }
            acc.Packs.Add(Convert.ToHexString(loc.PackId));
            acc.ReadBytes += loc.Entry.FrameLength;
        }
    }

    private static void AddSample(Acc acc, string rel)
    {
        if (acc.Sample.Count < RestorePlan.MaxConflictSample) acc.Sample.Add(rel);
    }

    private static long? DriveFreeBytes(string root) => new DriveInfo(root).AvailableFreeSpace;

    /// <summary>Plik próbny o losowej nazwie, CreateNew + DeleteOnClose — nic nie zostaje, nic nie jest nadpisywane.</summary>
    public static bool ProbeWritable(string root)
    {
        var probe = Path.Join(root, ".kobian-write-probe-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)));
        try
        {
            using var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
