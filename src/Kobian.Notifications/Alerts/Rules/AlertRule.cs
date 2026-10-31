using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kobian.Notifications.Alerts.Rules;

public enum RuleScope { Job, Target, Repo, Host }

public enum TriggerKind { Event, Periodic, AfterRun, AfterPlan, Reconcile, Audit, Probe, External }

/// <summary>Reguła alertu wczytana z danych (D9.8). Warunek jest już skompilowaną funkcją z kodu.</summary>
public sealed record AlertRule(
    string Code,
    int Version,
    string Category,
    AlertSeverity Severity,
    TriggerKind Trigger,
    TimeSpan? Every,
    RuleScope Scope,
    string Fn,
    AlertCondition Condition,
    string? Kb,
    bool QuietHours,
    TimeSpan Reminder,
    TimeSpan? EscalateAfter)
{
    /// <summary>Reguły zewnętrzne (A12, heartbeat) nie są ewaluowane lokalnie — wynik zgłasza komponent zewnętrzny.</summary>
    public bool EvaluatedLocally => Trigger != TriggerKind.External;
}

/// <summary>Katalog reguł A01–A17 (D9.8): walidacja schematu przy ładowaniu; błąd = FormatException (komponent nie startuje).</summary>
public sealed partial class AlertRuleCatalog
{
    private const string ResourceName = "Kobian.Notifications.Alerts.Rules.alert-rules.json";
    private static readonly string[] RuleKeys = ["code", "version", "category", "severity", "trigger", "scope", "condition", "kb", "quietHours", "reminderHours", "escalateAfterHours"];

    private readonly Dictionary<string, AlertRule> _rules;

    private AlertRuleCatalog(Dictionary<string, AlertRule> rules) => _rules = rules;

    public IReadOnlyCollection<AlertRule> Rules => _rules.Values;

    public AlertRule Get(string code) => _rules[code];

    public static AlertRuleCatalog LoadEmbedded()
    {
        using var s = typeof(AlertRuleCatalog).Assembly.GetManifestResourceStream(ResourceName)
                      ?? throw new FormatException("Brak osadzonych reguł alertów");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public static AlertRuleCatalog Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 }); }
        catch (JsonException ex) { throw new FormatException("Reguły: niepoprawny JSON", ex); }
        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) throw new FormatException("Reguły: oczekiwano tablicy");
        var rules = new Dictionary<string, AlertRule>(StringComparer.Ordinal);
        foreach (var e in root.EnumerateArray())
        {
            AlertRule rule;
            try { rule = ParseRule(e); }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { throw new FormatException("Reguła: brak lub zły typ pola", ex); }
            if (!rules.TryAdd(rule.Code, rule)) throw new FormatException($"Zduplikowany kod reguły {rule.Code}");
        }
        for (var i = 1; i <= 17; i++)
            if (!rules.ContainsKey($"A{i:00}")) throw new FormatException($"Brak reguły A{i:00}");
        return new AlertRuleCatalog(rules);
    }

    [GeneratedRegex("^A[0-9]{2}$")]
    private static partial Regex CodeRx();

    [GeneratedRegex("^KB-[0-9]{4}$")]
    private static partial Regex KbRx();

    private static AlertRule ParseRule(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("Reguła musi być obiektem");
        var names = e.EnumerateObject().Select(p => p.Name).ToList();
        foreach (var n in names) if (Array.IndexOf(RuleKeys, n) < 0) throw new FormatException($"Nieznane pole reguły: {n}");
        foreach (var k in RuleKeys) if (!names.Contains(k)) throw new FormatException($"Brak pola reguły: {k}");

        var code = Str(e, "code");
        if (!CodeRx().IsMatch(code)) throw new FormatException($"Niepoprawny kod reguły: {code}");
        var version = Int(e, "version", 1, 1000, code);
        var sev = Str(e, "severity") switch
        {
            "warning" => AlertSeverity.Warning,
            "critical" => AlertSeverity.Critical,
            "security" => AlertSeverity.Security,
            var x => throw new FormatException($"{code}: nieznana ważność {x}"),
        };
        var scope = Str(e, "scope") switch
        {
            "job" => RuleScope.Job,
            "target" => RuleScope.Target,
            "repo" => RuleScope.Repo,
            "host" => RuleScope.Host,
            var x => throw new FormatException($"{code}: nieznany zakres {x}"),
        };

        var t = e.GetProperty("trigger");
        if (t.ValueKind != JsonValueKind.Object) throw new FormatException($"{code}: trigger musi być obiektem");
        var kind = Str(t, "kind") switch
        {
            "event" => TriggerKind.Event,
            "periodic" => TriggerKind.Periodic,
            "afterRun" => TriggerKind.AfterRun,
            "afterPlan" => TriggerKind.AfterPlan,
            "reconcile" => TriggerKind.Reconcile,
            "audit" => TriggerKind.Audit,
            "probe" => TriggerKind.Probe,
            "external" => TriggerKind.External,
            var x => throw new FormatException($"{code}: nieznany trigger {x}"),
        };
        TimeSpan? every = null;
        if (kind == TriggerKind.Periodic)
        {
            every = TimeSpan.FromSeconds(Int(t, "everySeconds", 60, 7 * 86400, code));
            if (t.EnumerateObject().Count() != 2) throw new FormatException($"{code}: trigger periodic ma pola kind i everySeconds");
        }
        else if (t.EnumerateObject().Count() != 1) throw new FormatException($"{code}: trigger bez dodatkowych pól");

        var c = e.GetProperty("condition");
        if (c.ValueKind != JsonValueKind.Object || c.EnumerateObject().Count() != 2) throw new FormatException($"{code}: condition = {{fn, args}}");
        var fn = Str(c, "fn");
        AlertCondition cond;
        try { cond = AlertConditions.Compile(fn, c.GetProperty("args")); }
        catch (KeyNotFoundException) { throw new FormatException($"{code}: condition wymaga pola args"); }
        catch (FormatException ex) { throw new FormatException($"{code}: {ex.Message}"); }

        string? kb = null;
        var kbEl = e.GetProperty("kb");
        if (kbEl.ValueKind == JsonValueKind.String)
        {
            kb = kbEl.GetString();
            if (kb is null || !KbRx().IsMatch(kb)) throw new FormatException($"{code}: niepoprawny kod KB");
        }
        else if (kbEl.ValueKind != JsonValueKind.Null) throw new FormatException($"{code}: kb = tekst lub null");

        var q = e.GetProperty("quietHours");
        if (q.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException($"{code}: quietHours = true/false");
        var esc = Int(e, "escalateAfterHours", 0, 24 * 90, code);
        // Cisza nocna nie dotyczy alertów krytycznych i bezpieczeństwa (9.4): reguła nie może tego obchodzić.
        if (q.GetBoolean() && sev != AlertSeverity.Warning) throw new FormatException($"{code}: cisza nocna tylko dla ostrzeżeń");
        return new AlertRule(code, version, Str(e, "category"), sev, kind, every, scope, fn, cond, kb, q.GetBoolean(),
            TimeSpan.FromHours(Int(e, "reminderHours", 1, 24 * 90, code)), esc == 0 ? null : TimeSpan.FromHours(esc));
    }

    private static string Str(JsonElement o, string key)
    {
        var p = o.GetProperty(key);
        return p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : throw new FormatException($"Pole {key}: oczekiwano tekstu");
    }

    private static int Int(JsonElement o, string key, int min, int max, string code)
    {
        var p = o.GetProperty(key);
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) && v >= min && v <= max
            ? v : throw new FormatException($"{code}: {key} = liczba całkowita {min}–{max}");
    }
}
