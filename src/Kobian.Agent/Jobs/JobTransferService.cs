using System.Globalization;
using System.Security.Cryptography;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Jobs;

/// <summary>Rozwiązanie konfliktu nazw przy imporcie (US-29): brak wyboru = konflikt blokuje cały import (KB-0124).</summary>
public enum ImportConflictPolicy
{
    Fail = 0,
    Skip = 1,
    Overwrite = 2,
    Rename = 3,
}

/// <summary>
/// Zmiana planowana/wykonana dla zadania z pliku: <c>Kind</c> = new | unchanged | conflict; <c>Action</c> = create | none | skip | overwrite | rename | blocked.
/// <c>NeedsCredentials</c> — <c>secretRef</c> nie ma wpisu w magazynie sekretów tego Agenta (kreator poprosi o poświadczenia — AC-29.1);
/// <c>MissingKeyMaterial</c> — plik klucza/nagłówka/hasła wskazany w definicji nie istnieje na tym komputerze.
/// </summary>
public sealed record ImportChange(string Job, string Kind, string Action, string? NewName = null, bool NeedsCredentials = false, bool MissingKeyMaterial = false);

/// <summary>Wynik importu: <c>Code</c> ≠ null = odrzucony w całości (nic nie zapisano); <c>Applied</c> = zapisano w katalogu.</summary>
public sealed record ImportResult(string? Code, IReadOnlyList<ImportProblem> Problems, IReadOnlyList<ImportChange> Changes, bool Applied);

/// <summary>Wynik eksportu: dokument kanoniczny albo kod KB z problemami (np. zadanie z definicją niezgodną ze schematem).</summary>
public sealed record ExportResult(string? Code, byte[]? Document, int Jobs, IReadOnlyList<ImportProblem> Problems);

/// <summary>
/// Eksport/import zadań (US-29, BL-47, SEC-6.7) nad katalogiem SQLite. Eksport: job_jobs + wyzwalacz cron → <see cref="JobExportFormat"/> (bez sekretów — definicja v1
/// zawiera tylko odwołania). Import: ścisły odczyt pliku, walidacja każdego zadania tym samym kodem co kreator (<see cref="JobDraftValidator"/>, <see cref="JobDefinition.Parse"/>),
/// plan zmian (nowe / bez zmian / konflikty nazw), a zapis — wszystko albo nic w jednej transakcji pisarza katalogu, z ponownym sprawdzeniem konfliktów w transakcji.
/// Import niczego nie uruchamia: zadania nowe i nadpisane są zapisywane jako WYŁĄCZONE (włącza je Administrator po sprawdzeniu poświadczeń — AC-29.1);
/// definicja v1 nie ma akcji ani skryptów, a pola akcji w pliku = odrzucenie (KB-0123).
/// </summary>
/// <remarks>
/// PROWIZORYCZNIE Qb86: AC-29.1 przewiduje import akcji (runSql, skrypt pre) jako nieaktywnych do zatwierdzenia przez Admin; definicja v1 nie ma modelu akcji
/// (kolumna quarantined_actions zostaje 0), więc plik z akcjami jest odrzucany w całości (KB-0123). Alternatywa po wprowadzeniu akcji: import z kwarantanną. Koszt zmiany: średni.
/// PROWIZORYCZNIE Qb87: każde nowe/nadpisane zadanie = wyłączone (także gdy w pliku „enabled”: true). Alternatywa: zachować stan z pliku. Koszt zmiany: mały.
/// PROWIZORYCZNIE Qb88: źródło musi być czytelne na serwerze docelowym już przy imporcie (reguła kreatora, KB-0101), inaczej cały plik odrzucony.
/// Alternatywa: ostrzeżenie w planie i zadanie wyłączone. Koszt zmiany: mały.
/// </remarks>
public sealed class JobTransferService(string catalogPath, CatalogWriter writer, JobDraftValidator validator, DestinationProbeService destinations, string programVersion, TimeProvider time)
{
    /// <summary>Katalog magazynu sekretów Agenta (<c>&lt;nazwa&gt;.json</c>) — do flagi <see cref="ImportChange.NeedsCredentials"/>; null = nie sprawdzaj.</summary>
    public string? SecretsDirectory { get; init; }

    public const string ImportSuffix = " (import)";

    // ---------------- eksport ----------------

