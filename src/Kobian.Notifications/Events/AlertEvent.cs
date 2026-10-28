using System.Globalization;
using Kobian.Notifications.Alerts;

namespace Kobian.Notifications.Events;

/// <summary>Przejście alertu, które generuje powiadomienie (D9.7): otwarcie, przypomnienie (także eskalacja), rozwiązanie.</summary>
public enum AlertTransition
{
    Opened,
    Reminder,
    Resolved,
}

/// <summary>
/// Dane powiadomienia o alercie — wyłącznie kody, identyfikatory, liczniki i znaczniki czasu; nazwa zadania tylko poza trybem minimalnym
/// (SEC-9.6). Szczegóły (<see cref="Details"/>) z zamkniętej listy kluczy liczbowych (<see cref="AlertEvent.DetailKeys"/>).
/// </summary>
public sealed record AlertNotice(
    string AlertId,
    string Code,
    AlertSeverity Severity,
    string Category,
    string? KbCode,
    string Scope,
    AlertTransition Transition,
    DateTimeOffset OpenedUtc,
    DateTimeOffset AtUtc,
    int Reminders,
    bool Escalated,
    string? JobName = null,
    DateTimeOffset? LastSuccessUtc = null,
    IReadOnlyDictionary<string, long>? Details = null);

/// <summary>
/// Zdarzenia <c>pl.kobian.alert.{opened,reminder,resolved}.v1</c> (D9.6, D9.7). Pola danych z zamkniętej listy, każde sprawdzane: kod reguły A01–A99,
/// kod KB w formacie KB-dddd, zakres <c>job:&lt;id&gt;</c> / <c>dest:&lt;id&gt;</c> / <c>repo:&lt;id&gt;</c> / <c>host</c> z identyfikatorem ASCII.
/// Brak ścieżek, nazw plików, hostów i sekretów; tryb minimalny pomija <c>job.name</c> (jak run.finished). Eskalacja = przypomnienie z <c>escalated=true</c>
/// (PROWIZORYCZNIE Qb94: drugi odbiorca nie jest jeszcze konfigurowalny — eskalacja trafia do tych samych kanałów).
/// </summary>
public static class AlertEvent
{
    public const string OpenedType = "pl.kobian.alert.opened.v1";
    public const string ReminderType = "pl.kobian.alert.reminder.v1";
    public const string ResolvedType = "pl.kobian.alert.resolved.v1";

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { OpenedType, ReminderType, ResolvedType };

    /// <summary>Dozwolone klucze szczegółów (liczby całkowite): skok zegara A17, odmowy A15, tożsamości oczekujące A09, kopie podejrzane/uszkodzone A04.</summary>
    public static readonly IReadOnlySet<string> DetailKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "clockDeltaSeconds", "deniedCalls", "pendingIdentities", "suspectSnapshots", "damagedSnapshots",
    };

    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal) { "availability", "integrity", "capacity", "security", "maintenance" };

    public static string TypeOf(AlertTransition t) => t switch
    {
        AlertTransition.Opened => OpenedType,
        AlertTransition.Reminder => ReminderType,
        _ => ResolvedType,
    };

    public static string SeverityText(AlertSeverity s) => s switch
    {
        AlertSeverity.Warning => "warning",
        AlertSeverity.Critical => "critical",
        _ => "security",
    };

    /// <summary>Rozbiór zakresu na (rodzaj, identyfikator); null = zakres spoza formatu.</summary>
    public static (string Kind, string? Id)? ParseScope(string scope)
    {
        if (scope == "host") return ("host", null);
        var i = scope.IndexOf(':', StringComparison.Ordinal);
        if (i <= 0) return null;
        var kind = scope[..i] switch
        {
            "job" => "job",
            "dest" => "destination",
            "repo" => "repository",
            _ => null,
        };
        var id = scope[(i + 1)..];
        return kind is null || !IsId(id) ? null : (kind, id);
    }

    public static byte[] Build(string eventId, string hostId, AlertNotice n, bool minimal)
    {
        ArgumentNullException.ThrowIfNull(n);
        if (!IsHex32(n.AlertId)) throw new ArgumentException("Invalid alert id.", nameof(n));
        if (!IsRuleCode(n.Code)) throw new ArgumentException("Invalid rule code.", nameof(n));
        if (n.KbCode is not null && !IsKb(n.KbCode)) throw new ArgumentException("Invalid KB code.", nameof(n));
        if (!Categories.Contains(n.Category)) throw new ArgumentException("Unknown category.", nameof(n));
        if (n.Reminders < 0) throw new ArgumentException("Negative reminders.", nameof(n));
        var scope = ParseScope(n.Scope) ?? throw new ArgumentException("Invalid scope.", nameof(n));
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["alertId"] = n.AlertId,
            ["code"] = n.Code,
            ["severity"] = SeverityText(n.Severity),
            ["category"] = n.Category,
            ["kb"] = n.KbCode,
            ["transition"] = n.Transition switch { AlertTransition.Opened => "opened", AlertTransition.Reminder => "reminder", _ => "resolved" },
            ["escalated"] = n.Escalated,
            ["reminders"] = n.Reminders,
            ["scope"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = scope.Kind, ["id"] = scope.Id },
            ["openedUtc"] = Iso(n.OpenedUtc),
            ["messageKey"] = "Alert." + n.Code + ".Title",
        };
        if (scope.Kind == "job")
            data["job"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = scope.Id, ["name"] = n.JobName };
        if (n.Transition == AlertTransition.Resolved) data["resolvedUtc"] = Iso(n.AtUtc);
        if (n.LastSuccessUtc is { } ls) data["lastSuccessUtc"] = Iso(ls);
        if (n.Details is { Count: > 0 } det)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in det.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!DetailKeys.Contains(k)) throw new ArgumentException("Unknown detail key.", nameof(n));
                d[k] = v;
            }
            data["details"] = d;
        }
        return CloudEvent.Serialize(eventId, hostId, TypeOf(n.Transition), n.AtUtc, n.Scope, data, minimal);
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static bool IsId(string v) => v is { Length: > 0 and <= 64 } && v.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    internal static bool IsRuleCode(string c) => c is { Length: 3 } && c[0] == 'A' && char.IsAsciiDigit(c[1]) && char.IsAsciiDigit(c[2]);

    private static bool IsHex32(string v) => v is { Length: 32 } && v.All(char.IsAsciiHexDigitLower);

    private static bool IsKb(string c) => c.Length == 7 && c.StartsWith("KB-", StringComparison.Ordinal) && c[3..].All(char.IsAsciiDigit);
}
