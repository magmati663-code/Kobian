using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Tests.Unit.TestDoubles;

/// <summary>Rodzaj awarii wstrzykiwanej w N-tym zapisie (RD-2, RD-21; S5c = bez kontenerów).</summary>
public enum FaultKind
{
    /// <summary>ENOSPC przy otwarciu obiektu.</summary>
    NoSpaceOnCreate,

    /// <summary>Zerwanie po zapisaniu części danych (połowa bufora trafia na cel w trybie DirectFinal).</summary>
    DisconnectMidWrite,

    /// <summary>Zabicie procesu: wyjątek spoza klas magazynu — nic nie sprząta (połowa danych zostaje przy DirectFinal).</summary>
    ProcessKill,

    /// <summary>Cel potwierdza zapis, ale gubi ostatni bajt (cicha korupcja do wykrycia przez V1).</summary>
    SilentTruncate,
}

/// <summary>Symulacja zabicia procesu (nie jest wyjątkiem magazynu — przechodzi przez potok bez obsługi).</summary>
public sealed class SimulatedProcessKillException : Exception
{
    public SimulatedProcessKillException() : base("Simulated process kill.") { }

    public SimulatedProcessKillException(string message) : base(message) { }

    public SimulatedProcessKillException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Magazyn w pamięci zgodny z kontraktem D4.6 (bez nadpisywania, Backup nie usuwa) z opcjonalnym wstrzykiwaniem awarii
/// w N-tym wywołaniu <c>CreateAsync</c>. <paramref name="atomicRename"/> = false → DirectFinal: obiekt widoczny w trakcie zapisu.
/// </summary>
public sealed class MemoryStorage(bool atomicRename = true)
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public bool AtomicRename { get; } = atomicRename;

    public IStorageSession Open(CredentialRole role = CredentialRole.Backup, int faultAt = 0, FaultKind fault = FaultKind.NoSpaceOnCreate) =>
        new Session(this, role, faultAt, fault);

    private sealed class Session(MemoryStorage store, CredentialRole role, int faultAt, FaultKind fault) : IStorageSession
    {
        private int _writes;

        public CredentialRole Role => role;

        public ProviderCapabilities Capabilities => new(
            (store.AtomicRename ? StorageCaps.AtomicRename : StorageCaps.None) | StorageCaps.RangeRead | (role == CredentialRole.Maintenance ? StorageCaps.DeleteAllowed : StorageCaps.None),
            255, 1, 1024 * 1024, null, DateTimeOffset.UnixEpoch);

        public ServerIdentity? PresentedIdentity => null;

        public ValueTask<StatResult?> StatAsync(string path, CancellationToken ct) =>
            ValueTask.FromResult(store.Objects.TryGetValue(path, out var b) ? new StatResult(path, b.Length, null, false) : null);

        public async IAsyncEnumerable<StatResult> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var kv in store.Objects.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k.Key, StringComparer.Ordinal))
                yield return new StatResult(kv.Key, kv.Value.Length, null, false);
            await Task.CompletedTask;
        }

        public ValueTask<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct)
        {
            if (!store.Objects.TryGetValue(path, out var b)) throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Object not found.");
            var len = (int)Math.Min(length ?? long.MaxValue, b.Length - offset);
            return ValueTask.FromResult<Stream>(new MemoryStream(b, (int)offset, len, writable: false));
        }

        public ValueTask<IObjectWriter> CreateAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
        {
            if (!StoragePath.IsValid(path)) throw new StorageException(ErrorClass.Security, KbCode.InvalidObjectName, "Invalid object name.");
            if (store.Objects.ContainsKey(path)) throw new StorageException(ErrorClass.Permanent, KbCode.ObjectExists, "Object exists.");
            var n = ++_writes;
            var f = n == faultAt ? fault : (FaultKind?)null;
            if (f == FaultKind.NoSpaceOnCreate) throw new StorageException(ErrorClass.Transient, KbCode.DestinationFull, "No space left (simulated).");
            return ValueTask.FromResult<IObjectWriter>(new Writer(store, path, expectedLength, mode, f));
        }

        public ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct) => ValueTask.FromResult<long?>(null);

        public ValueTask DeleteAsync(string path, CancellationToken ct)
        {
            if (role != CredentialRole.Maintenance) throw new InvalidOperationException("Backup credential cannot delete (D4.1).");
            store.Objects.TryRemove(path, out _);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]?> ServerHashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Writer(MemoryStorage store, string path, long expected, WriteMode mode, FaultKind? fault) : IObjectWriter
    {
        private readonly MemoryStream _buf = new();

        public long CommittedOffset => _buf.Length;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (fault is FaultKind.DisconnectMidWrite or FaultKind.ProcessKill)
            {
                _buf.Write(data.Span[..(data.Length / 2)]);
                if (mode == WriteMode.DirectFinal) store.Objects[path] = _buf.ToArray(); // częściowy obiekt zostaje na celu
                if (fault == FaultKind.ProcessKill) throw new SimulatedProcessKillException();
                throw new StorageException(ErrorClass.Transient, KbCode.TransientIo, "Connection reset (simulated).");
            }
            _buf.Write(data.Span);
            if (mode == WriteMode.DirectFinal) store.Objects[path] = _buf.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<StatResult> CommitAsync(CancellationToken ct)
        {
            var bytes = _buf.ToArray();
            if (fault == FaultKind.SilentTruncate && bytes.Length > 0) bytes = bytes[..^1];
            else if (bytes.Length != expected) throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Size mismatch.");
            if (mode == WriteMode.StagedRename && !store.Objects.TryAdd(path, bytes)) throw new StorageException(ErrorClass.Permanent, KbCode.ObjectExists, "Object exists.");
            if (mode == WriteMode.DirectFinal) store.Objects[path] = bytes;
            return ValueTask.FromResult(new StatResult(path, bytes.Length, null, false));
        }

        public ValueTask AbortAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
