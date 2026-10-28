using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kobian.Domain;
using Kobian.Scheduler.Timing;

namespace Kobian.Agent.Jobs;

/// <summary>Harmonogram zadania w eksporcie: wyrażenie cron (strefa — w zadaniu) i stan wyzwalacza.</summary>
public sealed record ExportedSchedule(string Cron, bool Enabled);

/// <summary>
/// Zadanie w pliku <c>kobian-export v1</c>: nazwa, typ, strefa i misfire z job_jobs, harmonogram z sched_triggers i definicja v1 w postaci kanonicznej
/// (bez sekretów — <c>secretRef</c> to nazwa w magazynie Agenta, pliki klucza/hasła to ścieżki, nigdy treść). <c>Enabled</c> — informacyjnie (import zawsze tworzy wyłączone).
/// </summary>
public sealed record ExportedJob(string Name, string Type, bool Enabled, string TimeZone, string Misfire, long? MisfireWithinSeconds, ExportedSchedule? Schedule, byte[] Definition);

/// <summary>Dokument eksportu: wersja programu, czas eksportu (UTC) i zadania (posortowane po nazwie).</summary>
public sealed record ExportDocument(string ProgramVersion, DateTimeOffset ExportedAtUtc, IReadOnlyList<ExportedJob> Jobs);

/// <summary>Błąd importu: ścieżka JSON (np. <c>$.jobs[0].definition.destination.root</c>), kod KB i stały powód po angielsku — bez wartości pól (NFR-11).</summary>
public sealed record ImportProblem(string Path, string Code, string Reason);

/// <summary>
/// Format <c>kobian-export v1</c> (US-29, AC-29.1–29.2, SEC-6.7, SEC-3.3; docs/job-export-format.md). Czysta logika bez katalogu:
/// <list type="bullet">
/// <item>zapis kanoniczny: klucze obiektów posortowane porządkowo (ordinal) na każdym poziomie, bez wartości <c>null</c>, wcięcie 2 spacje, LF, UTF-8 bez BOM,
/// znaki spoza ASCII jako <c>\uXXXX</c> (domyślny koder) — ten sam stan katalogu daje te same bajty (poza <c>exportedAt</c>);</item>
/// <item>odczyt ścisły: limit rozmiaru, głębokości, liczby zadań i długości napisów (KB-0121), duplikaty kluczy i pola nieznane = odrzucenie (KB-0120),
/// pola i wartości wyglądające na sekret (KB-0122), pola akcji/skryptów (KB-0123). Cały plik albo nic — lista wszystkich znalezionych problemów (AC-29.2).</item>
/// </list>
/// </summary>
public static class JobExportFormat
{
    public const string FormatName = "kobian-export";
    public const int FormatVersion = 1;

    /// <summary>
    /// PROWIZORYCZNIE Qb83: 60 KiB — import idzie jedną wiadomością gRPC, a host Agenta przyjmuje ≤ 64 KiB (SEC-3.3, AgentHost).
    /// Eksport większy niż limit jest odrzucany (KB-0121), żeby nie powstał plik niemożliwy do zaimportowania (rada: <c>--job</c>).
    /// Alternatywa: strumień klienta w kawałkach z limitem 1 MiB. Koszt zmiany: mały (nowe RPC, ten sam parser).
    /// </summary>
    public const int MaxBytes = Kobian.Api.Contracts.ApiConventions.MaxJobsDocumentBytes;

    /// <summary>Najgłębsza zagnieżdżona struktura: dokument(1) → jobs(2) → zadanie(3) → definition(4) → destination/database/retention(5) → sources[](6); zapas do 8.</summary>
    public const int MaxDepth = 8;

    public const int MaxJobs = 200;
    public const int MaxStringLength = 1024;
    public const int MaxProblems = 100;
    public const long MaxMisfireWithinSeconds = 7L * 24 * 3600;

