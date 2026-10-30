using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Wizard;

/// <summary>Panel wyboru folderu na serwerze: dwuklik albo Enter na folderze = wejście do niego.</summary>
public sealed partial class ServerBrowserPanel : UserControl
{
    private readonly ListBox? _folders;

    public ServerBrowserPanel()
    {
        AvaloniaXamlLoader.Load(this);
        _folders = this.FindControl<ListBox>("Folders");
        if (_folders is null) return;
        _folders.DoubleTapped += (_, _) => Open();
        _folders.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Open();
            e.Handled = true;
        };
    }

    private void Open()
    {
        if (DataContext is WizardViewModel vm && _folders?.SelectedItem is string folder) vm.BrowseOpenCommand.Execute(folder);
    }
}
