namespace Kobian.Notifications.Alerts.Rules;

public enum RunOutcome
{
    Success,
    Warning,
    Failed,
}

/// <summary>
/// Fakty o jednym zakresie (zadanie/cel/repozytorium/host) wejściowe dla funkcji warunków D9.8.
/// Wartość niezmienna i bez ścieżek ani nazw — zakresy są nieprzejrzystymi identyfikatorami (SEC-9.6).
/// </summary>
public sealed record AlertFacts
{
    public RunOutcome? LastOutcome { get; init; }
    public bool RetriesExhausted { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    /// <summary>Od kiedy zakres istnieje i jest włączony — punkt odniesienia, gdy nie było jeszcze sukcesu.</summary>
    public DateTimeOffset? Since { get; init; }
    public TimeSpan? ScheduleInterval { get; init; }
    public TimeSpan? Rpo { get; init; }
    public double? FreeSpaceRatio { get; init; }
    public double? DaysToFull { get; init; }
    public int? DaysToExpiry { get; init; }
    public int? DaysSincePatch { get; init; }
    public bool? VendorSupported { get; init; }
    /// <summary>Obserwowany minus oczekiwany upływ czasu (A17): dodatni = skok w przód.</summary>
    public TimeSpan? ClockDelta { get; init; }
    public int DenialsInWindow { get; init; }
    public IReadOnlySet<string> Flags { get; init; } = new HashSet<string>();
}