    private static readonly string[] MisfireValues = ["runOnceIfMissed", "skip", "runIfMissedWithin"];

    // ---------------- schemat ----------------

    private enum K
    {
        Str,
        Int,
        Bool,
        Obj,
        StrArray,
        ObjArray,
    }

    private sealed record Node(K Kind, bool Required = false, IReadOnlyDictionary<string, Node>? Children = null);

    private static Dictionary<string, Node> Fields(params (string Name, Node Node)[] f) => f.ToDictionary(x => x.Name, x => x.Node, StringComparer.Ordinal);

    private static readonly Node Retention = new(K.Obj, Children: Fields(
        ("keepLast", new(K.Int)), ("keepDaily", new(K.Int)), ("keepWeekly", new(K.Int)), ("keepMonthly", new(K.Int)), ("keepYearly", new(K.Int)),
        ("keepWithinDays", new(K.Int)), ("lockDays", new(K.Int))));

    private static readonly Node Verify = new(K.Obj, Children: Fields(("enabled", new(K.Bool)), ("intervalDays", new(K.Int)), ("samplePercent", new(K.Int))));

    private static readonly Node Destination = new(K.Obj, true, Fields(
        ("scheme", new(K.Str, true)), ("root", new(K.Str, true)), ("host", new(K.Str)), ("port", new(K.Int)), ("hostKey", new(K.Str)), ("secretRef", new(K.Str))));

    private static readonly Node Key = new(K.Obj, true, Fields(("file", new(K.Str, true)), ("passwordFile", new(K.Str)), ("repoHeader", new(K.Str))));

    private static readonly Node Database = new(K.Obj, true, Fields(
        ("engine", new(K.Str)), ("server", new(K.Str, true)), ("name", new(K.Str, true)), ("auth", new(K.Str)), ("secretRef", new(K.Str)), ("certificate", new(K.Str)),
        ("strict", new(K.Bool)), ("allowLocalhostUntrusted", new(K.Bool)), ("staging", new(K.Str, true)), ("sqlStaging", new(K.Str)), ("lowImpact", new(K.Bool)),
        ("conflictWaitMinutes", new(K.Int))));

    private static readonly Node FilesDefinition = new(K.Obj, true, Fields(
        ("version", new(K.Int, true)), ("sources", new(K.StrArray, true)), ("destination", Destination), ("key", Key), ("packTargetSize", new(K.Int)), ("retention", Retention), ("verify", Verify)));

    private static readonly Node DatabaseDefinition = new(K.Obj, true, Fields(
        ("version", new(K.Int, true)), ("database", Database), ("destination", Destination), ("key", Key), ("packTargetSize", new(K.Int)), ("retention", Retention), ("verify", Verify)));

    private static readonly Node Schedule = new(K.Obj, Children: Fields(("cron", new(K.Str, true)), ("enabled", new(K.Bool))));

    // „definition” sprawdzana osobno (schemat zależy od „type”) — tu tylko jako obiekt wymagany.
    private static readonly Node Job = new(K.Obj, Children: Fields(
        ("name", new(K.Str, true)), ("type", new(K.Str, true)), ("enabled", new(K.Bool)), ("timeZone", new(K.Str, true)), ("misfire", new(K.Str)),
        ("misfireWithinSeconds", new(K.Int)), ("schedule", Schedule), ("definition", new(K.Obj, true))));

    private static readonly Node Document = new(K.Obj, true, Fields(
        ("format", new(K.Str, true)), ("version", new(K.Int, true)), ("programVersion", new(K.Str, true)), ("exportedAt", new(K.Str, true)),
        ("jobs", new(K.ObjArray, true, Job.Children))));

    // ---------------- zapis ----------------

