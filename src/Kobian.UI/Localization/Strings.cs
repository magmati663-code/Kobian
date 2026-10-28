using System.Globalization;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace Kobian.UI.Localization;

/// <summary>Teksty UI z UiStrings.resx (PL neutralny, EN satelita — 6.9). Brak klucza = klucz w nawiasach (widoczny w teście pokrycia, nie wyjątek).</summary>
public static class Strings
{
    private static readonly ResourceManager Rm = new("Kobian.UI.Resources.UiStrings", typeof(Strings).Assembly);

    public static string Get(string key, CultureInfo? culture = null) =>
        Rm.GetString(key, culture ?? CultureInfo.CurrentUICulture) ?? "[" + key + "]";

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Wszystkie klucze neutralnego zasobu (testy pokrycia EN i braków).</summary>
    internal static IReadOnlyCollection<string> Keys(CultureInfo culture)
    {
        var set = Rm.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        var keys = new List<string>();
        if (set is null) return keys;
        foreach (System.Collections.DictionaryEntry e in set) keys.Add((string)e.Key);
        return keys;
    }
}

/// <summary>
/// Liczba mnoga wg reguł CLDR bez zależności (6.9): PL one (1) / few (2–4 poza 12–14) / many; EN one / other (few = many).
/// </summary>
public static class Plural
{
    public enum Category
    {
        One,
        Few,
        Many,
    }

    public static Category For(long n, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        n = Math.Abs(n);
        if (culture.TwoLetterISOLanguageName == "pl")
        {
            if (n == 1) return Category.One;
            var d = n % 10;
            var h = n % 100;
            return d is >= 2 and <= 4 && h is < 12 or > 14 ? Category.Few : Category.Many;
        }
        return n == 1 ? Category.One : Category.Many;
    }

    /// <summary><paramref name="baseKey"/> np. „Plural.Day” → „Plural.Day.One/Few/Many” sformatowane liczbą.</summary>
    public static string Text(string baseKey, long n, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentUICulture;
        return string.Format(c, Strings.Get($"{baseKey}.{For(n, c)}", c), n.ToString("N0", c)); // separator tysięcy wg kultury („12 480 plików”)
    }
}

/// <summary>Znacznik XAML <c>{l:T Klucz}</c> — tekst statyczny z zasobów w chwili ładowania widoku.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
