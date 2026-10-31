using System.Globalization;
using System.Xml.Linq;
using Kobian.Agent.Hosting;
using Kobian.Application.Install;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// it. 100 (D10.2, D2.12, 2.6; Qb50): plan custom action MSI (grupy, ACL katalogu danych, VssAccessControl, prawa LSA), wykonawca z zatrzymaniem na błędzie,
/// tryb `--provision` poza Windows i zgodność installer/Package.wxs z <see cref="AgentServiceProfile"/> (MSI budowany tylko na Windows — W-42).
/// </summary>
public sealed class InstallerTests
{
    private sealed class Recorder(string? failOn = null) : IInstallProvisioner
    {
        public List<InstallStep> Applied { get; } = [];

        public void Apply(InstallStep action)
        {
            if (action.Id == failOn) throw new InstallStepException("symulowany błąd", 5);
            Applied.Add(action);
        }
    }

    private static readonly XNamespace W = "http://wixtoolset.org/schemas/v4/wxs";

    private static XDocument Package()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kobian.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir!.FullName, "installer", "Package.wxs"));
    }

    [Fact, Trait("Plan", "D2.12"), Trait("Plan", "2.6")]
    public void Install_plan_creates_groups_then_protected_data_dirs_then_vss_and_lsa()
    {
        var steps = AgentInstallPlan.Install(@"C:\ProgramData");
        Assert.Equal(["group-operators", "group-admins", "data-dir", "data-secrets", "data-keys", "data-certs", "data-staging", "vss-access", "lsa-rights"], steps.Select(s => s.Id));
        Assert.Equal([AgentPipeAcl.OperatorsGroupName, AgentPipeAcl.AdminsGroupName], steps.OfType<EnsureLocalGroup>().Select(g => g.Name));
        var dirs = steps.OfType<EnsureDirectoryAcl>().ToList();
        Assert.Equal(@"C:\ProgramData\Kobian", dirs[0].Path);
        Assert.Equal(@"C:\ProgramData\Kobian\secrets", dirs[1].Path);
        Assert.All(dirs, d =>
        {
            Assert.True(d.Protect);
            // 2.6: tylko SYSTEM, Administrators, konto usługi; grupy Kobian bez dostępu bezpośredniego (tylko przez API)
            Assert.Equal(["S-1-5-18", "S-1-5-32-544", AgentServiceProfile.Account], d.Allow.Select(a => a.Principal));
            Assert.DoesNotContain(d.Allow, a => a.Principal.Contains("Kobian ", StringComparison.Ordinal) || a.Principal is "S-1-1-0" or "S-1-5-32-545" or "S-1-5-11");
        });
        var vss = Assert.Single(steps.OfType<SetRegistryDword>());
        Assert.Equal((@"SYSTEM\CurrentControlSet\Services\VSS\VssAccessControl", AgentServiceProfile.Account, 2), (vss.Key, vss.ValueName, vss.Value));
        Assert.Equal(AgentServiceProfile.LsaRights, Assert.Single(steps.OfType<GrantAccountRights>()).Rights);
        Assert.Equal(@"D:\Data\Kobian", AgentInstallPlan.DataDir(@"D:\Data\"));
        foreach (var bad in new[] { @"\\srv\share", "ProgramData", @"\\?\C:\x", "C:\\a\nb", "" })
            Assert.ThrowsAny<ArgumentException>(() => AgentInstallPlan.DataDir(bad));
    }

    [Fact, Trait("Plan", "D10.2")]
    public void Uninstall_plan_removes_only_system_grants_never_data_or_groups()
    {
        var steps = AgentInstallPlan.Uninstall();
        Assert.Equal(["vss-access", "lsa-rights"], steps.Select(s => s.Id));
        Assert.DoesNotContain(steps, s => s is EnsureDirectoryAcl or EnsureLocalGroup);
        Assert.IsType<RemoveRegistryValue>(steps[0]);
        Assert.IsType<RevokeAccountRights>(steps[1]);
    }

    [Fact, Trait("Plan", "D2.12")]
    public void Install_stops_at_first_failure_uninstall_continues_and_exit_codes_follow()
    {
        var rec = new Recorder(failOn: "data-dir");
        var log = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(AgentInstallCommand.StepFailed, AgentInstallCommand.Run(AgentInstallPlan.ProvisionArg, log, rec, @"C:\ProgramData"));
        Assert.Equal(["group-operators", "group-admins"], rec.Applied.Select(s => s.Id)); // dalsze kroki nie ruszyły — MSI wycofa instalację
        Assert.Contains("failed data-dir (win32 5)", log.ToString(), StringComparison.Ordinal);

        var un = new Recorder(failOn: "vss-access");
        Assert.Equal(AgentExitCodes.Ok, AgentInstallCommand.Run(AgentInstallPlan.UnprovisionArg, TextWriter.Null, un));
        Assert.Equal(["lsa-rights"], un.Applied.Select(s => s.Id)); // best effort — odinstalowanie nie jest blokowane

        var ok = new Recorder();
        Assert.Equal(AgentExitCodes.Ok, AgentInstallCommand.Run(AgentInstallPlan.ProvisionArg, TextWriter.Null, ok, @"C:\ProgramData"));
        Assert.Equal(9, ok.Applied.Count);
        Assert.Equal(AgentExitCodes.Configuration, AgentInstallCommand.Run(AgentInstallPlan.ProvisionArg, TextWriter.Null, ok, "/var/lib"));
    }

    [Fact, Trait("Plan", "D2.12")]
    public void Install_mode_needs_exactly_one_known_argument_and_is_windows_only()
    {
        Assert.Equal("--provision", AgentInstallPlan.Mode(["--provision"]));
        Assert.Equal("--unprovision", AgentInstallPlan.Mode(["--unprovision"]));
        Assert.Null(AgentInstallPlan.Mode([]));
        Assert.Null(AgentInstallPlan.Mode(["--provision", "x"]));
        Assert.Null(AgentInstallPlan.Mode(["--PROVISION"]));
        if (!OperatingSystem.IsWindows())
        {
            var log = new StringWriter(CultureInfo.InvariantCulture);
            Assert.Equal(AgentExitCodes.Configuration, AgentInstallCommand.Run(AgentInstallPlan.ProvisionArg, log));
            Assert.Contains("only on Windows", log.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact, Trait("Plan", "D10.2"), Trait("Plan", "D2.12")]
    public void Package_wxs_matches_the_service_profile()
    {
        var doc = Package();
        var svc = Assert.Single(doc.Descendants(W + "ServiceInstall"));
        Assert.Equal(AgentServiceProfile.ServiceName, (string?)svc.Attribute("Name"));
        Assert.Equal(AgentServiceProfile.DisplayName, (string?)svc.Attribute("DisplayName"));
        Assert.Equal(AgentServiceProfile.Description, (string?)svc.Attribute("Description"));
        Assert.Equal(AgentServiceProfile.Account, (string?)svc.Attribute("Account"));
        Assert.Equal("auto", (string?)svc.Attribute("Start"));
        Assert.Null(svc.Attribute("Password")); // konto wirtualne — bez hasła w MSI
        var cfg = Assert.Single(svc.Elements(W + "ServiceConfig"));
        Assert.Equal(("yes", AgentServiceProfile.SidType), ((string?)cfg.Attribute("DelayedAutoStart"), (string?)cfg.Attribute("ServiceSid")));
        Assert.Equal(AgentServiceProfile.RequiredPrivileges.Order(), cfg.Elements(W + "RequiredPrivilege").Select(p => (string)p.Attribute("Name")!).Order());
        Assert.DoesNotContain(cfg.Elements(W + "RequiredPrivilege"), p => AgentServiceProfile.ForbiddenPrivileges.Contains((string)p.Attribute("Name")!));
        var fa = Assert.Single(svc.Elements(W + "ServiceConfigFailureActions"));
        Assert.Equal(((long)AgentServiceProfile.RecoveryResetPeriod.TotalSeconds).ToString(CultureInfo.InvariantCulture), (string?)fa.Attribute("ResetPeriod"));
        Assert.Equal(AgentServiceProfile.RecoveryActions.Select(a => ((long)a.Delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
            fa.Elements(W + "Failure").Select(f => (string)f.Attribute("Delay")!));
        Assert.All(fa.Elements(W + "Failure"), f => Assert.Equal("restart", (string?)f.Attribute("Action")));

        var cas = doc.Descendants(W + "CustomAction").ToDictionary(c => (string)c.Attribute("Id")!);
        Assert.Equal(AgentInstallPlan.ProvisionArg, (string?)cas["KobianProvision"].Attribute("ExeCommand"));
        Assert.Equal(AgentInstallPlan.UnprovisionArg, (string?)cas["KobianUnprovision"].Attribute("ExeCommand"));
        Assert.All(cas.Values, c =>
        {
            Assert.Equal(("deferred", "no", "AgentExe"), ((string?)c.Attribute("Execute"), (string?)c.Attribute("Impersonate"), (string?)c.Attribute("FileRef")));
            Assert.Null(c.Attribute("Script")); // bez skryptów VBScript/JScript (SEC-10.7)
        });
        Assert.Equal("check", (string?)cas["KobianProvision"].Attribute("Return"));
        var seq = doc.Descendants(W + "Custom").ToDictionary(c => (string)c.Attribute("Action")!);
        Assert.Equal("InstallServices", (string?)seq["KobianProvision"].Attribute("After"));

        Assert.Equal("perMachine", (string?)Assert.Single(doc.Descendants(W + "Package")).Attribute("Scope"));
        var install = Assert.Single(doc.Descendants(W + "Directory"), d => (string?)d.Attribute("Id") == "INSTALLFOLDER");
        Assert.Equal(("Kobian", "ProgramFiles64Folder"), ((string?)install.Attribute("Name"), (string?)install.Parent!.Attribute("Id")));
        // D10.2: odinstalowanie nie usuwa danych — brak RemoveFolder/RemoveFile w MSI i brak katalogu ProgramData w pakiecie
        Assert.Empty(doc.Descendants(W + "RemoveFolder"));
        Assert.Empty(doc.Descendants(W + "RemoveFile"));
        Assert.DoesNotContain(doc.Descendants(W + "StandardDirectory"), d => (string?)d.Attribute("Id") == "CommonAppDataFolder");
    }
}
