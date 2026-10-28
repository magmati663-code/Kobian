using System.Diagnostics;
using System.Security.Cryptography;
using Kobian.Agent.Hosting;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-72 (it. 91): host usługi Windows Agenta — profil usługi (D2.5, D2.12, NFR-09), ścieżki stanu w trybie usługi (R2.2.1, Qb41),
/// pojedyncza instancja (D2.15 pkt 3, AC-37.3, CE-12). Część SCM/Event Log/ACL mutexu: kolejka Windows W-33.
/// </summary>
public sealed class AgentServiceHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-svc-" + Guid.NewGuid().ToString("N"));

    public AgentServiceHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------- profil usługi ----------

    [Fact, Trait("BL", "BL-72")]
    public void Profile_UsesVirtualAccount_DelayedAuto_Unrestricted()
    {
        Assert.Equal(@"NT SERVICE\KobianAgent", AgentServiceProfile.Account);
        Assert.Equal("KobianAgent", AgentServiceProfile.ServiceName);
        Assert.Equal("delayed-auto", AgentServiceProfile.StartMode);
        Assert.Equal("unrestricted", AgentServiceProfile.SidType);
        Assert.Equal(@"Global\Kobian.Agent.Instance", AgentServiceProfile.InstanceMutexName);
        Assert.Equal(2, AgentServiceProfile.VssAccessControlValue);
    }

    [Fact, Trait("BL", "BL-72")]
    public void Profile_Privileges_AreExactlyD212_WithoutForbidden()
    {
        Assert.Equal(["SeBackupPrivilege", "SeRestorePrivilege", "SeChangeNotifyPrivilege", "SeShutdownPrivilege"], AgentServiceProfile.RequiredPrivileges);
        Assert.Empty(AgentServiceProfile.RequiredPrivileges.Intersect(AgentServiceProfile.ForbiddenPrivileges));
        Assert.Contains("SeImpersonatePrivilege", AgentServiceProfile.ForbiddenPrivileges);
        Assert.Equal(["SeBackupPrivilege", "SeRestorePrivilege"], AgentServiceProfile.LsaRights);
    }

    [Fact, Trait("BL", "BL-72"), Trait("NFR", "NFR-09")]
    public void Profile_Recovery_RestartsWithin60s_ThenEvery5Min()
    {
        var a = AgentServiceProfile.RecoveryActions;
        Assert.Equal(3, a.Count);
        Assert.True(a[0].Delay <= TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromMinutes(1), a[1].Delay);
        Assert.Equal(TimeSpan.FromMinutes(5), a[2].Delay);
        Assert.Equal(TimeSpan.FromDays(1), AgentServiceProfile.RecoveryResetPeriod);
    }

    [Fact, Trait("BL", "BL-72")]
    public void ScCommands_AreArgumentLists_WithQuotedBinaryAndRecovery()
    {
        var cmds = AgentServiceProfile.ScCommands(@"C:\Program Files\Kobian\Kobian.Agent.exe");
        var create = cmds[0];
        Assert.Equal("create", create[0]);
        Assert.Equal("\"C:\\Program Files\\Kobian\\Kobian.Agent.exe\"", create[create.ToList().IndexOf("binPath=") + 1]);
        Assert.Equal(@"NT SERVICE\KobianAgent", create[create.ToList().IndexOf("obj=") + 1]);
        Assert.Equal("delayed-auto", create[create.ToList().IndexOf("start=") + 1]);
        Assert.Contains(cmds, c => c[0] == "privs" && c[2] == "SeBackupPrivilege/SeRestorePrivilege/SeChangeNotifyPrivilege/SeShutdownPrivilege");
        Assert.Contains(cmds, c => c[0] == "failure" && c.Contains("86400") && c.Contains("restart/60000/restart/60000/restart/300000"));
        Assert.Contains(cmds, c => c[0] == "sidtype" && c[2] == "unrestricted");
        Assert.DoesNotContain(cmds.SelectMany(c => c), s => s.Contains("LocalSystem", StringComparison.OrdinalIgnoreCase));
    }

    [Theory, Trait("BL", "BL-72"), Trait("Category", "Security")]
    [InlineData(@"Kobian.Agent.exe")]
    [InlineData(@"..\Kobian.Agent.exe")]
    [InlineData(@"\\evil\share\Kobian.Agent.exe")]
    [InlineData(@"\\?\C:\Kobian\Kobian.Agent.exe")]
    [InlineData("C:\\Kobian\\a\" start= auto")]
    [InlineData("C:\\Kobian\\a.exe\r\nx")]
    [InlineData(@"/usr/bin/kobian")]
    public void ScCommands_RejectNonLocalOrInjectedPaths(string path) =>
        Assert.Throws<ArgumentException>(() => AgentServiceProfile.ScCommands(path));

    // ---------- ścieżki ----------

    private static Func<string, string?> Env(params (string K, string V)[] vars)
    {
        var d = vars.ToDictionary(x => x.K, x => x.V);
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    [Fact, Trait("BL", "BL-72")]
    public void Paths_Console_RequiresCatalogVariable()
    {
        var (p, e) = AgentPaths.Resolve(Env(), runningAsService: false, programData: _dir);
        Assert.Null(p);
        Assert.Contains("KOBIAN_CATALOG", e, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-72")]
    public void Paths_Service_DefaultsToProgramDataKobian()
    {
        var (p, e) = AgentPaths.Resolve(Env(), runningAsService: true, programData: _dir);
        Assert.Null(e);
        Assert.Equal(Path.Combine(_dir, "Kobian", "catalog.db"), p!.Catalog);
        Assert.Equal(Path.Combine(_dir, "Kobian"), p.StateDir);
        Assert.Equal(Path.Combine(_dir, "Kobian", "secrets"), p.SecretsDir);
    }

    [Fact, Trait("BL", "BL-72")]
    public void Paths_Service_WithoutProgramData_IsConfigurationError()
    {
        var (p, e) = AgentPaths.Resolve(Env(), runningAsService: true, programData: "  ");
        Assert.Null(p);
        Assert.NotNull(e);
    }

    [Fact, Trait("BL", "BL-72")]
    public void Paths_Service_RejectsRelativeValues()
    {
        Assert.Null(AgentPaths.Resolve(Env(("KOBIAN_CATALOG", "catalog.db")), true, _dir).Paths);
        var cat = Path.Combine(_dir, "c.db");
        Assert.Null(AgentPaths.Resolve(Env(("KOBIAN_CATALOG", cat), ("KOBIAN_STATE_DIR", "state")), true, _dir).Paths);
        Assert.Null(AgentPaths.Resolve(Env(("KOBIAN_CATALOG", cat), ("KOBIAN_SECRETS_DIR", "s")), true, _dir).Paths);
    }

    [Fact, Trait("BL", "BL-72")]
    public void Paths_Console_RelativeCatalog_IsExpanded_AndOverridesApply()
    {
        var (p, _) = AgentPaths.Resolve(Env(("KOBIAN_CATALOG", "rel/catalog.db")), false, null);
        Assert.Equal(Path.GetFullPath("rel/catalog.db"), p!.Catalog);
        var state = Path.Combine(_dir, "st");
        var sec = Path.Combine(_dir, "sec");
        var (q, _) = AgentPaths.Resolve(Env(("KOBIAN_CATALOG", Path.Combine(_dir, "c.db")), ("KOBIAN_STATE_DIR", state), ("KOBIAN_SECRETS_DIR", sec)), true, _dir);
        Assert.Equal(state, q!.StateDir);
        Assert.Equal(sec, q.SecretsDir);
        var (r, _) = AgentPaths.Resolve(Env(("KOBIAN_CATALOG", Path.Combine(_dir, "c.db")), ("KOBIAN_STATE_DIR", state)), true, _dir);
        Assert.Equal(Path.Combine(state, "secrets"), r!.SecretsDir);
    }

    [Theory, Trait("BL", "BL-72"), Trait("Category", "Security")]
    [InlineData(@"\\server\share\catalog.db")]
    [InlineData("//server/share/catalog.db")]
    [InlineData(@"\\?\C:\Kobian\catalog.db")]
    [InlineData(@"\\.\pipe\catalog")]
    [InlineData("/tmp/a\0b.db")]
    [InlineData("/tmp/a\nb.db")]
    public void Paths_RejectNetworkDeviceAndControlCharacters(string value)
    {
        foreach (var svc in new[] { false, true })
        {
            Assert.Null(AgentPaths.Resolve(Env(("KOBIAN_CATALOG", value)), svc, _dir).Paths);
            Assert.Null(AgentPaths.Resolve(Env(("KOBIAN_CATALOG", Path.Combine(_dir, "c.db")), ("KOBIAN_STATE_DIR", value)), svc, _dir).Paths);
        }
        var (_, e) = AgentPaths.Resolve(Env(("KOBIAN_CATALOG", value)), false, _dir);
        Assert.DoesNotContain("server", e, StringComparison.OrdinalIgnoreCase); // komunikat bez wartości zmiennej
    }

    // ---------- pojedyncza instancja ----------

    [Fact, Trait("BL", "BL-72"), Trait("AC", "AC-37.3")]
    public void Guard_SecondAcquire_FailsWhileHeld_AndSucceedsAfterRelease()
    {
        var cat = Path.Combine(_dir, "sub", "catalog.db");
        using (var first = SingleInstanceGuard.TryAcquire(cat, useGlobalMutex: false))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstanceGuard.TryAcquire(cat, useGlobalMutex: false));
            Assert.False(File.Exists(cat)); // strażnik nie tworzy katalogu (catalog.db)
        }
        // plik blokady zostaje (bez wyścigu usuwania) i zawiera tylko PID
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(SingleInstanceGuard.LockPathFor(cat)));
        using var again = SingleInstanceGuard.TryAcquire(cat, useGlobalMutex: false);
        Assert.NotNull(again);
    }

    [Fact, Trait("BL", "BL-72"), Trait("AC", "AC-37.3"), Trait("Category", "Integration")]
    public void SecondAgentProcess_ExitsWithKb7007_WithoutTouchingCatalog()
    {
        var cat = Path.Combine(_dir, "catalog.db");
        Assert.NotEqual(CatalogMode.Emergency, CatalogBootstrap.Bootstrap(cat, TimeProvider.System));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var before = Hash(cat);
        var stamp = File.GetLastWriteTimeUtc(cat);
        using var running = SingleInstanceGuard.TryAcquire(cat, useGlobalMutex: false); // „usługa działa”
        Assert.NotNull(running);

        var sw = Stopwatch.StartNew();
        var (code, stderr) = RunAgent(("KOBIAN_CATALOG", cat), ("KOBIAN_SCHEDULER", "0"));
        sw.Stop();

        Assert.Equal(AgentExitCodes.AlreadyRunning, code);
        Assert.Contains(KbCode.SecondInstance, stderr, StringComparison.Ordinal);
        Assert.Equal(before, Hash(cat));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(cat));
        Assert.False(File.Exists(cat + "-wal") && new FileInfo(cat + "-wal").Length > 0);
        // AC-37.3: ≤ 2 s od startu procesu (informacyjnie w środowisku budowy; zawiera start runtime .NET).
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"second instance took {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact, Trait("BL", "BL-72"), Trait("Category", "Integration")]
    public void AgentProcess_WithoutCatalogVariable_ExitsWithConfigurationCode()
    {
        var (code, stderr) = RunAgent();
        Assert.Equal(AgentExitCodes.Configuration, code);
        Assert.Contains("KOBIAN_CATALOG", stderr, StringComparison.Ordinal);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static (int Code, string Stderr) RunAgent(params (string K, string V)[] env)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Kobian.Agent.dll");
        Assert.True(File.Exists(dll), "Kobian.Agent.dll missing next to tests");
        var host = Environment.ProcessPath;
        if (host is null || !Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(dll);
        foreach (var k in new[] { "KOBIAN_CATALOG", "KOBIAN_STATE_DIR", "KOBIAN_SECRETS_DIR" }) psi.Environment.Remove(k);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail("Agent did not exit");
        }
        return (p.ExitCode, err.GetAwaiter().GetResult());
    }
}

