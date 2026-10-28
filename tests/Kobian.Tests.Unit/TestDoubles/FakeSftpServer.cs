using System.Collections.Concurrent;
using Kobian.Application.Storage;
using Kobian.Providers.Storage.Sftp;

namespace Kobian.Tests.Unit.TestDoubles;

/// <summary>Serwer SFTP w pamięci (OpenSSH-podobny: rename bez nadpisania, lstat, wstrzykiwane awarie). Bez sieci i kontenerów (S5c = nie).</summary>
public sealed class FakeSftpServer : ISftpConnector
{
    internal sealed class Node
    {
        public SftpEntryKind Kind;
        public byte[] Data = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/srv/repo"] = new Node { Kind = SftpEntryKind.Directory } };

    public SftpFeatures Features { get; set; } = SftpFeatures.Fsync | SftpFeatures.StatVfs;

    public long FreeBytes { get; set; } = 1L << 40;

    /// <summary>Liczba bajtów, po której zapis zrywa połączenie (jednorazowo); -1 = brak.</summary>
    public long DropAfterBytes { get; set; } = -1;

    public SftpStatus? FailOpenWithStatus { get; set; }

    public int Connections { get; private set; }

    public int OpenSessionLimit { get; set; } = int.MaxValue;

    public int ConnectCount => Connections;

    public const string Root = "/srv/repo";

    /// <summary>Klucz hosta przedstawiany przez serwer (D4.11).</summary>
    public ServerIdentity HostIdentity { get; set; } = new("ssh-ed25519", "SHA256:TESTFAKEFINGERPRINT", "fake");

    /// <summary>
    /// BL-35: weryfikacja klucza hosta jak w SshNetSftpConnector — opcje przez <see cref="SshNetOptions.Parse"/>, klucz spoza pinów (albo tryb identity_probe)
    /// = odmowa HostKeyMismatch z przedstawionym odciskiem PRZED uwierzytelnieniem. Domyślnie wyłączona (dotychczasowe testy nie podają pinów zgodnych z atrapą).
    /// </summary>
    public bool VerifyHostKey { get; set; }

    /// <summary>Połączenia, w których „wysłano poświadczenie” (klucz hosta przyjęty).</summary>
    public int AuthenticatedConnections { get; private set; }

    public bool Exists(string path)
    {
        lock (_gate) return _nodes.ContainsKey(path);
    }

    public byte[]? Read(string path)
    {
        lock (_gate) return _nodes.TryGetValue(path, out var n) && n.Kind == SftpEntryKind.File ? n.Data.ToArray() : null;
    }

    public IReadOnlyList<string> Paths()
    {
        lock (_gate) return _nodes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public void PutFile(string path, byte[] data)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[path] = new Node { Kind = SftpEntryKind.File, Data = data.ToArray() };
        }
    }

