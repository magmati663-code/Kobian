using System.IO.Enumeration;
using System.Security.Cryptography;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;

namespace Kobian.Engine.Restore;

public enum RestoreStatus
{
    Succeeded = 0,
    Warning = 1,
    Failed = 2,
}

/// <summary>Problem z jednym elementem: kod KB i ścieżka WZGLĘDNA w snapshocie (tylko w procesie/raporcie lokalnym — NFR-11).</summary>
public sealed record RestoreIssue(string Code, string RelativePath);

public sealed record RestoreResult(RestoreStatus Status, long Files, long Bytes, IReadOnlyList<RestoreIssue> Issues, long Resumed = 0);

/// <summary>
/// Wykonawca restore (BL-43, D8.1; V1 — fileSha256 każdego pliku): drzewa ze snapshotu → pliki w katalogu docelowym.
/// Zasady: nigdy nie nadpisuje (KB-5105, D8.9), nie przechodzi przez dowiązania w celu (KB-5104), nazwy z repo są niezaufane
/// (walidacja segmentu + kontrola wyjścia poza korzeń), plik z niezgodnym hashem jest usuwany i raportowany (KB-5201).
/// Dowiązania z kopii nie są odtwarzane (raport). Mapowanie nazw Windows (R1–R9) — <see cref="WindowsPathMapper"/> w adapterze hosta (W-5).
/// </summary>
public sealed class RestoreExecutor(RepoReader reader)
{
    private readonly RepoReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public Task<RestoreResult> RestoreAsync(SnapshotObject snapshot, string targetRoot, string? pathFilter, CancellationToken ct) =>
        RestoreAsync(snapshot, targetRoot, pathFilter, null, ct);

    /// <summary>
    /// Odtworzenie wybranych ścieżek DOKŁADNIE (it. 80, Qb34): każda ścieżka = plik albo folder z całą zawartością; porównanie porządkowe
    /// (bez wzorców — nazwa z „*”/„?” nie dobiera sąsiadów). Ścieżki walidowane jak w przeglądarce (KB-5003); żadna nie istnieje w kopii → KB-5003.
    /// Foldery spoza wybranych ścieżek nie są nawet odczytywane.
    /// </summary>
    public async Task<RestoreResult> RestoreExactAsync(SnapshotObject snapshot, string targetRoot, IReadOnlyCollection<string> exactPaths, CancellationToken ct)
    {
        var normalized = ExactPathSelection.Normalize(exactPaths);
        var r = await RestoreAsync(snapshot, targetRoot, null, normalized, ct).ConfigureAwait(false);
        return r;
    }

    public const int MaxExactPaths = ExactPathSelection.MaxPaths;