    public ExportResult Export(IReadOnlyCollection<string>? jobNames)
    {
        var wanted = jobNames is { Count: > 0 } ? new HashSet<string>(jobNames, StringComparer.Ordinal) : null;
        var jobs = new List<ExportedJob>();
        var problems = new List<ImportProblem>();
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        foreach (var row in ReadJobs(c, null))
        {
            if (wanted is not null && !wanted.Remove(row.Name)) continue;
            if (row.Type is not ("files" or "database")) continue; // inne typy (replication, verify…) nie mają definicji v1 — poza zakresem US-29
            var (canonical, defProblems) = JobExportFormat.CanonicalDefinition(row.Type, row.Definition, "job:" + jobs.Count.ToString(CultureInfo.InvariantCulture));
            if (canonical is null)
            {
                problems.Add(new("job:" + row.Name, defProblems.Count > 0 ? defProblems[0].Code : KbCode.JobDefinitionInvalid, "stored definition does not match the export schema"));
                continue;
            }
            jobs.Add(new ExportedJob(row.Name, row.Type, row.Enabled, row.TimeZone, row.Misfire, row.MisfireWithin, row.Schedule, canonical));
        }
        if (wanted is { Count: > 0 }) return new ExportResult(KbCode.JobNotFound, null, 0, [.. wanted.Order(StringComparer.Ordinal).Select(n => new ImportProblem("job:" + n, KbCode.JobNotFound, "no such job"))]);
        if (problems.Count > 0) return new ExportResult(problems[0].Code, null, 0, problems);
        var doc = JobExportFormat.Write(new ExportDocument(programVersion, time.GetUtcNow(), jobs));
        if (doc.Length > JobExportFormat.MaxBytes)
            return new ExportResult(KbCode.ImportTooLarge, null, 0, [new("$", KbCode.ImportTooLarge, "export exceeds the import limit; export fewer jobs")]);
        return new ExportResult(null, doc, jobs.Count, []);
    }

    // ---------------- import ----------------

    /// <summary>
    /// Import pliku. <paramref name="validateOnly"/> = dry-run (tylko plan). Problemy pliku → nic nie zapisano (AC-29.2). Konflikt nazw przy
    /// <see cref="ImportConflictPolicy.Fail"/> → KB-0124 (w dry-run także — plan pokazuje, co by zablokowało zapis).
    /// </summary>
    public async Task<ImportResult> ImportAsync(byte[] document, bool validateOnly, ImportConflictPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        var (doc, problems) = JobExportFormat.Read(document);
        if (doc is null) return Rejected(problems);

        var semantic = new List<ImportProblem>();
        for (var i = 0; i < doc.Jobs.Count; i++) ValidateWithWizardRules(doc.Jobs[i], string.Create(CultureInfo.InvariantCulture, $"$.jobs[{i}]"), semantic);
        if (semantic.Count > 0) return Rejected(semantic);

        if (validateOnly)
        {
            using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
            return Plan(c, null, doc, policy, apply: false);
        }
        return await writer.ExecuteControlAsync((c, tx) => Plan(c, tx, doc, policy, apply: true), ct).ConfigureAwait(false);
    }

    private static ImportResult Rejected(IReadOnlyList<ImportProblem> problems)
    {
        // Kod najpoważniejszy: sekret > akcja > rozmiar > reszta (kolejność wykrycia).
        var code = problems.Any(p => p.Code == KbCode.ImportContainsSecret) ? KbCode.ImportContainsSecret
            : problems.Any(p => p.Code == KbCode.ImportActionsRejected) ? KbCode.ImportActionsRejected
            : problems.Any(p => p.Code == KbCode.ImportTooLarge) ? KbCode.ImportTooLarge
            : problems.Count > 0 ? problems[0].Code : KbCode.ImportFormatInvalid;
        return new ImportResult(code, problems, [], false);
    }

    /// <summary>Reguły kreatora (D6.7 — jedno źródło reguł dla UI, CLI i importu): źródło czytelne, cel poprawny i obsługiwany, brak nakładania. Nazwa — osobno (konflikty).</summary>
    private void ValidateWithWizardRules(ExportedJob job, string path, List<ImportProblem> problems)
    {
        var def = JobDefinition.Parse(job.Type, job.Definition); // już sprawdzona w JobExportFormat.Read
        // PROWIZORYCZNIE Qb85: materiał klucza tylko z lokalnych ścieżek — UNC/urządzenie w imporcie = odmowa (sprawdzenie istnienia pliku
        // nie może łączyć konta usługi z obcym serwerem SMB). Alternatywa: zezwolić bez sprawdzania istnienia. Koszt zmiany: mały.
        if (new[] { def.KeyFile, def.PasswordFile, def.RepoHeader }.Any(p => p is not null && IsRemoteOrDevicePath(p)))
            problems.Add(new(path + ".definition.key", KbCode.JobDefinitionInvalid, "key material must be on a local path"));
        if (job.Type == "files")
        {
            foreach (var p in validator.Validate(new JobDraft("x", [.. def.Sources.Where(s => s.Path is not null).Select(s => s.Path!)], def.Destination, null, null,
                         [.. def.Sources.Where(s => s.Remote is not null).Select(s => s.Remote!)])).Where(p => p.Field != "name"))
                problems.Add(new(path + p.Field switch { "sources" => ".definition.sources", "destination" => ".definition.destination", _ => "." + p.Field }, p.Code, "rejected by job validator"));
        }
        else if (destinations.ValidateShape(def.Destination) is { } dc)
        {
            problems.Add(new(path + ".definition.destination", dc, "rejected by job validator"));
        }
    }

