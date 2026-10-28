using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Wizard;

public sealed partial class WizardView : UserControl
{
    public WizardView() => AvaloniaXamlLoader.Load(this);
}
