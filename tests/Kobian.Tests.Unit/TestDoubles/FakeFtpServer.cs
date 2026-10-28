using Kobian.Application.Storage;
using Kobian.Providers.Storage.Ftp;

namespace Kobian.Tests.Unit.TestDoubles;

/// <summary>
/// Serwer FTP w pamięci na poziomie portu <see cref="IFtpTransport"/> (vsftpd-podobny: STOR i RNTO NADPISUJĄ istniejący plik — jak rename(2);
/// APPE dopisuje; MLST/SIZE/REST/AVBL/HASH sterowane flagami; wstrzykiwane kody odpowiedzi i zerwania). Bez sieci i kontenerów.
/// </summary>
public sealed class FakeFtpServer : IFtpConnector
{
    internal sealed class Node
    {
        public FtpEntryKind Kind;
        public byte[] Data = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/kopie"] = new Node { Kind = FtpEntryKind.Directory } };

    public const string Root = "/kopie";

    public FtpFeatures Features { get; set; } = FtpFeatures.Mlsd | FtpFeatures.Size | FtpFeatures.Rest | FtpFeatures.Avbl;

    public long FreeBytes { get; set; } = 1L << 40;

    /// <summary>Liczba bajtów, po której zapis zrywa kanał danych (jednorazowo); -1 = brak.</summary>
    public long DropAfterBytes { get; set; } = -1;

    /// <summary>Kod odpowiedzi zwracany na STOR/APPE (np. 550, 552, 451); null = sukces.</summary>
    public int? FailUploadWithCode { get; set; }

    /// <summary>Wyjątek zgłaszany przy połączeniu (np. 530, 421 too many connections, odmowa TLS).</summary>
    public FtpProtocolException? FailConnect { get; set; }

    /// <summary>Ile pierwszych odpowiedzi SIZE po zapisie ma podać rozmiar „w trakcie zapisu” (serwer z asynchronicznym zapisem, D4.14).</summary>
    public int SizeLagReplies { get; set; }

    /// <summary>Uruchamiane tuż przed RNTO (po sprawdzeniu istnienia) — symulacja wyścigu z innym zapisem.</summary>
    public Action? BeforeRnto { get; set; }

    public int Connections { get; private set; }

    public List<CredentialRole> Roles { get; } = [];

    public List<string> Commands { get; } = [];

    public ServerIdentity Identity { get; set; } = new("tls-spki", "SHA256:TESTFAKESPKI", "fake");

    /// <summary>
    /// BL-35: walidacja TLS jak FtpsCertificatePolicy — <see cref="ChainTrusted"/> symuluje certyfikat z zaufanego urzędu (przyjęty bez pinu, chyba że
    /// require_pin), inaczej wymagany pin SPKI z opcji. Domyślnie wyłączona (dotychczasowe testy).
    /// </summary>
    public bool VerifyTls { get; set; }

    public bool ChainTrusted { get; set; }

    public bool Exists(string path)
    {
        lock (_gate) return _nodes.ContainsKey(path);
    }

    public byte[]? Read(string path)
    {
        lock (_gate) return _nodes.TryGetValue(path, out var n) && n.Kind == FtpEntryKind.File ? n.Data.ToArray() : null;
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
            _nodes[path] = new Node { Kind = FtpEntryKind.File, Data = data.ToArray() };
        }
    }

