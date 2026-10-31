using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Kobian.UI.Shell;

/// <summary>Klucz zasobu (np. ikony „Kb.Icon.Nav.Jobs”) → zasób aplikacji z bieżącego motywu; brak = null (bez wyjątku w renderowaniu).</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public static ResourceKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var r) ? r : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
