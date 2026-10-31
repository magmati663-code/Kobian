using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Grpc.Core;
using Kobian.Api.V1;
using Kobian.Domain;
using Kobian.UI.Progress;
using Kobian.UI.Services;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>RD-79 (SCR-07): widok postępu uruchomienia — mapowanie WatchRun, ponawianie po zerwaniu strumienia, stany końcowe, „przestań obserwować”.</summary>
public sealed class RunProgressTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static RunProgressUpdate Upd(string state = "Running", bool final = false, RunOutcomeKind r = RunOutcomeKind.Unknown, string? code = null, long read = 0, long? files = null, long? up = null) =>
        new("r1", "Dokumenty", "backup", state, r, code, files, up, read, final);

    /// <summary>Kolejne wywołania WatchAsync dostają kolejne scenariusze; scenariusz = pozycje, na końcu opcjonalny wyjątek.</summary>
    private sealed class Scripted(params Scenario[] attempts) : IRunProgressSource
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<RunProgressUpdate> WatchAsync(string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var s = attempts[Math.Min(Calls++, attempts.Length - 1)];
            foreach (var u in s.Items) { await Task.Yield(); yield return u; }
            if (s.Throw is { } ex) throw ex;
            if (s.Hang) await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private sealed record Scenario(RunProgressUpdate[] Items, Exception? Throw = null, bool Hang = false);

    private static (RunProgressViewModel Vm, List<TimeSpan> Waits) Make(IRunProgressSource src)
    {
        var waits = new List<TimeSpan>();
        return (new RunProgressViewModel(src, "r1", (d, _) => { waits.Add(d); return Task.CompletedTask; }), waits);
    }

    private static async Task<T> InPolish<T>(Func<Task<T>> f)
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        try { return await f(); }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur); }
    }

    [Fact, Trait("BL", "BL-64"), Trait("RD", "RD-79")]
    public async Task Successful_run_goes_connecting_to_running_to_done_with_counters()
    {
        var src = new Scripted(new Scenario([Upd("Queued"), Upd(read: 1536, files: 3), Upd("Succeeded", true, RunOutcomeKind.Ok, files: 12, up: 2048, read: 3 * 1024 * 1024)]));
        var (vm, _) = Make(src);
        Assert.Equal(ProgressStage.Connecting, vm.Stage);
        await InPolish(async () =>
        {
            await vm.RunAsync(_ct);
            return 0;
        });
        Assert.Equal(ProgressStage.Done, vm.Stage);
        Assert.True(vm.IsSuccess);
        Assert.False(vm.IsActive);
        Assert.False(vm.HasProblem);
        Assert.Equal(1, src.Calls);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Texts_are_localized_and_show_only_known_counters()
    {
        var src = new Scripted(new Scenario([Upd(read: 1536, files: 3), Upd("Succeeded", true, RunOutcomeKind.Ok, files: 12, up: 2048, read: 3 * 1024 * 1024)], Hang: false));
        var (vm, _) = Make(src);
        var texts = await InPolish(async () =>
        {
            await vm.RunAsync(_ct);
            return (vm.HeadlineText, vm.StateText, vm.CountersText);
        });
        Assert.Equal("Kopia · Dokumenty", texts.HeadlineText);
        Assert.Equal("Zakończono", texts.StateText);
        Assert.Contains("Odczytano: 3", texts.CountersText, StringComparison.Ordinal);
        Assert.Contains("Wysłano do celu: 2", texts.CountersText, StringComparison.Ordinal);
        Assert.Contains("Pliki: 12", texts.CountersText, StringComparison.Ordinal);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Counters_omit_values_the_service_has_not_reported()
    {
        var src = new Scripted(new Scenario([Upd("Running")], Hang: true));
        var (vm, _) = Make(src);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var t = vm.RunAsync(cts.Token);
        while (vm.Stage != ProgressStage.Running) await Task.Delay(5, _ct);
        Assert.Equal("", vm.CountersText); // backup w toku: usługa nie zna jeszcze liczników — nic nie zmyślamy
        Assert.True(vm.ShowStop);
        vm.StopWatchingCommand.Execute(null);
        await t;
        Assert.Equal(ProgressStage.Stopped, vm.Stage);
        Assert.True(vm.IsFinished);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Failed_run_shows_kb_message_and_problem_badge()
    {
        var src = new Scripted(new Scenario([Upd("Failed", true, RunOutcomeKind.Failed, code: "KB-2104")]));
        var (vm, _) = Make(src);
        await vm.RunAsync(_ct);
        Assert.Equal(ProgressStage.Done, vm.Stage);
        Assert.True(vm.IsFailure);
        Assert.True(vm.IsProblemBadge);
        Assert.Contains("KB-2104", vm.ProblemText, StringComparison.Ordinal);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Warning_run_is_not_a_problem_badge_and_ok_run_hides_stray_code()
    {
        var (vm, _) = Make(new Scripted(new Scenario([Upd("Warning", true, RunOutcomeKind.Warning, code: "KB-5105")])));
        await vm.RunAsync(_ct);
        Assert.True(vm.IsWarning);
        Assert.False(vm.IsProblemBadge);
        Assert.Contains("KB-5105", vm.ProblemText, StringComparison.Ordinal);

        var (ok, _) = Make(new Scripted(new Scenario([Upd("Succeeded", true, RunOutcomeKind.Ok, code: "KB-0000")])));
        await ok.RunAsync(_ct);
        Assert.False(ok.HasProblem);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Broken_stream_is_retried_with_growing_backoff_and_counter_resets_after_progress()
    {
        var io = new IOException("zerwane");
        var src = new Scripted(
            new Scenario([], io), new Scenario([], io), new Scenario([Upd(read: 10)], io), // trzecia próba coś dostarcza → licznik zerowany
            new Scenario([], io),
            new Scenario([Upd("Succeeded", true, RunOutcomeKind.Ok)]));
        var (vm, waits) = Make(src);
        await vm.RunAsync(_ct);
        Assert.Equal(ProgressStage.Done, vm.Stage);
        Assert.Equal(5, src.Calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits); // po postępie seria zaczyna od 1 s
        Assert.Equal(0, vm.ReconnectAttempt);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Reconnect_status_text_names_the_attempt()
    {
        var states = new List<string>();
        var src = new Scripted(new Scenario([], new IOException()), new Scenario([Upd("Succeeded", true, RunOutcomeKind.Ok)]));
        var vm = new RunProgressViewModel(src, "r1", (_, _) => Task.CompletedTask);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StateText) && vm.Stage == ProgressStage.Reconnecting) states.Add(vm.StateText); };
        await InPolish(async () => { await vm.RunAsync(_ct); return 0; });
        Assert.Contains("ponawiam próbę 1 z 5", Assert.Single(states), StringComparison.Ordinal);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Too_many_failed_reconnects_end_in_lost_with_agent_unreachable()
    {
        var src = new Scripted(new Scenario([], new IOException()));
        var (vm, waits) = Make(src);
        await vm.RunAsync(_ct);
        Assert.Equal(ProgressStage.Lost, vm.Stage);
        Assert.Equal(RunProgressViewModel.MaxReconnects + 1, src.Calls);
        Assert.Equal(RunProgressViewModel.MaxReconnects, waits.Count);
        Assert.Equal(TimeSpan.FromSeconds(15), waits[^1]);
        Assert.Contains(KbCode.AgentUnreachable, vm.ProblemText, StringComparison.Ordinal);
        Assert.True(vm.IsProblemBadge);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Stream_that_ends_without_final_message_is_treated_as_a_broken_connection()
    {
        var src = new Scripted(new Scenario([Upd()]), new Scenario([Upd("Succeeded", true, RunOutcomeKind.Ok)]));
        var (vm, waits) = Make(src);
        await vm.RunAsync(_ct);
        Assert.Equal(ProgressStage.Done, vm.Stage);
        Assert.Single(waits);
    }

    [Theory, Trait("RD", "RD-79")]
    [InlineData(StatusCode.PermissionDenied, KbCode.ApiAccessDenied)]
    [InlineData(StatusCode.Unauthenticated, KbCode.ApiAccessDenied)]
    [InlineData(StatusCode.Unimplemented, KbCode.OperationNotAvailable)]
    public async Task Service_refusal_does_not_retry_and_maps_to_kb_code(StatusCode status, string expected)
    {
        var src = new Scripted(new Scenario([], new InvalidOperationException("odmowa", new RpcException(new Status(status, "x")))));
        var (vm, waits) = Make(src);
        await vm.RunAsync(_ct);
        Assert.Equal(ProgressStage.Lost, vm.Stage);
        Assert.Empty(waits);
        Assert.Equal(1, src.Calls);
        Assert.Contains(expected, vm.ProblemText, StringComparison.Ordinal);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Unknown_run_id_final_message_reports_kb_0107()
    {
        var src = new Scripted(new Scenario([Upd("", true, RunOutcomeKind.Failed, code: KbCode.RunNotFound)]));
        var (vm, _) = Make(src);
        await vm.RunAsync(_ct);
        Assert.Equal(ProgressStage.Lost, vm.Stage);
        Assert.Contains(KbCode.RunNotFound, vm.ProblemText, StringComparison.Ordinal);
    }

    [Fact, Trait("RD", "RD-79")]
    public async Task Caller_cancellation_stops_quietly_and_unknown_state_names_fall_back()
    {
        var src = new Scripted(new Scenario([Upd("Zagadkowy")], Hang: true));
        var (vm, _) = Make(src);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var t = InPolish(async () => { await vm.RunAsync(cts.Token); return 0; });
        while (vm.Stage != ProgressStage.Running) await Task.Delay(5, _ct);
        Assert.Equal("Stan nieznany", await InPolish(() => Task.FromResult(Kobian.UI.Localization.Strings.Get("Progress.State.Unknown", new CultureInfo("pl-PL")))));
        await cts.CancelAsync();
        await t;
        Assert.Equal(ProgressStage.Stopped, vm.Stage);
    }

    [Fact, Trait("RD", "RD-79")]
    public void Grpc_adapter_maps_run_progress_including_optional_fields()
    {
        var info = new RunInfo { RunId = "r9", Job = "ERP", Kind = "restore", State = "Warning", Result = OverallState.Warning, Code = "KB-5105", Files = 7, BytesUploaded = 99 };
        var u = GrpcDashboardSource.MapProgress(new RunProgress { Run = info, BytesRead = 1234, Final = true });
        Assert.Equal(new RunProgressUpdate("r9", "ERP", "restore", "Warning", RunOutcomeKind.Warning, "KB-5105", 7, 99, 1234, true), u);

        var bare = GrpcDashboardSource.MapProgress(new RunProgress { Run = new RunInfo { RunId = "r1", Job = "J", State = "Running" } });
        Assert.Null(bare.Code);
        Assert.Null(bare.Files);
        Assert.Null(bare.BytesUploaded);
        Assert.False(bare.Final);
        Assert.Equal(RunOutcomeKind.Ok, bare.Result); // OverallState domyślnie OK — stan końcowy rozstrzyga pole Final, nie wynik
    }

    [Fact, Trait("RD", "RD-79")]
    public void Translations_exist_in_both_languages_for_every_state_and_kind()
    {
        string[] keys = ["Title", "Connecting", "Reconnecting", "Lost", "Stopped", "StopWatching", "StopNote", "Badge.Ok", "Badge.Warning", "Badge.Failed", "BytesRead", "BytesUploaded", "Files",
            "Kind.backup", "Kind.restore", "Kind.check", "Kind.forget", "Kind.other",
            "State.Queued", "State.Running", "State.Succeeded", "State.Warning", "State.Failed", "State.Skipped", "State.Missed", "State.Interrupted", "State.Cancelled", "State.Unknown"];
        foreach (var k in keys)
            foreach (var c in new[] { "pl-PL", "en-US" })
                Assert.False(Kobian.UI.Localization.Strings.Get("Progress." + k, new CultureInfo(c)).StartsWith('['), k + " " + c);
    }

    private static void Pump(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.True(done(), "kolejka UI nie doprowadziła widoku do oczekiwanego stanu");
    }

    public static TheoryData<string, string> Shots() => new() { { "running", "Light" }, { "warning", "Dark" }, { "failed", "Light" }, { "lost", "Dark" } };

    /// <summary>Widok renderowany naprawdę (Avalonia.Headless): zrzut do przeglądu + AutomationId + dostępność przycisku „Przestań obserwować” tylko w trakcie.</summary>
    [Theory, MemberData(nameof(Shots)), Trait("Plan", "R2.6.1"), Trait("RD", "RD-79")]
    public Task Progress_view_renders(string kind, string theme) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        IRunProgressSource src = kind switch
        {
            "running" => new Scripted(new Scenario([Upd(read: 5L * 1024 * 1024 * 1024, files: 4210)], Hang: true)),
            "warning" => new Scripted(new Scenario([Upd("Warning", true, RunOutcomeKind.Warning, code: "KB-5105", files: 120, read: 40_000_000)])),
            "failed" => new Scripted(new Scenario([Upd("Failed", true, RunOutcomeKind.Failed, code: "KB-2104")])),
            _ => new Scripted(new Scenario([], new IOException())),
        };
        var vm = new RunProgressViewModel(src, "r1", (_, _) => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        var run = vm.RunAsync(cts.Token);
        Pump(() => kind == "running" ? vm.Stage == ProgressStage.Running : run.IsCompleted); // kontynuacje VM wracają na wątek UI — pompujemy kolejkę zamiast czekać
        var window = new Window { Content = new RunProgressView { DataContext = vm }, Width = 640, Height = 320 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-07-progress-{kind}-{theme}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 3_000, "zrzut pusty");
        var visible = window.GetVisualDescendants().OfType<Control>().Where(x => x.IsEffectivelyVisible)
            .Select(x => Avalonia.Automation.AutomationProperties.GetAutomationId(x)).Where(i => !string.IsNullOrEmpty(i)).ToHashSet();
        Assert.Contains("SCR-07.Progress.Panel", visible);
        Assert.Equal(kind == "running", visible.Contains("SCR-07.Progress.Stop"));
        Assert.Equal(kind is "warning" or "failed" or "lost", visible.Contains("SCR-07.Progress.Problem"));
        await cts.CancelAsync();
        Pump(() => run.IsCompleted);
        await run;
        window.Close();
    }, _ct);
}
