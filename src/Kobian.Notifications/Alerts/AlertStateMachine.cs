namespace Kobian.Notifications.Alerts;

public enum AlertState
{
    Open,
    Notified,
    Acknowledged,
    Silenced,
    Resolved,
}

public enum AlertSeverity
{
    Warning,
    Critical,
    Security,
}

public enum AlertOutbox
{
    None,
    Opened,
    Reminder,
    Resolved,
}

/// <summary>Stan alertu (wiersz alert_alerts) — wartość niezmienna.</summary>
public sealed record Alert(
    string Code,
    string Scope,
    AlertSeverity Severity,
    AlertState State,
    DateTimeOffset OpenedAt,
    DateTimeOffset? LastNotifiedAt = null,
    int Reminders = 0,
    DateTimeOffset? AcknowledgedAt = null,
    DateTimeOffset? SilencedUntil = null,
    bool Escalated = false)
{
    /// <summary>Klucz deduplikacji: code:scope (D9.7) — jeden otwarty alert na klucz.</summary>
    public string DedupKey => Code + ":" + Scope;
}

/// <summary>
/// Ewaluacja jednego cyklu (D9.7 + pseudokod AlertEvaluator.Tick): wejście = czy warunek reguły jest spełniony, czas,
/// cisza nocna; wyjście = nowy stan alertu i wiadomość do outbox. Czysta funkcja.
/// </summary>
public static class AlertStateMachine
{
    public static readonly TimeSpan Reminder = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReopenAfterAck = TimeSpan.FromHours(72);
    public static readonly TimeSpan MaxSilence = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxSecuritySilence = TimeSpan.FromHours(24);

    public static (Alert? Alert, AlertOutbox Outbox) Tick(Alert? current, string code, string scope, AlertSeverity severity, bool conditionActive, DateTimeOffset now, bool quietHours, TimeSpan? reminder = null)
    {
        var remindEvery = reminder ?? Reminder;
        if (current is null || current.State == AlertState.Resolved)
        {
            if (!conditionActive) return (current, AlertOutbox.None);
            var a = new Alert(code, scope, severity, AlertState.Open, now);
            // Cisza nocna opóźnia tylko Ostrzeżenia; Krytyczne i Bezpieczeństwo — nigdy.
            return quietHours && severity == AlertSeverity.Warning ? (a, AlertOutbox.None) : (a with { State = AlertState.Notified, LastNotifiedAt = now }, AlertOutbox.Opened);
        }

        var c = current;
        if (!conditionActive)
        {
            // Open bez wysłanego powiadomienia — rozwiązanie bez wiadomości (np. sukces w ciągu 5 min).
            var outbox = c.State == AlertState.Open ? AlertOutbox.None : AlertOutbox.Resolved;
            return (c with { State = AlertState.Resolved }, outbox);
        }

        switch (c.State)
        {
            case AlertState.Open when !(quietHours && c.Severity == AlertSeverity.Warning):
                return (c with { State = AlertState.Notified, LastNotifiedAt = now }, AlertOutbox.Opened);
            case AlertState.Notified when now - c.LastNotifiedAt >= remindEvery && !(quietHours && c.Severity == AlertSeverity.Warning):
                return (c with { LastNotifiedAt = now, Reminders = c.Reminders + 1 }, AlertOutbox.Reminder);
            case AlertState.Acknowledged when now - c.AcknowledgedAt >= ReopenAfterAck:
                return (c with { State = AlertState.Notified, LastNotifiedAt = now, AcknowledgedAt = null }, AlertOutbox.Reminder);
            case AlertState.Silenced when now >= c.SilencedUntil:
                return (c with { State = AlertState.Notified, LastNotifiedAt = now, SilencedUntil = null }, AlertOutbox.Reminder);
            default:
                return (c, AlertOutbox.None);
        }
    }

    /// <summary>Potwierdzenie (Operator+): tylko z Notified.</summary>
    public static Alert Acknowledge(Alert a, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(a);
        return a.State == AlertState.Notified ? a with { State = AlertState.Acknowledged, AcknowledgedAt = now } : a;
    }

    /// <summary>Wyciszenie (Admin, audyt): maks. 7 dni; alerty Bezpieczeństwa maks. 24 h.</summary>
    public static Alert Silence(Alert a, DateTimeOffset now, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.State != AlertState.Notified) return a;
        var max = a.Severity == AlertSeverity.Security ? MaxSecuritySilence : MaxSilence;
        var d = duration > max ? max : duration;
        return d <= TimeSpan.Zero ? a : a with { State = AlertState.Silenced, SilencedUntil = now + d };
    }
}
