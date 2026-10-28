using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;

namespace Kobian.Engine.Repo;

public enum CheckStatus
{
    Succeeded = 0,
    Warning = 1,
    Failed = 2,
}

/// <summary>
/// Znalezisko <c>check</c>: kod KB, obiekt (Target) jako „rodzaj:identyfikator-hex” (snapshot/index/pack/tree/chunk) i — gdy dotyczy — snapshot.
/// Identyfikatory są losowe lub kluczowane HMAC; raport nie zawiera nazw plików ani treści (NFR-11).
/// </summary>
public sealed record CheckIssue(string Code, string Target, string? SnapshotId);

/// <summary>
/// Opcje <c>check</c>. <paramref name="SamplePercent"/> = 0 → tylko V2; 1–100 → dodatkowo V3 na próbce packów.
/// Próbka jest deterministyczna i rotacyjna (3.7): packi posortowane po id, kubełków = ⌈100 / %⌉, wybierany kubełek <c>Rotation mod kubełki</c> —
/// kolejne wartości <paramref name="Rotation"/> (np. numer tygodnia) pokrywają 100% packów w ⌈100 / %⌉ przebiegach.
/// </summary>
public sealed record CheckOptions(int SamplePercent = 0, long Rotation = 0, int MaxIssues = 1000)
{
    /// <summary>
    /// Próbka stabilna (V3 z harmonogramu, BL-26): kubełek packa wynika z jego identyfikatora (<see cref="RepoChecker.SampleBucket"/>), a nie z pozycji na liście —
    /// nowe packi dopisane między cyklami nie przesuwają starych, więc każdy pack obecny przez ⌈100 / %⌉ kolejnych cykli jest sprawdzony co najmniej raz.
    /// Domyślnie false (próbka pozycyjna — zachowanie <c>kobian check</c> bez zmian).
    /// </summary>
    public bool StableSample { get; init; }

    /// <summary>Packi (hex) zweryfikowane V3 we wcześniejszych cyklach — do wyznaczenia <see cref="CheckReport.FullyVerifiedSnapshots"/>; null = brak historii.</summary>
    public IReadOnlySet<string>? KnownVerifiedPacks { get; init; }
}

/// <summary>Raport <c>check</c>: <c>Level</c> = 2 (V2) lub 3 (V2 + V3), uszkodzone snapshoty (hex) i lista znalezisk (obcięta do <c>MaxIssues</c>).</summary>
public sealed record CheckReport(
    CheckStatus Status,
    int Level,
    int Snapshots,
    int Indexes,
    int Packs,
    int PacksRead,
    long BlocksVerified,
    IReadOnlyList<string> DamagedSnapshots,
    IReadOnlyList<CheckIssue> Issues,
    bool IssuesTruncated)
{
    /// <summary>Packi (hex) pobrane i sprawdzone w całości w tym przebiegu V3 bez błędu.</summary>
    public IReadOnlyList<string> VerifiedPacks { get; init; } = [];

    /// <summary>Packi (hex), których V3 w tym przebiegu się nie powiodła (SHA-256, stopka, AEAD/HMAC bloku).</summary>
    public IReadOnlyList<string> FailedPacks { get; init; } = [];

    /// <summary>Wszystkie snapshoty (hex) wylistowane na celu — porównanie z katalogiem (kopia z katalogu, której brak na celu = inny nośnik albo usunięcie).</summary>
    public IReadOnlyList<string> SnapshotsListed { get; init; } = [];

    /// <summary>Bajty pobrane przez V3 (koszt transferu na celach zdalnych — 3.7 „średni”).</summary>
    public long BytesRead { get; init; }

    /// <summary>
    /// Snapshoty (hex) nieuszkodzone, których WSZYSTKIE packi są sprawdzone V3 (w tym przebiegu albo wcześniej — <see cref="CheckOptions.KnownVerifiedPacks"/>):
    /// kandydaci do verify_level 3. Puste, gdy przebieg był tylko V2.
    /// </summary>
    public IReadOnlyList<string> FullyVerifiedSnapshots { get; init; } = [];
}

