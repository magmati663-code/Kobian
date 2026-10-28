using Avalonia;

namespace Kobian.UI;

/// <summary>Klient desktop Kobian (D6.1, Avalonia 12). Działa bez podwyższenia; uprzywilejowaną pracę wykonuje usługa (6.7).</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
