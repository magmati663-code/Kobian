using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.History;
using Kobian.UI.Restore;
using Kobian.UI.Services;
using Kobian.UI.Shell;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>Atrapa BrowseTree: syntetyczne drzewo (bez danych klienta), opcjonalna awaria połączenia, rejestr żądań.</summary>
internal sealed class FakeBrowseSource(IReadOnlyList<SnapshotEntry> snapshots) : ISnapshotBrowseSource
{
    private static readonly DateTimeOffset M = new(2026, 10, 12, 16, 5, 0, TimeSpan.Zero);

    public bool Fail { get; set; }

    public List<(string Snapshot, string Path)> Calls { get; } = [];

    public Func<string, string, Task>? Gate { get; set; }

    public static readonly Dictionary<string, BrowseItem[]> Tree = new(StringComparer.Ordinal)
    {
        [""] =
        [
            new("Faktury", BrowseItemKind.Directory, 0, M), new("Kadry", BrowseItemKind.Directory, 0, M), new("Umowy", BrowseItemKind.Directory, 0, M),
            new("cennik-2026.xlsx", BrowseItemKind.File, 48_213, M.AddDays(-3)), new("skrót do serwera", BrowseItemKind.Link, 0, null),
            new("notatki.txt", BrowseItemKind.File, 1_204, M.AddHours(-5)),
        ],
        ["Faktury"] = [new("2026", BrowseItemKind.Directory, 0, M), new("zestawienie.pdf", BrowseItemKind.File, 3_481_600, M.AddDays(-1))],
        ["Faktury/2026"] = [new("FV-0001.pdf", BrowseItemKind.File, 182_000, M.AddDays(-9)), new("FV-0002.pdf", BrowseItemKind.File, 176_500, M.AddDays(-2))],
        ["Kadry"] = [],
        ["Umowy"] = [new("wzór umowy.docx", BrowseItemKind.File, 61_440, M.AddDays(-30))],
    };

    public Task<IReadOnlyList<SnapshotEntry>> ListSnapshotsAsync(string? job, CancellationToken ct) =>
        Fail ? throw new IOException("offline") : Task.FromResult(snapshots);

    public async Task<BrowseResult> BrowseAsync(string snapshotId, string path, CancellationToken ct)
    {
        Calls.Add((snapshotId, path));
        if (Gate is not null) await Gate(snapshotId, path);
        if (Fail) throw new IOException("offline");
        return Tree.TryGetValue(path, out var items) ? new BrowseResult(null, path, items, false) : new BrowseResult("KB-5003", path, [], false);
    }
}

