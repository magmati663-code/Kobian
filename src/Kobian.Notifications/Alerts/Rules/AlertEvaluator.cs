using System.Text.RegularExpressions;

namespace Kobian.Notifications.Alerts.Rules;

/// <summary>Zakres reguły z faktami; <c>Scope</c> to nieprzejrzysty identyfikator (bez ścieżek i nazw — SEC-9.6).</summary>
public sealed record ScopedFacts(string Scope, AlertFacts Facts);

/// <summary>Wynik oceny jednego zakresu: nowy stan alertu, wiadomość do outbox i flaga eskalacji (drugi odbiorca).</summary>
public sealed record AlertUpdate(AlertRule Rule, string Scope, Alert? Alert, AlertOutbox Outbox, bool Escalate);

/// <summary>
/// AlertEvaluator.Tick (D9.8): dla reguł należnych i ich zakresów ocenia warunek, stosuje maszynę stanów D9.7
/// i zwraca zmiany. Czysty względem I/O — fakty dostarcza wywołujący; stan alertów trzyma w pamięci (trwałość: CatalogWriter).
/// </summary>
public sealed partial class AlertEvaluator
{
    private readonly AlertRuleCatalog _catalog;
    private readonly Func<AlertRule, IReadOnlyList<ScopedFacts>> _scopes;
    private readonly Dictionary<string, Alert> _alerts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastEvaluated = new(StringComparer.Ordinal);

    public AlertEvaluator(AlertRuleCatalog catalog, Func<AlertRule, IReadOnlyList<ScopedFacts>> scopes, IEnumerable<Alert>? existing = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        foreach (var a in existing ?? []) _alerts[a.DedupKey] = a;
    }

    /// <summary>Górny limit zakresów jednej reguły w jednym ticku (SEC-3.3, odporność na DoS); przekroczenie = błąd, nie ciche obcięcie.</summary>
    public const int MaxScopesPerRule = 10_000;

    public IReadOnlyCollection<Alert> Alerts => _alerts.Values;

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,128}$")]
    private static partial Regex ScopeRx();

    /// <summary>Reguła należna: okresowa po upływie interwału (lub pierwsza), pozostałe lokalne przy każdym ticku; zewnętrzna nigdy.</summary>
    public bool IsDue(AlertRule rule, DateTimeOffset now)
    {
        if (!rule.EvaluatedLocally) return false;
        if (rule.Trigger != TriggerKind.Periodic) return true;
        return !_lastEvaluated.TryGetValue(rule.Code, out var last) || now - last >= rule.Every;
    }

    public IReadOnlyList<AlertUpdate> Tick(DateTimeOffset now, bool quietHours)
    {
        var updates = new List<AlertUpdate>();
        foreach (var rule in _catalog.Rules.OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            if (!IsDue(rule, now)) continue;
            _lastEvaluated[rule.Code] = now;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var scopes = _scopes(rule);
            // Obcięcie po cichu zamknęłoby alerty „znikających” zakresów, dlatego przekroczenie limitu przerywa tick (fail closed).
            if (scopes.Count > MaxScopesPerRule)
                throw new InvalidOperationException($"Reguła {rule.Code}: liczba zakresów {scopes.Count} przekracza limit {MaxScopesPerRule}");
            foreach (var sf in scopes)
            {
                seen.Add(sf.Scope);
                Add(updates, Apply(rule, sf.Scope, sf.Facts, now, quietHours));
            }
            // Zakres, który zniknął (zadanie usunięte), nie może zostawić wiecznie otwartego alertu.
            foreach (var key in _alerts.Keys.Where(k => k.StartsWith(rule.Code + ":", StringComparison.Ordinal)).ToList())
            {
                var scope = key[(rule.Code.Length + 1)..];
                if (!seen.Contains(scope)) Add(updates, Apply(rule, scope, null, now, quietHours));
            }
        }
        return updates;
    }

    /// <summary>Ocena jednego zakresu; używana też przez komponenty zewnętrzne (np. heartbeat A12). Fakty null = warunek nieaktywny.</summary>
    public AlertUpdate? Apply(AlertRule rule, string scope, AlertFacts? facts, DateTimeOffset now, bool quietHours)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!ScopeRx().IsMatch(scope)) throw new ArgumentException("Zakres alertu musi być nieprzejrzystym identyfikatorem (bez ścieżek i nazw)", nameof(scope));
        var key = rule.Code + ":" + scope;
        _alerts.TryGetValue(key, out var current);
        var active = facts is not null && rule.Condition(facts, now);
        var (next, outbox) = AlertStateMachine.Tick(current, rule.Code, scope, rule.Severity, active, now, quietHours && rule.QuietHours, rule.Reminder);
        var escalate = false;
        if (next is { State: AlertState.Notified or AlertState.Acknowledged } n && rule.EscalateAfter is { } after
            && !n.Escalated && rule.Severity != AlertSeverity.Warning && now - n.OpenedAt >= after)
        {
            next = n with { Escalated = true };
            escalate = true;
        }
        if (next is null) return null;
        _alerts[key] = next;
        if (ReferenceEquals(next, current) && outbox == AlertOutbox.None) return null;
        return new AlertUpdate(rule, scope, next, outbox, escalate);
    }

    private static void Add(List<AlertUpdate> list, AlertUpdate? u)
    {
        if (u is not null) list.Add(u);
    }
}
