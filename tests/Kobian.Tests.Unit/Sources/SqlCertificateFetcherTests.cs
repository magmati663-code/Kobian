using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql.Tls;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

/// <summary>
/// BL-40 cz. 5 (Qb32): pobranie certyfikatu TLS serwera SQL bez logowania — TDS 7.x (TLS w pakietach PRELOGIN) i TDS 8 (strict, ALPN tds/8.0),
/// instancja nazwana przez SQL Browser (SSRP), błędy KB-3001/KB-3006. Serwer zastępczy na pętli zwrotnej (S5c = bez kontenerów).
/// </summary>
public sealed class SqlCertificateFetcherTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    public async Task Prelogin_mode_returns_leaf_certificate_and_never_logs_in()
    {
        await using var srv = FakeTdsTlsServer.Start(strict: false);
        var info = await new TdsCertificateFetcher().FetchAsync($"127.0.0.1,{srv.Port}", strict: false, _ct);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(srv.Certificate.RawData)), info.Sha256Hex);
        Assert.Equal(srv.Certificate.RawData, info.RawData);
        Assert.True(info.SelfIssued);
        Assert.False(info.SystemTrusted);
        Assert.Contains("CN=kobian-sql-test", info.Subject, StringComparison.Ordinal);
        await srv.WaitIdleAsync(_ct);
        Assert.Equal(0x01, srv.ClientEncryptionOption); // ENCRYPT_ON w PRELOGIN klienta
        Assert.False(srv.ReceivedApplicationData); // brak LOGIN7 ani innych danych po TLS
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Strict_mode_uses_tls_from_first_byte_with_tds8_alpn()
    {
        await using var srv = FakeTdsTlsServer.Start(strict: true);
        var info = await new TdsCertificateFetcher().FetchAsync($"127.0.0.1,{srv.Port}", strict: true, _ct);
        Assert.Equal(srv.Certificate.RawData, info.RawData);
        await srv.WaitIdleAsync(_ct);
        Assert.Equal("tds/8.0", srv.NegotiatedAlpn);
        Assert.False(srv.ReceivedApplicationData);
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    public async Task Server_without_encryption_is_KB3006()
    {
        await using var srv = FakeTdsTlsServer.Start(strict: false, encryptionReply: 0x02);
        var e = await Assert.ThrowsAsync<KobianException>(() => new TdsCertificateFetcher().FetchAsync($"127.0.0.1,{srv.Port}", false, _ct));
        Assert.Equal(KbCode.SqlTlsUnavailable, e.Code);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Non_tds_service_is_KB3006()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync(_ct);
            var s = c.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n"), _ct);
            await Task.Delay(200, _ct);
        }, _ct);
        try
        {
            var e = await Assert.ThrowsAsync<KobianException>(() => new TdsCertificateFetcher().FetchAsync($"127.0.0.1,{port}", false, _ct));
            Assert.Equal(KbCode.SqlTlsUnavailable, e.Code);
        }
        finally
        {
            await serve;
            l.Stop();
        }
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Closed_port_is_KB3001()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var e = await Assert.ThrowsAsync<KobianException>(() => new TdsCertificateFetcher().FetchAsync($"127.0.0.1,{port}", false, _ct));
        Assert.Equal(KbCode.SqlUnreachable, e.Code);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Silent_server_times_out_as_KB3001()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var f = new TdsCertificateFetcher { Timeout = TimeSpan.FromMilliseconds(300) };
            var e = await Assert.ThrowsAsync<KobianException>(() => f.FetchAsync($"127.0.0.1,{port}", false, _ct));
            Assert.Equal(KbCode.SqlUnreachable, e.Code);
        }
        finally
        {
            l.Stop();
        }
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Named_instance_port_comes_from_sql_browser()
    {
        await using var srv = FakeTdsTlsServer.Start(strict: false);
        using var browser = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var browserPort = ((IPEndPoint)browser.Client.LocalEndPoint!).Port;
        string? asked = null;
        var answer = Task.Run(async () =>
        {
            var req = await browser.ReceiveAsync(_ct);
            asked = Encoding.ASCII.GetString(req.Buffer, 1, req.Buffer.Length - 2);
            var body = Encoding.ASCII.GetBytes($"ServerName;SRV;InstanceName;SQLEXPRESS;IsClustered;No;Version;16.0.1000.6;tcp;{srv.Port};;");
            var resp = new byte[body.Length + 3];
            resp[0] = 0x05;
            resp[1] = (byte)body.Length;
            resp[2] = (byte)(body.Length >> 8);
            body.CopyTo(resp, 3);
            await browser.SendAsync(resp, req.RemoteEndPoint, _ct);
        }, _ct);

        var info = await new TdsCertificateFetcher { BrowserPort = browserPort }.FetchAsync("127.0.0.1\\SQLEXPRESS", false, _ct);
        await answer;
        Assert.Equal("SQLEXPRESS", asked);
        Assert.Equal(srv.Certificate.RawData, info.RawData);
    }

    [Theory, Trait("BL", "BL-40")]
    [InlineData("srv", "srv", null, null)]
    [InlineData("srv,1500", "srv", 1500, null)]
    [InlineData("srv\\SQLEXPRESS", "srv", null, "SQLEXPRESS")]
    [InlineData("srv\\MSSQLSERVER", "srv", null, null)]
    [InlineData(".\\SQLEXPRESS", "localhost", null, "SQLEXPRESS")]
    [InlineData("[::1],1433", "::1", 1433, null)]
    public void Endpoint_parsing(string server, string host, int? port, string? instance)
    {
        var ep = SqlEndpoint.Parse(server);
        Assert.Equal(host, ep.Host);
        Assert.Equal(port, ep.Port);
        Assert.Equal(instance, ep.Instance);
    }

    [Theory, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    [InlineData("np:srv")]
    [InlineData("admin:srv")]
    [InlineData("srv;Encrypt=false")]
    [InlineData("srv,0")]
    [InlineData("")]
    public void Endpoint_rejects_protocol_prefixes_and_garbage(string server) =>
        Assert.Equal(KbCode.JobDefinitionInvalid, Assert.Throws<KobianException>(() => SqlEndpoint.Parse(server)).Code);

    [Fact, Trait("BL", "BL-40")]
    public void Ssrp_reply_parsing_requires_matching_instance_and_valid_port()
    {
        static byte[] R(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            return [0x05, (byte)b.Length, (byte)(b.Length >> 8), .. b];
        }
        Assert.Equal(49172, SsrpClient.ParseTcpPort(R("ServerName;S;InstanceName;SQLEXPRESS;tcp;49172;;"), "sqlexpress"));
        Assert.Null(SsrpClient.ParseTcpPort(R("ServerName;S;InstanceName;OTHER;tcp;49172;;"), "SQLEXPRESS"));
        Assert.Null(SsrpClient.ParseTcpPort(R("ServerName;S;InstanceName;SQLEXPRESS;np;\\\\S\\pipe\\sql;;"), "SQLEXPRESS"));
        Assert.Null(SsrpClient.ParseTcpPort(R("ServerName;S;InstanceName;SQLEXPRESS;tcp;70000;;"), "SQLEXPRESS"));
        Assert.Null(SsrpClient.ParseTcpPort([0x05, 0xFF, 0x00, 0x41], "SQLEXPRESS")); // długość większa niż dane
        Assert.Null(SsrpClient.ParseTcpPort([0x04, 0x00, 0x00], "SQLEXPRESS"));
    }

    [Fact, Trait("BL", "BL-40")]
    public void Prelogin_encryption_option_parsing()
    {
        Assert.Equal(0x01, TdsCertificateFetcher.ParseEncryption(TdsCertificateFetcher.PreloginPayload()));
        Assert.Equal(0x02, TdsCertificateFetcher.ParseEncryption([0x00, 0, 6, 0, 6, 0xFF, 0, 0, 0, 0, 0, 0])); // brak opcji ENCRYPTION
        Assert.Throws<InvalidDataException>(() => TdsCertificateFetcher.ParseEncryption([0x01, 0x7F, 0x00, 0, 1, 0xFF]));
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Prelogin_stream_rejects_oversized_or_foreign_packets()
    {
        var bad = new MemoryStream([0x04, 0x01, 0x00, 0x10, 0, 0, 1, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new TdsPreloginStream(bad).ReadExactlyAsync(new byte[16], _ct));
        var huge = new MemoryStream([0x12, 0x01, 0xFF, 0xFF, 0, 0, 1, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new TdsPreloginStream(huge).ReadExactlyAsync(new byte[16], _ct));
    }

    /// <summary>Zastępczy serwer SQL na pętli zwrotnej: PRELOGIN + TLS w pakietach 0x12 (albo TDS 8), certyfikat samopodpisany.</summary>
    internal sealed class FakeTdsTlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly bool _strict;
        private readonly byte _encryptionReply;
        private readonly Task _loop;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeTdsTlsServer(bool strict, byte encryptionReply, X509Certificate2 certificate)
        {
            _strict = strict;
            _encryptionReply = encryptionReply;
            Certificate = certificate;
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public X509Certificate2 Certificate { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int ClientEncryptionOption { get; private set; } = -1;
        public bool ReceivedApplicationData { get; private set; }
        public string? NegotiatedAlpn { get; private set; }

        public static FakeTdsTlsServer Start(bool strict, byte encryptionReply = 0x01) => new(strict, encryptionReply, CreateCertificate("kobian-sql-test"));

        public static X509Certificate2 CreateCertificate(string cn)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pkcs12), null);
        }

        public Task WaitIdleAsync(CancellationToken ct) => _idle.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        /// <summary>Liczba przyjętych połączeń (zatwierdzenie = drugie pobranie certyfikatu).</summary>
        public int Connections { get; private set; }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested) await ServeOneAsync();
        }

        private async Task ServeOneAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Connections++;
                var net = client.GetStream();
                Stream transport = net;
                if (!_strict)
                {
                    var header = new byte[8];
                    await net.ReadExactlyAsync(header, _stop.Token);
                    var body = new byte[((header[2] << 8) | header[3]) - 8];
                    await net.ReadExactlyAsync(body, _stop.Token);
                    ClientEncryptionOption = TdsCertificateFetcher.ParseEncryption(body);
                    byte[] reply = [0x00, 0, 11, 0, 6, 0x01, 0, 17, 0, 1, 0xFF, 16, 0, 0x03, 0xE8, 0, 0, _encryptionReply];
                    await net.WriteAsync(TdsPreloginStream.Header(0x04, reply.Length, 1), _stop.Token);
                    await net.WriteAsync(reply, _stop.Token);
                    if (_encryptionReply == 0x02) return;
                    transport = new TdsPreloginStream(net);
                }

                await using var ssl = new SslStream(transport, leaveInnerStreamOpen: true);
                var options = new SslServerAuthenticationOptions { ServerCertificate = Certificate };
                if (_strict) options.ApplicationProtocols = [new SslApplicationProtocol("tds/8.0")];
                try
                {
                    await ssl.AuthenticateAsServerAsync(options, _stop.Token);
                    NegotiatedAlpn = ssl.NegotiatedApplicationProtocol.ToString();
                    ReceivedApplicationData = await ssl.ReadAsync(new byte[1], _stop.Token) > 0;
                }
                catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or InvalidDataException)
                {
                    // klient przerwał handshake po odczytaniu certyfikatu — oczekiwane
                    if (_strict && NegotiatedAlpn is null) NegotiatedAlpn = TryAlpn(ssl);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
            {
                // nasłuch zatrzymany albo klient rozłączył się w PRELOGIN
            }
            finally
            {
                _idle.TrySetResult();
            }
        }

        private static string? TryAlpn(SslStream s)
        {
            try
            {
                return s.NegotiatedApplicationProtocol.ToString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            _stop.Dispose();
            Certificate.Dispose();
        }
    }
}
