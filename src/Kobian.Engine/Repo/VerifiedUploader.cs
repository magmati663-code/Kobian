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
public sealed class VerifiedUploader(IStorageSession session, bool readBack = true)
{
    public IStorageSession Session { get; } = session ?? throw new ArgumentNullException(nameof(session));

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
        var sha = SHA256.HashData(data.Span);
        var mode = (Session.Capabilities.Caps & StorageCaps.AtomicRename) != 0 ? WriteMode.StagedRename : WriteMode.DirectFinal;
        try
        {
            var w = await Session.CreateAsync(path, data.Length, mode, 0, ct).ConfigureAwait(false);
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
