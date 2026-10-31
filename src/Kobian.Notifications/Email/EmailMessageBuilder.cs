using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Kobian.Notifications.Events;
using MimeKit;

namespace Kobian.Notifications.Email;

/// <summary>
/// Budowa wiadomości MimeKit z koperty CloudEvents z outboxa (BL-45; 9.7, SEC-9.1, SEC-9.6). Nagłówki wyłącznie przez API MimeKit (kodowanie RFC 2047,
/// brak ręcznego składania); z ładunku czytana jest zamknięta lista pól, każde sprawdzane (identyfikatory ASCII, kody KB-dddd, liczby, czasy ISO) —
/// wartość niepasująca jest pomijana/zastępowana „?”, nie przepisywana. Nazwa zadania tylko poza trybem minimalnym, oczyszczona ze znaków sterujących
/// i skrócona. Temat ≤ 120 znaków (R2.9.5). Treść: text/plain + HTML (z kodowaniem encji). Bez sekretów — budowniczy ich nie dostaje.
/// </summary>
public static class EmailMessageBuilder
{
    public const int MaxSubjectLength = 120;
    public const int MaxJobNameLength = 60;

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 8 };
    private static readonly string[] ResultKeys = ["succeeded", "warning", "failed", "cancelled", "skipped", "interrupted"];
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "succeeded", "warning", "failed", "cancelled", "skipped" };
    private static readonly HashSet<string> Causes = new(StringComparer.Ordinal) { "schedule", "manual", "misfire", "retry", "resume", "event" };

    /// <summary>Zdarzenia obsługiwane przez kanał e-mail.</summary>
    public static readonly IReadOnlySet<string> SupportedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        RunFinishedEvent.Type, "pl.kobian.report.ready.v1", AlertEvent.OpenedType, AlertEvent.ReminderType, AlertEvent.ResolvedType,
    };

    /// <exception cref="FormatException">Ładunek nie jest obsługiwanym zdarzeniem (trwały błąd — nie ponawiać).</exception>
    public static MimeMessage Build(EmailSettings settings, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (payload.Length is 0 or > CloudEvent.MaxBytes) throw new FormatException("Event payload is empty or too large.");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload.ToArray(), JsonOptions);
        }
        catch (JsonException)
        {
            throw new FormatException("Event payload is not valid JSON.");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Event payload is not an object.");
            var type = Str(root, "type");
            if (type is null || !SupportedTypes.Contains(type)) throw new FormatException("Unsupported event type for e-mail.");
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw new FormatException("Event has no data.");
            var eventId = Id(Str(root, "id"));
            var time = Time(Str(root, "time")) ?? DateTimeOffset.UnixEpoch;
            var lang = settings.Language;
            string subject;
            List<(string Label, string Value)> rows;
            List<string> notes = [];
            var isAlert = AlertEvent.Types.Contains(type);
            if (type == RunFinishedEvent.Type)
                (subject, rows) = RunFinished(settings, data, notes);
            else if (isAlert)
                (subject, rows) = Alert(settings, type, data, notes);
            else
                (subject, rows) = Report(settings, data, notes);

            var msg = new MimeMessage();
            msg.From.Add(Address(settings.From));
            foreach (var to in settings.To) msg.To.Add(Address(to));
            msg.Subject = Truncate(subject, MaxSubjectLength);
            msg.Date = time;
            var domain = settings.From[(settings.From.IndexOf('@', StringComparison.Ordinal) + 1)..];
            msg.MessageId = eventId is null ? MimeKit.Utils.MimeUtils.GenerateMessageId(domain) : eventId + "@" + domain;
            msg.Headers.Add("Auto-Submitted", "auto-generated"); // RFC 3834 — bez autoodpowiedzi
            msg.Headers.Add("X-Auto-Response-Suppress", "All");
            msg.Headers.Add("X-Kobian-Event-Type", type);
            if (eventId is not null) msg.Headers.Add("X-Kobian-Event-Id", eventId); // stały przy ponowieniach — deduplikacja u odbiorcy

            var intro = EmailTemplates.Get(lang, type == RunFinishedEvent.Type ? "Intro.Run" : isAlert ? "Intro.Alert" : "Intro.Report");
            var footer = EmailTemplates.Get(lang, "Footer");
            var text = new StringBuilder().Append(intro).Append("\n\n");
            foreach (var (label, value) in rows) text.Append(label).Append(": ").Append(value).Append('\n');
            foreach (var n in notes) text.Append('\n').Append(n).Append('\n');
            text.Append("\n-- \n").Append(footer).Append('\n');
            var html = new StringBuilder("<!DOCTYPE html><html><body style=\"font-family:sans-serif\"><p>").Append(WebUtility.HtmlEncode(intro)).Append("</p><table cellpadding=\"4\">");
            foreach (var (label, value) in rows)
                html.Append("<tr><th align=\"left\">").Append(WebUtility.HtmlEncode(label)).Append("</th><td>").Append(WebUtility.HtmlEncode(value)).Append("</td></tr>");
            html.Append("</table>");
            foreach (var n in notes) html.Append("<p>").Append(WebUtility.HtmlEncode(n)).Append("</p>");
            html.Append("<p style=\"color:#666\">").Append(WebUtility.HtmlEncode(footer)).Append("</p></body></html>");
            msg.Body = new BodyBuilder { TextBody = text.ToString(), HtmlBody = html.ToString() }.ToMessageBody();
            return msg;
        }
    }

    private static (string Subject, List<(string, string)> Rows) RunFinished(EmailSettings s, JsonElement d, List<string> notes)
    {
        var lang = s.Language;
        var status = Str(d, "status") is { } st && Statuses.Contains(st) ? st : "failed";
        var kb = Kb(Str(d, "kb"));
        string? jobId = null, jobName = null;
        if (d.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object)
        {
            jobId = Id(Str(job, "id"));
            if (!s.Minimal) jobName = JobName(Str(job, "name"));
        }
        var jobLabel = jobName ?? string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "JobById"), jobId is null ? "?" : jobId[..Math.Min(8, jobId.Length)]);
        var statusText = EmailTemplates.Get(lang, "Status." + status);
        var subject = string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "Subject.Run"), kb is null ? statusText : statusText + " " + kb, jobLabel, s.HostLabel);
        var rows = new List<(string, string)>
        {
            (EmailTemplates.Get(lang, "Host"), s.HostLabel),
            (EmailTemplates.Get(lang, "Status"), statusText),
        };
        if (kb is not null) rows.Add((EmailTemplates.Get(lang, "Code"), kb));
        if (jobName is not null) rows.Add((EmailTemplates.Get(lang, "Job"), jobName));
        rows.Add((EmailTemplates.Get(lang, "JobId"), jobId ?? "?"));
        if (!s.Minimal)
        {
            if (Time(Str(d, "startedUtc")) is { } started) rows.Add((EmailTemplates.Get(lang, "Started"), Iso(started)));
            AddNumber(rows, d, "durationSeconds", EmailTemplates.Get(lang, "Duration"));
            if (d.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                AddNumber(rows, stats, "files", EmailTemplates.Get(lang, "Files"));
                AddNumber(rows, stats, "bytesRaw", EmailTemplates.Get(lang, "BytesRaw"));
                AddNumber(rows, stats, "bytesNew", EmailTemplates.Get(lang, "BytesNew"));
            }
            AddNumber(rows, d, "attempt", EmailTemplates.Get(lang, "Attempt"));
            if (Str(d, "cause") is { } cause && Causes.Contains(cause)) rows.Add((EmailTemplates.Get(lang, "Cause"), cause));
            if (Id(Str(d, "snapshotId")) is { } snap) rows.Add((EmailTemplates.Get(lang, "Snapshot"), snap));
        }
        if (kb is not null) notes.Add(string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "CodeHint"), kb));
        return (subject, rows);
    }

    /// <summary>
    /// alert.* (D9.7, BL-82): temat „[Kobian] &lt;przejście&gt; A03: &lt;tytuł reguły&gt; — &lt;zakres&gt; (host)”. Tytuł i zalecane działanie wyłącznie z szablonu
    /// (po kodzie reguły z zamkniętego formatu), nie z ładunku; zakres = nazwa zadania (poza trybem minimalnym) albo skrócony identyfikator.
    /// </summary>
    private static (string Subject, List<(string, string)> Rows) Alert(EmailSettings s, string type, JsonElement d, List<string> notes)
    {
        var lang = s.Language;
        var code = Str(d, "code") is { } c && AlertEvent.IsRuleCode(c) && EmailTemplates.Has(lang, "Alert." + c + ".Title") ? c : null;
        var title = code is null ? EmailTemplates.Get(lang, "Alert.Unknown") : EmailTemplates.Get(lang, "Alert." + code + ".Title");
        var escalated = d.TryGetProperty("escalated", out var esc) && esc.ValueKind == JsonValueKind.True;
        var transition = type == AlertEvent.OpenedType ? "opened" : type == AlertEvent.ReminderType ? (escalated ? "escalated" : "reminder") : "resolved";
        var severity = Str(d, "severity") is { } sv && sv is "warning" or "critical" or "security" ? sv : "critical";
        var kb = Kb(Str(d, "kb"));
        string? kind = null, id = null, jobName = null;
        if (d.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object)
        {
            kind = Str(sc, "kind") is { } k && k is "job" or "destination" or "repository" or "host" ? k : null;
            id = Id(Str(sc, "id"));
        }
        if (kind == "job" && !s.Minimal && d.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object) jobName = JobName(Str(job, "name"));
        var scopeLabel = kind switch
        {
            "job" => jobName ?? string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "JobById"), Short(id)),
            "destination" => string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "Alert.Scope.destination"), id ?? "?"),
            "repository" => string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "Alert.Scope.repository"), Short(id)),
            _ => EmailTemplates.Get(lang, "Alert.Scope.host"),
        };
        var transitionText = EmailTemplates.Get(lang, "Alert.Transition." + transition);
        var subject = string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "Subject.Alert"), transitionText, (code ?? "A??") + ": " + title, scopeLabel, s.HostLabel);
        var rows = new List<(string, string)>
        {
            (EmailTemplates.Get(lang, "Host"), s.HostLabel),
            (EmailTemplates.Get(lang, "Alert.Rule"), (code ?? "?") + " — " + title),
            (EmailTemplates.Get(lang, "Alert.State"), transitionText),
            (EmailTemplates.Get(lang, "Alert.Severity"), EmailTemplates.Get(lang, "Alert.Severity." + severity)),
        };
        if (kb is not null) rows.Add((EmailTemplates.Get(lang, "Code"), kb));
        if (jobName is not null) rows.Add((EmailTemplates.Get(lang, "Job"), jobName));
        if (kind is "job") rows.Add((EmailTemplates.Get(lang, "JobId"), id ?? "?"));
        else if (kind is "destination" or "repository") rows.Add((EmailTemplates.Get(lang, "Alert.Scope"), scopeLabel));
        if (Time(Str(d, "openedUtc")) is { } opened) rows.Add((EmailTemplates.Get(lang, "Alert.Opened"), Iso(opened)));
        if (Time(Str(d, "resolvedUtc")) is { } resolved) rows.Add((EmailTemplates.Get(lang, "Alert.Resolved"), Iso(resolved)));
        if (!s.Minimal)
        {
            if (Time(Str(d, "lastSuccessUtc")) is { } ls) rows.Add((EmailTemplates.Get(lang, "Alert.LastSuccess"), Iso(ls)));
            AddNumber(rows, d, "reminders", EmailTemplates.Get(lang, "Alert.Reminders"));
            if (d.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Object)
            {
                AddSigned(rows, det, "clockDeltaSeconds", EmailTemplates.Get(lang, "Alert.Detail.clockDeltaSeconds"));
                AddNumber(rows, det, "deniedCalls", EmailTemplates.Get(lang, "Alert.Detail.deniedCalls"));
                AddNumber(rows, det, "pendingIdentities", EmailTemplates.Get(lang, "Alert.Detail.pendingIdentities"));
                AddNumber(rows, det, "suspectSnapshots", EmailTemplates.Get(lang, "Alert.Detail.suspectSnapshots"));
                AddNumber(rows, det, "damagedSnapshots", EmailTemplates.Get(lang, "Alert.Detail.damagedSnapshots"));
            }
        }
        if (transition != "resolved" && code is not null) notes.Add(EmailTemplates.Get(lang, "Alert." + code + ".Action"));
        if (transition == "resolved") notes.Add(EmailTemplates.Get(lang, "Alert.ResolvedNote"));
        if (kb is not null) notes.Add(string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "CodeHint"), kb));
        return (subject, rows);
    }

    private static string Short(string? id) => id is null ? "?" : id[..Math.Min(8, id.Length)];

    private static void AddSigned(List<(string, string)> rows, JsonElement e, string name, string label)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            rows.Add((label, n.ToString(CultureInfo.InvariantCulture)));
    }

    private static (string Subject, List<(string, string)> Rows) Report(EmailSettings s, JsonElement d, List<string> notes)
    {
        var lang = s.Language;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (d.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object)
            foreach (var k in ResultKeys)
                counts[k] = Num(results, k) ?? 0;
        var from = Time(Str(d, "fromUtc"));
        var to = Time(Str(d, "toUtc"));
        var day = to?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?";
        var subject = string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "Subject.Report"), s.HostLabel + " " + day,
            counts.GetValueOrDefault("succeeded"), counts.GetValueOrDefault("failed"));
        var rows = new List<(string, string)>
        {
            (EmailTemplates.Get(lang, "Host"), s.HostLabel),
            (EmailTemplates.Get(lang, "Window"), (from is null ? "?" : Iso(from.Value)) + " – " + (to is null ? "?" : Iso(to.Value))),
        };
        foreach (var k in ResultKeys) rows.Add((EmailTemplates.Get(lang, "Result." + k), counts.GetValueOrDefault(k).ToString(CultureInfo.InvariantCulture)));
        var failed = (Str(d, "failedJobIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Id).Where(x => x is not null).Take(20).ToArray();
        if (failed.Length > 0) rows.Add((EmailTemplates.Get(lang, "FailedJobs"), string.Join(", ", failed)));
        AddNumber(rows, d, "deadNotifications", EmailTemplates.Get(lang, "Dead"));
        string? anchor = null;
        if (d.TryGetProperty("auditAnchor", out var a) && a.ValueKind == JsonValueKind.Object && Str(a, "text") is { } t && IsAnchor(t)) anchor = t;
        rows.Add((EmailTemplates.Get(lang, "Anchor"), anchor ?? EmailTemplates.Get(lang, "NoAnchor")));
        if (anchor is not null) notes.Add(string.Format(CultureInfo.InvariantCulture, EmailTemplates.Get(lang, "AnchorHint"), anchor));
        return (subject, rows);
    }

    private static MailboxAddress Address(string text) =>
        EmailAddressPolicy.TryParse(text, out var a) ? a! : throw new FormatException("Invalid e-mail address in settings.");

    private static void AddNumber(List<(string, string)> rows, JsonElement e, string name, string label)
    {
        if (Num(e, name) is { } n) rows.Add((label, n.ToString(CultureInfo.InvariantCulture)));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;

    internal static string? Id(string? v) =>
        v is { Length: > 0 and <= 64 } && v.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? v : null;

    private static string? Kb(string? v) =>
        v is { Length: 7 } && v.StartsWith("KB-", StringComparison.Ordinal) && v[3..].All(char.IsAsciiDigit) ? v : null;

    private static bool IsAnchor(string t)
    {
        var i = t.IndexOf(':', StringComparison.Ordinal);
        return i is > 0 and <= 19 && t[..i].All(char.IsAsciiDigit) && t.Length - i - 1 == 64 && t[(i + 1)..].All(char.IsAsciiHexDigitLower);
    }

    private static DateTimeOffset? Time(string? v) =>
        v is not null && DateTimeOffset.TryParseExact(v, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : null;

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Nazwa zadania: znaki sterujące i formatujące (np. CR/LF, RLO) usunięte, białe znaki zwinięte, ≤ 60 znaków.</summary>
    internal static string? JobName(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var sb = new StringBuilder();
        foreach (var ch in v)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) continue;
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(ch);
            if (sb.Length >= MaxJobNameLength) break;
        }
        var r = sb.ToString().Trim();
        return r.Length == 0 ? null : r;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