/// <summary>
/// Weryfikacja repozytorium (BL-26, KOSZT-14; 3.7 V2/V3; bez <c>--repair</c> — D8.10, rola 8). Tylko odczyt: działa na sesji <see cref="CredentialRole.Backup"/>,
/// niczego nie zapisuje ani nie usuwa.
/// <list type="bullet">
/// <item>V2 (strukturalna): każdy snapshot odczytany i uwierzytelniony; jego indeksy istnieją, są uwierzytelnione i mają SHA-256 z odwołania w snapshocie;
/// drzewa od korzenia czytane i dekodowane; każdy blok (dane, ADS, poddrzewa) jest w indeksach TEGO snapshotu (nie innego); każdy pack z indeksów istnieje na celu
/// z rozmiarem z indeksu. Obiekty bez odwołań (indeksy, packi) → ostrzeżenie KB-1504 (np. po przerwanym zadaniu — D3.14), nie błąd.</item>
/// <item>V3 (próbkowa): wybrane packi pobrane w całości — SHA-256, stopka, AEAD i HMAC każdego bloku (<see cref="RepoReader.VerifyPackAsync"/>).</item>
/// </list>
/// Snapshot jest „uszkodzony”, gdy dotyczy go znalezisko albo używa packa z błędem — wynik dla S6/D8.10 („które snapshoty są dotknięte”).
/// Dane z celu są niezaufane: limity rozmiarów obiektów i dekoderów jak w restore (SEC-3.3); lista znalezisk ograniczona (DoS).
/// PROWIZORYCZNIE: drzewa współdzielone przez snapshoty są czytane osobno dla każdego snapshotu (prostota przed wydajnością; pamięć podręczna — później, NFR do zmierzenia).
/// </summary>
public sealed class RepoChecker(IStorageSession session, RepoKeys keys)
{
    private readonly IStorageSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly RepoKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));

    public async Task<CheckReport> CheckAsync(CheckOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SamplePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(options), "SamplePercent must be 0..100.");
        if (options.MaxIssues < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxIssues must be positive.");
        var st = new State(options.MaxIssues);

        var snapIds = new List<byte[]>();
        await foreach (var o in _session.ListAsync(RepoPaths.SnapshotsDir, ct).ConfigureAwait(false))
            if (RepoPaths.TryParseId(o.Path, RepoPaths.SnapshotsDir, ".snap") is { } id) snapIds.Add(id);
        var indexFiles = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var o in _session.ListAsync(RepoPaths.IndexDir, ct).ConfigureAwait(false))
            if (RepoPaths.TryParseId(o.Path, RepoPaths.IndexDir, ".idx") is { } id) indexFiles.Add(Hex(id));
        var packFiles = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var o in _session.ListAsync(RepoPaths.DataDir, ct).ConfigureAwait(false))
            if (TryParsePackPath(o.Path) is { } hex) packFiles[hex] = o.Length;
        snapIds.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));

        var indexCache = new Dictionary<string, (IndexObject Index, byte[] Sha)?>(StringComparer.Ordinal);
        var resolver = new IndexResolver(_session, _keys, cacheObjects: false);
        var packsByHex = new SortedDictionary<string, IndexPack>(StringComparer.Ordinal);
        var packsUsedBySnapshot = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var snapshotsRead = 0;
        foreach (var snapId in snapIds)
        {
            ct.ThrowIfCancellationRequested();
            var snapHex = Hex(snapId);
            SnapshotObject snap;
            try
            {
                snap = await MetadataStore.LoadSnapshotAsync(_session, _keys, snapId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (Code(ex) is { } code)
            {
                st.Issue(code, "snapshot:" + snapHex, snapHex);
                if (!IsTransient(ex)) st.Damaged.Add(snapHex); // jak w CheckSnapshotAsync: nieczytelny snapshot jest uszkodzony (suspect w katalogu, ocena RepoDamageAssessor); chwilowa awaria sieci — nie
                continue;
            }
            snapshotsRead++;
            var used = new HashSet<string>(StringComparer.Ordinal);
            packsUsedBySnapshot[snapHex] = used;
            await CheckSnapshotAsync(resolver, snap, snapHex, indexCache, packsByHex, (hex, size, _) => ValueTask.FromResult(packFiles.TryGetValue(hex, out var l) && l == size), used, st, ct).ConfigureAwait(false);
        }

        // Każdy pack z indeksów musi istnieć z rozmiarem z indeksu (V1/V2).
        var badPacks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (hex, pk) in packsByHex)
        {
            if (!packFiles.TryGetValue(hex, out var len))
            {
                st.Issue(KbCode.CheckPackMissing, "pack:" + hex, null);
                badPacks.Add(hex);
            }
            else if (len != pk.Size)
            {
                st.Issue(KbCode.CheckPackSizeMismatch, "pack:" + hex, null);
                badPacks.Add(hex);
            }
        }

        // V3: próbka rotacyjna spośród packów, które przeszły V2.
        var packsRead = 0;
        long blocks = 0;
        long bytesRead = 0;
        var verified = new List<string>();
        var failed = new List<string>();
        if (options.SamplePercent > 0)
        {
            var buckets = Buckets(options.SamplePercent);
            var slot = Slot(options.Rotation, buckets);
            using var verifier = new RepoReader(_session, _keys);
            var i = 0;
            foreach (var (hex, pk) in packsByHex)
            {
                var bucket = options.StableSample ? SampleBucket(hex, buckets) : i % buckets;
                i++;
                if (bucket != slot || badPacks.Contains(hex)) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    bytesRead += pk.Size;
                    blocks += await verifier.VerifyPackAsync(pk, ct).ConfigureAwait(false);
                    packsRead++;
                    verified.Add(hex);
                }
                catch (Exception ex) when (Code(ex) is { } code)
                {
                    st.Issue(code, "pack:" + hex, null);
                    badPacks.Add(hex);
                    failed.Add(hex);
                }
            }
        }

        foreach (var (snapHex, used) in packsUsedBySnapshot)
            if (used.Overlaps(badPacks)) st.Damaged.Add(snapHex);

        // verify_level 3: snapshot bez znalezisk, którego każdy pack przeszedł V3 (teraz albo w poprzednich cyklach).
        var fully = new List<string>();
        if (options.SamplePercent > 0)
        {
            var ok = new HashSet<string>(verified, StringComparer.Ordinal);
            foreach (var (snapHex, used) in packsUsedBySnapshot)
            {
                if (st.Damaged.Contains(snapHex)) continue;
                if (used.All(p => ok.Contains(p) || options.KnownVerifiedPacks?.Contains(p) == true)) fully.Add(snapHex);
            }
            fully.Sort(StringComparer.Ordinal);
        }

        // Obiekty bez odwołań — tylko informacja; packi sprawdzamy wyłącznie, gdy wszystkie indeksy dało się odczytać (inaczej fałszywe „sieroty”).
        // D3.16: indeks skonsolidowany, którego zastąpione indeksy są w użyciu (albo odwrotnie: oryginał zastąpiony przez użyty indeks skonsolidowany — czeka na
        // prune po karencji) nie jest sierotą.
        var covered = new HashSet<string>(indexCache.Keys, StringComparer.Ordinal);
        if (indexFiles.Any(h => !indexCache.ContainsKey(h)))
        {
            await resolver.EnsureMapAsync(ct).ConfigureAwait(false);
            foreach (var (hex, info) in resolver.Consolidated)
            {
                if (!info.Supersedes.Keys.Any(indexCache.ContainsKey)) continue;
                covered.Add(hex);
                covered.UnionWith(info.Supersedes.Keys);
            }
        }
        foreach (var hex in indexFiles.Order(StringComparer.Ordinal))
            if (!covered.Contains(hex)) st.Issue(KbCode.CheckOrphanObject, "index:" + hex, null);
        if (indexCache.Values.All(v => v is not null) && indexFiles.All(covered.Contains))
            foreach (var hex in packFiles.Keys.Order(StringComparer.Ordinal))
                if (!packsByHex.ContainsKey(hex)) st.Issue(KbCode.CheckOrphanObject, "pack:" + hex, null);

        var status = st.Failed ? CheckStatus.Failed : st.Warned ? CheckStatus.Warning : CheckStatus.Succeeded;
        return new CheckReport(status, options.SamplePercent > 0 ? 3 : 2, snapshotsRead, indexCache.Count, packsByHex.Count, packsRead, blocks,
            st.Damaged.Order(StringComparer.Ordinal).ToList(), st.Issues, st.Truncated)
        {
            SnapshotsListed = [.. snapIds.Select(Hex)],
            VerifiedPacks = verified,
            FailedPacks = failed,
            BytesRead = bytesRead,
            FullyVerifiedSnapshots = fully,
        };
    }

    /// <summary>Liczba kubełków próbki V3 = liczba cykli pełnego pokrycia: ⌈100 / %⌉ (1–100 %).</summary>
    public static int Buckets(int samplePercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samplePercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(samplePercent, 100);
        return (100 + samplePercent - 1) / samplePercent;
    }

    /// <summary>Kubełek wybrany w cyklu <paramref name="rotation"/> (także ujemnym): rotation mod kubełki.</summary>
    public static int Slot(long rotation, int buckets) => (int)(((rotation % buckets) + buckets) % buckets);

    /// <summary>
    /// Kubełek packa w próbce stabilnej: pierwsze 8 bajtów identyfikatora (losowe 16 B — BackupPipeline) jako liczba big-endian mod kubełki.
    /// Niezależny od innych packów — dopisanie packa nie zmienia kubełków pozostałych. Identyfikator spoza formatu (nie 32 hex) → ArgumentException.
    /// </summary>
    public static int SampleBucket(string packHex, int buckets)
    {
        ArgumentNullException.ThrowIfNull(packHex);
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);
        if (packHex.Length != 32) throw new ArgumentException("Pack id must be 32 hex characters.", nameof(packHex));
        var v = ulong.Parse(packHex.AsSpan(0, 16), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
        return (int)(v % (ulong)buckets);
    }

    /// <summary>
    /// V2 jednego snapshotu — wariant „po każdym uruchomieniu” (3.7, D3.14 „V2 po commit”): snapshot, jego indeksy (SHA-256 z odwołań), drzewa i bloki jak w
    /// <see cref="CheckAsync"/>, ale bez listowania całego celu — istnienie i rozmiar sprawdzane wyłącznie dla packów, których snapshot faktycznie używa (Stat, jeden na pack).
    /// Nie zgłasza sierot (KB-1504) i nie czyta danych packów (to V3). Tylko odczyt.
    /// </summary>
    public async Task<CheckReport> CheckSnapshotAsync(byte[] snapId, CancellationToken ct, int maxIssues = 1000)
    {
        ArgumentNullException.ThrowIfNull(snapId);
        if (maxIssues < 1) throw new ArgumentOutOfRangeException(nameof(maxIssues), "maxIssues must be positive.");
        var st = new State(maxIssues);
        var snapHex = Hex(snapId);
        SnapshotObject snap;
        try
        {
            snap = await MetadataStore.LoadSnapshotAsync(_session, _keys, snapId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Code(ex) is { } code)
        {
            st.Issue(code, "snapshot:" + snapHex, snapHex);
            if (!IsTransient(ex)) st.Damaged.Add(snapHex);
            return new CheckReport(CheckStatus.Failed, 2, 0, 0, 0, 0, 0, [snapHex], st.Issues, st.Truncated);
        }
        var indexCache = new Dictionary<string, (IndexObject Index, byte[] Sha)?>(StringComparer.Ordinal);
        var packsByHex = new SortedDictionary<string, IndexPack>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var stats = new Dictionary<string, long?>(StringComparer.Ordinal);
        async ValueTask<long?> StatPack(string hex)
        {
            if (stats.TryGetValue(hex, out var known)) return known;
            var r = await _session.StatAsync(RepoPaths.Pack(Convert.FromHexString(hex)), ct).ConfigureAwait(false);
            long? len = r is { IsDirectory: false } ? r.Length : null;
            stats[hex] = len;
            return len;
        }

        await CheckSnapshotAsync(new IndexResolver(_session, _keys), snap, snapHex, indexCache, packsByHex, async (hex, size, _) => await StatPack(hex).ConfigureAwait(false) == size, used, st, ct).ConfigureAwait(false);
        var usedPacks = 0;
        foreach (var hex in used.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!packsByHex.TryGetValue(hex, out var pk)) continue; // blok zlokalizowany ⇒ pack jest w indeksie snapshotu
            usedPacks++;
            var len = await StatPack(hex).ConfigureAwait(false);
            if (len is null) st.Issue(KbCode.CheckPackMissing, "pack:" + hex, snapHex);
            else if (len != pk.Size) st.Issue(KbCode.CheckPackSizeMismatch, "pack:" + hex, snapHex);
            else continue;
            st.Damaged.Add(snapHex);
        }
        var status = st.Failed ? CheckStatus.Failed : CheckStatus.Succeeded;
        return new CheckReport(status, 2, 1, indexCache.Count, usedPacks, 0, 0, st.Damaged.Order(StringComparer.Ordinal).ToList(), st.Issues, st.Truncated);
    }

    private async Task CheckSnapshotAsync(IndexResolver resolver, SnapshotObject snap, string snapHex, Dictionary<string, (IndexObject Index, byte[] Sha)?> indexCache,
        SortedDictionary<string, IndexPack> packsByHex, Func<string, long, CancellationToken, ValueTask<bool>> packPresent, HashSet<string> used, State st, CancellationToken ct)
    {
        using var reader = new RepoReader(_session, _keys);
        var indexesOk = true;
        // D3.16: odwołania rozwiązywane przez IndexResolver — indeks zastąpiony przez konsolidację zastępuje widok indeksu skonsolidowanego (ten sam SHA-256
        // w kluczu 32, packi z klucza 33); w pamięci podręcznej (klucz = id z odwołania) skrót, z którym odwołanie musi się zgadzać.
        var pending = new List<(byte[] IndexId, byte[] Sha256)>();
        var pendingHex = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (indexId, expectedSha) in snap.Indexes)
        {
            var hex = Hex(indexId);
            if (!indexCache.ContainsKey(hex) && pendingHex.Add(hex)) pending.Add((indexId, expectedSha));
        }
        await foreach (var (pos, r, err) in resolver.ResolveAllAsync(pending, ct).ConfigureAwait(false))
        {
            var hex = Hex(pending[pos].IndexId);
            if (err is not null)
            {
                st.Issue(Code(err.SourceException) ?? KbCode.ReferenceIntegrityFailed, "index:" + hex, snapHex);
                indexCache[hex] = null;
                continue;
            }
            indexCache[hex] = (r!.Index, r.ViaConsolidation ? pending[pos].Sha256 : r.Sha256);
            foreach (var pk in r.Index.Packs) packsByHex.TryAdd(Hex(pk.PackId), pk);
        }
        foreach (var (indexId, expectedSha) in snap.Indexes)
        {
            var hex = Hex(indexId);
            if (indexCache[hex] is not { } e)
            {
                indexesOk = false;
                st.Damaged.Add(snapHex);
                continue;
            }
            if (!CryptographicOperations.FixedTimeEquals(e.Sha, expectedSha))
            {
                st.Issue(KbCode.ReferenceIntegrityFailed, "index:" + hex, snapHex);
                st.Damaged.Add(snapHex);
                indexesOk = false;
                continue;
            }
            reader.AddIndex(e.Index);
        }
        if (!indexesOk && reader.KnownBlocks == 0) return;

        var seenChunks = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<byte[]>();
        stack.Push(snap.RootTreeId);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var treeId = stack.Pop();
            var treeHex = Convert.ToHexStringLower(treeId);
            if (!seenChunks.Add(treeHex)) continue;
            if (!Locate(reader, treeId, used))
            {
                st.Issue(KbCode.CheckBlockNotIndexed, "tree:" + treeHex, snapHex);
                st.Damaged.Add(snapHex);
                continue;
            }
            // Drzewo w packu, którego brak (lub ma zły rozmiar): znalezisko zgłasza kontrola packów (KB-1501/1502) — bez wtórnego błędu odczytu.
            reader.TryLocate(treeId, out var treeLoc);
            if (!await packPresent(Hex(treeLoc!.PackId), treeLoc.PackSize, ct).ConfigureAwait(false))
            {
                st.Damaged.Add(snapHex);
                continue;
            }
            IReadOnlyList<TreeEntry> entries;
            try
            {
                entries = await reader.ReadTreeAsync(treeId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (Code(ex) is { } code)
            {
                st.Issue(code, "tree:" + treeHex, snapHex);
                st.Damaged.Add(snapHex);
                continue;
            }
            foreach (var en in entries)
            {
                if (en.SubtreeId is { } sub) stack.Push(sub);
                foreach (var c in en.Chunks ?? []) CheckChunk(reader, c, snapHex, seenChunks, used, st);
                foreach (var ads in en.Ads ?? [])
                    foreach (var c in ads.Chunks) CheckChunk(reader, c, snapHex, seenChunks, used, st);
            }
        }
    }

    private static void CheckChunk(RepoReader reader, byte[] chunkId, string snapHex, HashSet<string> seen, HashSet<string> used, State st)
    {
        var hex = Convert.ToHexStringLower(chunkId);
        if (!seen.Add(hex)) return;
        if (Locate(reader, chunkId, used)) return;
        st.Issue(KbCode.CheckBlockNotIndexed, "chunk:" + hex, snapHex);
        st.Damaged.Add(snapHex);
    }

    private static bool Locate(RepoReader reader, byte[] chunkId, HashSet<string> used)
    {
        if (!reader.TryLocate(chunkId, out var loc) || loc is null) return false;
        used.Add(Hex(loc.PackId));
        return true;
    }

    /// <summary>Kod KB błędu odczytu obiektu z celu; null = błąd nieoczekiwany (propaguje, nie jest maskowany jako „uszkodzenie”).</summary>
    /// <summary>Awaria przejściowa (timeout, utrata połączenia) nie dowodzi uszkodzenia snapshotu — nie oznaczamy go jako uszkodzonego/suspect.</summary>
    private static bool IsTransient(Exception ex) => ex is KobianException { ErrorClass: ErrorClass.Transient } or StorageException { IsTransient: true };

    private static string? Code(Exception ex) => ex switch
    {
        KobianException k => k.Code,
        StorageException s => s.Code,
        _ => null,
    };

    /// <summary><c>data/&lt;pp&gt;/&lt;32 hex&gt;.pack</c> z <c>pp</c> = pierwsze 2 znaki id; inne nazwy są ignorowane (np. pliki tymczasowe).</summary>
    internal static string? TryParsePackPath(string path)
    {
        var prefix = RepoPaths.DataDir + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length != prefix.Length + 3 + 32 + 5) return null;
        var hex = path.Substring(prefix.Length + 3, 32);
        if (path[prefix.Length + 2] != '/' || !path.EndsWith(".pack", StringComparison.Ordinal) || path.Substring(prefix.Length, 2) != hex[..2]) return null;
        foreach (var c in hex)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return null;
        return hex;
    }

    private static string Hex(byte[] id) => Convert.ToHexStringLower(id);

    private sealed class State(int maxIssues)
    {
        public List<CheckIssue> Issues { get; } = [];

        public HashSet<string> Damaged { get; } = new(StringComparer.Ordinal);

        public bool Failed { get; private set; }

        public bool Warned { get; private set; }

        public bool Truncated { get; private set; }

        public void Issue(string code, string obj, string? snapshot)
        {
            if (code == KbCode.CheckOrphanObject) Warned = true;
            else Failed = true;
            if (Issues.Count >= maxIssues)
            {
                Truncated = true;
                return;
            }
            Issues.Add(new CheckIssue(code, obj, snapshot));
        }
    }
}
