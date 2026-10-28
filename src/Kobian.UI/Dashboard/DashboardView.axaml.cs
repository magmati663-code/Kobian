using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Dashboard;

/// <summary>SCR-01 — bez logiki w code-behind (D6.6).</summary>
public sealed partial class DashboardView : UserControl
{
    public DashboardView() => AvaloniaXamlLoader.Load(this);
}
