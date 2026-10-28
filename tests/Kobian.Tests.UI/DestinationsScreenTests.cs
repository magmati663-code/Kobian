using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.Destinations;
using Kobian.UI.Services;
using Kobian.UI.Shell;
using Xunit;

namespace Kobian.Tests.UI;

internal sealed class FakeDestinations : IDestinationsSource
{
    public bool Fail { get; set; }

    public int Unreadable { get; set; }

    public List<DestinationItem> Items { get; } =
    [
        new("dest-0000000000000001", "local", @"E:\Kopie\Biuro", ["Dokumenty biura", "Zdjęcia"]),
        new("dest-0000000000000002", "local", @"F:\USB-kopie", ["Poczta"]),
        new("dest-0000000000000003", "sftp", "/srv/backup/kobian", ["ERP Optima — noc"]),
    ];

    public Task<DestinationList> ListDestinationsAsync(CancellationToken ct) => Fail ? throw new IOException("offline") : Task.FromResult(new DestinationList(Items, Unreadable));

    public Task<DestinationCheck> CheckDestinationAsync(DestinationItem d, CancellationToken ct) => Task.FromResult(d.Id[^1] switch
    {
        '1' => new DestinationCheck(null, 412L << 30, 931L << 30, true),
        '2' => new DestinationCheck(null, 18L << 30, 238L << 30, true),
        _ => new DestinationCheck("KB-2106", null, null, false),
    });
}

/// <summary>SCR-09 Cele (it. 82): lista z zadań, sprawdzenie miejsca, błąd KB, offline, adapter, zrzuty.</summary>
public sealed class DestinationsScreenTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_destinations_and_checks_space_on_demand()
    {
        var vm = new DestinationsViewModel(new FakeDestinations { Unreadable = 1 });
        await vm.RefreshAsync(_ct);
        Assert.Equal(3, vm.Cards.Count);
        Assert.Contains("Zdjęcia", vm.Cards[0].JobsText, StringComparison.Ordinal);
        Assert.True(vm.HasUnreadable);
        Assert.False(vm.Cards[0].Checked);
        await vm.CheckCommand.ExecuteAsync(vm.Cards[0]);
        Assert.True(vm.Cards[0].HasSpace);
        Assert.InRange(vm.Cards[0].FreePercent, 44, 45);
        Assert.False(vm.Cards[0].LowSpace);
        await vm.CheckCommand.ExecuteAsync(vm.Cards[1]);
        Assert.True(vm.Cards[1].LowSpace); // 7,6% < 15%
        await vm.CheckCommand.ExecuteAsync(vm.Cards[2]);
        Assert.True(vm.Cards[2].HasError);
        Assert.Contains("KB-2106", vm.Cards[2].ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_and_empty_states()
    {
        var src = new FakeDestinations { Fail = true };
        var vm = new DestinationsViewModel(src);
        await vm.RefreshAsync(_ct);
        Assert.True(vm.IsOffline);
        src.Fail = false;
        src.Items.Clear();
        await vm.RefreshAsync(_ct);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void Grpc_adapter_maps_destinations_and_probe()
    {
        var r = new ListDestinationsReply { UnreadableJobs = 2 };
        var d = new DestinationInfo { Id = "dest-x", Scheme = "local", Root = "/r" };
        d.Jobs.Add("A");
        r.Destinations.Add(d);
        var list = GrpcDashboardSource.MapDestinations(r);
        Assert.Equal((2, "A"), (list.UnreadableJobs, list.Items.Single().Jobs.Single()));
        Assert.Equal(new DestinationCheck(null, 5, 10, true), GrpcDashboardSource.MapCheck(new ProbeDestinationReply { Status = ProbeStatus.Ok, FreeBytes = 5, TotalBytes = 10, RepositoryExists = true }));
        Assert.Equal("KB-2106", GrpcDashboardSource.MapCheck(new ProbeDestinationReply { Status = ProbeStatus.Failed, Code = "KB-2106" }).Code);
    }

    public static TheoryData<string, string, int> Shots() => new()
    {
        { "checked", "Light", 1180 },
        { "checked", "Dark", 1180 },
        { "list", "Light", 800 },
        { "offline", "Light", 1180 },
    };

    [Theory, MemberData(nameof(Shots)), Trait("Plan", "R2.6.1")]
    public Task Destinations_screen_renders(string state, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var vm = new DestinationsViewModel(new FakeDestinations { Fail = state == "offline", Unreadable = state == "list" ? 1 : 0 });
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), destinations: vm);
        shell.Navigate("SCR-09");
        await vm.RefreshAsync(CancellationToken.None);
        if (state == "checked") foreach (var c in vm.Cards.Where(c => c.CanCheck).ToList()) await vm.CheckCommand.ExecuteAsync(c); // it. 120: cel SFTP bez adresu — przycisk wyłączony
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1100 : 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-09-{state}-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var visible = window.GetVisualDescendants().OfType<Control>()
            .Where(x => x.IsEffectivelyVisible).Select(Avalonia.Automation.AutomationProperties.GetAutomationId).Where(i => i is not null).ToHashSet();
        Assert.Contains("SCR-09.Title", visible);
        Assert.Equal(state != "offline", visible.Contains("SCR-09.List"));
        Assert.Equal(state == "offline", visible.Contains("SCR-09.Offline"));
        Assert.Equal(state == "list", visible.Contains("SCR-09.Unreadable"));
        window.Close();
    }, _ct);
}
