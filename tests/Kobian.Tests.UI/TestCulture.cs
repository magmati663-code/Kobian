using System.Globalization;
using System.Runtime.CompilerServices;

namespace Kobian.Tests.UI;

/// <summary>Teksty interfejsu sprawdzane po polsku niezależnie od języka systemu (runner macOS/Windows bywa angielski); testy angielskich tekstów ustawiają kulturę same.</summary>
internal static class TestCulture
{
#pragma warning disable CA2255 // inicjator modułu w zestawie testów (nie biblioteka) — jedyne miejsce przed uruchomieniem pierwszego testu
    [ModuleInitializer]
    internal static void PolishByDefault()
#pragma warning restore CA2255
    {
        var pl = new CultureInfo("pl-PL");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = pl;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = pl;
    }
}
