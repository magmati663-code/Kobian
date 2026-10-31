using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Local;

/// <summary>Zapis obiektu na dysk lokalny wg D4.7 (StagedRename / DirectFinal).</summary>
internal sealed class LocalObjectWriter : IObjectWriter
{
    private readonly FileStream _fs;
    private readonly string _writePath;
    private readonly string _finalPath;
    private readonly long _expected;
    private readonly WriteMode _mode;
    private readonly string _relative;
    private bool _closed;

    public LocalObjectWriter(FileStream fs, string writePath, string finalPath, long expected, long start, WriteMode mode, string relative)
    {
        _fs = fs;
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
            await _fs.WriteAsync(data, ct).ConfigureAwait(false);
            CommittedOffset += data.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw LocalErrorMap.Map(ex);
        }
    }

    public async ValueTask<StatResult> CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        try
        {
            await _fs.FlushAsync(ct).ConfigureAwait(false);
            _fs.Flush(flushToDisk: true);
            var length = _fs.Length;
            await _fs.DisposeAsync().ConfigureAwait(false);
            _closed = true;
            if (length != _expected)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Object length after write differs from expected.");
            if (_mode == WriteMode.StagedRename)
            {
                if (File.Exists(_finalPath)) throw LocalStorageSession.Exists();
                try
                {
                    File.Move(_writePath, _finalPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(_finalPath))
                {
                    throw LocalStorageSession.Exists();
                }
            }
            var fi = new FileInfo(_finalPath);
            if (fi.Length != _expected)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Committed object length differs from expected.");
            return new StatResult(_relative, fi.Length, fi.LastWriteTimeUtc, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw LocalErrorMap.Map(ex);
        }
    }

    public async ValueTask AbortAsync(CancellationToken ct)
    {
        if (!_closed)
        {
            await _fs.DisposeAsync().ConfigureAwait(false);
            _closed = true;
        }
        // StagedRename: plik tymczasowy jest własnym artefaktem uruchomienia — usuwany (D4.7). DirectFinal: zostaje (prune).
        if (_mode == WriteMode.StagedRename && File.Exists(_writePath) && !string.Equals(_writePath, _finalPath, StringComparison.Ordinal))
            File.Delete(_writePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed) await AbortAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
