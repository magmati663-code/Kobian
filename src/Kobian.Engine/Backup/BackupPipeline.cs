using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Compression;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Format.Pack;
using Kobian.Format.Repo;

namespace Kobian.Engine.Backup;

public enum BackupStatus
{
    Succeeded = 0,
    Warning = 1,
    Failed = 2,
}

/// <summary>Indeks bazowy (dozwolone packi — allowedPacks, D3.2/R2.3.6) z jego SHA-256 do klucza 11 snapshotu.</summary>
public sealed record BaselineIndex(byte[] IndexId, byte[] Sha256, IndexObject Index);

public sealed record BackupRequest(
    ISourceTree Source,
    string JobId,
    byte[] HostId,
    SnapshotMode Mode = SnapshotMode.Full,
    IReadOnlyList<BaselineIndex>? Baseline = null,
    byte[]? ParentSnapId = null,
    long? PackTargetSize = null,
    IReadOnlyDictionary<string, string>? DbMeta = null,
    TimeProvider? Time = null,
    byte[]? RunId = null,
    BaselineSet? BaselineStream = null,
    ParentTree? ParentTree = null);

/// <summary>
/// Drzewo poprzedniej migawki do skrótu „plik bez zmian” w kopii przyrostowej: plik o tym samym rozmiarze i czasie modyfikacji, którego czas jest starszy niż
/// <see cref="RacyWindow"/> przed STARTEM przebiegu poprzedniej migawki, dostaje bloki z tej migawki bez czytania treści. Granica „racy”: zmiana pliku w tym samym
/// znaczniku czasu, w którym go czytano, jest niewidoczna po mtime, więc takie pliki czytamy zawsze. Skrót jest wyłączany przez wywołującego co kilka dni
/// (pełna weryfikacja treści) — wykrywa zmianę treści, która zachowała rozmiar i mtime (np. narzędzia przywracające czas).
/// </summary>
/// <summary>Czas poszczególnych faz potoku kopii (sumy; fazy sekwencyjne w jednym wątku — suma ≈ czas przebiegu poza listowaniem katalogów).</summary>
public sealed record PhaseTimes(TimeSpan Read, TimeSpan Hash, TimeSpan Compress, TimeSpan PackEncrypt, TimeSpan UploadWait)
{
    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"read={Read.TotalSeconds:F2}s hash={Hash.TotalSeconds:F2}s compress={Compress.TotalSeconds:F2}s encrypt={PackEncrypt.TotalSeconds:F2}s uploadWait={UploadWait.TotalSeconds:F2}s");
}

public sealed record ParentTree(byte[] RootTreeId, DateTimeOffset RunStarted, Func<byte[], CancellationToken, Task<IReadOnlyList<TreeEntry>>> ReadTree)
{
    public static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);
}

/// <summary>Wynik uruchomienia. <see cref="Code"/> = kod KB dominującego problemu; ścieżek pominiętych plików nie zwracamy poza procesem (NFR-11).</summary>
public sealed record BackupResult(BackupStatus Status, string? Code, byte[]? SnapId, SnapshotStats Stats, long Skipped, IReadOnlyList<string> SkippedPaths);

/// <summary>
/// Potok backupu plików (BL-22, R2.3.6) z niezmiennikami D3.14: chunker stały 4 MiB, dedup dokładny (słownik — D3.15 warstwa 1),
/// packi zamykane po <c>PackTargetSize</c>, upload z weryfikacją, commit: indeks → odczyt kontrolny → snapshot (atomowo) — ostatni.
/// Błąd zapisu do repo = Failed (KB-1402), snapshot nie powstaje (I-1); niespójność referencji = Failed (KB-1403, I-3).
/// Błąd odczytu źródła = plik pominięty, Warning (KB-1405). Bez D3.17 (stat z uchwytu — adapter Windows, BL-23).
/// </summary>
public sealed class BackupPipeline
{
    public const int ChunkSize = PackLayout.MaxRawLengthData;
    private readonly RepoKeys _keys;
    private readonly VerifiedUploader _up;

