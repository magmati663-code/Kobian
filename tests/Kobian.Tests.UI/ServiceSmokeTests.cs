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
}
