using System.Security.Cryptography;
using System.Text;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Repo;

namespace Kobian.Engine.Repo;

/// <summary>Kto trzyma blokadę: <c>HostId</c>/<c>InstallationId</c> = 16 B hex (bez nazw komputerów na celu — NFR-11).</summary>
public sealed record RepoLockOwner(string HostId, string InstallationId)
{
    /// <summary>Właściciel z identyfikatora hosta (<c>CatalogJobService.HostIdFor</c>) i losowej instancji procesu.</summary>
    public static RepoLockOwner For(ReadOnlySpan<byte> hostId) =>
        new(Convert.ToHexStringLower(hostId[..16]), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
}

/// <summary>Parametry dzierżaw D3.13: TTL 1800 s, odświeżanie co 5 min, odczekanie wyłącznej = 2 × odświeżanie.</summary>
public sealed record RepoLockOptions(int TtlSec = 1800, TimeSpan? Refresh = null, TimeSpan? ExclusiveSettle = null, int MaxLockFiles = 4096)
{
    public TimeSpan RefreshEvery => Refresh ?? TimeSpan.FromMinutes(5);

    public TimeSpan Settle => ExclusiveSettle ?? 2 * RefreshEvery;
}

/// <summary>Wynik przeglądu <c>locks/</c>: żywe blokady z poprawnym MAC, pliki przeterminowane (do sprzątnięcia w sesji Maintenance), pliki z błędnym MAC (KB-1201 — alert, nie blokują).</summary>
public sealed record RepoLockScan(IReadOnlyList<RepoLockInfo> Live, IReadOnlyList<string> ExpiredFiles, IReadOnlyList<string> Invalid);

/// <summary>
/// Blokady repozytorium jako dzierżawy (D3.13, PM-18, CE-18). Współdzielona (backup) i wyłączna (prune, sesja Maintenance).
/// <list type="bullet">
/// <item>Zapis tylko nowymi plikami (cele append-only, D4.1): odświeżenie = nowy plik z nowym <c>lockId</c>; stary wygasa sam, a w sesji Maintenance jest usuwany.</item>
/// <item>Blokada przeterminowana (<c>now − renewedAt &gt; ttlSec</c>) jest ignorowana; z błędnym MAC — ignorowana i raportowana (KB-1201).
/// Blokada z <c>renewedAt</c> w przyszłości (zegar innego hosta) liczy się jako żywa — błąd zegara przedłuża ochronę, nigdy jej nie skraca.</item>
/// <item>Współdzielona: odmowa przy żywej wyłącznej (przed i po zapisie własnej — KB-1202).</item>
/// <item>Wyłączna: brak żywych innych blokad → zapis własnej → odczekanie <see cref="RepoLockOptions.Settle"/> → ponowny przegląd; żywa cudza blokada = rezygnacja (KB-1202).
/// Dwie wyłączne w wyścigu obie rezygnują (bezpieczne, bez zakleszczenia na dłużej niż jedna próba).</item>
/// </list>
/// </summary>
public static class RepoLocks
{
    public static async Task<RepoLockScan> ScanAsync(IStorageSession session, RepoKeys keys, DateTimeOffset now, RepoLockOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(keys);
        options ??= new RepoLockOptions();
        var live = new List<RepoLockInfo>();
        var invalid = new List<string>();
        var expired = new List<string>();
        var seen = 0;
        await foreach (var o in session.ListAsync(RepoPaths.LocksDir, ct).ConfigureAwait(false))
        {
            if (o.IsDirectory || RepoPaths.TryParseId(o.Path, RepoPaths.LocksDir, ".lock") is not { } id) continue;
            // Limit plików (DoS przez zalanie locks/): nadmiar = repo traktowane jako zablokowane, bez czytania dalej.
            if (++seen > options.MaxLockFiles) throw new KobianException(KbCode.RepoLocked, ErrorClass.Transient, "Too many lock files on the destination.");
            if (o.Length is <= 0 or > RepoLockFile.MaxBytes) { invalid.Add(o.Path); continue; }
            byte[] raw;
            try { raw = await MetadataStore.ReadAllAsync(session, o.Path, RepoLockFile.MaxBytes, ct).ConfigureAwait(false); }
            catch (KobianException) { continue; } // zniknął między listą a odczytem (wygasły usunięty przez prune innego hosta); inne błędy odczytu przerywają przegląd

            try
            {
                var info = RepoLockFile.Verify(raw, keys, Convert.ToHexStringLower(id));
                if (now - info.RenewedAt > TimeSpan.FromSeconds(info.TtlSec)) expired.Add(o.Path);
                else live.Add(info);
            }
            catch (KobianException ex) when (ex.Code == KbCode.RepoLockInvalid)
            {
                invalid.Add(o.Path);
            }
        }
        return new RepoLockScan(live, expired, invalid);
    }

