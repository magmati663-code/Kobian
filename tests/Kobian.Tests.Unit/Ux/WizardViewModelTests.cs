using System.Globalization;
using System.Xml.Linq;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.Unit.Ux;

public sealed class WizardViewModelTests
{
    private sealed class FakeService : IWizardService
    {
        public Dictionary<WizardState, List<StepIssue>> Issues { get; } = [];

        public List<WizardState> Calls { get; } = [];

        public Task<IReadOnlyList<StepIssue>> ValidateStepAsync(WizardState wizardStep, WizardDraft draft, CancellationToken ct)
        {
            Calls.Add(wizardStep);
            return Task.FromResult<IReadOnlyList<StepIssue>>(Issues.TryGetValue(wizardStep, out var l) ? l : []);
        }

        public Task<ProbeResult> ProbeAsync(WizardDraft draft, CancellationToken ct) => Task.FromResult(new ProbeResult(ProbeOutcome.Ok));

        public Task<RepositoryResult> CreateRepositoryAsync(WizardDraft draft, string repositoryPassword, CancellationToken ct) =>
            Task.FromResult(new RepositoryResult([], null));

        public Task<string?> SaveAsync(WizardDraft draft, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> RunFirstBackupAsync(string jobName, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact, Trait("AC", "AC-01.2")]
    public async Task Next_validates_in_service_and_blocks_on_issue_with_kb_message_and_action()
    {
        var svc = new FakeService();
        svc.Issues[WizardState.S1Sources] = [new StepIssue("sources[1]", "KB-0102")];
        var vm = new WizardViewModel(svc);
        CultureInfo.CurrentUICulture = new CultureInfo("pl-PL");
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S1Sources, vm.State);
        var msg = Assert.Single(vm.Messages);
        Assert.Contains("KB-0102", msg, StringComparison.Ordinal);
        Assert.Contains("nakładają", msg, StringComparison.Ordinal);
        Assert.Contains("Usuń podfolder", msg, StringComparison.Ordinal); // jedna akcja naprawcza (6.3)
        svc.Issues.Clear();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Destination, vm.State);
        Assert.Equal(2, vm.StepNumber);
        Assert.Empty(vm.Messages);
        Assert.Equal([WizardState.S1Sources, WizardState.S1Sources], svc.Calls);
    }

    [Fact, Trait("AC", "AC-28.1")]
    public void Recovery_key_screen_has_no_back()
    {
        var vm = new WizardViewModel(new FakeService());
        vm.Apply(WizardEvent.Next);
        vm.Apply(WizardEvent.Next);
        vm.Apply(WizardEvent.ProbeOk);
        vm.Apply(WizardEvent.Next);
        vm.Apply(WizardEvent.RepositoryCreated);
        Assert.Equal(WizardState.S4RecoveryKey, vm.State);
        Assert.False(vm.CanGoBack);
        vm.BackCommand.Execute(null);
        Assert.Equal(WizardState.S4RecoveryKey, vm.State);
    }

    [Fact]
    public void Property_change_notifications_for_bindings()
    {
        var vm = new WizardViewModel(new FakeService());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Apply(WizardEvent.Next);
        Assert.Contains(nameof(WizardViewModel.State), changed);
        Assert.Contains(nameof(WizardViewModel.StepNumber), changed);
    }
}

/// <summary>R2.6.5 / NFR-13: każdy kod KB ma komunikat PL i EN w zasobach UI.</summary>
public sealed class KbResourcesTests
{
    [Fact, Trait("NFR", "NFR-13")]
    public void Every_kb_code_has_polish_and_english_message()
    {
        var codes = typeof(Kobian.Domain.KbCode).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();
        foreach (var code in codes)
        {
            var pl = KbText.Message(code, new CultureInfo("pl-PL"));
            var en = KbText.Message(code, new CultureInfo("en-US"));
            Assert.NotEqual(code, pl);
            Assert.NotEqual(code, en);
            Assert.NotEqual(pl, en);
        }
    }

    [Fact, Trait("NFR", "NFR-13")]
    public void Every_kb_code_has_polish_and_english_action()
    {
        var codes = typeof(Kobian.Domain.KbCode).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();
        foreach (var code in codes)
        {
            var pl = KbText.Action(code, new CultureInfo("pl-PL"));
            var en = KbText.Action(code, new CultureInfo("en-US"));
            Assert.False(string.IsNullOrWhiteSpace(pl), code);
            Assert.False(string.IsNullOrWhiteSpace(en), code);
            Assert.NotEqual(pl, en);
            // EN nie może zawierać polskich znaków diakrytycznych (niedotłumaczona akcja)
            Assert.DoesNotMatch("[ąćęłńóśźżĄĆĘŁŃÓŚŹŻ]", en!);
        }
        Assert.Contains("Run check", KbText.Action("KB-1301", new CultureInfo("en-US")), StringComparison.Ordinal);
        Assert.Contains("Uruchom check", KbText.Action("KB-1301", new CultureInfo("pl-PL")), StringComparison.Ordinal);
    }

    [Fact]
    public void Resx_files_have_identical_action_keys()
    {
        static HashSet<string> Keys(string f) => XDocument.Load(f).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).Where(n => n.EndsWith("_Action", StringComparison.Ordinal)).ToHashSet();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kobian.sln"))) dir = dir.Parent;
        var res = Path.Combine(dir!.FullName, "src", "Kobian.UI", "Resources");
        var pl = Keys(Path.Combine(res, "KbMessages.resx"));
        Assert.NotEmpty(pl);
        Assert.Equal(pl, Keys(Path.Combine(res, "KbMessages.en.resx")));
    }

    [Fact]
    public void Resx_files_have_identical_message_keys()
    {
        static HashSet<string> Keys(string f) => XDocument.Load(f).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).Where(n => n.EndsWith("_Message", StringComparison.Ordinal)).ToHashSet();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kobian.sln"))) dir = dir.Parent;
        var res = Path.Combine(dir!.FullName, "src", "Kobian.UI", "Resources");
        Assert.Equal(Keys(Path.Combine(res, "KbMessages.resx")), Keys(Path.Combine(res, "KbMessages.en.resx")));
    }
}
