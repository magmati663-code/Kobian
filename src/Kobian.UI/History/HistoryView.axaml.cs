using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.History;

public sealed partial class HistoryView : UserControl
{
    public HistoryView() => AvaloniaXamlLoader.Load(this);
}