    private ImportResult Plan(SqliteConnection c, SqliteTransaction? tx, ExportDocument doc, ImportConflictPolicy policy, bool apply)
    {
        var existing = ReadJobs(c, tx).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var taken = new HashSet<string>(existing.Keys, StringComparer.Ordinal);
        foreach (var j in doc.Jobs) taken.Add(j.Name);
        var changes = new List<ImportChange>();
        var problems = new List<ImportProblem>();
        var ops = new List<Action>();
        var now = Iso(time.GetUtcNow());
        for (var i = 0; i < doc.Jobs.Count; i++)
        {
            var j = doc.Jobs[i];
            var (needsCred, missingKey) = Readiness(j);
            if (!existing.TryGetValue(j.Name, out var cur))
            {
                changes.Add(new(j.Name, "new", "create", null, needsCred, missingKey));
                ops.Add(() => Insert(c, tx!, j, j.Name, now));
                continue;
            }
            if (Same(cur, j))
            {
                changes.Add(new(j.Name, "unchanged", "none", null, needsCred, missingKey));
                continue;
            }
            var path = string.Create(CultureInfo.InvariantCulture, $"$.jobs[{i}].name");
            switch (policy)
            {
                case ImportConflictPolicy.Skip:
                    changes.Add(new(j.Name, "conflict", "skip", null, needsCred, missingKey));
                    break;
                case ImportConflictPolicy.Overwrite when cur.Protected:
                    // SEC-7.6: zadanie „chronione” zmienia tylko Administrator ze step-up — nie import hurtowy.
                    changes.Add(new(j.Name, "conflict", "blocked", null, needsCred, missingKey));
                    problems.Add(new(path, KbCode.ImportNameConflict, "protected job cannot be overwritten by import"));
                    break;
                case ImportConflictPolicy.Overwrite:
                    changes.Add(new(j.Name, "conflict", "overwrite", null, needsCred, missingKey));
                    ops.Add(() => Overwrite(c, tx!, cur.JobId, j, now));
                    break;
                case ImportConflictPolicy.Rename:
                    var newName = FreeName(j.Name, taken);
                    taken.Add(newName);
                    changes.Add(new(j.Name, "conflict", "rename", newName, needsCred, missingKey));
                    ops.Add(() => Insert(c, tx!, j, newName, now));
                    break;
                default:
                    changes.Add(new(j.Name, "conflict", "blocked", null, needsCred, missingKey));
                    problems.Add(new(path, KbCode.ImportNameConflict, "job exists with a different definition"));
                    break;
            }
        }
        if (problems.Count > 0) return new ImportResult(KbCode.ImportNameConflict, problems, changes, false);
        if (!apply) return new ImportResult(null, [], changes, false);
        foreach (var op in ops) op(); // wyjątek SQLite = wycofanie całej transakcji pisarza (nic nie zapisano)
        return new ImportResult(null, [], changes, ops.Count > 0);
    }

