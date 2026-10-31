using System.Diagnostics;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Runda 16: kopia w tle nie ma dławić pracy użytkownika. Usługa działa domyślnie z priorytetem „poniżej normalnego” (Windows: klasa priorytetu; Linux/macOS: nice 10) —
/// przy wolnym procesorze nic się nie zmienia, przy rywalizacji interaktywne programy mają pierwszeństwo. Zmienna <c>KOBIAN_PRIORITY</c>: normal | belownormal | idle.
/// </summary>
public static class ProcessPriority
{
    public static ProcessPriorityClass? Parse(string? value) => (value ?? "belownormal").Trim().ToLowerInvariant() switch
    {
        "normal" => ProcessPriorityClass.Normal,
        "belownormal" or "below-normal" or "low" => ProcessPriorityClass.BelowNormal,
        "idle" => ProcessPriorityClass.Idle,
        _ => null,
    };

    /// <summary>Ustawia priorytet bieżącego procesu; zwraca ustawioną klasę albo null (nieznana wartość / brak uprawnień — proces działa jak dotąd).</summary>
    public static ProcessPriorityClass? Apply(string? value)
    {
        var wanted = Parse(value);
        if (wanted is null) return null;
        try
        {
            using var p = Process.GetCurrentProcess();
            if (p.PriorityClass != wanted) p.PriorityClass = wanted.Value;
            return wanted;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
