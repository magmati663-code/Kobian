using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Ftp;

/// <summary>
/// Zapis obiektu na FTP wg D4.7/D4.14: StagedRename (STOR do tmp + RNTO poprzedzone sprawdzeniem istnienia) / DirectFinal (STOR, wznowienie przez APPE).
/// Odpowiedź 226 nie jest dowodem zapisu — po zamknięciu kanału danych rozmiar sprawdza SIZE w TYPE I (z ponowieniem).
/// </summary>
internal sealed class FtpObjectWriter : IObjectWriter
{
    private readonly FtpStorageSession _s;
    private readonly IFtpUpload _up;
    private readonly string _writePath;
    private readonly string _finalPath;
    private readonly long _expected;
    private readonly WriteMode _mode;
    private readonly string _relative;
    private bool _closed;

    public FtpObjectWriter(FtpStorageSession s, IFtpUpload up, string writePath, string finalPath, long expected, long start, WriteMode mode, string relative)
    {
        _s = s;
        _up = up;
        _writePath = writePath;
        _finalPath = finalPath;
        _expected = expected;
        _mode = mode;
        _relative = relative;
        CommittedOffset = start;
    }

    /// <summary>Bajty przyjęte przez kanał danych (FTP nie ma potwierdzeń na poziomie bajtów — D4.13); przy wznowieniu sesja i tak sprawdza SIZE.</summary>
    public long CommittedOffset { get; private set; }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (CommittedOffset + data.Length > _expected)
            throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Write exceeds expected object length.");
        try
        {
            await _up.WriteAsync(data, ct).ConfigureAwait(false);
            CommittedOffset += data.Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw FtpErrorMap.Map(ex);
        }
    }

    public async ValueTask<StatResult> CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var t = _s.Transport;
        try
        {
            _closed = true;
            try
            {
                await _up.CompleteAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await _up.DisposeAsync().ConfigureAwait(false);
            }
            await _s.SizeWithRetryAsync(_writePath, _expected, ct).ConfigureAwait(false);
            if (_mode == WriteMode.StagedRename)
            {
                if (await t.StatAsync(_finalPath, ct).ConfigureAwait(false) is not null) throw FtpStorageSession.Exists();
                FtpProtocolException? renameFailure = null;
                try
                {
                    await t.RenameNoReplaceAsync(_writePath, _finalPath, ct).ConfigureAwait(false);
                }
                catch (FtpProtocolException ex)
                {
                    renameFailure = ex;
                }
                if (renameFailure is not null)
                {
                    // Serwery odrzucające RNTO na istniejący cel (IIS, FileZilla Server) — to wyścig z innym zapisem, nie błąd przejściowy.
                    if (await t.StatAsync(_finalPath, ct).ConfigureAwait(false) is not null) throw FtpStorageSession.Exists();
                    throw renameFailure;
                }
            }
            var fin = await t.SizeAsync(_finalPath, ct).ConfigureAwait(false);
            if (fin != _expected)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Committed object length differs from expected.");
            var st = await t.StatAsync(_finalPath, ct).ConfigureAwait(false);
            return new StatResult(_relative, _expected, st?.Modified, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw FtpErrorMap.Map(ex);
        }
    }

    public async ValueTask AbortAsync(CancellationToken ct)
    {
        if (!_closed)
        {
            _closed = true;
            try
            {
                await _up.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // zamknięcie kanału danych po awarii połączenia — nie maskuj pierwotnej przyczyny
            }
        }
        // StagedRename: tmp to artefakt tego uruchomienia; sesja Backup nie usuwa (D4.1) — porządkuje prune/Maintenance.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed) await AbortAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