    public BackupPipeline(RepoKeys keys, VerifiedUploader uploader)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _up = uploader ?? throw new ArgumentNullException(nameof(uploader));
    }

    /// <summary>
    /// RD-73 (it. 118): dodatkowe uploadery packów — KAŻDY z własną sesją (sesja jest jednowątkowa, D4.10). Puste = wysyłka sekwencyjna przez główny uploader
    /// (zachowanie sprzed it. 118). Indeks i snapshot zawsze przez główny uploader, po zakończeniu wszystkich wysyłek packów.
    /// </summary>
    public IReadOnlyList<VerifiedUploader> PackUploaders { get; init; } = [];

    /// <summary>Regulator AIMD (D4.16) — limit wysyłek w toku = min(liczba uploaderów, Current, budżet buforów); null = liczba uploaderów.</summary>
    public IUploadParallelism? Parallelism { get; init; }

    /// <summary>
    /// RD-75 (it. 120): domyślny budżet pamięci buforów packów (budowany + w toku wysyłki). NFR-05: ≤ 500 MB procesu podczas kopii; NFR-22: struktura dedup
    /// ≤ 150 MB; reszta (runtime, Kestrel, SQLite, drzewa, kompresja) ~100–150 MB — na bufory packów zostaje ~192 MiB. Przy packu 16 MiB (Local/SFTP) bufor
    /// ma ~20,6 MiB → do 9 buforów, czyli limit nie ogranicza dzisiejszych 2–4 sesji; ogranicza duże packi (np. 64–512 MiB z packTargetSize).
    /// PROWIZORYCZNIE Qb74: wartość do potwierdzenia pomiarem Private Bytes na Windows (W-48).
    /// </summary>
    public const long DefaultPackBufferBudget = 192L << 20;

    /// <summary>
    /// RD-75: budżet pamięci buforów packów w bajtach — (wysyłki w toku + pack budowany) × <see cref="PackBufferCapacity"/> ≤ budżet; zawsze co najmniej
    /// jedna wysyłka (w trybie równoległym to 2 bufory: wysyłany + budowany — minimum ponad budżet przy bardzo dużym packu).
    /// </summary>
    public long PackBufferBudget { get; init; } = DefaultPackBufferBudget;

    /// <summary>Diagnostyka (testy, Bench): największa liczba buforów packów przydzielonych naraz w ostatnim <see cref="RunAsync"/> (wypożyczone + w puli).</summary>
    public int PeakPackBuffers { get; private set; }

    /// <summary>Diagnostyka (testy, Bench, metryki): ile plików ostatniego <see cref="RunAsync"/> ominęło odczyt treści dzięki <see cref="ParentTree"/>.</summary>
    public long LastReusedFiles { get; private set; }

    /// <summary>
    /// Runda 9 planu: ile bloków dużego pliku jest przygotowywanych (HMAC chunkId + kompresja) równolegle, podczas gdy główny wątek czyta, liczy SHA-256 pliku, szyfruje
    /// do packa i wysyła. 1 = jak dotąd (sekwencyjnie). Domyślnie rdzenie − 1, najwyżej 3 (każdy blok w locie to bufor 4 MiB).
    /// Kolejność bloków w packach i wynik logiczny są takie same jak sekwencyjnie.
    /// </summary>
    public int BlockParallelism { get; init; } = int.TryParse(Environment.GetEnvironmentVariable("KOBIAN_BLOCK_PARALLELISM"), out var envDepth) && envDepth is >= 1 and <= 8
        ? envDepth
        : Math.Clamp(Environment.ProcessorCount - 1, 1, 3);

    /// <summary>Diagnostyka (metryki, Bench): łączny czas faz ostatniego <see cref="RunAsync"/> — odczyt, hasze, kompresja, szyfrowanie do packa, oczekiwanie na wysyłkę.</summary>
    public PhaseTimes LastTimes { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>Diagnostyka: ile buforów packów utworzono w ostatnim <see cref="RunAsync"/> (reszta packów — bufory z puli).</summary>
    public int PackBuffersCreated { get; private set; }

    /// <summary>
    /// RD-75: pojemność bufora packa dla celu rozmiaru — pack zamykany po przekroczeniu celu, więc ≤ cel + jeden blok (≤ 4 MiB + nagłówki) + trailer
    /// (~60 B/blok; zapas cel/32) + nagłówek/stopka (zapas 64 KiB). Bufor z góry tej pojemności nie podwaja się przy przekroczeniu celu (MemoryStream
    /// rośnie ×2: pack 16 MiB + blok dawał tablicę 32 MiB). Większy pack (rzadko: wielki trailer, drzewo 16 MiB) — bufor rośnie i nie wraca do puli.
    /// </summary>
    public static int PackBufferCapacity(long packTarget) =>
        (int)Math.Min(Array.MaxLength, Math.Max(1, packTarget) + ChunkSize + (Math.Max(1, packTarget) / 32) + (64 * 1024));

    public async Task<BackupResult> RunAsync(BackupRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        using var run = new RunState(this, req);
        try
        {
            try
            {
                return await RunCoreAsync(run, ct).ConfigureAwait(false);
            }
            finally
            {
                await run.AbandonUploadsAsync().ConfigureAwait(false); // błąd w trakcie: wysyłki w toku dokończone/porzucone przed zwolnieniem buforów
                PeakPackBuffers = run.PeakBuffers;
                LastReusedFiles = run.Reused;
                LastTimes = run.Times();
                PackBuffersCreated = run.BuffersCreated;
            }
        }
        catch (RepoWriteException)
        {
            return new BackupResult(BackupStatus.Failed, KbCode.RepoWriteFailed, null, run.Stats(), run.Skipped.Count, run.Skipped);
        }
        catch (KobianException ex) when (ex.Code is KbCode.ReferenceIntegrityFailed or KbCode.SourceUnreadable)
        {
            return new BackupResult(BackupStatus.Failed, ex.Code, null, run.Stats(), run.Skipped.Count, run.Skipped);
        }
    }

    private static async Task<BackupResult> RunCoreAsync(RunState run, CancellationToken ct)
    {
        var root = await run.DirectoryAsync("", ct, run.ParentRoot).ConfigureAwait(false);
        await run.FlushPackAsync(ct).ConfigureAwait(false);
        var snapId = await run.CommitAsync(root, ct).ConfigureAwait(false);
        var status = run.Skipped.Count > 0 ? BackupStatus.Warning : BackupStatus.Succeeded;
        return new BackupResult(status, status == BackupStatus.Warning ? KbCode.SourceUnreadable : null, snapId, run.Stats(), run.Skipped.Count, run.Skipped);
    }

    private sealed class RunState : IDisposable
    {
        private readonly BackupPipeline _p;
        private readonly BackupRequest _req;
        private readonly BlockCompressor _zstd = new();
        private readonly DedupTable _dedup; // RD-22: bazowe bloki w posortowanej tablicy (32 B/blok, it. 102), nowe w słowniku flag — I-2/I-3 bez zmian
        private readonly List<IndexPack> _newPacks = [];
        private readonly List<InFlight> _inFlight = []; // RD-73: wysyłki packów w toku (tylko wątek potoku dodaje i odbiera)
        private readonly Stack<VerifiedUploader> _free;
        private readonly long _target;
        private readonly Stack<MemoryStream> _bufferPool = new(); // RD-75: bufory packów do ponownego użycia (bez nowych tablic LOH na każdy pack)
        private readonly int _bufferCapacity;
        private readonly int _budgetInFlight;
        private int _buffersAllocated;
        private MemoryStream? _packBuf;
        private PackWriter? _pack;
        private byte[]? _packId;
        private AesGcmCipher? _packCipher;
        private long _files, _dirs, _raw, _new;
        private long _tRead, _tHash, _tCompress, _tPack, _tUpload; // Stopwatch ticks

        public PhaseTimes Times() => new(Span(_tRead), Span(_tHash), Span(_tCompress), Span(_tPack), Span(_tUpload));

        private static TimeSpan Span(long ticks) => TimeSpan.FromSeconds((double)ticks / System.Diagnostics.Stopwatch.Frequency);
        private byte[]? _fileBuf;
        private readonly Kobian.Format.Pack.PackScratch _scratch = new();

        public RunState(BackupPipeline p, BackupRequest req)
        {
            _p = p; _req = req;
            _free = new Stack<VerifiedUploader>(p.PackUploaders.Reverse());
            _target = req.PackTargetSize ?? p._up.Session.Capabilities.RecommendedPackSize;
            _bufferCapacity = PackBufferCapacity(_target);
            // RD-75: (w toku + budowany) × pojemność ≤ budżet → w toku ≤ budżet/pojemność − 1, co najmniej 1.
            _budgetInFlight = (int)Math.Clamp((Math.Max(0, p.PackBufferBudget) / _bufferCapacity) - 1, 1, int.MaxValue);
            var baseBlocks = req.Mode != SnapshotMode.Full && req.Baseline is not null ? req.Baseline.Sum(b => b.Index.Packs.Sum(pk => pk.Entries.Count)) : 0;
            if (req.Mode != SnapshotMode.Full && req.BaselineStream is not null)
            {
                if (req.Baseline is not null) throw new ArgumentException("Use Baseline or BaselineStream, not both.", nameof(req));
                _dedup = req.BaselineStream.Take(); // BL-83: bloki bazowe już w tabeli, obiekty indeksów zwolnione
                return;
            }
            _dedup = new DedupTable(baseBlocks); // dokładna pojemność — bez rezerwy listy (szczyt przy zamrożeniu 2 × 32 B/blok)
            if (req.Mode != SnapshotMode.Full && req.Baseline is not null)
                foreach (var b in req.Baseline)
                    foreach (var pk in b.Index.Packs)
                        foreach (var e in pk.Entries) _dedup.AddConfirmed(e.ChunkId);
        }

        public List<string> Skipped { get; } = [];

        // ---- przygotowanie bloków dużych plików na wielu wątkach (runda 9) ----
        private readonly System.Collections.Concurrent.ConcurrentStack<BlockCompressor> _compressors = new();
        private readonly List<BlockCompressor> _allCompressors = [];
        private readonly Stack<byte[]> _blockBufs = new();
        private int _blockBufCount;

        private sealed record Prepared(byte[] Id, CompressionAlgorithm Algorithm, byte[]? Payload, int Length, byte[] Buffer);

        private BlockCompressor RentCompressor()
        {
            if (_compressors.TryPop(out var c)) return c;
            c = new BlockCompressor();
            lock (_allCompressors) _allCompressors.Add(c);
            return c;
        }

        /// <summary>Wątek roboczy: HMAC chunkId i (dla bloków spoza bazy) kompresja. Tylko odczyty wspólnych struktur (tablica bazowa jest zamrożona przed startem).</summary>
        private Prepared Prepare(byte[] buffer, int length)
        {
            var span = buffer.AsSpan(0, length);
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var id = _p._keys.ChunkId(span);
            Interlocked.Add(ref _tHash, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            if (_dedup.IsBase(id)) return new Prepared(id, CompressionAlgorithm.None, null, length, buffer); // blok znany: nie będzie zapisu, kompresja zbędna
            var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            var c = RentCompressor();
            try
            {
                var (alg, payload) = c.Compress(span);
                return new Prepared(id, alg, payload, length, buffer);
            }
            finally
            {
                _compressors.Push(c);
                Interlocked.Add(ref _tCompress, System.Diagnostics.Stopwatch.GetTimestamp() - t1);
            }
        }

        private byte[]? TryTakeBlockBuffer(int limit)
        {
            if (_blockBufs.TryPop(out var b)) return b;
            if (_blockBufCount >= limit) return null;
            _blockBufCount++;
            return new byte[ChunkSize];
        }

        /// <summary>Wątek potoku (w kolejności plików i bloków): rejestracja w tablicy dedup, zapis do packa, ewentualna wysyłka.</summary>
        private async Task AddPreparedAsync(Prepared p, CancellationToken ct)
        {
            if (!_dedup.Reference(p.Id)) return;
            if (_pack is null) StartPack();
            var (alg, payload) = p.Payload is null ? _zstd.Compress(p.Buffer.AsSpan(0, p.Length)) : (p.Algorithm, p.Payload);
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _pack!.AddBlob(p.Id, BlobType.Data, alg, p.Length, payload);
            _tPack += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _new += p.Length;
            await FlushIfFullAsync(ct).ConfigureAwait(false);
        }

        private async Task<(long Size, List<byte[]> Chunks, byte[] Sha)?> FileParallelAsync(string rel, int depth, CancellationToken ct)
        {
            _dedup.IsBase(new byte[32]); // zamrożenie tablicy bazowej PRZED wątkami roboczymi (dalej tylko odczyty)
            var chunks = new List<byte[]>();
            long size = 0;
            var failed = false;
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Stream s;
            try { s = _req.Source.OpenRead(rel); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            var pending = new Queue<Task<Prepared>>();
            async Task ConsumeOldestAsync()
            {
                var p = await pending.Dequeue().ConfigureAwait(false);
                await AddPreparedAsync(p, ct).ConfigureAwait(false);
                chunks.Add(p.Id);
                _blockBufs.Push(p.Buffer);
            }
            try
            {
                await using (s.ConfigureAwait(false))
                {
                    while (true)
                    {
                        byte[]? buf;
                        while ((buf = TryTakeBlockBuffer(depth + 1)) is null) await ConsumeOldestAsync().ConfigureAwait(false);
                        int n;
                        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        try { n = await FillAsync(s, buf, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _blockBufs.Push(buf); failed = true; break; }
                        _tRead += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                        if (n == 0) { _blockBufs.Push(buf); break; }
                        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                        h.AppendData(buf.AsSpan(0, n));
                        _tHash += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
                        size += n;
                        var b = buf;
                        var len = n;
                        pending.Enqueue(Task.Run(() => Prepare(b, len), CancellationToken.None));
                        while (pending.Count > 0 && pending.Peek().IsCompleted) await ConsumeOldestAsync().ConfigureAwait(false);
                        if (n < ChunkSize) break;
                    }
                    while (pending.Count > 0) await ConsumeOldestAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // błąd w trakcie: zadania w toku dokończone przed zwolnieniem buforów i kompresorów (nie zostają w tle po powrocie)
                while (pending.Count > 0)
                {
                    try { _blockBufs.Push((await pending.Dequeue().ConfigureAwait(false)).Buffer); }
                    catch (Exception) { /* błąd pierwotny jest już w drodze; tu tylko czekamy na koniec zadań */ }
                }
            }
            if (failed) return null;
            _raw += size;
            return (size, chunks, h.GetHashAndReset());
        }

        public int PeakBuffers { get; private set; }

        public int BuffersCreated { get; private set; }

        public SnapshotStats Stats() => new(_files, _dirs, _raw, _new, _newPacks.Count);

        private static bool NameAccepted(string name)
        {
            try
            {
                TreeCodec.ValidateName(name);
                return true;
            }
            catch (KobianException)
            {
                return false;
            }
        }

        /// <summary>Ile plików ominęło odczyt treści (skrót z poprzedniej migawki) — diagnostyka i testy.</summary>
        public long Reused { get; private set; }

        /// <summary>Korzeń drzewa poprzedniej migawki dla skrótu (null = brak skrótu).</summary>
        public byte[]? ParentRoot => _req.Source.MetadataReliable && _req.Mode == SnapshotMode.Incremental ? _req.ParentTree?.RootTreeId : null;

        /// <summary>Wpis z poprzedniej migawki, jeśli plik można uznać za niezmieniony (i jego bloki są w bazie); inaczej null.</summary>
        private TreeEntry? Unchanged(SourceNode c, TreeEntry? parent)
        {
            if (parent is not { Kind: EntryKind.File, FileSha256: not null, MtimeNs: { } pm } || _req.ParentTree is not { } pt) return null;
            if (pm != c.MtimeNs || parent.Size != c.Size || c.MtimeNs <= 0) return null;
            var racyFrom = (pt.RunStarted - ParentTree.RacyWindow - DateTimeOffset.UnixEpoch).Ticks * 100;
            if (c.MtimeNs >= racyFrom) return null;
            foreach (var id in parent.Chunks ?? []) if (!_dedup.IsBase(id)) return null;
            return parent;
        }

        public async Task<byte[]> DirectoryAsync(string rel, CancellationToken ct, byte[]? parentTreeId = null)
        {
            ct.ThrowIfCancellationRequested();
            Dictionary<string, TreeEntry>? parentByName = null;
            if (parentTreeId is not null && _req.ParentTree is { } ptree && _req.Source.MetadataReliable)
            {
                try
                {
                    parentByName = (await ptree.ReadTree(parentTreeId, ct).ConfigureAwait(false)).ToDictionary(e => e.Name, StringComparer.Ordinal);
                }
                catch (Exception ex) when (ex is KobianException or StorageException or FormatException)
                {
                    parentByName = null; // drzewo poprzedniej migawki nieczytelne: ten katalog czytamy w całości (zawsze poprawne)
                }
            }
            List<SourceNode> children;
            try
            {
                children = [.. _req.Source.List(rel)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (rel.Length == 0) throw new KobianException(KbCode.SourceUnreadable, ErrorClass.Permanent, "Source root unreadable.", ex); // brak korzenia = Failed, nie Warning
                Skipped.Add(rel);
                children = [];
            }
            var entries = new List<TreeEntry>(children.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in children)
            {
                var path = rel.Length == 0 ? c.Name : rel + "/" + c.Name;
                // Nazwa, której format drzewa nie przyjmie (znak „\” lub NUL, niesparowany surogat UTF-16 — NTFS na to pozwala, > 1024 B UTF-8, duplikat po stratnym dekodowaniu nazw bajtowych):
                // TreeCodec.Encode rzucałby KB-1306 i cały backup kończył się Failed przy każdym uruchomieniu. Taki element jest pomijany z ostrzeżeniem (KB-1405), reszta idzie dalej.
                if (!NameAccepted(c.Name) || !names.Add(c.Name))
                {
                    Skipped.Add(path);
                    continue;
                }
                switch (c.Kind)
                {
                    case SourceKind.Directory:
                        byte[]? parentSub = parentByName is not null && parentByName.TryGetValue(c.Name, out var pd) && pd.Kind == EntryKind.Directory ? pd.SubtreeId : null;
                        var sub = await DirectoryAsync(path, ct, parentSub).ConfigureAwait(false);
                        _dirs++;
                        entries.Add(new TreeEntry(c.Name, EntryKind.Directory, MtimeNs: c.MtimeNs, SubtreeId: sub, UnixMode: c.UnixMode));
                        break;
                    case SourceKind.Symlink:
                        entries.Add(new TreeEntry(c.Name, EntryKind.Symlink, MtimeNs: c.MtimeNs, LinkTarget: c.LinkTarget));
                        break;
                    case SourceKind.Special:
                        Skipped.Add(path); // FIFO/gniazdo/urządzenie: pominięte z ostrzeżeniem, kopia idzie dalej
                        break;
                    default:
                        if (parentByName is not null && Unchanged(c, parentByName.GetValueOrDefault(c.Name)) is { } same)
                        {
                            foreach (var id in same.Chunks ?? []) _dedup.Reference(id); // blok bazowy: bez zapisu, tylko ślad użycia
                            _files++;
                            _raw += same.Size;
                            Reused++;
                            entries.Add(new TreeEntry(c.Name, EntryKind.File, same.Size, c.MtimeNs, Chunks: same.Chunks, FileSha256: same.FileSha256, UnixMode: c.UnixMode));
                            break;
                        }
                        var f = _p.BlockParallelism > 1 && c.Size > ChunkSize
                            ? await FileParallelAsync(path, _p.BlockParallelism, ct).ConfigureAwait(false)
                            : await FileAsync(path, ct).ConfigureAwait(false);
                        if (f is null) { Skipped.Add(path); break; }
                        _files++;
                        entries.Add(new TreeEntry(c.Name, EntryKind.File, f.Value.Size, c.MtimeNs, Chunks: f.Value.Chunks, FileSha256: f.Value.Sha, UnixMode: c.UnixMode));
                        break;
                }
            }
            var tree = TreeCodec.Encode(entries);
            var treeId = _p._keys.ChunkId(tree);
            await AddBlobAsync(treeId, BlobType.Tree, tree, ct).ConfigureAwait(false);
            return treeId;
        }

        private async Task<(long Size, List<byte[]> Chunks, byte[] Sha)?> FileAsync(string rel, CancellationToken ct)
        {
            var chunks = new List<byte[]>();
            // Jeden bufor odczytu na uruchomienie (pliki są przetwarzane po kolei; blok jest kopiowany przez ToArray zanim bufor posłuży następnemu plikowi).
            // Wcześniej każdy plik alokował 4 MiB na stercie dużych obiektów — przy 50 tys. małych plików ~200 GB alokacji do wyzerowania (CPU, RSS ~900 MB).
            var buf = _fileBuf ??= new byte[ChunkSize];
            long size = 0;
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Stream s;
            try { s = _req.Source.OpenRead(rel); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            await using (s.ConfigureAwait(false))
            {
                while (true)
                {
                    int n;
                    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    try { n = await FillAsync(s, buf, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
                    _tRead += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    if (n == 0) break;
                    var block = buf.AsSpan(0, n);
                    var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    h.AppendData(block);
                    var id = _p._keys.ChunkId(block);
                    _tHash += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
                    await AddBlobAsync(id, BlobType.Data, buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    chunks.Add(id);
                    size += n;
                    if (n < buf.Length) break;
                }
            }
            _raw += size;
            return (size, chunks, h.GetHashAndReset());
        }

        private static async Task<int> FillAsync(Stream s, byte[] buf, CancellationToken ct)
        {
            var total = 0;
            while (total < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        // raw: pamięć wywołującego (bufor odczytu pliku) — używana synchronicznie, przed pierwszym await; bez kopii bloku (ToArray) także dla bloków już znanych.
        private async Task AddBlobAsync(byte[] id, BlobType type, ReadOnlyMemory<byte> raw, CancellationToken ct)
        {
            if (!_dedup.Reference(id)) return;
            if (_pack is null) StartPack();
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var (alg, payload) = _zstd.Compress(raw.Span);
            var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            _pack!.AddBlob(id, type, alg, raw.Length, payload);
            _tCompress += t1 - t0;
            _tPack += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            _new += raw.Length;
            // Pack zamykany też po liczbie ramek: format dopuszcza MaxFrames (65 536) bloków w packu, a ten sam zapis rzuciłby KB-1308 (Failed, deterministycznie przy każdym biegu)
            // dla źródeł z dziesiątkami tysięcy bardzo małych bloków (puste katalogi, drobne pliki) przy dużym packTargetSize.
            await FlushIfFullAsync(ct).ConfigureAwait(false);
        }

        private async Task FlushIfFullAsync(CancellationToken ct)
        {
            if (_pack is null || (_pack.Length < _target && _pack.BlobCount < Kobian.Format.Pack.PackLayout.MaxFrames)) return;
            var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            await FlushPackAsync(ct).ConfigureAwait(false);
            _tUpload += System.Diagnostics.Stopwatch.GetTimestamp() - t2;
        }

        private void StartPack()
        {
            _packId = RandomNumberGenerator.GetBytes(16);
            var key = _p._keys.DerivePackKey(_packId);
            try { _packCipher = new AesGcmCipher(key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            _packBuf = RentBuffer();
            _pack = new PackWriter(_packBuf, _packCipher, _p._keys.RepoId, _packId, _p._keys.CommitTag(_packId), _scratch);
        }

        public async Task FlushPackAsync(CancellationToken ct)
        {
            if (_pack is null) return;
            var entries = _pack.Finish();
            // RD-48: wysyłka wprost z bufora strumienia (bez ToArray) — szczyt pamięci przy packu 64 MiB nie podwaja się.
            // Bufor jest odłączony od pola przed wysyłką i nikt go już nie modyfikuje (PackWriter zakończony).
            var buf = _packBuf!;
            var bytes = new ReadOnlyMemory<byte>(buf.GetBuffer(), 0, checked((int)buf.Length));
            var packId = _packId!;
            _packCipher!.Dispose();
            _pack = null; _packBuf = null; _packCipher = null;
            if (_p.PackUploaders.Count == 0)
            {
                byte[] sha;
                try
                {
                    sha = await _p._up.UploadAsync(RepoPaths.Pack(packId), bytes, ct).ConfigureAwait(false);
                }
                finally
                {
                    ReturnBuffer(buf); // wysyłka zakończona (także błędem) — bufor nie jest już czytany
                }
                _newPacks.Add(new IndexPack(packId, sha, bytes.Length, [.. entries]));
                _dedup.ConfirmPending(); // I-2: potwierdzone dopiero po zweryfikowanym uploadzie
                return;
            }
            // RD-73: partia bloków TEGO packa odłączona teraz, potwierdzona po jego weryfikacji (I-2 per pack); limit wysyłek w toku z AIMD (D4.16).
            var batch = _dedup.TakePending();
            while (_inFlight.Count >= Limit() || _free.Count == 0) await HarvestAsync(waitForOne: true).ConfigureAwait(false);
            var up = _free.Pop();
            _inFlight.Add(new InFlight(up.UploadAsync(RepoPaths.Pack(packId), bytes, ct), up, packId, bytes.Length, entries, batch, buf));
        }

        private sealed record InFlight(Task<byte[]> Upload, VerifiedUploader Uploader, byte[] PackId, int Length, IReadOnlyList<PackEntry> Entries, List<ChunkKey> Batch, MemoryStream Buffer);

        private int Limit() => Math.Max(1, Math.Min(Math.Min(_p.PackUploaders.Count, _budgetInFlight), _p.Parallelism?.Current ?? int.MaxValue));

        /// <summary>RD-75: bufor z puli (wyczyszczony, pojemność zachowana) albo nowy o pojemności <see cref="PackBufferCapacity"/>.</summary>
        private MemoryStream RentBuffer()
        {
            if (_bufferPool.TryPop(out var ms))
            {
                ms.SetLength(0);
                ms.Position = 0;
                return ms;
            }
            _buffersAllocated++;
            BuffersCreated++;
            PeakBuffers = Math.Max(PeakBuffers, _buffersAllocated);
            return new MemoryStream(_bufferCapacity);
        }

        /// <summary>Zwrot bufora po zakończonej wysyłce. Bufor, który urósł ponad pojemność (nietypowo duży pack), jest zwalniany zamiast trzymany w puli.</summary>
        private void ReturnBuffer(MemoryStream ms)
        {
            if (ms.Capacity <= _bufferCapacity)
            {
                _bufferPool.Push(ms);
                return;
            }
            _buffersAllocated--;
            ms.Dispose();
        }

        /// <summary>Odbiór zakończonych wysyłek (w kolejności rozpoczęcia): potwierdzenie partii, uploader wraca do puli. Błąd → wyjątek (po zgłoszeniu sygnału AIMD).</summary>
        private async Task HarvestAsync(bool waitForOne)
        {
            if (_inFlight.Count == 0) return;
            if (waitForOne && !_inFlight.Any(f => f.Upload.IsCompleted))
                await Task.WhenAny(_inFlight.Select(f => (Task)f.Upload)).ConfigureAwait(false);
            for (var i = 0; i < _inFlight.Count;)
            {
                var f = _inFlight[i];
                if (!f.Upload.IsCompleted)
                {
                    i++;
                    continue;
                }
                _inFlight.RemoveAt(i);
                _free.Push(f.Uploader);
                byte[] sha;
                try
                {
                    sha = await f.Upload.ConfigureAwait(false);
                }
                catch (RepoWriteException ex)
                {
                    if (IsLimitSignal(ex)) _p.Parallelism?.OnLimitSignal();
                    throw;
                }
                finally
                {
                    ReturnBuffer(f.Buffer);
                }
                _p.Parallelism?.OnSuccess();
                _newPacks.Add(new IndexPack(f.PackId, sha, f.Length, [.. f.Entries]));
                _dedup.Confirm(f.Batch);
            }
        }

        private static bool IsLimitSignal(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
                if (e is StorageException { Code: KbCode.ServerSessionLimit }) return true;
            return false;
        }

        /// <summary>Wszystkie wysyłki w toku zakończone (przed indeksem/snapshotem).</summary>
        private async Task DrainAsync()
        {
            while (_inFlight.Count > 0) await HarvestAsync(waitForOne: true).ConfigureAwait(false);
        }

        /// <summary>Po błędzie: czeka na wysyłki w toku (ich błędy ignorowane — run i tak Failed), żeby nie zwalniać buforów w trakcie zapisu.</summary>
        public async Task AbandonUploadsAsync()
        {
            foreach (var f in _inFlight)
            {
                try { await f.Upload.ConfigureAwait(false); }
                catch (Exception ex) when (ex is RepoWriteException or OperationCanceledException) { /* run już przerwany */ }
                f.Buffer.Dispose();
            }
            _inFlight.Clear();
        }

        public async Task<byte[]> CommitAsync(byte[] rootTreeId, CancellationToken ct)
        {
            await DrainAsync().ConfigureAwait(false); // RD-73: indeks dopiero po weryfikacji wszystkich packów
            // I-3 na danych dokładnych: każdy referencjonowany blok jest w nowym zweryfikowanym packu albo w indeksie bazowym.
            if (!_dedup.AllReferencedConfirmed())
                    throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Referenced block not in verified pack.");
            var indexes = new List<(byte[], byte[])>();
            if (_req.Mode != SnapshotMode.Full && _req.Baseline is not null)
                indexes.AddRange(_req.Baseline.Select(b => (b.IndexId, b.Sha256)));
            if (_req.Mode != SnapshotMode.Full && _req.BaselineStream is not null)
                indexes.AddRange(_req.BaselineStream.Refs);
            var runId = _req.RunId ?? RandomNumberGenerator.GetBytes(16); // RunId z katalogu (run_runs) spina snapshot z uruchomieniem
            if (_newPacks.Count > 0)
            {
                var indexId = RandomNumberGenerator.GetBytes(16);
                var sealedIdx = MetadataStore.Seal(_p._keys, MetadataKind.Index, indexId, IndexCodec.Encode(new IndexObject(_newPacks, [], _req.HostId, runId)));
                var sha = await _p._up.UploadAsync(RepoPaths.Index(indexId), sealedIdx, ct).ConfigureAwait(false);
                indexes.Add((indexId, sha));
            }
            var snapId = RandomNumberGenerator.GetBytes(16);
            var now = (_req.Time ?? TimeProvider.System).GetUtcNow();
            var snap = new SnapshotObject(snapId, _req.HostId, _req.JobId, runId, now, _req.Mode, _req.ParentSnapId, null, rootTreeId, Stats(),
                indexes, null, [], _req.DbMeta, _req.Source is IMultiRootSource multi ? [.. multi.DisplayRoots] : [_req.Source.DisplayRoot], Skipped.Count, 0);
            var sealedSnap = MetadataStore.Seal(_p._keys, MetadataKind.Snapshot, snapId, SnapshotCodec.Encode(snap));
            await _p._up.UploadAsync(RepoPaths.Snapshot(snapId), sealedSnap, ct).ConfigureAwait(false); // ostatni zapis (D3.14 pkt 3)
            return snapId;
        }

        public void Dispose()
        {
            lock (_allCompressors) foreach (var c in _allCompressors) c.Dispose();
            _zstd.Dispose();
            _packCipher?.Dispose();
            while (_bufferPool.TryPop(out var ms)) ms.Dispose();
            _packBuf?.Dispose();
        }
    }
}
