using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Restore;

public sealed partial class DbRestoreView : UserControl
{
    public DbRestoreView() => AvaloniaXamlLoader.Load(this);
}
