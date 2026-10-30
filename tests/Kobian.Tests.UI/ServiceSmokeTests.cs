using System.Security.Cryptography;
using Kobian.Api.Contracts;
using Kobian.UI.Services;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>
/// Kreator przeciw ZAINSTALOWANEJ usłudze (CI: usługa Windows z install.ps1, systemd, launchd) — te same wywołania co aplikacja:
/// walidacja, sprawdzenie celu, „Utwórz repozytorium”, zapis zadania i pierwsza kopia. Uruchamiane tylko, gdy ustawiono KOBIAN_SMOKE_DIR
/// (folder dostępny dla usługi). KOBIAN_SMOKE_PRIVATE_DIR (opcjonalnie): folder w profilu użytkownika (ACL bez konta usługi) — kreator ma się udać
/// albo pokazać konkretny kod błędu, nigdy KB-0001. Przy niepowodzeniu komunikat zawiera dziennik nieoczekiwanych błędów Agenta (KOBIAN_SMOKE_LOG).
/// </summary>
[Trait("Category", "ServiceSmoke")]
public sealed class ServiceSmokeTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static WizardDraft Draft(string source, string destination) =>
        new("smoke-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), [source], DestinationKind.Local, destination,
            null, 22, null, null, null, 7, 4, 6);

    private static string Password() => "Smoke-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    private static string AgentLog()
    {
        var path = Env("KOBIAN_SMOKE_LOG");
        try
        {
            return path is not null && File.Exists(path) ? "\n--- " + path + " ---\n" + File.ReadAllText(path) : "\n(brak dziennika błędów Agenta)";
        }
        catch (IOException ex)
        {
            return "\n(dziennik nieczytelny: " + ex.Message + ")";
        }
        catch (UnauthorizedAccessException ex)
        {
            return "\n(dziennik nieczytelny: " + ex.Message + ")";
        }
    }

    private static string Show(IEnumerable<StepIssue> issues) => string.Join(", ", issues.Select(i => i.Field + "=" + i.KbCode));

    [Fact]
    public async Task Wizard_creates_repository_saves_job_and_runs_first_backup_on_the_installed_service()
    {
        var root = Env("KOBIAN_SMOKE_DIR");
        Assert.SkipWhen(root is null, "KOBIAN_SMOKE_DIR nie ustawiono — test tylko w CI z zainstalowaną usługą.");
        var data = Path.Combine(root!, "dane");
        var dest = Path.Combine(root!, "kopie-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        Directory.CreateDirectory(Path.Combine(data, "podfolder"));
        await File.WriteAllTextAsync(Path.Combine(data, "dokument.txt"), "Zażółć gęślą jaźń", _ct);
        await File.WriteAllBytesAsync(Path.Combine(data, "podfolder", "dane.bin"), RandomNumberGenerator.GetBytes(300_000), _ct);
        Directory.CreateDirectory(dest);

        using var source = new GrpcDashboardSource(ApiConventions.DefaultPipeName);
        var wizard = source.CreateWizardService();
        var draft = Draft(data, dest);
        try
        {
            var issues = await wizard.ValidateStepAsync(WizardState.S6Summary, draft, _ct);
            Assert.True(issues.Count == 0, "Walidacja: " + Show(issues) + AgentLog());
            var probe = await wizard.ProbeAsync(draft, _ct);
            Assert.True(probe.Outcome == ProbeOutcome.Ok, "Sprawdzenie celu: " + probe.Outcome + " " + probe.KbCode + AgentLog());
            var repo = await wizard.CreateRepositoryAsync(draft, Password(), _ct);
            Assert.True(repo.Issues.Count == 0, "Utwórz repozytorium: " + Show(repo.Issues) + AgentLog());
            Assert.False(string.IsNullOrEmpty(repo.RecoveryKey));
            Assert.True(File.Exists(Path.Combine(dest, "kobian.repo")), "Brak kobian.repo w " + dest);
            var saved = await wizard.SaveAsync(draft, _ct);
            Assert.True(saved is null, "Zapis zadania: " + saved + AgentLog());
            var run = await wizard.RunFirstBackupAsync(draft.JobName, _ct);
            Assert.True(run is null, "Pierwsza kopia: " + run + AgentLog());
        }
        catch (Exception ex) when (UiFailure.IsServiceFailure(ex))
        {
            Assert.Fail("Usługa: " + UiFailure.Describe(ex) + " (" + ex.GetType().Name + ": " + ex.Message + ")" + AgentLog());
        }
    }

    [Fact]
    public async Task Destination_the_service_cannot_write_gives_a_specific_code_not_KB_0001()
    {
        var root = Env("KOBIAN_SMOKE_DIR");
        var priv = Env("KOBIAN_SMOKE_PRIVATE_DIR");
        Assert.SkipWhen(root is null || priv is null, "KOBIAN_SMOKE_DIR/KOBIAN_SMOKE_PRIVATE_DIR nie ustawiono.");
        var data = Path.Combine(root!, "dane-p");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "a.txt"), "a", _ct);
        var dest = Path.Combine(priv!, "kopie-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        Directory.CreateDirectory(dest);

        using var source = new GrpcDashboardSource(ApiConventions.DefaultPipeName);
        var wizard = source.CreateWizardService();
        var draft = Draft(data, dest);
        var codes = new List<string?>();
        try
        {
            var probe = await wizard.ProbeAsync(draft, _ct);
            codes.Add(probe.KbCode);
            if (probe.Outcome == ProbeOutcome.Ok)
            {
                var repo = await wizard.CreateRepositoryAsync(draft, Password(), _ct);
                codes.AddRange(repo.Issues.Select(i => i.KbCode)); // sukces też jest w porządku (usługa Windows ma SeBackup/SeRestore)
            }
        }
        catch (Exception ex) when (UiFailure.IsServiceFailure(ex))
        {
            codes.Add(UiFailure.CodeOf(ex));
        }
        TestContext.Current.TestOutputHelper?.WriteLine("Kody: " + string.Join(", ", codes));
        Assert.True(!codes.Contains("KB-0001"), "Kody: " + string.Join(", ", codes) + AgentLog());
    }

    /// <summary>„host:port:login:hasło:folder” z KOBIAN_SMOKE_SFTP / KOBIAN_SMOKE_FTPS (serwery testowe uruchomione w CI na tej samej maszynie).</summary>
    private static (string Host, int Port, string Login, string Password, string Root)? ServerEnv(string name)
    {
        var parts = Env(name)?.Split(':');
        return parts is { Length: 5 } ? (parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2], parts[3], parts[4]) : null;
    }

    [Fact]
    public async Task Sftp_and_ftps_servers_as_sources_login_approve_browse_and_back_up()
    {
        var root = Env("KOBIAN_SMOKE_DIR");
        var sftp = ServerEnv("KOBIAN_SMOKE_SFTP");
        var ftps = ServerEnv("KOBIAN_SMOKE_FTPS");
        Assert.SkipWhen(root is null || sftp is null || ftps is null, "KOBIAN_SMOKE_SFTP/KOBIAN_SMOKE_FTPS nie ustawiono — serwery testowe tylko w CI.");
        using var source = new GrpcDashboardSource(ApiConventions.DefaultPipeName);
        var wizard = source.CreateWizardService();
        try
        {
            var servers = new List<ServerSource>();
            foreach (var (scheme, s) in new[] { ("sftp", sftp!.Value), ("ftps", ftps!.Value) })
            {
                var name = CredentialNames.Suggest(s.Host, s.Login + "-" + scheme);
                Assert.Null(await wizard.SaveCredentialAsync(name, s.Login, s.Password, _ct));
                var server = new ServerSource(scheme, s.Host, s.Port, s.Root, name, null);
                var probe = await wizard.ProbeServerAsync(server, _ct);
                Assert.True(probe.Outcome == ProbeOutcome.UnknownIdentity && probe.HostKeyFingerprint is not null,
                    scheme + ": pierwsze połączenie powinno pytać o odcisk, jest " + probe.Outcome + " " + probe.KbCode + AgentLog());
                server = server with { HostKey = probe.HostKeyFingerprint };
                var listed = await wizard.BrowseServerAsync(server, _ct);
                Assert.True(listed.KbCode is null, scheme + ": przeglądanie " + s.Root + ": " + listed.KbCode + AgentLog());
                Assert.True(listed.Folders.Contains("biuro") && listed.Folders.Contains("zdjecia"), scheme + ": w " + s.Root + " jest " + string.Join(", ", listed.Folders));
                var deeper = await wizard.BrowseServerAsync(server with { Root = s.Root + "/biuro" }, _ct);
                Assert.Equal(["umowy"], deeper.Folders);
                Assert.True((await wizard.ProbeServerAsync(server, _ct)).Outcome == ProbeOutcome.Ok);
                servers.Add(scheme == "sftp" ? server : server with { Root = s.Root + "/biuro" });
            }

            var dest = Path.Combine(root!, "kopie-serwery-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
            Directory.CreateDirectory(dest);
            var draft = Draft("unused", dest) with { Sources = [], SourceServers = servers };
            var issues = await wizard.ValidateStepAsync(WizardState.S6Summary, draft, _ct);
            Assert.True(issues.Count == 0, "Walidacja: " + Show(issues) + AgentLog());
            var repo = await wizard.CreateRepositoryAsync(draft, Password(), _ct);
            Assert.True(repo.Issues.Count == 0, "Utwórz repozytorium: " + Show(repo.Issues) + AgentLog());
            Assert.Null(await wizard.SaveAsync(draft, _ct));
            var run = await wizard.RunFirstBackupAsync(draft.JobName, _ct);
            Assert.True(run is null, "Kopia z serwerów: " + run + AgentLog());
        }
        catch (Exception ex) when (UiFailure.IsServiceFailure(ex))
        {
            Assert.Fail("Usługa: " + UiFailure.Describe(ex) + " (" + ex.GetType().Name + ": " + ex.Message + ")" + AgentLog());
        }
    }
}
