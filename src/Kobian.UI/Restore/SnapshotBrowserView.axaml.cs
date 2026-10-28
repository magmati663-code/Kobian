using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Restore;

public sealed partial class SnapshotBrowserView : UserControl
{
    public SnapshotBrowserView() => AvaloniaXamlLoader.Load(this);
}

/// <summary>Folder pogrubiony, plik zwykły (bez osobnego stylu na wiersz).</summary>
public sealed class BoldConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly BoldConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
