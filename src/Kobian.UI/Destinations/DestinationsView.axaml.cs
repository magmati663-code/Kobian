using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Destinations;

public sealed partial class DestinationsView : UserControl
{
    public DestinationsView() => AvaloniaXamlLoader.Load(this);
}