    /// <summary>Nazwa wolna: „nazwa (import)”, „nazwa (import 2)”, … — z obcięciem bazy do limitu długości nazwy.</summary>
    internal static string FreeName(string name, ISet<string> taken)
    {
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? ImportSuffix : string.Create(CultureInfo.InvariantCulture, $" (import {n})");
            var baseName = name.Length + suffix.Length > JobDraftValidator.MaxNameLength ? name[..(JobDraftValidator.MaxNameLength - suffix.Length)].TrimEnd() : name;
            var candidate = baseName + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    internal static bool IsRemoteOrDevicePath(string p) => p.Length >= 2 && p[0] is '\\' or '/' && p[1] is '\\' or '/';

    private (bool NeedsCredentials, bool MissingKey) Readiness(ExportedJob j)
    {
        var def = JobDefinition.Parse(j.Type, j.Definition);
        var refs = new[] { def.Remote?.SecretRef, def.MsSql?.SecretRef }.Concat(def.Sources.Select(s => s.Remote?.SecretRef)).Where(r => r is not null);
        // secretRef z importowanego pliku jest niezaufany: krótszy niż prefiks wyrzucał ArgumentOutOfRangeException (RPC kończył się błędem wewnętrznym także w dry-run),
        // a „file:../../x” sondowało dowolną ścieżkę. Poprawny wpis ma kształt walidowany przez ValidSecretRef; niepoprawny liczy się jako brak poświadczeń.
        var needs = SecretsDirectory is not null && refs.Any(r => !JobDestination.ValidSecretRef(r) || !File.Exists(Path.Combine(SecretsDirectory, r!["file:".Length..] + ".json")));
        var missing = new[] { def.KeyFile, def.PasswordFile, def.RepoHeader }.Any(p => p is not null && !File.Exists(p));
        return (needs, missing);
    }

    private static bool Same(JobRow cur, ExportedJob j)
    {
        if (cur.Type != j.Type || cur.TimeZone != j.TimeZone || cur.Misfire != j.Misfire || cur.MisfireWithin != j.MisfireWithinSeconds || cur.Schedule != j.Schedule) return false;
        var (canonical, _) = JobExportFormat.CanonicalDefinition(cur.Type, cur.Definition);
        return canonical is not null && canonical.AsSpan().SequenceEqual(j.Definition);
    }

    private static void Insert(SqliteConnection c, SqliteTransaction tx, ExportedJob j, string name, string now)
    {
        var jobId = "job-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        Exec(c, tx, "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,misfire_within_sec,created_at,updated_at) VALUES ($i,$n,$y,$d,$h,0,$z,$m,$w,$t,$t);",
            ("$i", jobId), ("$n", name), ("$y", j.Type), ("$d", j.Definition), ("$h", SHA256.HashData(j.Definition)), ("$z", j.TimeZone), ("$m", j.Misfire),
            ("$w", j.MisfireWithinSeconds), ("$t", now));
        if (j.Schedule is { } s) UpsertTrigger(c, tx, jobId, s);
    }

    private static void Overwrite(SqliteConnection c, SqliteTransaction tx, string jobId, ExportedJob j, string now)
    {
        Exec(c, tx, "UPDATE job_jobs SET type=$y, definition=$d, definition_sha256=$h, enabled=0, time_zone=$z, misfire=$m, misfire_within_sec=$w, updated_at=$t WHERE job_id=$i;",
            ("$i", jobId), ("$y", j.Type), ("$d", j.Definition), ("$h", SHA256.HashData(j.Definition)), ("$z", j.TimeZone), ("$m", j.Misfire), ("$w", j.MisfireWithinSeconds), ("$t", now));
        if (j.Schedule is { } s) UpsertTrigger(c, tx, jobId, s);
        else Exec(c, tx, "UPDATE sched_triggers SET enabled=0 WHERE trigger_id=$t;", ("$t", ScheduleService.TriggerIdFor(jobId)));
    }

    private static void UpsertTrigger(SqliteConnection c, SqliteTransaction tx, string jobId, ExportedSchedule s)
    {
        var spec = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["cron"] = s.Cron });
        Exec(c, tx, "INSERT INTO sched_triggers(trigger_id,job_id,kind,spec,enabled) VALUES ($t,$j,'cron',$s,$e) ON CONFLICT(trigger_id) DO UPDATE SET job_id=$j, spec=$s, enabled=$e;",
            ("$t", ScheduleService.TriggerIdFor(jobId)), ("$j", jobId), ("$s", spec), ("$e", s.Enabled ? 1 : 0));
    }

    // ---------------- katalog ----------------

    private sealed record JobRow(string JobId, string Name, string Type, byte[] Definition, bool Enabled, bool Protected, string TimeZone, string Misfire, long? MisfireWithin, ExportedSchedule? Schedule);

    private static List<JobRow> ReadJobs(SqliteConnection c, SqliteTransaction? tx)
    {
        var list = new List<JobRow>();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT j.job_id, j.name, j.type, j.definition, j.enabled, j.protected, j.time_zone, j.misfire, j.misfire_within_sec, t.spec, t.enabled
            FROM job_jobs j LEFT JOIN sched_triggers t ON t.trigger_id = 'cron-' || j.job_id AND t.kind = 'cron'
            ORDER BY j.name;
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            ExportedSchedule? schedule = null;
            if (!rd.IsDBNull(9))
            {
                try
                {
                    schedule = new ExportedSchedule(CatalogScheduler.ParseCron((byte[])rd[9]), rd.GetInt64(10) != 0);
                }
                catch (KobianException)
                {
                    schedule = null; // uszkodzona specyfikacja wyzwalacza — eksport bez harmonogramu (ListSchedules pokazuje „?”)
                }
            }
            list.Add(new JobRow(rd.GetString(0), rd.GetString(1), rd.GetString(2), (byte[])rd[3], rd.GetInt64(4) != 0, rd.GetInt64(5) != 0, rd.GetString(6), rd.GetString(7),
                rd.IsDBNull(8) ? null : rd.GetInt64(8), schedule));
        }
        return list;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Skrót SHA-256 dokumentu (hex) — do audytu importu (który plik zaimportowano), bez treści.</summary>
    public static string DocumentHash(ReadOnlySpan<byte> document) => Convert.ToHexStringLower(SHA256.HashData(document));

}
