using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kobian.Tests.Unit.TestDoubles;

/// <summary>
/// Minimalny, skryptowy serwer kanału STEROWANIA FTP/FTPS na 127.0.0.1 (port efemeryczny) — tylko do testów adaptera FluentFTP bez prawdziwego serwera:
/// AUTH TLS (lub odmowa 534), Implicit TLS, USER/PASS, FEAT, MLST korzenia. Brak kanału danych (PASV) — transfery to kolejka SP-04.
/// Zapisuje odebrane komendy (z hasłem zamaskowanym) i to, czy USER/PASS przyszły przed TLS.
/// </summary>
public sealed class LoopbackFtpsControlServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly X509Certificate2? _cert;
    private readonly Task _loop;
    private readonly object _gate = new();

    public LoopbackFtpsControlServer(X509Certificate2? cert, bool implicitTls = false, bool refuseAuthTls = false, string? user = null, string? secret = null)
    {
        _cert = cert;
        ImplicitTls = implicitTls;
        RefuseAuthTls = refuseAuthTls;
        User = user;
        Secret = secret;
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool ImplicitTls { get; }

    public bool RefuseAuthTls { get; }

    public string? User { get; }

    public string? Secret { get; }

    public List<string> Commands { get; } = [];

    /// <summary>Czy USER lub PASS dotarły kanałem nieszyfrowanym.</summary>
    public bool CredentialsSentInClear { get; private set; }

    public bool PassReceived { get; private set; }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try
            {
                c = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(c));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        Stream s = client.GetStream();
        var tls = false;
        try
        {
            if (ImplicitTls)
            {
                s = await StartTlsAsync(s);
                tls = true;
            }
            await SendAsync(s, "220 loopback test server");
            while (!_cts.IsCancellationRequested)
            {
                var line = await ReadLineAsync(s);
                if (line is null) return;
                var sp = line.IndexOf(' ', StringComparison.Ordinal);
                var cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                var arg = sp < 0 ? "" : line[(sp + 1)..];
                lock (_gate) Commands.Add(cmd == "PASS" ? "PASS ***" : line);
                switch (cmd)
                {
                    case "AUTH":
                        if (RefuseAuthTls || _cert is null)
                        {
                            await SendAsync(s, "534 TLS not available");
                            break;
                        }
                        await SendAsync(s, "234 AUTH TLS OK");
                        s = await StartTlsAsync(s);
                        tls = true;
                        break;
                    case "USER":
                        if (!tls) CredentialsSentInClear = true;
                        await SendAsync(s, "331 Password required");
                        break;
                    case "PASS":
                        if (!tls) CredentialsSentInClear = true;
                        PassReceived = true;
                        await SendAsync(s, arg == Secret ? "230 Logged in" : "530 Login incorrect");
                        break;
                    case "FEAT":
                        await SendRawAsync(s, "211-Features:\r\n MLST type*;size*;modify*;\r\n SIZE\r\n REST STREAM\r\n UTF8\r\n211 End\r\n");
                        break;
                    case "SYST":
                        await SendAsync(s, "215 UNIX Type: L8");
                        break;
                    case "PWD":
                        await SendAsync(s, "257 \"/\" is current directory");
                        break;
                    case "MLST":
                        if (arg == "/kopie")
                            await SendRawAsync(s, "250-Listing /kopie\r\n type=dir;modify=20260101000000; /kopie\r\n250 End\r\n");
                        else
                            await SendAsync(s, "550 No such file");
                        break;
                    case "SIZE":
                        await SendAsync(s, "550 No such file");
                        break;
                    case "QUIT":
                        await SendAsync(s, "221 Bye");
                        return;
                    default:
                        await SendAsync(s, "200 OK");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
            // klient zerwał połączenie (np. odrzucił certyfikat) — oczekiwane w testach
        }
    }

    private async Task<Stream> StartTlsAsync(Stream s)
    {
        var ssl = new SslStream(s, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _cert,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, _cts.Token);
        return ssl;
    }

    private static Task SendAsync(Stream s, string line) => SendRawAsync(s, line + "\r\n");

    private static async Task SendRawAsync(Stream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(b);
        await s.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(Stream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 4096)
        {
            var n = await s.ReadAsync(one);
            if (n == 0) return null;
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