/// <summary>BL-72: plan ACL pipe Agenta w trybie usługi (D2.9, D2.15 pkt 1). Konwersja do PipeSecurity i połączenie z sieci — W-34.</summary>
public sealed class AgentPipeAclTests
{
    private const string Service = "S-1-5-80-1234567890-1234567890-1234567890-1234567890-1234567890";
    private const string Operators = "S-1-5-21-1111111111-2222222222-3333333333-1005";

    [Fact, Trait("BL", "BL-72"), Trait("Category", "Security")]
    public void Plan_DeniesNetworkFirst_AndClientsCannotCreateInstances()
    {
        var acl = AgentPipeAcl.Plan(Service, Operators);
        Assert.Equal(PipeAceKind.Deny, acl[0].Kind);
        Assert.Equal(AgentPipeAcl.NetworkSid, acl[0].Sid);
        Assert.Single(acl, a => a.Kind == PipeAceKind.Deny);
        Assert.All(acl.Where(a => a.Kind == PipeAceKind.Allow && a.Sid != Service),
            a => Assert.Equal(PipeAceRights.ReadWrite, a.Rights));
        Assert.Single(acl, a => a.Rights.HasFlag(PipeAceRights.CreateNewInstance));
        Assert.Equal(PipeAceRights.ReadWrite | PipeAceRights.CreateNewInstance, acl.Single(a => a.Sid == Service).Rights);
        Assert.DoesNotContain(acl, a => a.Kind == PipeAceKind.Allow && a.Rights.HasFlag(PipeAceRights.FullControl));
        Assert.Contains(acl, a => a.Sid == Operators);
        // Users (S-1-5-32-545), Everyone (S-1-1-0), Authenticated Users (S-1-5-11) — brak wpisu = odmowa
        Assert.DoesNotContain(acl, a => a.Sid is "S-1-5-32-545" or "S-1-1-0" or "S-1-5-11");
    }

    [Fact, Trait("BL", "BL-72")]
    public void Plan_WithoutOperatorsGroup_HasOnlyServiceSystemAdministrators()
    {
        var acl = AgentPipeAcl.Plan(Service, null);
        Assert.Equal([AgentPipeAcl.NetworkSid, Service, AgentPipeAcl.SystemSid, AgentPipeAcl.AdministratorsSid], acl.Select(a => a.Sid));
    }

    [Theory, Trait("BL", "BL-72"), Trait("Category", "Security")]
    [InlineData("KobianAgent", null)]
    [InlineData("", null)]
    [InlineData("S-1-5-2", null)]
    [InlineData(Service, "S-1-5-2")]
    [InlineData(Service, "Kobian Operators")]
    [InlineData("S-1-5-80-1\nS-1-1-0", null)]
    public void Plan_RejectsInvalidOrNetworkSids(string service, string? ops) =>
        Assert.Throws<ArgumentException>(() => AgentPipeAcl.Plan(service, ops));
}