    /// <summary>Dokument w postaci kanonicznej. Definicje muszą przejść <see cref="CanonicalDefinition"/> (wywołujący — eksport z katalogu).</summary>
    public static byte[] Write(ExportDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var jobs = new JsonArray();
        foreach (var j in doc.Jobs.OrderBy(j => j.Name, StringComparer.Ordinal))
        {
            var o = new JsonObject
            {
                ["name"] = j.Name,
                ["type"] = j.Type,
                ["enabled"] = j.Enabled,
                ["timeZone"] = j.TimeZone,
                ["misfire"] = j.Misfire,
                ["definition"] = JsonNode.Parse(j.Definition),
            };
            if (j.MisfireWithinSeconds is { } mw) o["misfireWithinSeconds"] = mw;
            if (j.Schedule is { } s) o["schedule"] = new JsonObject { ["cron"] = s.Cron, ["enabled"] = s.Enabled };
            jobs.Add(o);
        }
        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = FormatVersion,
            ["programVersion"] = doc.ProgramVersion,
            ["exportedAt"] = doc.ExportedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["jobs"] = jobs,
        };
        return Canonical(root, indented: true);
    }

    /// <summary>
    /// Definicja v1 zadania w postaci kanonicznej (zwarta): tylko pola schematu, klucze posortowane, bez <c>null</c>.
    /// Problemy schematu/sekretów/akcji → lista (pusta = poprawna); semantykę sprawdza dalej <see cref="JobDefinition.Parse"/>.
    /// </summary>
    public static (byte[]? Canonical, IReadOnlyList<ImportProblem> Problems) CanonicalDefinition(string type, ReadOnlySpan<byte> json, string path = "$")
    {
        var problems = new List<ImportProblem>();
        if (type is not ("files" or "database"))
        {
            problems.Add(new(path, KbCode.JobDefinitionInvalid, "unsupported job type"));
            return (null, problems);
        }
        if (json.Length is 0 or > JobDefinition.MaxBytes)
        {
            problems.Add(new(path, KbCode.ImportTooLarge, "definition size out of range"));
            return (null, problems);
        }
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray(), DocOptions);
            CheckNode(doc.RootElement, type == "files" ? FilesDefinition : DatabaseDefinition, path, problems);
            ScanValues(doc.RootElement, path, problems);
            return problems.Count > 0 ? (null, problems) : (Canonical(JsonNode.Parse(doc.RootElement.GetRawText())!, indented: false), problems);
        }
        catch (JsonException)
        {
            problems.Add(new(path, KbCode.ImportFormatInvalid, "invalid JSON"));
            return (null, problems);
        }
    }

    private static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = MaxDepth + 1, AllowDuplicateProperties = false };

    private static byte[] Canonical(JsonNode node, bool indented)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented, NewLine = "\n", IndentSize = 2 }))
            WriteSorted(w, node);
        if (indented) ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    private static void WriteSorted(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                w.WriteStartObject();
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (v is null) continue; // null = brak pola (postać kanoniczna)
                    w.WritePropertyName(k);
                    WriteSorted(w, v);
                }
                w.WriteEndObject();
                break;
            case JsonArray a:
                w.WriteStartArray();
                foreach (var v in a) WriteSorted(w, v);
                w.WriteEndArray();
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out var n):
                w.WriteNumberValue(n);
                break;
            case null:
                w.WriteNullValue();
                break;
            default:
                node.WriteTo(w);
                break;
        }
    }

    // ---------------- odczyt ----------------

    /// <summary>Wynik odczytu: dokument (gdy brak problemów) albo lista problemów (cały plik odrzucony — AC-29.2).</summary>
    public static (ExportDocument? Doc, IReadOnlyList<ImportProblem> Problems) Read(ReadOnlySpan<byte> input)
    {
        var problems = new List<ImportProblem>();
        if (input.StartsWith("﻿"u8)) input = input[3..]; // BOM (Notatnik)
        if (input.Length == 0) return Fail(problems, "$", KbCode.ImportFormatInvalid, "empty file");
        if (input.Length > MaxBytes) return Fail(problems, "$", KbCode.ImportTooLarge, "file too large");
        if (Depth(input) > MaxDepth) return Fail(problems, "$", KbCode.ImportTooLarge, "nesting too deep");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(input.ToArray(), DocOptions);
        }
        catch (JsonException ex)
        {
            return Fail(problems, "$", KbCode.ImportFormatInvalid,
                ex.LineNumber is { } l ? string.Create(CultureInfo.InvariantCulture, $"invalid JSON (line {l + 1})") : "invalid JSON");
        }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Fail(problems, "$", KbCode.ImportFormatInvalid, "document must be an object");
            if (r.TryGetProperty("format", out var f) && (f.ValueKind != JsonValueKind.String || f.GetString() != FormatName))
                return Fail(problems, "$.format", KbCode.ImportFormatInvalid, "not a kobian-export file");
            if (r.TryGetProperty("version", out var v) && (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != FormatVersion))
                return Fail(problems, "$.version", KbCode.ImportFormatInvalid, "unsupported format version");
            if (r.TryGetProperty("jobs", out var ja) && ja.ValueKind == JsonValueKind.Array && ja.GetArrayLength() > MaxJobs)
                return Fail(problems, "$.jobs", KbCode.ImportTooLarge, "too many jobs");

            CheckNode(r, Document, "$", problems);
            ScanValues(r, "$", problems);
            if (problems.Count > 0) return (null, Cap(problems));

            var jobs = new List<ExportedJob>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var i = 0;
            foreach (var je in r.GetProperty("jobs").EnumerateArray())
            {
                var p = string.Create(CultureInfo.InvariantCulture, $"$.jobs[{i++}]");
                var job = ReadJob(je, p, problems);
                if (job is null) continue;
                if (!names.Add(job.Name)) problems.Add(new(p + ".name", KbCode.ImportFormatInvalid, "duplicate job name in file"));
                else jobs.Add(job);
            }
            if (problems.Count > 0) return (null, Cap(problems));
            var at = DateTimeOffset.TryParseExact(r.GetProperty("exportedAt").GetString(), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : (DateTimeOffset?)null;
            if (at is null) return Fail(problems, "$.exportedAt", KbCode.ImportFormatInvalid, "invalid timestamp");
            return (new ExportDocument(r.GetProperty("programVersion").GetString()!, at.Value, jobs), problems);
        }
    }

    private static ExportedJob? ReadJob(JsonElement e, string p, List<ImportProblem> problems)
    {
        var before = problems.Count;
        var name = e.GetProperty("name").GetString()!;
        if (string.IsNullOrWhiteSpace(name) || name.Length > JobDraftValidator.MaxNameLength || name.Any(char.IsControl) || name.Trim() != name)
            problems.Add(new(p + ".name", KbCode.JobDefinitionInvalid, "invalid job name"));
        var type = e.GetProperty("type").GetString()!;
        if (type is not ("files" or "database")) problems.Add(new(p + ".type", KbCode.JobDefinitionInvalid, "unsupported job type"));
        var tz = e.GetProperty("timeZone").GetString()!;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)) problems.Add(new(p + ".timeZone", KbCode.ScheduleInvalid, "unknown time zone"));
        var misfire = e.TryGetProperty("misfire", out var mf) && mf.ValueKind == JsonValueKind.String ? mf.GetString()! : "skip";
        if (!MisfireValues.Contains(misfire, StringComparer.Ordinal)) problems.Add(new(p + ".misfire", KbCode.ScheduleInvalid, "unknown misfire policy"));
        long? within = null;
        if (e.TryGetProperty("misfireWithinSeconds", out var mw) && mw.ValueKind == JsonValueKind.Number)
        {
            within = mw.GetInt64();
            if (within is < 0 or > MaxMisfireWithinSeconds || misfire != "runIfMissedWithin")
                problems.Add(new(p + ".misfireWithinSeconds", KbCode.ScheduleInvalid, "out of range or not applicable"));
        }
        var enabled = !e.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
        ExportedSchedule? schedule = null;
        if (e.TryGetProperty("schedule", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            var cron = s.GetProperty("cron").GetString()!;
            schedule = new ExportedSchedule(cron, !s.TryGetProperty("enabled", out var se) || se.ValueKind != JsonValueKind.False);
            if (problems.Count == before && !ValidCron(cron, tz)) problems.Add(new(p + ".schedule.cron", KbCode.ScheduleInvalid, "invalid schedule"));
        }
        byte[]? def = null;
        if (type is "files" or "database")
        {
            var raw = Encoding.UTF8.GetBytes(e.GetProperty("definition").GetRawText());
            var (canonical, defProblems) = CanonicalDefinition(type, raw, p + ".definition");
            problems.AddRange(defProblems);
            if (canonical is not null)
            {
                try
                {
                    _ = JobDefinition.Parse(type, canonical); // ta sama walidacja co przy uruchomieniu (cel, ścieżki, retencja, sekcja database)
                    def = canonical;
                }
                catch (KobianException ex)
                {
                    problems.Add(new(p + ".definition", ex.Code, "definition rejected by validator"));
                }
            }
        }
        return problems.Count > before || def is null ? null : new ExportedJob(name, type, enabled, tz, misfire, within, schedule, def);
    }

    private static bool ValidCron(string cron, string tz)
    {
        if (string.IsNullOrWhiteSpace(cron) || cron.Length > ScheduleService.MaxCronLength || cron.Trim() != cron) return false;
        try
        {
            return CronSchedule.Parse(cron, tz).RespectsMinimumInterval(DateTimeOffset.UtcNow);
        }
        catch (KobianException)
        {
            return false;
        }
    }

    private static (ExportDocument?, IReadOnlyList<ImportProblem>) Fail(List<ImportProblem> problems, string path, string code, string reason)
    {
        problems.Add(new(path, code, reason));
        return (null, problems);
    }

    private static List<ImportProblem> Cap(List<ImportProblem> problems) => problems.Count > MaxProblems ? problems.GetRange(0, MaxProblems) : problems;

    /// <summary>Głębokość zagnieżdżenia liczona na bajtach przed parsowaniem (SEC-3.3): nawiasy poza napisami. Bez rekursji.</summary>
    internal static int Depth(ReadOnlySpan<byte> s)
    {
        int depth = 0, max = 0;
        var inString = false;
        for (var i = 0; i < s.Length; i++)
        {
            var b = s[i];
            if (inString)
            {
                if (b == (byte)'\\') i++;
                else if (b == (byte)'"') inString = false;
                continue;
            }
            switch (b)
            {
                case (byte)'"': inString = true; break;
                case (byte)'{' or (byte)'[':
                    if (++depth > max) max = depth;
                    if (max > MaxDepth) return max;
                    break;
                case (byte)'}' or (byte)']': depth--; break;
            }
        }
        return max;
    }

    /// <summary>Kształt wg schematu: typy, pola wymagane, pola nieznane (rozróżnione: sekret / akcja / nieznane). <c>null</c> w polu opcjonalnym = brak pola.</summary>
    private static void CheckNode(JsonElement e, Node n, string path, List<ImportProblem> problems)
    {
        if (problems.Count >= MaxProblems) return;
        switch (n.Kind)
        {
            case K.Str:
                if (e.ValueKind != JsonValueKind.String) problems.Add(new(path, KbCode.ImportFormatInvalid, "expected string"));
                else if (e.GetString()!.Length > MaxStringLength) problems.Add(new(path, KbCode.ImportTooLarge, "string too long"));
                return;
            case K.Int:
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out _)) problems.Add(new(path, KbCode.ImportFormatInvalid, "expected integer"));
                return;
            case K.Bool:
                if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) problems.Add(new(path, KbCode.ImportFormatInvalid, "expected boolean"));
                return;
            case K.StrArray:
                if (e.ValueKind != JsonValueKind.Array) { problems.Add(new(path, KbCode.ImportFormatInvalid, "expected array")); return; }
                var si = 0;
                foreach (var x in e.EnumerateArray()) CheckNode(x, new Node(K.Str), string.Create(CultureInfo.InvariantCulture, $"{path}[{si++}]"), problems);
                return;
            case K.ObjArray:
                if (e.ValueKind != JsonValueKind.Array) { problems.Add(new(path, KbCode.ImportFormatInvalid, "expected array")); return; }
                var oi = 0;
                foreach (var x in e.EnumerateArray()) CheckNode(x, new Node(K.Obj, Children: n.Children), string.Create(CultureInfo.InvariantCulture, $"{path}[{oi++}]"), problems);
                return;
            case K.Obj:
                if (e.ValueKind != JsonValueKind.Object) { problems.Add(new(path, KbCode.ImportFormatInvalid, "expected object")); return; }
                if (n.Children is null) return; // „definition” — sprawdzana osobno wg typu
                foreach (var p in e.EnumerateObject())
                {
                    var childPath = path + "." + SafeName(p.Name);
                    if (!n.Children.TryGetValue(p.Name, out var child))
                    {
                        problems.Add(ClassifyUnknown(p.Name, childPath));
                        continue;
                    }
                    if (p.Value.ValueKind == JsonValueKind.Null)
                    {
                        if (child.Required) problems.Add(new(childPath, KbCode.ImportFormatInvalid, "required field is null"));
                        continue;
                    }
                    CheckNode(p.Value, child, childPath, problems);
                }
                foreach (var (name, child) in n.Children)
                    if (child.Required && !e.TryGetProperty(name, out _)) problems.Add(new(path + "." + name, KbCode.ImportFormatInvalid, "missing required field"));
                return;
        }
    }

    /// <summary>Nazwa pola w ścieżce błędu: tylko bezpieczne znaki, ≤ 64 (nazwa pochodzi z wrogiego pliku i trafia do odpowiedzi/audytu).</summary>
    private static string SafeName(string name)
    {
        var s = new string([.. name.Take(64).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '?')]);
        return s.Length == 0 ? "?" : s;
    }

    private static ImportProblem ClassifyUnknown(string name, string path)
    {
        var n = name.ToLowerInvariant().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (ActionMarkers.Any(m => n.Contains(m, StringComparison.Ordinal)) || n is "pre" or "post" or "run" or "before" or "after")
            return new(path, KbCode.ImportActionsRejected, "scripts and actions are not accepted");
        if (SecretNameMarkers.Any(m => n.Contains(m, StringComparison.Ordinal)))
            return new(path, KbCode.ImportContainsSecret, "secret-like field");
        return new(path, KbCode.ImportFormatInvalid, "unknown field");
    }

    private static readonly string[] ActionMarkers = ["script", "action", "command", "hook", "exec", "runsql", "sql", "service", "shell", "powershell", "cmd", "program", "process"];

    private static readonly string[] SecretNameMarkers = ["password", "passwd", "pwd", "secret", "token", "apikey", "privatekey", "credential", "connectionstring", "passphrase"];

    // ---------------- wykrywanie sekretów w wartościach ----------------

    /// <summary>
    /// Każda wartość tekstowa w dokumencie (także w polach znanych) sprawdzana heurystyką <see cref="LooksLikeSecret"/>; pin <c>hostKey</c> („SHA256:…”) wyłączony,
    /// a w polach ścieżek (<see cref="PathFields"/>) tylko wzorce jednoznaczne (bez entropii — losowe nazwy folderów tymczasowych dawałyby fałszywe alarmy).
    /// </summary>
    private static void ScanValues(JsonElement e, string path, List<ImportProblem> problems, string? field = null)
    {
        if (problems.Count >= MaxProblems) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "hostKey" && p.Value.ValueKind == JsonValueKind.String && JobDestinationPin(p.Value.GetString()!)) continue;
                    ScanValues(p.Value, path + "." + SafeName(p.Name), problems, p.Name);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var x in e.EnumerateArray()) ScanValues(x, string.Create(CultureInfo.InvariantCulture, $"{path}[{i++}]"), problems, field);
                break;
            case JsonValueKind.String:
                if (LooksLikeSecret(e.GetString()!, field is not null && PathFields.Contains(field))) problems.Add(new(path, KbCode.ImportContainsSecret, "value looks like a secret"));
                break;
        }
    }

    private static readonly HashSet<string> PathFields = new(StringComparer.Ordinal) { "sources", "root", "file", "passwordFile", "repoHeader", "certificate", "staging", "sqlStaging", "programVersion" };

    private static bool JobDestinationPin(string v) =>
        v.Length is > 7 and <= JobDestination.MaxHostKeyLength && v.StartsWith("SHA256:", StringComparison.Ordinal)
        && v.AsSpan(7).IndexOfAnyExcept(Base64Chars) < 0;

    private static readonly System.Buffers.SearchValues<char> Base64Chars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");

    /// <summary>
    /// Heurystyka (SEC-6.7, AC-01.3): blok PEM, przypisanie „password=/pwd=/secret=/token=…”, poświadczenie w URL (<c>scheme://user:pass@</c>), JWT,
    /// a w każdym fragmencie między spacjami/dwukropkami/przecinkami (poza polami ścieżek): ciąg szesnastkowy ≥ 32 znaki albo ciąg ≥ 20 znaków o entropii ≥ 4,2 bita/znak z literami i cyframi.
    /// PROWIZORYCZNIE Qb84: fałszywy alarm = odrzucenie (bezpieczniej); alternatywa: ostrzeżenie zamiast odrzucenia. Koszt zmiany: mały.
    /// </summary>
    public static bool LooksLikeSecret(string v, bool isPath = false)
    {
        ArgumentNullException.ThrowIfNull(v);
        if (v.Contains("-----BEGIN", StringComparison.Ordinal) || v.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) return true;
        var lower = v.ToLowerInvariant();
        foreach (var m in AssignmentMarkers)
        {
            var at = lower.IndexOf(m, StringComparison.Ordinal);
            if (at < 0) continue;
            var rest = lower.AsSpan(at + m.Length).TrimStart();
            if (rest.Length > 0 && rest[0] is '=' or ':') return true;
        }
        var scheme = v.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var authority = v.AsSpan(scheme + 3);
            var end = authority.IndexOfAny('?', '#', ' '); // bez cięcia na '/': nieprzekodowane hasło może go zawierać
            if (end >= 0) authority = authority[..end];
            var atSign = authority.LastIndexOf('@');
            if (atSign > 0 && authority[..atSign].Contains(':')) return true;
        }
        if (v.StartsWith("eyJ", StringComparison.Ordinal) && v.Count(c => c == '.') == 2) return true;
        if (isPath) return false;
        foreach (var token in v.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 32 && token.All(char.IsAsciiHexDigit)) return true;
            if (token.Length >= 20 && token.Any(char.IsAsciiDigit) && token.Any(char.IsAsciiLetter) && Entropy(token) >= 4.2) return true;
        }
        return false;
    }

    private static readonly string[] AssignmentMarkers = ["password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "accesskey", "access_key"];

    // Bez '/' i '\\': base64 zawiera '/', a pola ścieżek i tak pomijają entropię.
    private static readonly char[] TokenSeparators = [' ', '\t', ':', ',', ';'];

    private static double Entropy(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in s) counts[c] = counts.GetValueOrDefault(c) + 1;
        var h = 0.0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / s.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }
}
