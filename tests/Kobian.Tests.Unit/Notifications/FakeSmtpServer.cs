using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kobian.Tests.Unit.Notifications;

/// <summary>
/// Minimalny serwer SMTP in-proc (BL-45) na 127.0.0.1 — wyłącznie do testów protokołu adaptera MailKit: EHLO, opcjonalnie STARTTLS (certyfikat
/// samopodpisany, niezaufany — klient MUSI go odrzucić), MAIL/RCPT/DATA/QUIT. Jedno połączenie; linie ≤ 8 KiB, treść ≤ 1 MiB.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly bool _offerStartTls;
    private readonly X509Certificate2? _cert;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeSmtpServer(bool offerStartTls)
    {
        _offerStartTls = offerStartTls;
        if (offerStartTls) _cert = SelfSigned();
        _listener.Start();
        _loop = Task.Run(() => ServeAsync(_cts.Token));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string? MailFrom { get; private set; }

    public List<string> RcptTo { get; } = [];

    public List<string> Commands { get; } = [];

    public string? Data { get; private set; }

    public bool TlsHandshakeFailed { get; private set; }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            Stream s = client.GetStream();
            await WriteAsync(s, "220 localhost ESMTP test\r\n", ct);
            while (true)
            {
                var line = await ReadLineAsync(s, 8192, ct);
                if (line is null) return;
                Commands.Add(line);
                var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                switch (cmd)
                {
                    case "EHLO":
                        await WriteAsync(s, _offerStartTls && s is not SslStream ? "250-localhost\r\n250-STARTTLS\r\n250 8BITMIME\r\n" : "250-localhost\r\n250 8BITMIME\r\n", ct);
                        break;
                    case "HELO":
                        await WriteAsync(s, "250 localhost\r\n", ct);
                        break;
                    case "STAR":
                        await WriteAsync(s, "220 ready\r\n", ct);
                        var ssl = new SslStream(s, leaveInnerStreamOpen: false);
                        try
                        {
                            await ssl.AuthenticateAsServerAsync(_cert!, false, false);
                        }
                        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
                        {
                            TlsHandshakeFailed = true;
                            await ssl.DisposeAsync();
                            return;
                        }
                        s = ssl;
                        break;
                    case "MAIL":
                        MailFrom = line;
                        await WriteAsync(s, "250 OK\r\n", ct);
                        break;
                    case "RCPT":
                        RcptTo.Add(line);
                        await WriteAsync(s, "250 OK\r\n", ct);
                        break;
                    case "DATA":
                        await WriteAsync(s, "354 end with .\r\n", ct);
                        var sb = new StringBuilder();
                        while (true)
                        {
                            var l = await ReadLineAsync(s, 8192, ct);
                            if (l is null || l == ".") break;
                            sb.Append(l).Append("\r\n");
                            if (sb.Length > 1024 * 1024) return;
                        }
                        Data = sb.ToString();
                        await WriteAsync(s, "250 queued\r\n", ct);
                        break;
                    case "QUIT":
                        await WriteAsync(s, "221 bye\r\n", ct);
                        return;
                    default:
                        await WriteAsync(s, "250 OK\r\n", ct);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // koniec połączenia
        }
    }

    private static async Task WriteAsync(Stream s, string text, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes(text), ct);
        await s.FlushAsync(ct);
    }

    private static async Task<string?> ReadLineAsync(Stream s, int max, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < max)
        {
            if (await s.ReadAsync(one, ct) == 0) return null;
            if (one[0] == '\n')
            {
                if (buf.Count > 0 && buf[^1] == '\r') buf.RemoveAt(buf.Count - 1);
                return Encoding.UTF8.GetString([.. buf]);
            }
            buf.Add(one[0]);
        }
        return null;
    }

    public async Task WaitAsync(TimeSpan timeout) => await _loop.WaitAsync(timeout);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
        _cts.Dispose();
        _cert?.Dispose();
    }
}
