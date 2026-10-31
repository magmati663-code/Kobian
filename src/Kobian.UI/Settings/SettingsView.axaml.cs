using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Kobian.UI.Settings;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}

/// <summary>Tekst ostrzeżenia w kolorze tokenu Kb.Brush.Warning.Text, inaczej domyślny.</summary>
public sealed class WarningBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly WarningBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true && Application.Current?.TryGetResource("Kb.Brush.Warning.Text", Application.Current.ActualThemeVariant, out var b) == true && b is IBrush brush
            ? brush
            : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