    public void PutSymlink(string path)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[path] = new Node { Kind = SftpEntryKind.Symlink };
        }
    }

    private void EnsureParents(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = "";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            cur += "/" + parts[i];
            if (!_nodes.ContainsKey(cur)) _nodes[cur] = new Node { Kind = SftpEntryKind.Directory };
        }
    }

    public ValueTask<ISftpTransport> ConnectAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        if (VerifyHostKey)
        {
            var opt = SshNetOptions.Parse(config);
            if (opt.IdentityProbe || !opt.Pins.Contains(HostIdentity.Fingerprint, StringComparer.Ordinal))
                throw new SftpProtocolException(SftpStatus.HostKeyMismatch, "SSH host key does not match the pinned key.") { PresentedIdentity = HostIdentity };
        }
        lock (_gate)
        {
            AuthenticatedConnections++;
            if (Connections >= OpenSessionLimit) throw new SftpProtocolException(SftpStatus.SessionLimit, "channel open refused");
            Connections++;
        }
        return ValueTask.FromResult<ISftpTransport>(new Transport(this));
    }

    private sealed class Transport(FakeSftpServer s) : ISftpTransport
    {
        public ServerIdentity? Identity => s.HostIdentity;

        public SftpFeatures Features => s.Features;

        public ValueTask<SftpEntry?> LstatAsync(string path, CancellationToken ct)
        {
            lock (s._gate)
            {
                return ValueTask.FromResult(s._nodes.TryGetValue(path, out var n)
                    ? new SftpEntry(path[(path.LastIndexOf('/') + 1)..], n.Kind, n.Data.Length, DateTimeOffset.UnixEpoch)
                    : null);
            }
        }

        public ValueTask<IReadOnlyList<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct)
        {
            lock (s._gate)
            {
                var prefix = path.TrimEnd('/') + "/";
                IReadOnlyList<SftpEntry> list = s._nodes
                    .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.IndexOf('/', prefix.Length) < 0)
                    .Select(kv => new SftpEntry(kv.Key[prefix.Length..], kv.Value.Kind, kv.Value.Data.Length, DateTimeOffset.UnixEpoch))
                    .ToList();
                return ValueTask.FromResult(list);
            }
        }

        public ValueTask<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
        {
            lock (s._gate)
            {
                if (!s._nodes.TryGetValue(path, out var n)) throw new SftpProtocolException(SftpStatus.NoSuchFile, "no such file");
                return ValueTask.FromResult<Stream>(new MemoryStream(n.Data[(int)offset..], writable: false));
            }
        }

        public ValueTask<ISftpWriteHandle> OpenWriteAsync(string path, SftpOpenMode mode, long offset, CancellationToken ct)
        {
            lock (s._gate)
            {
                if (s.FailOpenWithStatus is { } st) throw new SftpProtocolException(st, "open failed");
                if (mode == SftpOpenMode.CreateNew)
                {
                    if (s._nodes.ContainsKey(path)) throw new SftpProtocolException(SftpStatus.Failure, "failure"); // OpenSSH: O_EXCL → SSH_FX_FAILURE
                    s._nodes[path] = new Node { Kind = SftpEntryKind.File };
                }
                else if (!s._nodes.TryGetValue(path, out var existing) || existing.Data.Length != offset)
                {
                    throw new SftpProtocolException(SftpStatus.Failure, "bad resume");
                }
            }
            return ValueTask.FromResult<ISftpWriteHandle>(new Handle(s, path));
        }

        public ValueTask RenameNoReplaceAsync(string source, string destination, CancellationToken ct)
        {
            lock (s._gate)
            {
                if (!s._nodes.TryGetValue(source, out var n)) throw new SftpProtocolException(SftpStatus.NoSuchFile, "no such file");
                if (s._nodes.ContainsKey(destination)) throw new SftpProtocolException(SftpStatus.Failure, "failure"); // zwykły rename: cel istnieje
                s._nodes.Remove(source);
                s._nodes[destination] = n;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask CreateDirectoryAsync(string path, CancellationToken ct)
        {
            lock (s._gate)
            {
                if (s._nodes.ContainsKey(path)) throw new SftpProtocolException(SftpStatus.Failure, "exists");
                s._nodes[path] = new Node { Kind = SftpEntryKind.Directory };
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteFileAsync(string path, CancellationToken ct)
        {
            lock (s._gate)
            {
                if (!s._nodes.Remove(path)) throw new SftpProtocolException(SftpStatus.NoSuchFile, "no such file");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<long?> FreeSpaceAsync(string path, CancellationToken ct) => ValueTask.FromResult<long?>(s.FreeBytes);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Handle(FakeSftpServer s, string path) : ISftpWriteHandle
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            lock (s._gate)
            {
                var n = s._nodes[path];
                if (s.FreeBytes < data.Length) throw new SftpProtocolException(SftpStatus.NoSpaceOnFilesystem, "no space");
                if (s.DropAfterBytes >= 0 && n.Data.Length + data.Length > s.DropAfterBytes)
                {
                    var keep = (int)Math.Max(0, s.DropAfterBytes - n.Data.Length);
                    n.Data = [.. n.Data, .. data.Span[..keep].ToArray()];
                    s.DropAfterBytes = -1;
                    throw new SftpProtocolException(SftpStatus.ConnectionLost, "connection lost");
                }
                n.Data = [.. n.Data, .. data.ToArray()];
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