    public static async Task<RepoLease> AcquireSharedAsync(IStorageSession session, RepoKeys keys, RepoLockOwner owner, TimeProvider time, RepoLockOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        options ??= new RepoLockOptions();
        var before = await ScanAsync(session, keys, time.GetUtcNow(), options, ct).ConfigureAwait(false);
        if (before.Live.Any(l => l.Exclusive)) throw Locked();
        var lease = await RepoLease.WriteAsync(session, keys, owner, exclusive: false, time, options, ct).ConfigureAwait(false);
        var after = await ScanAsync(session, keys, time.GetUtcNow(), options, ct).ConfigureAwait(false);
        if (after.Live.Any(l => l.Exclusive))
        {
            await lease.ReleaseAsync(ct).ConfigureAwait(false);
            throw Locked();
        }
        lease.LastScan = after;
        return lease;
    }

    /// <summary>Wyłączna blokada — tylko sesja <see cref="CredentialRole.Maintenance"/> (prune). <paramref name="delay"/> = odczekanie (testy: przesunięcie zegara).</summary>
    public static async Task<RepoLease> AcquireExclusiveAsync(IStorageSession session, RepoKeys keys, RepoLockOwner owner, TimeProvider time, RepoLockOptions? options,
        Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(time);
        if (session.Role != CredentialRole.Maintenance) throw new InvalidOperationException("Exclusive repository lock requires a maintenance session (D4.1).");
        options ??= new RepoLockOptions();
        delay ??= (t, c) => Task.Delay(t, time, c);
        var before = await ScanAsync(session, keys, time.GetUtcNow(), options, ct).ConfigureAwait(false);
        if (before.Live.Count > 0) throw Locked();
        var lease = await RepoLease.WriteAsync(session, keys, owner, exclusive: true, time, options, ct).ConfigureAwait(false);
        try
        {
            await delay(options.Settle, ct).ConfigureAwait(false);
            await lease.RenewIfDueAsync(ct).ConfigureAwait(false);
            var after = await ScanAsync(session, keys, time.GetUtcNow(), options, ct).ConfigureAwait(false);
            if (after.Live.Any(l => !lease.Owns(l.LockId))) throw Locked();
            lease.LastScan = after;
            return lease;
        }
        catch
        {
            await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static KobianException Locked() => new(KbCode.RepoLocked, ErrorClass.Transient, "The repository is locked by another operation.");
}

/// <summary>Trzymana dzierżawa. <see cref="RenewIfDueAsync"/> wołaj między krokami długiej operacji; <see cref="ReleaseAsync"/> usuwa własne pliki tylko w sesji Maintenance.</summary>
public sealed class RepoLease : IAsyncDisposable
{
    private readonly IStorageSession _session;
    private readonly RepoKeys _keys;
    private readonly RepoLockOwner _owner;
    private readonly TimeProvider _time;
    private readonly RepoLockOptions _options;
    private readonly List<string> _files = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _released;
    private CancellationTokenSource? _autoCts;
    private Task? _autoTask;

    private RepoLease(IStorageSession session, RepoKeys keys, RepoLockOwner owner, bool exclusive, TimeProvider time, RepoLockOptions options, DateTimeOffset createdAt)
    {
        _session = session;
        _keys = keys;
        _owner = owner;
        Exclusive = exclusive;
        _time = time;
        _options = options;
        CreatedAt = createdAt;
    }

    public bool Exclusive { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset RenewedAt { get; private set; }

    /// <summary>Bieżący (najnowszy) identyfikator blokady.</summary>
    public string LockId { get; private set; } = "";

    /// <summary>Przegląd z chwili przyznania (np. do raportu KB-1201).</summary>
    public RepoLockScan? LastScan { get; internal set; }

    public bool Owns(string lockId) => _ids.Contains(lockId);

    internal static async Task<RepoLease> WriteAsync(IStorageSession session, RepoKeys keys, RepoLockOwner owner, bool exclusive, TimeProvider time, RepoLockOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var now = Second(time.GetUtcNow());
        var lease = new RepoLease(session, keys, owner, exclusive, time, options, now);
        await lease.WriteNewAsync(now, ct).ConfigureAwait(false);
        return lease;
    }

    /// <summary>Odświeża, gdy od ostatniego zapisu minął okres odświeżania: nowy plik (append-only); w sesji Maintenance poprzedni plik jest usuwany.</summary>
    public async Task RenewIfDueAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await RenewCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task RenewCoreAsync(CancellationToken ct)
    {
        var now = Second(_time.GetUtcNow());
        if (now - RenewedAt < _options.RefreshEvery) return;
        var old = _files[^1];
        await WriteNewAsync(now, ct).ConfigureAwait(false);
        if (_session.Role == CredentialRole.Maintenance) await TryDeleteAsync(old, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Odświeżanie w tle (długi backup). Dzierżawa musi mieć WŁASNĄ sesję (sesje są jednowątkowe — D4.10), nieużywaną przez potok.
    /// Błąd odświeżenia nie przerywa kopii: ochronę przed usunięciem świeżych packów daje też karencja 24 h od oznaczenia (D3.7).
    /// </summary>
    public void StartAutoRenew()
    {
        ObjectDisposedException.ThrowIf(_released, this);
        if (_autoTask is not null) return;
        _autoCts = new CancellationTokenSource();
        var ct = _autoCts.Token;
        _autoTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.RefreshEvery, _time, ct).ConfigureAwait(false);
                    await RenewIfDueAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is KobianException or StorageException or IOException) { /* następna próba za okres odświeżania */ }
            }
        }, CancellationToken.None);
    }

