using System.Globalization;
using Kobian.UI.Localization;

namespace Kobian.UI.Dashboard;

/// <summary>Stan alertu widziany przez UI (D9.7): open → notified → acknowledged / silenced.</summary>
public enum AlertState
{
    Open,
    Notified,
    Acknowledged,
    Silenced,
}

/// <summary>Waga alertu; kolejność = ważność (bezpieczeństwo najpierw — spójnie z kolejnością z Agenta).</summary>
public enum AlertSeverity
{
    Warning,
    Critical,
    Security,
}

/// <summary>
/// Aktywny alert z katalogu (GetStatus.alerts, BL-82). Wyłącznie metadane: kod reguły A01–A17, zakres, nazwa zadania/identyfikator, czas, kod KB — bez ścieżek i treści (NFR-11, SEC-9.6).
/// </summary>
public sealed record DashboardAlert(
    string Id,
    string Code,
    AlertSeverity Severity,
    AlertState State,
    string ScopeKind,
    string Subject,
    DateTimeOffset OpenedUtc,
    string? Kb = null,
    int Reminders = 0,
    bool Escalated = false);

/// <summary>Opcja czasu wyciszenia: minuty (zakres RPC 1…10080) + klucz etykiety w zasobach.</summary>
public sealed record SilenceOption(int Minutes, string LabelKey)
{
    public string Label => Strings.Get(LabelKey);

    public override string ToString() => Label;
}

/// <summary>Wiersz listy alertów na pulpicie: tytuł z zasobów po kodzie reguły + jedna zalecana reakcja; akcje wg roli (6.7).</summary>
public sealed class AlertTileViewModel(DashboardAlert alert, CallerRole role, string openedText)
{
    public DashboardAlert Alert { get; } = alert;

    public string Id => Alert.Id;

    public string Code => Alert.Code;

    /// <summary>Tytuł reguły (klucz <c>Alert.A04</c>); nieznany kod (nowszy Agent) → ogólny tytuł z kodem — nigdy pusty wiersz.</summary>
    public string Title
    {
        get
        {
            var t = Strings.Get("Alert." + Alert.Code);
            return t.StartsWith('[') ? Strings.Format("Alert.Unknown", Alert.Code) : t;
        }
    }

    public string? Subject => string.IsNullOrEmpty(Alert.Subject) ? null : Alert.Subject;

    public string? Kb => Alert.Kb;

    public bool IsSecurity => Alert.Severity == AlertSeverity.Security;

    public bool IsCritical => Alert.Severity == AlertSeverity.Critical;

    public bool IsWarning => Alert.Severity == AlertSeverity.Warning;

    public string? EscalatedText => Alert.Escalated ? Strings.Get("Alert.Escalated") : null;

    public string SeverityText => Strings.Get("Alert.Severity." + Alert.Severity);

    public string StateText => Strings.Get("Alert.State." + Alert.State);

    public string OpenedText { get; } = openedText;

    public bool Awaiting => Alert.State is AlertState.Open or AlertState.Notified;

    /// <summary>Potwierdzenie: Operator; tylko alert oczekujący na reakcję (inaczej Agent odpowie KB-6120).</summary>
    public bool CanAcknowledge => role >= CallerRole.Operator && Awaiting;

    /// <summary>Wyciszenie: Administrator; nie dla już wyciszonego. Alerty bezpieczeństwa Agent wycisza najdłużej na 24 h (stan maszyny).</summary>
    public bool CanSilence => role >= CallerRole.Admin && Alert.State != AlertState.Silenced;

    /// <summary>Dostępne czasy wyciszenia (Qb106 — rozstrzygnięte it. 133): 1 h, 4 h, 24 h; 7 dni nie dla alertów bezpieczeństwa (Agent i tak skraca je do 24 h — nie obiecujemy dłużej).</summary>
    public IReadOnlyList<SilenceOption> SilenceOptions { get; } = BuildSilenceOptions(alert.Severity);

    /// <summary>Wybrany czas wyciszenia; domyślnie 4 h (<see cref="DashboardViewModel.DefaultSilenceMinutes"/>).</summary>
    public SilenceOption SelectedSilence { get; set; } = BuildSilenceOptions(alert.Severity).First(o => o.Minutes == DashboardViewModel.DefaultSilenceMinutes);

    private static SilenceOption[] BuildSilenceOptions(AlertSeverity severity) => severity == AlertSeverity.Security
        ? [new(60, "Alert.Silence.1h"), new(240, "Alert.Silence.4h"), new(1440, "Alert.Silence.24h")]
        : [new(60, "Alert.Silence.1h"), new(240, "Alert.Silence.4h"), new(1440, "Alert.Silence.24h"), new(10080, "Alert.Silence.7d")];

    public string AccessibleName => $"{SeverityText}: {Title}. {StateText}. {OpenedText}";
}
