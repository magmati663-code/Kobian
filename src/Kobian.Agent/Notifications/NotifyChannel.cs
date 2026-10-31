namespace Kobian.Agent.Notifications;

/// <summary>
/// Kanał, do którego trafia zdarzenie <c>run.finished</c> (BL-45): nazwa kolumny <c>channel</c> w <c>core_outbox</c>, tryb minimalny (SEC-9.6 — bez nazwy
/// zadania; ładunek budowany osobno dla każdego kanału) i filtr „tylko problemy” (9.7: wynik uruchomienia domyślnie tylko Warning/Failed).
/// </summary>
public sealed record NotifyChannel(string Name, bool Minimal, bool ProblemsOnly = false)
{
    /// <summary>Czy zdarzenie o danym statusie (succeeded/warning/failed/cancelled/skipped) trafia do kanału.</summary>
    public bool Accepts(string status) => !ProblemsOnly || status is "failed" or "warning";
}
