using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Engine.Repo;

/// <summary>Wyjątek klasy Repo-Write (D3.14): każda awaria zapisu/weryfikacji obiektu na celu.</summary>
public sealed class RepoWriteException : Exception
{
    public RepoWriteException(string message, Exception? inner = null) : base(message, inner) { }

    public RepoWriteException() : base("Repository write failed.") { }

    public RepoWriteException(string message) : base(message) { }
}

/// <summary>
/// Zapis obiektu z weryfikacją (D4.14): StagedRename, gdy cel ma atomowy rename, inaczej DirectFinal; po zatwierdzeniu
/// Stat (rozmiar, V1-a) i — domyślnie — odczyt kontrolny z porównaniem SHA-256 (V1-b pełny). Każdy błąd → <see cref="RepoWriteException"/>.
/// </summary>
public sealed class VerifiedUploader(IStorageSession session, bool readBack = true) : IAsyncDisposable
{
    private IStorageSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly List<IStorageSession> _reopened = [];

    /// <summary>Bieżąca sesja: pierwotna albo (po zerwaniu połączenia) nowa z <see cref="Reopen"/>.</summary>
    public IStorageSession Session => _session;

    /// <summary>
    /// Otwarcie świeżej sesji na ten sam cel (jak pierwotna). Ustawione = błąd PRZEJŚCIOWY wysyłki (zerwane połączenie, przekroczony czas) nie kończy całego przebiegu:
    /// po opóźnieniu wysyłka packa jest ponawiana na nowej sesji, do <see cref="MaxAttempts"/> prób. Błąd trwały i niezgodność skrótu (V1-a/V1-b) nigdy nie są ponawiane.
    /// </summary>
    public Func<CancellationToken, ValueTask<IStorageSession>>? Reopen { get; init; }

    public int MaxAttempts { get; init; } = 4;

    /// <summary>Opóźnienie przed ponowieniem nr n (1-based); domyślnie 2 s · 3^(n−1) ±25% (2 s, 6 s, 18 s).</summary>
    public Func<int, CancellationToken, Task> Delay { get; init; } = DefaultDelayAsync;

    /// <summary>Ile razy wysyłka była ponawiana w tym uploaderze (diagnostyka, testy).</summary>
    public int Retries { get; private set; }

    private static Task DefaultDelayAsync(int retry, CancellationToken ct)
    {
        var baseMs = 2000.0 * Math.Pow(3, retry - 1);
        var jitter = 0.75 + (RandomNumberGenerator.GetInt32(0, 1000) / 2000.0); // 0,75…1,25 (RandomNumberGenerator: polityka zakazanych API)
        return Task.Delay(TimeSpan.FromMilliseconds(baseMs * jitter), ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _reopened)
        {
#pragma warning disable CA1031 // zamknięcie porzuconej sesji nie może przesłonić wyniku kopii
            try { await r.DisposeAsync().ConfigureAwait(false); }
            catch { /* sesja i tak porzucona */ }
#pragma warning restore CA1031
        }
        _reopened.Clear();
    }

    private static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            // Pełny dysk celu jest „przejściowy” dla wykonawcy przebiegów (miejsce można zwolnić), ale ponawianie tego packa po kilku sekundach nic nie da.
            if (e is StorageException se) return se.IsTransient && se.Code != KbCode.DestinationFull;
            if (e is IOException or TimeoutException or System.Net.Sockets.SocketException) return true;
        }
        return false;
    }

    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) if (e is StorageException { Code: KbCode.ObjectExists }) return true;
        return false;
    }

    public Task<byte[]> UploadAsync(string path, byte[] data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        return UploadAsync(path, new ReadOnlyMemory<byte>(data), ct);
    }

    /// <summary>
    /// Wariant bez kopiowania bufora (RD-48): wywołujący gwarantuje, że <paramref name="data"/> nie zmienia się do zakończenia
    /// wywołania (np. wycinek <c>MemoryStream.GetBuffer()</c> zamkniętego packu). Weryfikacja V1-a/V1-b bez zmian.
    /// </summary>
    public async Task<byte[]> UploadAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await UploadOnceAsync(path, data, attempt > 1, ct).ConfigureAwait(false);
            }
            catch (RepoWriteException ex) when (Reopen is not null && attempt < MaxAttempts && !ct.IsCancellationRequested && IsTransient(ex))
            {
                Retries++;
                await Delay(attempt, ct).ConfigureAwait(false);
                IStorageSession fresh;
                try
                {
                    fresh = await Reopen(ct).ConfigureAwait(false);
                }
                catch (Exception reopenEx) when (reopenEx is StorageException or IOException or UnauthorizedAccessException)
                {
                    throw new RepoWriteException("Repository write failed (reconnect failed).", ex); // pierwotny błąd; kolejna próba nie ma sesji
                }
                _reopened.Add(fresh);
                _session = fresh;
            }
        }
    }

    private async Task<byte[]> UploadOnceAsync(string path, ReadOnlyMemory<byte> data, bool retried, CancellationToken ct)
    {
        var sha = SHA256.HashData(data.Span);
        var mode = (Session.Capabilities.Caps & StorageCaps.AtomicRename) != 0 ? WriteMode.StagedRename : WriteMode.DirectFinal;
        try
        {
            IObjectWriter w;
            try
            {
                w = await Session.CreateAsync(path, data.Length, mode, 0, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (retried && IsAlreadyExists(ex))
            {
                // Poprzednia próba mogła zapisać cały obiekt, a zerwać się przed potwierdzeniem: obiekt o tej samej długości i skrócie jest naszym obiektem (cel nie nadpisuje).
                var existing = await Session.StatAsync(path, ct).ConfigureAwait(false);
                if (existing is not null && existing.Length == data.Length)
                {
                    await using var rs = await Session.OpenReadAsync(path, 0, data.Length, ct).ConfigureAwait(false);
                    if (CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(rs, ct).ConfigureAwait(false), sha)) return sha;
                }
                throw;
            }
            await using (w.ConfigureAwait(false))
            {
                try
                {
                    await w.WriteAsync(data, ct).ConfigureAwait(false);
                    await w.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    try { await w.AbortAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is StorageException or IOException) { /* sprzątanie best-effort; obiekt-sierota usuwa prune */ }
                    throw;
                }
            }
            var st = await Session.StatAsync(path, ct).ConfigureAwait(false);
            if (st is null || st.Length != data.Length) throw new RepoWriteException("Uploaded object size mismatch (V1-a).");
            if (readBack)
            {
                await using var s = await Session.OpenReadAsync(path, 0, data.Length, ct).ConfigureAwait(false);
                var got = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(got, sha)) throw new RepoWriteException("Uploaded object hash mismatch (V1-b).");
            }
            return sha;
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException or KobianException)
        {
            throw new RepoWriteException("Repository write failed.", ex);
        }
    }
}
