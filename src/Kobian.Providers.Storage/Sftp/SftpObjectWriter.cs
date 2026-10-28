using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Sftp;

/// <summary>Zapis obiektu na SFTP wg D4.7: StagedRename (tmp + rename bez nadpisania) / DirectFinal (z wznowieniem).</summary>
internal sealed class SftpObjectWriter : IObjectWriter
{
    private readonly ISftpTransport _t;
    private readonly ISftpWriteHandle _h;
    private readonly string _writePath;
    private readonly string _finalPath;
    private readonly long _expected;
    private readonly WriteMode _mode;
    private readonly string _relative;
    private bool _closed;

    public SftpObjectWriter(ISftpTransport t, ISftpWriteHandle h, string writePath, string finalPath, long expected, long start, WriteMode mode, string relative)
    {
        _t = t;
        _h = h;
        _writePath = writePath;
        _finalPath = finalPath;
        _expected = expected;
        _mode = mode;
        _relative = relative;
        CommittedOffset = start;
    }

    public long CommittedOffset { get; private set; }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (CommittedOffset + data.Length > _expected)
            throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Write exceeds expected object length.");
        try
        {
            await _h.WriteAsync(data, ct).ConfigureAwait(false);
            CommittedOffset += data.Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    public async ValueTask<StatResult> CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        try
        {
            await _h.FlushAsync(ct).ConfigureAwait(false);
            await _h.DisposeAsync().ConfigureAwait(false);
            _closed = true;
            var written = await _t.LstatAsync(_writePath, ct).ConfigureAwait(false);
            if (written is null || written.Length != _expected)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Object length after write differs from expected.");
            if (_mode == WriteMode.StagedRename)
            {
                if (await _t.LstatAsync(_finalPath, ct).ConfigureAwait(false) is not null) throw SftpStorageSession.Exists();
                SftpProtocolException? renameFailure = null;
                try
                {
                    await _t.RenameNoReplaceAsync(_writePath, _finalPath, ct).ConfigureAwait(false);
                }
                catch (SftpProtocolException ex)
                {
                    renameFailure = ex;
                }
                if (renameFailure is not null)
                {
                    // Zwykły rename kończy się błędem, gdy cel istnieje — to wyścig z innym zapisem, nie przejściowy błąd.
                    if (await _t.LstatAsync(_finalPath, ct).ConfigureAwait(false) is not null) throw SftpStorageSession.Exists();
                    throw renameFailure;
                }
            }
            var fin = await _t.LstatAsync(_finalPath, ct).ConfigureAwait(false);
            if (fin is null || fin.Length != _expected)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Committed object length differs from expected.");
            return new StatResult(_relative, fin.Length, fin.Modified, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    public async ValueTask AbortAsync(CancellationToken ct)
    {
        if (!_closed)
        {
            _closed = true;
            try
            {
                await _h.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // zamknięcie uchwytu po awarii połączenia — nie maskuj pierwotnej przyczyny
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
