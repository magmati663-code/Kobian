using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Kobian.UI.Restore;

public sealed partial class RestoreView : UserControl
{
    public RestoreView()
    {
        AvaloniaXamlLoader.Load(this);
        if (this.FindControl<Button>("PickFolder") is { } pick) pick.Click += OnPickFolder;
    }

    /// <summary>
    /// Wybór folderu w oknie systemowym. Uwaga: ścieżka dotyczy komputera z interfejsem — Agent działa na tym samym komputerze (pipe lokalny, D2.4),
    /// więc to ta sama przestrzeń nazw; zdalny Agent wymagałby wyboru po stronie Agenta (poza 1.0).
    /// </summary>
    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RestoreViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } sp) return;
        try
        {
            var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = vm.Title });
            if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path) vm.Target = path;
        }
        catch (InvalidOperationException)
        {
            // brak okna wyboru na tej platformie — zostaje wpisanie ścieżki
        }
    }
}
