using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Shell;

public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