/// <summary>SCR-06 Przeglądarka kopii (BL-63, AC-07.1): ViewModel na atrapie, mapowanie BrowseTree w adapterze gRPC, zrzuty w powłoce.</summary>
public sealed class SnapshotBrowserTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static SnapshotBrowserViewModel Vm(FakeBrowseSource src) => new(src, TimeZoneInfo.Utc);

    [Fact, Trait("AC", "AC-07.1"), Trait("BL", "BL-63")]
    public async Task Selects_newest_snapshot_and_lists_root_dirs_first()
    {
        var src = new FakeBrowseSource(FakeHistorySource.Sample());
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        Assert.Equal(5, vm.Snapshots.Count);
        Assert.Equal("a1b2c3d4e5f60718", vm.SelectedSnapshot!.Entry.Id);
        Assert.Equal(("a1b2c3d4e5f60718", ""), src.Calls.Single());
        Assert.Equal(["Faktury", "Kadry", "Umowy", "cennik-2026.xlsx", "skrót do serwera", "notatki.txt"], vm.Rows.Select(r => r.Name));
        Assert.True(vm.Rows[0].IsDirectory);
        Assert.Equal("", vm.Rows[0].SizeText);
        Assert.EndsWith(" KB", vm.Rows[3].SizeText, StringComparison.Ordinal);
        Assert.Single(vm.Breadcrumbs);
        Assert.False(vm.CanGoUp);
        Assert.True(vm.CanRestore);
        Assert.Contains("3", vm.FolderSummary, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-63")]
    public async Task Opening_folders_builds_breadcrumbs_and_up_returns()
    {
        var vm = Vm(new FakeBrowseSource(FakeHistorySource.Sample()));
        await vm.RefreshAsync(_ct);
        await vm.OpenCommand.ExecuteAsync(vm.Rows.Single(r => r.Name == "Faktury"));
        await vm.OpenCommand.ExecuteAsync(vm.Rows.Single(r => r.Name == "2026"));
        Assert.Equal("Faktury/2026", vm.CurrentPath);
        Assert.Equal(["Faktury", "2026"], vm.Breadcrumbs.Skip(1).Select(b => b.Text));
        Assert.True(vm.Breadcrumbs[^1].IsLast);
        Assert.True(vm.CanGoUp);
        await vm.UpCommand.ExecuteAsync(null);
        Assert.Equal("Faktury", vm.CurrentPath);
        await vm.GoToCommand.ExecuteAsync(vm.Breadcrumbs[0]);
        Assert.Equal("", vm.CurrentPath);
        // plik nie jest otwierany
        await vm.OpenCommand.ExecuteAsync(vm.Rows.Single(r => r.Name == "notatki.txt"));
        Assert.Equal("", vm.CurrentPath);
    }

    [Fact, Trait("BL", "BL-63")]
    public async Task Empty_folder_and_error_code_states()
    {
        var vm = Vm(new FakeBrowseSource(FakeHistorySource.Sample()));
        await vm.RefreshAsync(_ct);
        await vm.OpenCommand.ExecuteAsync(vm.Rows.Single(r => r.Name == "Kadry"));
        Assert.True(vm.IsFolderEmpty);
        Assert.False(vm.HasError);
        await vm.LoadAsync("Brak", _ct);
        Assert.Equal("KB-5003", vm.ErrorCode);
        Assert.Contains("KB-5003", vm.ErrorText, StringComparison.Ordinal);
        Assert.False(vm.CanRestore);
        Assert.False(vm.IsFolderEmpty);
    }

    [Fact, Trait("BL", "BL-63")]
    public async Task Late_reply_of_a_stale_request_is_ignored()
    {
        var src = new FakeBrowseSource(FakeHistorySource.Sample());
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        var slow = new TaskCompletionSource();
        src.Gate = (_, p) => p == "Faktury" ? slow.Task : Task.CompletedTask;
        var first = vm.LoadAsync("Faktury", _ct);
        await vm.LoadAsync("Umowy", _ct);
        slow.SetResult();
        await first;
        Assert.Equal("Umowy", vm.CurrentPath);
        Assert.Equal(["wzór umowy.docx"], vm.Rows.Select(r => r.Name));
    }

    [Fact, Trait("BL", "BL-63")]
    public async Task Restore_requests_carry_snapshot_and_path_links_are_not_restorable()
    {
        var vm = Vm(new FakeBrowseSource(FakeHistorySource.Sample()));
        await vm.RefreshAsync(_ct);
        var got = new List<RestoreSelection>();
        vm.RestoreRequested += (_, s) => got.Add(s);
        vm.RestoreFolderCommand.Execute(null);
        vm.RestoreItemCommand.Execute(vm.Rows.Single(r => r.Name == "notatki.txt"));
        vm.RestoreItemCommand.Execute(vm.Rows.Single(r => r.Name == "skrót do serwera"));
        Assert.Equal([("", true), ("notatki.txt", false)], got.Select(g => (g.Path, g.IsDirectory)));
        Assert.All(got, g => Assert.Equal("a1b2c3d4e5f60718", g.Snapshot.Id));
    }

    [Fact, Trait("BL", "BL-63")]
    public async Task Offline_then_stale_banner_and_history_restore_opens_the_snapshot()
    {
        var src = new FakeBrowseSource(FakeHistorySource.Sample()) { Fail = true };
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        Assert.True(vm.IsOffline);
        Assert.Equal("KB-9001", vm.OfflineCode);
        src.Fail = false;

        var history = new HistoryViewModel(new FakeHistorySource(FakeHistorySource.Sample(), FakeHistorySource.SampleRuns()), new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), history: history, browser: vm);
        await history.RefreshAsync(_ct);
        var row = history.Days.SelectMany(d => d.Rows).First(r => r.Entry?.Id == "ffeeddccbbaa9988");
        history.RestoreCommand.Execute(row);
        Assert.Equal("SCR-06", shell.Selected.ScreenId);
        Assert.Same(vm, shell.CurrentPage);
        await vm.RefreshAsync(_ct);
        Assert.Equal("ffeeddccbbaa9988", vm.SelectedSnapshot!.Entry.Id);
        Assert.False(vm.IsOffline);
    }

    [Fact, Trait("BL", "BL-63")]
    public void Grpc_adapter_maps_browse_reply_and_failures()
    {
        var ok = new BrowseTreeReply { Result = OverallState.Ok, Path = "a", SnapshotId = "ab" };
        ok.Entries.Add(new BrowseEntryInfo { Name = "d", Kind = BrowseEntryKind.Directory });
        ok.Entries.Add(new BrowseEntryInfo { Name = "f", Kind = BrowseEntryKind.File, Size = -5, ModifiedUnixMs = 1_760_000_000_000 });
        ok.Entries.Add(new BrowseEntryInfo { Name = "x", Kind = BrowseEntryKind.Link, ModifiedUnixMs = long.MaxValue });
        var r = GrpcDashboardSource.MapBrowse(ok, "a");
        Assert.Null(r.Code);
        Assert.Equal([BrowseItemKind.Directory, BrowseItemKind.File, BrowseItemKind.Link], r.Items.Select(i => i.Kind));
        Assert.Equal(0, r.Items[1].Size);
        Assert.NotNull(r.Items[1].ModifiedUtc);
        Assert.Null(r.Items[2].ModifiedUtc); // poza zakresem dat — bez wyjątku
        var bad = GrpcDashboardSource.MapBrowse(new BrowseTreeReply { Result = OverallState.Failed, Code = "KB-5003" }, "zz");
        Assert.Equal(("KB-5003", "zz", 0), (bad.Code, bad.Path, bad.Items.Count));
        Assert.Equal("KB-0001", GrpcDashboardSource.MapBrowse(new BrowseTreeReply { Result = OverallState.Failed }, "").Code);
    }

    public static TheoryData<string, string, int> Shots() => new()
    {
        { "root", "Light", 1180 },
        { "root", "Dark", 1180 },
        { "folder", "Light", 1180 },
        { "folder", "Light", 800 },
        { "error", "Light", 1180 },
        { "empty", "Dark", 1180 },
    };

    [Theory, MemberData(nameof(Shots)), Trait("Plan", "R2.6.1"), Trait("BL", "BL-63")]
    public Task Browser_screen_renders(string state, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var vm = Vm(new FakeBrowseSource(state == "empty" ? [] : FakeHistorySource.Sample()));
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), browser: vm);
        shell.Navigate("SCR-06");
        await vm.RefreshAsync(CancellationToken.None);
        if (state == "folder") await vm.LoadAsync("Faktury/2026", CancellationToken.None);
        if (state == "error") await vm.LoadAsync("Brak", CancellationToken.None);
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1300 : 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-06-{state}-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var visible = window.GetVisualDescendants().OfType<Control>()
            .Where(x => x.IsEffectivelyVisible).Select(Avalonia.Automation.AutomationProperties.GetAutomationId).Where(i => i is not null).ToHashSet();
        Assert.Contains("SCR-06.Title", visible);
        Assert.Equal(state != "empty", visible.Contains("SCR-06.Snapshots"));
        Assert.Equal(state is "root" or "folder", visible.Contains("SCR-06.Rows"));
        Assert.Equal(state == "error", visible.Contains("SCR-06.Error"));
        Assert.Equal(state == "empty", visible.Contains("SCR-06.Empty"));
        window.Close();
    }, _ct);
}
