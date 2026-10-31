using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Jobs;

public sealed partial class JobsView : UserControl
{
    public JobsView() => AvaloniaXamlLoader.Load(this);
}
