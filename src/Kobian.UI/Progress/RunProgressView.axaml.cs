using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Progress;

public sealed partial class RunProgressView : UserControl
{
    public RunProgressView() => AvaloniaXamlLoader.Load(this);
}
