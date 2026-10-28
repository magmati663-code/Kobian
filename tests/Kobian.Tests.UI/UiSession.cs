using Avalonia;
using Avalonia.Headless;
using Kobian.UI;

namespace Kobian.Tests.UI;

/// <summary>Jedna sesja Avalonia.Headless na proces (wątek UI), renderowanie Skia — zrzuty są prawdziwym obrazem, nie atrapą.</summary>
public static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Lazy = new(() => HeadlessUnitTestSession.StartNew(typeof(UiSession)));

    public static HeadlessUnitTestSession Session => Lazy.Value;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont();

    /// <summary>Katalog zrzutów do przeglądu (KOBIAN_UI_SNAPSHOTS albo obok testów).</summary>
    public static string SnapshotDir()
    {
        var dir = Environment.GetEnvironmentVariable("KOBIAN_UI_SNAPSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "snapshots");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
