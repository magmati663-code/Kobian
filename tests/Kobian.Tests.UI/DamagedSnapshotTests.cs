using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.Shell;
using Kobian.UI.History;
using Kobian.UI.Restore;
using Kobian.UI.Services;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>it. 115 (RD-70): znacznik kopii uszkodzonej z `check --repair` w SCR-06 i SCR-10; kopia nieodtwarzalna nie jest proponowana do odtworzenia.</summary>
public sealed class DamagedSnapshotTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static IReadOnlyList<SnapshotEntry> Snaps()
    {
        var n = FakeDashboardSource.Now;
        return
        [
            new("a1b2c3d4e5f60718", "Dokumenty biura", n.AddHours(-2), "incremental", 12_480, 3_221_225_472, "unrecoverable"),
            new("9a8b7c6d5e4f3021", "Dokumenty biura", n.AddHours(-11.5), "incremental", 12_402, 3_200_000_000, "partial", 3),
            new("ffeeddccbbaa9988", "Dokumenty biura", n.AddDays(-3), "full", 12_300, 3_100_000_000),
        ];
    }

    private static async Task InPolish(Func<Task> f)
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        try { await f(); }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur); }
    }

    [Fact, Trait("RD", "RD-70"), Trait("Plan", "D8.10")]
    public Task Browser_blocks_restore_of_unrecoverable_copy_and_explains_partial_one() => InPolish(async () =>
    {
        var src = new FakeBrowseSource(Snaps());
        var vm = new SnapshotBrowserViewModel(src, TimeZoneInfo.Utc);
        await vm.RefreshAsync(_ct);
        var bad = vm.SelectedSnapshot!;
        Assert.True(bad.IsUnrecoverable);
        Assert.Equal("nie do odtworzenia", bad.DamageText);
        Assert.Contains(bad.DamageText, bad.AccessibleName, StringComparison.Ordinal);
        Assert.False(vm.CanRestore);
        Assert.True(vm.HasDamageNotice);
        Assert.Contains("Wybierz inną kopię", vm.DamageNotice, StringComparison.Ordinal);
        Assert.All(vm.Rows, r => Assert.False(r.CanRestoreItem));
        var got = new List<RestoreSelection>();
        vm.RestoreRequested += (_, s) => got.Add(s);
        vm.RestoreFolderCommand.Execute(null);
        vm.RestoreItemCommand.Execute(vm.Rows.First(r => r.IsFile));
        Assert.Empty(got);

        vm.SelectedSnapshot = vm.Snapshots.Single(s => s.Entry.Id == "9a8b7c6d5e4f3021");
        await vm.LoadAsync("", _ct);
        Assert.True(vm.CanRestore); // częściowo uszkodzona — reszta plików odtwarzalna
        Assert.Equal("uszkodzona: 3 pliki", vm.SelectedSnapshot!.DamageText);
        Assert.Contains("nie da się odtworzyć: 3 pliki", vm.DamageNotice, StringComparison.Ordinal);
        vm.RestoreFolderCommand.Execute(null);
        Assert.Single(got);

        vm.SelectedSnapshot = vm.Snapshots.Single(s => s.Entry.Id == "ffeeddccbbaa9988");
        Assert.False(vm.HasDamageNotice);
        Assert.Equal("", vm.DamageNotice);
    });

    [Fact, Trait("RD", "RD-70")]
    public Task History_marks_damaged_copies_and_hides_restore_for_unrecoverable() => InPolish(async () =>
    {
        var vm = new HistoryViewModel(new FakeHistorySource(Snaps(), []), new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);
        await vm.RefreshAsync(_ct);
        var rows = vm.Days.SelectMany(d => d.Rows).ToDictionary(r => r.Entry!.Id);
        Assert.Equal(("nie do odtworzenia", true, false), (rows["a1b2c3d4e5f60718"].ModeText, rows["a1b2c3d4e5f60718"].IsFailed, rows["a1b2c3d4e5f60718"].CanRestore));
        Assert.Equal(("uszkodzona: 3 pliki", true, true), (rows["9a8b7c6d5e4f3021"].ModeText, rows["9a8b7c6d5e4f3021"].IsWarning, rows["9a8b7c6d5e4f3021"].CanRestore));
        Assert.True(rows["ffeeddccbbaa9988"].IsFull);
        SnapshotEntry? asked = null;
        vm.RestoreRequested += (_, e) => asked = e;
        vm.RestoreCommand.Execute(rows["a1b2c3d4e5f60718"]);
        Assert.Null(asked);
    });

    [Fact, Trait("RD", "RD-70")]
    public void Grpc_mapping_accepts_only_known_damage_values()
    {
        var r = new ListSnapshotsResponse();
        r.Snapshots.Add(new SnapshotInfo { Id = "a", Job = "j", Mode = "full", Damage = "partial", DamagedFiles = 2 });
        r.Snapshots.Add(new SnapshotInfo { Id = "b", Job = "j", Mode = "full", Damage = "unrecoverable" });
        r.Snapshots.Add(new SnapshotInfo { Id = "c", Job = "j", Mode = "full", Damage = "<script>", DamagedFiles = -4 });
        r.Snapshots.Add(new SnapshotInfo { Id = "d", Job = "j", Mode = "full" });
        var m = GrpcDashboardSource.MapSnapshots(r);
        Assert.Equal(["partial", "unrecoverable", null, null], m.Select(s => s.Damage));
        Assert.Equal(2, m[0].DamagedFiles);
        Assert.Equal(0, m[2].DamagedFiles);
    }

    [Theory, Trait("RD", "RD-70")]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task Browser_renders_damage_notice(string theme) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var vm = new SnapshotBrowserViewModel(new FakeBrowseSource(Snaps()), TimeZoneInfo.Utc);
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), browser: vm);
        shell.Navigate("SCR-06");
        await vm.RefreshAsync(CancellationToken.None);
        var window = new MainWindow { DataContext = shell, Width = 1180, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-06-damaged-{theme}-1180.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var controls = window.GetVisualDescendants().OfType<Control>().Where(x => x.IsEffectivelyVisible).ToList();
        var ids = controls.Select(Avalonia.Automation.AutomationProperties.GetAutomationId).Where(i => i is not null).ToHashSet();
        Assert.Contains("SCR-06.Damage", ids);
        var restore = controls.OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "SCR-06.RestoreFolder");
        Assert.False(restore.IsEffectivelyEnabled);
        window.Close();
    }, _ct);
}
