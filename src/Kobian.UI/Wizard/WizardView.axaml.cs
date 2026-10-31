using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Kobian.UI.Localization;

namespace Kobian.UI.Wizard;

public sealed partial class WizardView : UserControl
{
    public WizardView()
    {
        AvaloniaXamlLoader.Load(this);
        if (this.FindControl<Button>("BrowseSources") is { } sources) sources.Click += OnBrowseSources;
        if (this.FindControl<Button>("BrowseDestination") is { } dest) dest.Click += OnBrowseDestination;
    }

    /// <summary>
    /// Systemowe okno wyboru folderów. Ścieżki dotyczą komputera z interfejsem — Agent działa na tym samym komputerze (pipe/gniazdo lokalne, D2.4).
    /// Brak okna na platformie (np. bez portalu XDG) → pusta lista; zostaje wpisanie ścieżki.
    /// </summary>
    private async Task<IReadOnlyList<string>> PickAsync(string title, bool multiple)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } sp) return [];
        try
        {
            var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = multiple, Title = title });
            return [.. picked.Select(f => f.TryGetLocalPath()).OfType<string>()];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private async void OnBrowseSources(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WizardViewModel vm) return;
        foreach (var path in await PickAsync(Strings.Get("Wizard.S1.BrowseTitle"), multiple: true)) vm.AddPickedSource(path);
    }

    private async void OnBrowseDestination(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WizardViewModel vm) return;
        if ((await PickAsync(Strings.Get("Wizard.S2.BrowseTitle"), multiple: false)) is [var path]) vm.DestinationPath = path;
    }
}