    public async Task ReleaseAsync(CancellationToken ct)
    {
        if (_released) return;
        if (_autoCts is not null)
        {
            await _autoCts.CancelAsync().ConfigureAwait(false);
            try { await _autoTask!.ConfigureAwait(false); } catch (OperationCanceledException) { /* zatrzymane */ }
            _autoCts.Dispose();
        }
        _released = true;
        try
        {
            if (_session.Role != CredentialRole.Maintenance) return; // sesja Backup nie usuwa (D4.1) — plik wygaśnie po ttlSec
            foreach (var f in _files) await TryDeleteAsync(f, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task WriteNewAsync(DateTimeOffset now, CancellationToken ct)
    {
        var id = RandomNumberGenerator.GetBytes(16);
        var hex = Convert.ToHexStringLower(id);
        var json = RepoLockFile.Create(_keys, new RepoLockInfo(hex, _owner.HostId, _owner.InstallationId, Exclusive, CreatedAt, now, _options.TtlSec));
        var path = RepoPaths.Lock(id);
        try
        {
            await new VerifiedUploader(_session).UploadAsync(path, Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
        }
        catch (RepoWriteException ex)
        {
            // Błąd celu (brak uprawnień, brak miejsca, utrata połączenia) to NIE „repozytorium zajęte”: zachowujemy kod i klasę z dostawcy — inaczej cel tylko do odczytu
            // wyglądał jak blokada innej operacji (KB-1202, przejściowy) i był ponawiany bez sensu. „Zajęte” zostaje dla kolizji/niespójnej weryfikacji zapisu.
            if (ex.InnerException is StorageException { Code: not KbCode.ObjectExists } se)
                throw new KobianException(se.Code, se.ErrorClass, "Repository lock could not be written.", ex);
            throw new KobianException(KbCode.RepoLocked, ErrorClass.Transient, "Repository lock could not be written.", ex);
        }
        _files.Add(path);
        _ids.Add(hex);
        LockId = hex;
        RenewedAt = now;
    }

    private async Task TryDeleteAsync(string path, CancellationToken ct)
    {
        try { await _session.DeleteAsync(path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is StorageException or IOException or InvalidOperationException) { /* wygaśnie po ttlSec */ }
    }

    private static DateTimeOffset Second(DateTimeOffset t) => new(t.UtcTicks - (t.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
}