    public void PutLink(string path)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[path] = new Node { Kind = FtpEntryKind.Link };
        }
    }

    private void EnsureParents(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = "";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            cur += "/" + parts[i];
            if (!_nodes.ContainsKey(cur)) _nodes[cur] = new Node { Kind = FtpEntryKind.Directory };
        }
    }

    public ValueTask<IFtpTransport> ConnectAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        if (FailConnect is { } fail) throw fail;
        if (VerifyTls)
        {
            var opt = FtpOptions.Parse(config);
            var accepted = (ChainTrusted && !opt.RequirePin) || TlsSpkiPin.Matches(opt.SpkiPins, Identity.Fingerprint);
            if (!accepted)
                throw new FtpProtocolException(FtpFailure.TlsIdentityRejected, "FTPS certificate is not trusted and its key is not pinned.") { PresentedIdentity = Identity };
        }
        lock (_gate)
        {
            Connections++;
            Roles.Add(role);
        }
        return ValueTask.FromResult<IFtpTransport>(new Transport(this));
    }

    private void Log(string cmd)
    {
        lock (_gate) Commands.Add(cmd);
    }

    private sealed class Transport(FakeFtpServer s) : IFtpTransport
    {
        private bool _transfer;

        public ServerIdentity? Identity => s.Identity;

        public FtpFeatures Features => s.Features;

        private void Idle()
        {
            if (_transfer) throw new InvalidOperationException("transfer in progress");
        }

        public ValueTask<FtpEntry?> StatAsync(string path, CancellationToken ct)
        {
            Idle();
            s.Log("MLST " + path);
            lock (s._gate)
            {
                return ValueTask.FromResult(s._nodes.TryGetValue(path, out var n)
                    ? new FtpEntry(path[(path.LastIndexOf('/') + 1)..], n.Kind, n.Data.Length, DateTimeOffset.UnixEpoch)
                    : null);
            }
        }

        public ValueTask<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string path, CancellationToken ct)
        {
            Idle();
            s.Log("MLSD " + path);
            lock (s._gate)
            {
                var prefix = path.TrimEnd('/') + "/";
                IReadOnlyList<FtpEntry> list = s._nodes
                    .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.IndexOf('/', prefix.Length) < 0)
                    .Select(kv => new FtpEntry(kv.Key[prefix.Length..], kv.Value.Kind, kv.Value.Data.Length, DateTimeOffset.UnixEpoch))
                    .ToList();
                return ValueTask.FromResult(list);
            }
        }

        public ValueTask<long?> SizeAsync(string path, CancellationToken ct)
        {
            Idle();
            s.Log("TYPE I");
            s.Log("SIZE " + path);
            lock (s._gate)
            {
                if (!s._nodes.TryGetValue(path, out var n) || n.Kind != FtpEntryKind.File) return ValueTask.FromResult<long?>(null);
                if (s.SizeLagReplies > 0)
                {
                    s.SizeLagReplies--;
                    return ValueTask.FromResult<long?>(n.Data.Length / 2);
                }
                return ValueTask.FromResult<long?>(n.Data.Length);
            }
        }

        public ValueTask<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
        {
            Idle();
            s.Log("REST " + offset);
            s.Log("RETR " + path);
            lock (s._gate)
            {
                if (!s._nodes.TryGetValue(path, out var n)) throw new FtpProtocolException(FtpFailure.NotFound, "550", null, 550);
                _transfer = true;
                return ValueTask.FromResult<Stream>(new ClosingStream(n.Data[(int)offset..], () => _transfer = false));
            }
        }

        public ValueTask<IFtpUpload> OpenUploadAsync(string path, long resumeFrom, CancellationToken ct)
        {
            Idle();
            s.Log((resumeFrom == 0 ? "STOR " : "APPE ") + path);
            lock (s._gate)
            {
                if (s.FailUploadWithCode is { } code) throw new FtpProtocolException(Classify(code), "upload refused", null, code);
                if (resumeFrom == 0)
                {
                    // STOR: tworzy albo NADPISUJE (jak każdy serwer FTP) — ochrona po stronie klienta.
                    s.EnsureParents(path);
                    s._nodes[path] = new Node { Kind = FtpEntryKind.File };
                }
                else if (!s._nodes.TryGetValue(path, out var existing) || existing.Data.Length != resumeFrom)
                {
                    throw new FtpProtocolException(FtpFailure.Transient, "bad append", null, 451);
                }
            }
            _transfer = true;
            return ValueTask.FromResult<IFtpUpload>(new Upload(s, path, () => _transfer = false));
        }

        public ValueTask RenameNoReplaceAsync(string source, string destination, CancellationToken ct)
        {
            Idle();
            lock (s._gate)
            {
                if (s._nodes.ContainsKey(destination)) throw new FtpProtocolException(FtpFailure.AlreadyExists, "exists");
            }
            s.BeforeRnto?.Invoke();
            s.Log("RNFR " + source);
            s.Log("RNTO " + destination);
            lock (s._gate)
            {
                if (!s._nodes.TryGetValue(source, out var n)) throw new FtpProtocolException(FtpFailure.NotFound, "550", null, 550);
                if (s._nodes.ContainsKey(destination)) throw new FtpProtocolException(FtpFailure.PermissionDenied, "553 exists", null, 553); // IIS/FileZilla: odmowa
                s._nodes.Remove(source);
                s._nodes[destination] = n;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask CreateDirectoryAsync(string path, CancellationToken ct)
        {
            Idle();
            s.Log("MKD " + path);
            lock (s._gate)
            {
                if (s._nodes.ContainsKey(path)) throw new FtpProtocolException(FtpFailure.PermissionDenied, "550 exists", null, 550);
                s._nodes[path] = new Node { Kind = FtpEntryKind.Directory };
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteFileAsync(string path, CancellationToken ct)
        {
            Idle();
            s.Log("DELE " + path);
            lock (s._gate)
            {
                if (!s._nodes.Remove(path)) throw new FtpProtocolException(FtpFailure.NotFound, "550", null, 550);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<long?> FreeSpaceAsync(string path, CancellationToken ct) => ValueTask.FromResult<long?>(s.FreeBytes);

        public ValueTask<byte[]?> HashSha256Async(string path, CancellationToken ct)
        {
            lock (s._gate)
            {
                return ValueTask.FromResult(s._nodes.TryGetValue(path, out var n) ? System.Security.Cryptography.SHA256.HashData(n.Data) : null);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static FtpFailure Classify(int code) => code switch
        {
            452 or 552 => FtpFailure.NoSpace,
            550 => FtpFailure.PermissionDenied,
            553 => FtpFailure.NameNotAllowed,
            530 => FtpFailure.AuthRejected,
            _ => FtpFailure.Transient,
        };
    }

    private sealed class Upload(FakeFtpServer srv, string path, Action done) : IFtpUpload
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            lock (srv._gate)
            {
                var n = srv._nodes[path];
                if (srv.FreeBytes < data.Length) throw new FtpProtocolException(FtpFailure.NoSpace, "552", null, 552);
                if (srv.DropAfterBytes >= 0 && n.Data.Length + data.Length > srv.DropAfterBytes)
                {
                    var keep = (int)Math.Max(0, srv.DropAfterBytes - n.Data.Length);
                    n.Data = [.. n.Data, .. data.Span[..keep].ToArray()];
                    srv.DropAfterBytes = -1;
                    done();
                    throw new IOException("data connection reset");
                }
                n.Data = [.. n.Data, .. data.ToArray()];
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken ct)
        {
            done();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            done();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ClosingStream(byte[] data, Action onClose) : MemoryStream(data, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            onClose();
            base.Dispose(disposing);
        }
    }
}