    private async Task<RestoreResult> RestoreAsync(SnapshotObject snapshot, string targetRoot, string? pathFilter, HashSet<string>? exact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targetRoot);
        var root = RestoreTarget.OpenRoot(targetRoot);
        var st = new State(root, pathFilter) { Exact = exact is null ? null : new ExactPathSelection(exact) };
        try
        {
            await DirAsync(snapshot.RootTreeId, "", root, st, ct).ConfigureAwait(false);
        }
        catch (KobianException ex) when (ex.ErrorClass == ErrorClass.Integrity)
        {
            st.Issues.Add(new RestoreIssue(KbCode.RestorePackMissing, ""));
            return new RestoreResult(RestoreStatus.Failed, st.Files, st.Bytes, st.Issues);
        }
        if (st.Exact is not null && st.Exact.Matched.Count == 0)
            return new RestoreResult(RestoreStatus.Failed, 0, 0, [new RestoreIssue(KbCode.SnapshotPathNotFound, "")]);
        var failed = st.Issues.Any(i => i.Code is KbCode.RestoreHashMismatch or KbCode.RestorePackMissing);
        var status = failed ? RestoreStatus.Failed : st.Issues.Count > 0 ? RestoreStatus.Warning : RestoreStatus.Succeeded;
        return new RestoreResult(status, st.Files, st.Bytes, st.Issues, st.Resumed);
    }

    private sealed class State(string root, string? filter)
    {
        public string Root { get; } = root;

        public string? Filter { get; } = filter;

        /// <summary>Tryb dokładnych ścieżek (null = filtr/wszystko).</summary>
        public ExactPathSelection? Exact { get; init; }

        public bool Covered(string rel) => Exact is null || Exact.Covered(rel);

        public bool OnPath(string rel) => Exact is not null && Exact.OnPath(rel);

        public List<RestoreIssue> Issues { get; } = [];

        public long Files { get; set; }

        public long Bytes { get; set; }

        /// <summary>Pliki uznane za odtworzone z poprzedniej próby (D8.8).</summary>
        public long Resumed { get; set; }
    }

    private async Task DirAsync(byte[] treeId, string rel, string dir, State st, CancellationToken ct)
    {
        foreach (var e in await _reader.ReadTreeAsync(treeId, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var childRel = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
            if (st.Exact is not null && !st.Covered(childRel) && !(e.Kind == EntryKind.Directory && st.OnPath(childRel))) continue; // poza wyborem: bez raportu
            if (!RestoreTarget.TryChild(st.Root, dir, e.Name, out var target))
            {
                st.Issues.Add(new RestoreIssue(KbCode.RestoreNameInvalid, childRel));
                continue;
            }
            switch (e.Kind)
            {
                case EntryKind.Directory when e.SubtreeId is not null:
                    var di = new DirectoryInfo(target);
                    if (di.Exists && di.LinkTarget is not null) { st.Issues.Add(new RestoreIssue(KbCode.RestoreReparseRefused, childRel)); continue; }
                    if (File.Exists(target)) { st.Issues.Add(new RestoreIssue(KbCode.RestoreTargetExists, childRel)); continue; }
                    if (st.Exact is not null)
                    {
                        var covered = st.Covered(childRel);
                        if (!covered && !st.OnPath(childRel)) continue; // poza wyborem — bez odczytu drzewa
                        if (covered) Directory.CreateDirectory(target);
                        await SubDirAsync(e.SubtreeId, childRel, target, st, ct).ConfigureAwait(false);
                        break;
                    }
                    if (st.Filter is null || DirectoryCouldMatch(st.Filter, childRel)) Directory.CreateDirectory(target);
                    await SubDirAsync(e.SubtreeId, childRel, target, st, ct).ConfigureAwait(false);
                    break;
                case EntryKind.File:
                    if (st.Exact is not null && !st.Covered(childRel)) continue;
                    if (st.Filter is not null && !FileSystemName.MatchesSimpleExpression(st.Filter, childRel, ignoreCase: true)) continue;
                    Directory.CreateDirectory(dir);
                    await FileAsync(e, childRel, target, st, ct).ConfigureAwait(false);
                    break;
                default:
                    if (st.Exact is not null && !st.Covered(childRel)) continue;
                    st.Issues.Add(new RestoreIssue(KbCode.RestoreReparseRefused, childRel)); // dowiązania nie są odtwarzane (D8.9 R8)
                    break;
            }
        }
    }

    /// <summary>Uszkodzone/brakujące drzewo JEDNEGO podkatalogu: raport (KB-5202 ze ścieżką katalogu) i dalej z rodzeństwem — reszta kopii ma wrócić; błąd drzewa korzenia nadal przerywa (nie ma czego odtwarzać).</summary>
    private async Task SubDirAsync(byte[] treeId, string rel, string dir, State st, CancellationToken ct)
    {
        try
        {
            await DirAsync(treeId, rel, dir, st, ct).ConfigureAwait(false);
        }
        catch (KobianException ex) when (ex.ErrorClass == ErrorClass.Integrity)
        {
            st.Issues.Add(new RestoreIssue(KbCode.RestorePackMissing, rel));
        }
    }

    internal static bool DirectoryCouldMatch(string filter, string rel) =>
        filter.StartsWith('*') || filter.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Przyrostek pliku w trakcie zapisu (R2.8.1: CreatePartial → weryfikacja → AtomicRename): pod nazwą docelową pojawia się tylko plik zweryfikowany.</summary>
    public const string PartialSuffix = ".kobian-partial";

    private async Task FileAsync(TreeEntry e, string rel, string target, State st, CancellationToken ct)
    {
        var journal = Journal;
        // Dziennik mówi „zweryfikowany w poprzedniej próbie”, ale folder docelowy mógł być w międzyczasie wyczyszczony (restore zakończony Failed bywa powtarzany po sprzątaniu celu):
        // zaliczenie pliku bez sprawdzenia dysku dawało Succeeded z brakującymi plikami. Plik musi istnieć i mieć oczekiwany rozmiar — inaczej odtwarzamy go od nowa.
        var onDiskAsRecorded = journal.IsRestored(rel) && File.Exists(target) && new FileInfo(target) is { LinkTarget: null } fi && fi.Length == e.Size;
        if (onDiskAsRecorded || (journal.WasStarted(rel) && await SameContentAsync(target, e, ct).ConfigureAwait(false)))
        {
            // D8.8: wznowienie — plik zweryfikowany w poprzedniej próbie (albo zapisany tuż przed awarią, z identyczną treścią) nie jest konfliktem
            await journal.FinishedAsync(rel, RestoreFileState.Verified).ConfigureAwait(false);
            st.Files++;
            st.Bytes += e.Size;
            st.Resumed++;
            Progress?.Invoke(st.Files, st.Bytes);
            return;
        }
        if (File.Exists(target) || Directory.Exists(target))
        {
            await journal.FinishedAsync(rel, RestoreFileState.Skipped).ConfigureAwait(false);
            st.Issues.Add(new RestoreIssue(KbCode.RestoreTargetExists, rel));
            return;
        }
        var partial = target + PartialSuffix;
        if (journal.WasStarted(rel) && File.Exists(partial))
        {
            // RD-76 (F7 it. 120): usuwamy wyłącznie plik, który może być naszym niedokończonym zapisem — zwykły plik (nie dowiązanie) nie większy niż oczekiwany rozmiar.
            // Podstawiony cudzy plik nie jest kasowany: pomijamy wpis z ostrzeżeniem (brak utraty danych, AC-07.3).
            var pi = new FileInfo(partial);
            if (pi.LinkTarget is not null || pi.Length > e.Size)
            {
                await journal.FinishedAsync(rel, RestoreFileState.Skipped).ConfigureAwait(false);
                st.Issues.Add(new RestoreIssue(KbCode.RestoreTargetExists, rel));
                return;
            }

            File.Delete(partial); // nasz niedokończony zapis z poprzedniej próby
        }

        await journal.StartedAsync(rel, e.Size, e.Chunks?.Count ?? 0).ConfigureAwait(false);
        FileStream fs;
        try
        {
            fs = new FileStream(partial, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None });
        }
        catch (PathTooLongException)
        {
            await journal.FinishedAsync(rel, RestoreFileState.Skipped).ConfigureAwait(false);
            st.Issues.Add(new RestoreIssue(KbCode.RestorePathTooLong, rel));
            return;
        }
        catch (IOException) when (File.Exists(partial) || Directory.Exists(partial))
        {
            await journal.FinishedAsync(rel, RestoreFileState.Skipped).ConfigureAwait(false);
            st.Issues.Add(new RestoreIssue(KbCode.RestoreTargetExists, rel)); // obcy plik o nazwie roboczej — nie ruszamy
            return;
        }
        catch (UnauthorizedAccessException)
        {
            await journal.FinishedAsync(rel, RestoreFileState.Skipped).ConfigureAwait(false);
            st.Issues.Add(new RestoreIssue(KbCode.RestoreTargetAccessDenied, rel));
            return;
        }
        var ok = false;
        long written = 0;
        try
        {
            await using (fs.ConfigureAwait(false))
            {
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var c in e.Chunks ?? [])
                {
                    var block = await _reader.ReadBlockAsync(c, Kobian.Format.Pack.BlobType.Data, ct).ConfigureAwait(false);
                    h.AppendData(block);
                    await fs.WriteAsync(block, ct).ConfigureAwait(false);
                    written += block.Length;
                }
                var sha = h.GetHashAndReset();
                ok = written == e.Size && (e.FileSha256 is null || CryptographicOperations.FixedTimeEquals(sha, e.FileSha256));
                if (ok) await fs.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (KobianException ex) when (ex.ErrorClass == ErrorClass.Integrity)
        {
            // Brakujący albo uszkodzony pack/blok TEGO pliku: plik jest raportowany i pomijany, a odtwarzanie idzie dalej — zdrowe pliki z innych packów mają wrócić
            // (wcześniej pierwszy taki błąd przerywał cały restore, a reszta drzewa nie była nawet próbowana). Status końcowy i tak jest Failed.
            DeletePartial(partial);
            await journal.FinishedAsync(rel, RestoreFileState.Quarantined).ConfigureAwait(false);
            st.Issues.Add(new RestoreIssue(ex.Code is KbCode.AuthenticationFailed or KbCode.BlockTooLarge or KbCode.PackCommitMismatch ? KbCode.RestoreHashMismatch : KbCode.RestorePackMissing, rel));
            return;
        }
        catch
        {
            // Inny błąd odczytu (anulowanie, awaria celu): strumień jest już zamknięty — nasz plik roboczy z niepełną treścią nie zostaje na dysku.
            DeletePartial(partial);
            throw;
        }
        if (!ok)
        {
            File.Delete(partial); // własny plik roboczy — uszkodzona treść nigdy nie trafia pod nazwę docelową (AC-07.3)
            await journal.FinishedAsync(rel, RestoreFileState.Quarantined).ConfigureAwait(false);
            st.Issues.Add(new RestoreIssue(KbCode.RestoreHashMismatch, rel));
            return;
        }
        if (e.MtimeNs is { } mt) File.SetLastWriteTimeUtc(partial, DateTime.UnixEpoch.AddTicks(mt / 100));
        try
        {
            File.Move(partial, target, overwrite: false); // nigdy nie nadpisuje (D8.9)
        }
        catch (IOException) when (File.Exists(target) || Directory.Exists(target))
        {
            File.Delete(partial); // ktoś utworzył plik docelowy w trakcie — pomijamy, bez nadpisania
            await journal.FinishedAsync(rel, RestoreFileState.Skipped).ConfigureAwait(false);
            st.Issues.Add(new RestoreIssue(KbCode.RestoreTargetExists, rel));
            return;
        }
        await journal.FinishedAsync(rel, RestoreFileState.Verified).ConfigureAwait(false);
        st.Files++;
        st.Bytes += written;
        Progress?.Invoke(st.Files, st.Bytes);
    }

    private static void DeletePartial(string partial)
    {
        try
        {
            File.Delete(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // sprzątanie best-effort; błąd oryginalny jest ważniejszy
        }
    }

    /// <summary>Plik docelowy istnieje i ma dokładnie treść z kopii (rozmiar + SHA-256) — wyłącznie przy wznowieniu, nie dowiązanie.</summary>
    internal static async Task<bool> SameContentAsync(string target, TreeEntry e, CancellationToken ct)
    {
        var fi = new FileInfo(target);
        if (!fi.Exists || fi.LinkTarget is not null || fi.Length != e.Size || e.FileSha256 is null) return false;
        try
        {
            await using var f = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var sha = await SHA256.HashDataAsync(f, ct).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(sha, e.FileSha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Dziennik wznowień (D8.8). Domyślnie bez dziennika.</summary>
    public IRestoreJournal Journal { get; init; } = NullRestoreJournal.Instance;

    /// <summary>RD-60 (it. 108): postęp po każdym odtworzonym pliku — (pliki, bajty) od początku uruchomienia. Wywołanie synchroniczne, ma być tanie.</summary>
    public Action<long, long>? Progress { get; init; }
}

/// <summary>Stan pliku w dzienniku restore (D8.8; wartości jak CHECK w restore_files).</summary>
public enum RestoreFileState
{
    Pending,
    Verified,
    Quarantined,
    Skipped,
}

/// <summary>
/// Port dziennika wznowień odtwarzania (D8.8, R2.8.2; it. 107): ścieżki WZGLĘDNE w kopii. <see cref="IsRestored"/> — zweryfikowany w poprzedniej próbie
/// tego samego planu; <see cref="WasStarted"/> — rozpoczęty w poprzedniej próbie (plik roboczy jest nasz; plik docelowy o identycznej treści = zapisany tuż przed awarią).
/// </summary>
public interface IRestoreJournal
{
    bool IsRestored(string rel);

    bool WasStarted(string rel);

    ValueTask StartedAsync(string rel, long size, int chunks);

    ValueTask FinishedAsync(string rel, RestoreFileState state);
}

public sealed class NullRestoreJournal : IRestoreJournal
{
    public static NullRestoreJournal Instance { get; } = new();

    public bool IsRestored(string rel) => false;

    public bool WasStarted(string rel) => false;

    public ValueTask StartedAsync(string rel, long size, int chunks) => ValueTask.CompletedTask;

    public ValueTask FinishedAsync(string rel, RestoreFileState state) => ValueTask.CompletedTask;
}
