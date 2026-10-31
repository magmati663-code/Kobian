using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;
using Kobian.Providers.Storage.Sftp;
using Kobian.Tests.Unit.Storage;
using Kobian.Tests.Unit.TestDoubles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-35 (D4.11, SEC-14.5, AC-12.2): podłączenie katalogu tożsamości do otwierania sesji SFTP/FTPS w Agencie — pin z katalogu, nie z definicji;
/// serwer z innym kluczem → KB-2104/KB-2109 i odcisk oczekujący (bez wysłania poświadczenia); zatwierdzenie Administratora → sesja działa;
/// pin z definicji przenoszony tylko przy braku pinu (kompatybilność wstecz). Atrapy w pamięci + prawdziwy TLS na 127.0.0.1 (LoopbackFtpsControlServer).
/// </summary>
public sealed class ServerIdentityGateTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-gate-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private ServerIdentityStore _store = null!;
    private const string Admin = "S-1-5-21-1000-2000-3000-500";

    private string Catalog => Path.Combine(_dir, "catalog.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _store = new ServerIdentityStore(Catalog, _writer, TimeProvider.System);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static ServerIdentity Ssh(string fp) => new("ssh-ed25519", fp, "ssh-ed25519");

    private CatalogAgentBackend Backend() => new(Catalog, "t") { Identities = _store };

    private async Task<ApproveServerIdentityReply> Decide(string dest, string fp, ServerIdentityDecision d) =>
        await Backend().ApproveServerIdentityAsync(new ApproveServerIdentityArgs { DestinationId = dest, Fingerprint = fp, Decision = d }, Admin, _ct);

    private static DestinationRequest SftpReq(string? hostKey) => new("sftp", FakeSftpServer.Root, "backup.example.test", null, "file:s1", hostKey);

    [Fact, Trait("D", "D4.11")]
    public async Task Wrong_pin_in_a_failed_first_attempt_is_not_stored_so_the_corrected_definition_works()
    {
        // Znalezione testem na żywym serwerze WebDAV: pomyłka w odcisku przy pierwszym „init” zapisywała zły pin jako zatwierdzony i blokowała cel.
        var k1 = ServerIdentityMachineTests.Fp();
        var wrong = ServerIdentityMachineTests.Fp();
        var srv = new FakeSftpServer { VerifyHostKey = true, HostIdentity = Ssh(k1) };
        var gate = new ServerIdentityGate(new SftpStorageProviderFactory(srv), _store);
        var dest = JobDestination.Id("sftp", SftpReq(k1).Root, JobDestination.Options(SftpReq(k1)));

        var ex = await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(JobDestination.ToConfig("job-1", SftpReq(wrong)), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.HostKeyMismatch, ex.Code);
        Assert.DoesNotContain(_store.Load(dest), r => r.IsEffectivePin);

        await (await gate.OpenAsync(JobDestination.ToConfig("job-1", SftpReq(k1)), CredentialRole.Backup, _ct)).DisposeAsync();
        Assert.Contains(_store.Load(dest), r => r.IsEffectivePin && r.Fingerprint == k1);
    }

    [Fact, Trait("D", "D4.11"), Trait("KB", "KB-2104"), Trait("AC", "AC-04.1")]
    public async Task Sftp_key_change_stops_the_session_until_an_administrator_approves_the_exact_new_key()
    {
        var k1 = ServerIdentityMachineTests.Fp();
        var k2 = ServerIdentityMachineTests.Fp();
        var srv = new FakeSftpServer { VerifyHostKey = true, HostIdentity = Ssh(k1) };
        var gate = new ServerIdentityGate(new SftpStorageProviderFactory(srv), _store);
        var req = SftpReq(k1);
        var cfg = JobDestination.ToConfig("job-1", req);
        var dest = JobDestination.Id("sftp", req.Root, JobDestination.Options(req));

        // Istniejące zadanie z pinem w definicji: działa (pin przeniesiony do katalogu jako zatwierdzony).
        await (await gate.OpenAsync(cfg, CredentialRole.Backup, _ct)).DisposeAsync();
        var seeded = Assert.Single(_store.Load(dest));
        Assert.Equal((k1, IdentityState.Pinned, ServerIdentityMachine.DefinitionActor), (seeded.Fingerprint, seeded.State, seeded.DecidedBy));

        // Serwer przedstawia inny klucz (reinstalacja albo MITM) → KB-2104, poświadczenie nie wysłane, nowy klucz oczekuje, stary pin zostaje.
        srv.HostIdentity = Ssh(k2);
        var auth = srv.AuthenticatedConnections;
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(cfg, CredentialRole.Backup, _ct));
        Assert.Equal((KbCode.HostKeyMismatch, ErrorClass.Security), (ex.Code, ex.ErrorClass));
        Assert.Equal(auth, srv.AuthenticatedConnections);
        var rows = _store.Load(dest);
        Assert.Equal(IdentityState.Pending, rows.Single(r => r.Fingerprint == k2).State);
        Assert.Equal([k1], ServerIdentityMachine.EffectivePins(rows));
        // Kolejne uruchomienia — nadal KB-2104 (Mismatch → Mismatch), bez nowych wierszy.
        await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(cfg, CredentialRole.Maintenance, _ct));
        Assert.Equal(2, _store.Load(dest).Count);

        // Zatwierdzić można tylko dokładnie ten odcisk.
        var wrong = await Decide(dest, ServerIdentityMachineTests.Fp(), ServerIdentityDecision.Replace);
        Assert.Equal((OverallState.Failed, KbCode.ServerIdentityNotPending), (wrong.Result, wrong.Code));
        var ok = await Decide(dest, k2, ServerIdentityDecision.Replace);
        Assert.Equal((OverallState.Ok, Kobian.Api.V1.ServerIdentityState.Pinned), (ok.Result, ok.State));

        // Ta sama definicja (stary hostKey k1) działa — pin pochodzi z katalogu; k1 wycofany.
        await (await gate.OpenAsync(cfg, CredentialRole.Backup, _ct)).DisposeAsync();
        Assert.Equal(IdentityState.Retired, _store.Load(dest).Single(r => r.Fingerprint == k1).State);
        // Powrót starego klucza (np. przywrócony serwer z kopii) wymaga ponownego zatwierdzenia.
        srv.HostIdentity = Ssh(k1);
        await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(cfg, CredentialRole.Backup, _ct));
        Assert.Equal(IdentityState.Pending, _store.Load(dest).Single(r => r.Fingerprint == k1).State);
    }

    [Fact, Trait("D", "D4.11"), Trait("SEC", "SEC-4.1")]
    public async Task Operator_editing_hostKey_in_the_job_definition_cannot_replace_the_approved_pin()
    {
        var k1 = ServerIdentityMachineTests.Fp();
        var attacker = ServerIdentityMachineTests.Fp();
        var srv = new FakeSftpServer { VerifyHostKey = true, HostIdentity = Ssh(k1) };
        var gate = new ServerIdentityGate(new SftpStorageProviderFactory(srv), _store);
        await (await gate.OpenAsync(JobDestination.ToConfig("job", SftpReq(k1)), CredentialRole.Backup, _ct)).DisposeAsync();

        srv.HostIdentity = Ssh(attacker); // serwer podstawiony i definicja zmieniona na jego klucz
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(JobDestination.ToConfig("job", SftpReq(attacker)), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.HostKeyMismatch, ex.Code);
        var dest = JobDestination.Id("sftp", FakeSftpServer.Root, JobDestination.Options(SftpReq(null)));
        Assert.Equal([k1], ServerIdentityMachine.EffectivePins(_store.Load(dest)));
    }

    [Fact, Trait("D", "D4.11"), Trait("BL", "BL-60")]
    public async Task Sftp_first_contact_without_any_pin_records_pending_via_identity_probe_without_credentials()
    {
        var k = ServerIdentityMachineTests.Fp();
        var srv = new FakeSftpServer { VerifyHostKey = true, HostIdentity = Ssh(k) };
        var providers = ServerIdentityGate.Wrap(new Dictionary<string, IStorageProviderFactory> { ["sftp"] = new SftpStorageProviderFactory(srv) }, _store);
        var probe = new DestinationProbeService(providers);
        var r = await probe.ProbeAsync(SftpReq(null), _ct);
        Assert.Equal((DestinationProbeStatus.UnknownIdentity, k), (r.Status, r.Identity!.Fingerprint));
        Assert.Equal(0, srv.AuthenticatedConnections);
        var dest = JobDestination.Id("sftp", FakeSftpServer.Root, JobDestination.Options(SftpReq(null)));
        Assert.Equal(IdentityState.Pending, Assert.Single(_store.Load(dest)).State);

        Assert.Equal(OverallState.Ok, (await Decide(dest, k, ServerIdentityDecision.Add)).Result);
        var ok = await probe.ProbeAsync(SftpReq(null), _ct); // bez odcisku w żądaniu — pin z katalogu
        Assert.Equal(DestinationProbeStatus.Ok, ok.Status);
        Assert.Equal(1, srv.AuthenticatedConnections);
    }

    [Fact, Trait("AC", "AC-12.2"), Trait("D", "D4.11")]
    public async Task Two_approved_pins_alternate_without_mismatch_and_a_third_key_stops()
    {
        var a = ServerIdentityMachineTests.Fp();
        var b = ServerIdentityMachineTests.Fp();
        var srv = new FakeFtpServer { VerifyTls = true, Identity = new ServerIdentity("tls-spki", a, "node-a") };
        var gate = new ServerIdentityGate(new FtpStorageProviderFactory(srv), _store);
        var req = new DestinationRequest("ftps", FakeFtpServer.Root, "nas.example.test", null, "file:f1", a);
        var cfg = JobDestination.ToConfig("job", req);
        var dest = JobDestination.Id("ftps", req.Root, JobDestination.Options(req));
        await (await gate.OpenAsync(cfg, CredentialRole.Backup, _ct)).DisposeAsync();
        srv.Identity = new ServerIdentity("tls-spki", b, "node-b");
        Assert.Equal(KbCode.TlsIdentityMismatch, (await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(cfg, CredentialRole.Backup, _ct))).Code);
        Assert.Equal(OverallState.Ok, (await Decide(dest, b, ServerIdentityDecision.Add)).Result);

        for (var i = 0; i < 20; i++)
        {
            srv.Identity = new ServerIdentity("tls-spki", i % 2 == 0 ? a : b, "node");
            await (await gate.OpenAsync(cfg, CredentialRole.Backup, _ct)).DisposeAsync();
        }
        Assert.DoesNotContain(_store.Load(dest), r => r.State == IdentityState.Pending); // 0 zdarzeń Mismatch
        srv.Identity = new ServerIdentity("tls-spki", ServerIdentityMachineTests.Fp(), "node-c");
        Assert.Equal(KbCode.TlsIdentityMismatch, (await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(cfg, CredentialRole.Backup, _ct))).Code);
    }

    [Fact, Trait("SEC", "SEC-14.5"), Trait("D", "D4.11")]
    public async Task Ftps_with_valid_ca_chain_records_spki_change_as_information_unless_the_destination_is_high_risk()
    {
        var first = ServerIdentityMachineTests.Fp();
        var renewed = ServerIdentityMachineTests.Fp();
        var srv = new FakeFtpServer { VerifyTls = true, ChainTrusted = true, Identity = new ServerIdentity("tls-spki", first, "CN=nas") };
        var gate = new ServerIdentityGate(new FtpStorageProviderFactory(srv), _store);
        var req = new DestinationRequest("ftps", FakeFtpServer.Root, "nas.example.test", null, "file:f1");
        var cfg = JobDestination.ToConfig("job", req);
        var dest = JobDestination.Id("ftps", req.Root, JobDestination.Options(req));

        await (await gate.OpenAsync(cfg, CredentialRole.Backup, _ct)).DisposeAsync();
        srv.Identity = new ServerIdentity("tls-spki", renewed, "CN=nas"); // odnowienie z nowym kluczem (Let's Encrypt) — bez fałszywego alarmu
        await (await gate.OpenAsync(cfg, CredentialRole.Backup, _ct)).DisposeAsync();
        var rows = _store.Load(dest);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((IdentityState.Pinned, ServerIdentityMachine.CaChainActor), (r.State, r.DecidedBy)));
        Assert.Empty(ServerIdentityMachine.EffectivePins(rows));

        // „Wysokie ryzyko”: ważny łańcuch nie wystarcza → KB-2109, SPKI czeka na Administratora; po zatwierdzeniu działa.
        Assert.Equal(OverallState.Ok, (await Backend().SetServerIdentityPolicyAsync(new SetServerIdentityPolicyArgs { DestinationId = dest, RequirePin = true }, Admin, _ct)).Result);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(cfg, CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.TlsIdentityMismatch, ex.Code);
        Assert.Equal(IdentityState.Pending, _store.Load(dest).Single(r => r.Fingerprint == renewed).State);
        Assert.Equal(OverallState.Ok, (await Decide(dest, renewed, ServerIdentityDecision.Replace)).Result);
        await (await gate.OpenAsync(cfg, CredentialRole.Backup, _ct)).DisposeAsync();
        // Kolejna zmiana SPKI przy ważnym CA na celu wysokiego ryzyka = zatrzymanie (jak SSH).
        srv.Identity = new ServerIdentity("tls-spki", ServerIdentityMachineTests.Fp(), "CN=nas");
        await Assert.ThrowsAsync<StorageException>(async () => await gate.OpenAsync(cfg, CredentialRole.Backup, _ct));
    }

    [Fact, Trait("D", "D4.11"), Trait("KB", "KB-2109"), Trait("Category", "Integration")]
    public async Task Self_signed_ftps_over_real_tls_is_refused_until_approved_then_works_without_pin_in_the_request()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        var p = Path.Combine(_dir, "nas.json");
        File.WriteAllText(p, $$$"""{"backup":{"user":"kopie","password":"{{{secret}}}"}}""");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var tmp = FluentFtpAdapterTests.SelfSigned("nas.example");
        using var cert = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pkcs12), null);
        await using var srv = new LoopbackFtpsControlServer(cert, user: "kopie", secret: secret);
        var probe = new DestinationProbeService(AgentProviders.Create(_dir, identities: _store));
        var req = new DestinationRequest("ftps", "/kopie", "127.0.0.1", srv.Port, "file:nas");

        var r = await probe.ProbeAsync(req, _ct);
        Assert.Equal((DestinationProbeStatus.UnknownIdentity, KbCode.TlsIdentityMismatch), (r.Status, r.Code));
        Assert.False(srv.PassReceived); // hasło nie trafiło do niezatwierdzonego serwera
        var dest = JobDestination.Id("ftps", "/kopie", JobDestination.Options(req));
        var pending = Assert.Single(_store.Load(dest));
        Assert.Equal((TlsSpkiPin.Fingerprint(cert), IdentityState.Pending, ServerIdentityMachine.TlsSpki), (pending.Fingerprint, pending.State, pending.Kind));

        Assert.Equal(OverallState.Ok, (await Decide(dest, pending.Fingerprint, ServerIdentityDecision.Add)).Result);
        var ok = await probe.ProbeAsync(req, _ct);
        Assert.Equal(DestinationProbeStatus.Ok, ok.Status);
        Assert.True(srv.PassReceived);
    }
}
