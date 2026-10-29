using System.Globalization;
using System.Text.Json;
using Kobian.Domain;

namespace Kobian.Cli.Commands;

/// <summary>
/// Polecenia CLI (6.8, BL-47): <c>status</c>, <c>job run</c>, <c>snapshots</c>, <c>restore</c>. Kody wyjścia: 0 OK, 1 Warning, 2 Failed,
/// 3 brak usługi (AC-37.1), 4 klient zbyt stary (AC-37.2), 64 błąd składni. Wynik JSON na stdout (schemat wersjonowany), komunikaty na stderr.
/// </summary>
public static class CliApp
{
    public const int ExitOk = 0, ExitWarning = 1, ExitFailed = 2, ExitUnavailable = 3, ExitClientTooOld = 4, ExitUsage = 64, ExitCancelled = 130;
    public const string SchemaVersion = "1";

    public static async Task<int> RunAsync(string[] args, IAgentClient client, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var a = new Args(args);
        if (a.OptionMissingValue() is { } missing)
        {
            await stderr.WriteLineAsync($"usage: option {missing} requires a value / opcja {missing} wymaga wartości").ConfigureAwait(false);
            return ExitUsage;
        }
        try
        {
            return a.Verb switch
            {
                "status" => await Status(a, client, stdout, ct).ConfigureAwait(false),
                "job" when a.Positional(1) == "run" && a.Positional(2) is { } job => await Run(a, client, job, stdout, ct).ConfigureAwait(false),
                "init" when a.Positional(1) is { } name && a.Option("--source") is { } src && a.Option("--dest") is { } dest && a.Option("--password-file") is { } pw =>
                    await Init(a, client, name, src, dest, pw, stdout, stderr, ct).ConfigureAwait(false),
                "schedule" when a.Positional(1) == "list" => await ScheduleList(a, client, stdout, ct).ConfigureAwait(false),
                "schedule" when a.Positional(1) == "set" && a.Positional(2) is { } sj && a.Option("--cron") is { } cron =>
                    await ScheduleSet(a, client, sj, cron, stdout, ct).ConfigureAwait(false),
                "schedule" when a.Positional(1) == "off" && a.Positional(2) is { } oj => await ScheduleSet(a, client, oj, null, stdout, ct).ConfigureAwait(false),
                "snapshots" => await Snapshots(a, client, stdout, ct).ConfigureAwait(false),
                "runs" => await Runs(a, client, stdout, stderr, ct).ConfigureAwait(false),
                "watch" when a.Positional(1) is { } wr => await Watch(client, wr, stdout, ct).ConfigureAwait(false),
                "forget" when a.Positional(1) is { } fj => await Forget(a, client, fj, stdout, ct).ConfigureAwait(false),
                "prune" when a.Positional(1) is { } pj && a.Flag("--dry-run") => await Prune(a, client, pj, stdout, ct).ConfigureAwait(false),
                "prune" when a.Positional(1) is { } pj => await PruneRun(a, client, pj, stdout, ct).ConfigureAwait(false),
                "retention" when a.Positional(1) == "release" && a.Flag("--confirm") => await RetentionRelease(a, client, stdout, ct).ConfigureAwait(false),
                "restore-db" when a.Option("--snapshot") is { } dbSnap => await RestoreDb(a, client, SnapshotRef(a, dbSnap), stdout, ct).ConfigureAwait(false),
                "destinations" when a.Positional(1) == "identity" => await Identity(a, client, stdout, stderr, ct).ConfigureAwait(false),
                "alerts" when a.Positional(1) is null or "list" => await AlertsList(a, client, stdout, ct).ConfigureAwait(false),
                "alerts" when a.Positional(1) == "ack" && a.Positional(2) is { } ackId => await AlertAct(a, client, ackId, null, stdout, ct).ConfigureAwait(false),
                "alerts" when a.Positional(1) == "silence" && a.Positional(2) is { } silId && int.TryParse(a.Positional(3), NumberStyles.None, CultureInfo.InvariantCulture, out var silMin)
                    && silMin is >= 1 and <= 10080 => await AlertAct(a, client, silId, silMin, stdout, ct).ConfigureAwait(false),
                "export" => await Export(a, client, stdout, stderr, ct).ConfigureAwait(false),
                "import" when a.Positional(1) is { } importFile => await Import(a, client, importFile, stdout, stderr, ct).ConfigureAwait(false),
                "audit" when a.Positional(1) == "verify" => await AuditVerify(a, client, stdout, ct).ConfigureAwait(false),
                "check" when a.Positional(1) is { } cj => await Check(a, client, cj, stdout, stderr, ct).ConfigureAwait(false),
                "restore" when a.Option("--snapshot") is { } snap && a.Option("--to") is { } to =>
                    TryFullPath(to) is { } fullTo
                        ? await Restore(a, client, SnapshotRef(a, snap), a.Option("--path"), fullTo, stdout, ct).ConfigureAwait(false)
                        : Usage(stderr),
                _ => Usage(stderr),
            };
        }
        catch (AgentUnavailableException ex)
        {
            // Wiadomość wyjątku niesie kod KB z Agenta tylko dla odmowy roli/uwierzytelnienia (GrpcAgentClient); w pozostałych przypadkach — brak połączenia.
            var text = ex.Message switch
            {
                KbCode.ApiAccessDenied => $"{KbCode.ApiAccessDenied}: access denied (role too low) / brak uprawnień do tej operacji",
                KbCode.CallerUnauthenticated => $"{KbCode.CallerUnauthenticated}: caller not authenticated / klient nieuwierzytelniony",
                _ => $"{KbCode.AgentUnreachable}: service unavailable / brak połączenia z usługą",
            };
            await stderr.WriteLineAsync(text).ConfigureAwait(false);
            return ExitUnavailable;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                // Ctrl+C (Program.cs anuluje token): czyste wyjście zamiast stosu wywołań; bieg w Agencie trwa dalej (`kobian watch` tylko obserwuje).
                await stderr.WriteLineAsync("cancelled / przerwano").ConfigureAwait(false);
                return ExitCancelled;
            }

            // Status Cancelled przysłał SERWER (zamykanie Agenta, zerwane połączenie), a użytkownik nie przerywał — to „usługa niedostępna”, nie przerwanie przez użytkownika.
            await stderr.WriteLineAsync($"{KbCode.AgentUnreachable}: service unavailable / brak połączenia z usługą").ConfigureAwait(false);
            return ExitUnavailable;
        }
        catch (AgentInternalException)
        {
            await stderr.WriteLineAsync($"{KbCode.Internal}: internal service error / wewnętrzny błąd usługi").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (ClientTooOldException)
        {
            await stderr.WriteLineAsync($"{KbCode.ClientRevisionTooOld}: client too old / klient zbyt stary").ConfigureAwait(false);
            return ExitClientTooOld;
        }
    }

    /// <summary>Ścieżka bezwzględna względem katalogu bieżącego CLI (jak <c>init</c>/<c>import</c>); Agent odrzuca względne jako KB-5001 „odmowa dostępu” — mylący komunikat dla <c>--to ./odtworzone</c>.</summary>
    private static string? TryFullPath(string p)
    {
        try
        {
            return Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static int Usage(TextWriter stderr)
    {
        stderr.WriteLine("usage: kobian status [--json] | init <name> --source <dir|sftp://host/folder|ftps://…|webdav://…> [--source …] [--source-secret-ref file:<name>] [--source-host-key SHA256:<...>] --dest <dir|\\\\server\\share|/path> --password-file <file> [--dest-scheme local|smb|sftp|ftps|webdav] [--secret-ref file:<name>] [--host <host>] [--port <n>] [--host-key SHA256:<...> (sftp: klucz hosta, ftps/webdav: SPKI)] [--json] | job run <name> [--wait] [--json] | schedule list [--job <name>] [--json] | schedule set <name> --cron '<5 pól>' [--json] | schedule off <name> [--json] | check <name> [--sample <0-100>] [--rotation <n>] [--repair] [--json] | snapshots [--job <name>] [--json] | runs [--job <name>] [--limit <1-1000>] [--json] | watch <run-id> | forget <name> [--dry-run] [--json] | prune <name> [--dry-run] [--json] | retention release --confirm [--json] | alerts [list] [--json] | alerts ack <alert-id> [--json] | alerts silence <alert-id> <minuty 1-10080> [--json] | audit verify [--anchor <seq>:<sha256>] [--json] | destinations identity list [--destination <dest-id>] [--json] | destinations identity approve <dest-id> <SHA256:...> [--replace] [--json] | destinations identity reject <dest-id> <SHA256:...> [--json] | destinations identity require-pin <dest-id> on|off [--json] | restore-db --snapshot <id|latest> [--job <name>] [--as <nazwa>] [--json] | restore --snapshot <id|latest> [--job <name>] [--path <filter>] --to <dir> [--dry-run] [--json] | export [--job <name>]... [--output <file> [--force]] [--json] | import <file> [--dry-run] [--on-conflict skip|overwrite|rename] [--json]");
        return ExitUsage;
    }

    private static int Exit(OverallState s) => s switch { OverallState.Ok => ExitOk, OverallState.Warning => ExitWarning, _ => ExitFailed };

    private static async Task<int> Status(Args a, IAgentClient c, TextWriter o, CancellationToken ct)
    {
        var st = await c.GetStatusAsync(ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "status", w =>
            {
                w.WriteString("state", Name(st.State));
                w.WriteString("agentVersion", st.AgentVersion);
                w.WriteStartArray("jobs");
                foreach (var j in st.Jobs)
                {
                    w.WriteStartObject();
                    w.WriteString("name", j.Name);
                    w.WriteString("lastResult", j.LastRunUtc is null ? "none" : Name(j.LastResult)); // zadanie bez przebiegu nie jest „ok” — skrypty monitoringu czytały to jako zdrowe
                    if (j.LastRunUtc is { } t) w.WriteString("lastRunUtc", t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                    else w.WriteNull("lastRunUtc");
                    if (j.LastCode is { } code) w.WriteString("code", code);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (st.Alerts is { } al)
                {
                    w.WriteStartObject("alerts");
                    w.WriteNumber("active", al.Active);
                    w.WriteNumber("unacknowledged", al.Unacknowledged);
                    w.WriteNumber("critical", al.Critical);
                    w.WriteNumber("silenced", al.Silenced);
                    w.WriteEndObject();
                }
                if (st.RuntimeCode is { } rc)
                {
                    w.WriteString("runtimeCode", rc);
                    if (st.RuntimeVersion is { } rv) w.WriteString("runtimeVersion", rv);
                }
                if (st.RetentionHold is { } h)
                {
                    w.WriteStartObject("retentionHold");
                    w.WriteString("code", h.Code);
                    if (h.HeldAtUtc is { } at) w.WriteString("heldAtUtc", Utc(at));
                    w.WriteNumber("clockDeltaSeconds", h.ClockDeltaSeconds);
                    w.WriteEndObject();
                }
            }).ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync($"Kobian Backup {st.AgentVersion}: {Name(st.State)}").ConfigureAwait(false);
            foreach (var j in st.Jobs) await o.WriteLineAsync($"  {j.Name}\t{(j.LastRunUtc is null ? "none" : Name(j.LastResult))}\t{j.LastCode}").ConfigureAwait(false);
            if (st.Alerts is { Active: > 0 } al)
                await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"  alerts {al.Active} active ({al.Unacknowledged} unacknowledged, {al.Critical} critical/security) — kobian alerts")).ConfigureAwait(false);
            if (st.RuntimeCode is { } rc)
                await o.WriteLineAsync($"  runtime {st.RuntimeVersion} UNPATCHED {rc} — install .NET 10 update / zainstaluj aktualizację .NET 10 (>= 10.0.13)").ConfigureAwait(false);
            if (st.RetentionHold is { } h)
                await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"  retention HELD {h.Code} since {(h.HeldAtUtc is { } at ? Utc(at) : "?")} (clock jump {h.ClockDeltaSeconds:+0;-0} s) — confirm: kobian retention release --confirm (Administrator)")).ConfigureAwait(false);
        }
        return Exit(st.State);
    }

    private static async Task<int> Run(Args a, IAgentClient c, string job, TextWriter o, CancellationToken ct)
    {
        var r = await c.RunJobAsync(job, a.Flag("--wait"), ct).ConfigureAwait(false);
        await Outcome(a, "run", r, o).ConfigureAwait(false);
        return Exit(r.Result);
    }

    /// <summary>
    /// Inicjalizacja repozytorium i zadania (BL-24). PROWIZORYCZNIE (Qb14): hasło tylko z pliku (--password-file), nigdy z argumentu ani zmiennej środowiskowej
    /// (argumenty widać w liście procesów). Ścieżki względne rozwiązywane względem katalogu bieżącego CLI, bo Agent wymaga ścieżek bezwzględnych.
    /// </summary>
    private static async Task<int> Init(Args a, IAgentClient c, string name, string source, string dest, string passwordFile, TextWriter o, TextWriter err, CancellationToken ct)
    {
        InitRequest req;
        try
        {
            var scheme = a.Option("--dest-scheme") ?? "local";
            // Tylko cel lokalny jest ścieżką tego komputera; UNC (smb) i ścieżka serwera SFTP/FTPS idą do Agenta bez zmian — walidacja wg schematu w Agencie (KB-0103/KB-2102/KB-2104).
            int? port = null;
            if (a.Option("--port") is { } ps)
            {
                if (!int.TryParse(ps, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pn) || pn is < 1 or > 65535)
                {
                    await err.WriteLineAsync($"{KbCode.JobDefinitionInvalid}: invalid port / niepoprawny port").ConfigureAwait(false);
                    return ExitUsage;
                }
                port = pn;
            }
            // --source wiele razy: foldery (ścieżki tego komputera) i serwery „sftp://host[:port]/folder”, „ftps://…”, „webdav://…” (poświadczenie --source-secret-ref,
            // klucz/odcisk serwera --source-host-key) — każde źródło jako osobny folder w kopii.
            var locals = new List<string>();
            var servers = new List<SourceServer>();
            foreach (var s in a.Options("--source"))
            {
                if (SourceServer.TryParse(s, a.Option("--source-secret-ref"), a.Option("--source-host-key")) is { } srv) servers.Add(srv);
                else locals.Add(Path.GetFullPath(s));
            }
            _ = source;
            req = new InitRequest(name, locals.FirstOrDefault() ?? "", scheme, scheme == "local" ? Path.GetFullPath(dest) : dest, Path.GetFullPath(passwordFile),
                SecretRef: a.Option("--secret-ref"), Host: a.Option("--host"), Port: port, HostKey: a.Option("--host-key"))
            {
                ExtraSources = [.. locals.Skip(1)],
                SourceServers = servers,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await err.WriteLineAsync($"{KbCode.JobDefinitionInvalid}: invalid path / niepoprawna ścieżka").ConfigureAwait(false);
            return ExitUsage;
        }
        var r = await c.InitJobAsync(req, ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "init", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                if (r.JobId is { } job) w.WriteString("jobId", job);
                if (r.RepoId is { } repo) w.WriteString("repoId", repo);
                if (r.KeyId is { } key) w.WriteString("keyId", key);
            }).ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync($"{Name(r.Result)}{(r.Code is null ? "" : " " + r.Code)}{(r.JobId is null ? "" : " job " + r.JobId)}{(r.RepoId is null ? "" : " repo " + r.RepoId)}").ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    /// <summary>
    /// Weryfikacja repozytorium zadania (BL-26, 3.7): bez <c>--sample</c> V2, z <c>--sample P</c> także V3 na rotacyjnej próbce P% packów.
    /// Kod wyjścia = wynik (0/1/2); argumenty liczbowe walidowane po stronie CLI (64), zakres ponownie w Agencie.
    /// </summary>
    private static async Task<int> Check(Args a, IAgentClient c, string job, TextWriter o, TextWriter err, CancellationToken ct)
    {
        int? sample = null;
        long? rotation = null;
        if (a.Option("--sample") is { } sp)
        {
            if (!int.TryParse(sp, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p > 100) return await BadNumber(err).ConfigureAwait(false);
            sample = p;
        }
        if (a.Option("--rotation") is { } rt)
        {
            if (!long.TryParse(rt, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r)) return await BadNumber(err).ConfigureAwait(false);
            rotation = r;
        }
        var x = await c.CheckAsync(job, sample, rotation, a.Flag("--repair"), ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "check", w =>
            {
                w.WriteString("result", Name(x.Result));
                if (x.Code is { } code) w.WriteString("code", code);
                w.WriteNumber("level", x.Level);
                w.WriteNumber("snapshots", x.Snapshots);
                w.WriteNumber("packs", x.Packs);
                w.WriteNumber("packsRead", x.PacksRead);
                w.WriteNumber("blocksVerified", x.BlocksVerified);
                w.WriteNumber("issues", x.Issues);
                w.WriteStartArray("damagedSnapshots");
                foreach (var d in x.DamagedSnapshots) w.WriteStringValue(d);
                w.WriteEndArray();
                w.WriteStartArray("findings");
                foreach (var (fc, ft) in x.Findings)
                {
                    w.WriteStartObject();
                    w.WriteString("code", fc);
                    w.WriteString("target", ft);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("damage");
                foreach (var d in x.Damage ?? [])
                {
                    w.WriteStartObject();
                    w.WriteString("snapshotId", d.SnapshotId);
                    w.WriteString("state", d.State);
                    w.WriteNumber("damagedFiles", d.DamagedFiles);
                    w.WriteNumber("damagedDirectories", d.DamagedDirectories);
                    w.WriteNumber("intactFiles", d.IntactFiles);
                    w.WriteBoolean("indexRecovered", d.IndexRecovered);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("recoveredPacks", x.RecoveredPacks);
                w.WriteNumber("unreadablePacks", x.UnreadablePacks);
            }).ConfigureAwait(false);
        }
        else
        {
            foreach (var (fc, ft) in x.Findings) await err.WriteLineAsync($"{fc}: {ft}").ConfigureAwait(false);
            foreach (var d in x.DamagedSnapshots) await o.WriteLineAsync($"damaged snapshot {d}").ConfigureAwait(false);
            foreach (var d in x.Damage ?? [])
                await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"snapshot {d.SnapshotId}: {d.State} (damaged files {d.DamagedFiles}, damaged folders {d.DamagedDirectories}, intact files {d.IntactFiles}){(d.IndexRecovered ? " [index rebuilt from packs, KB-1505]" : "")}")).ConfigureAwait(false);
            if (x.RecoveredPacks + x.UnreadablePacks > 0)
                await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"recovered index: {x.RecoveredPacks} packs from trailers, {x.UnreadablePacks} unreadable")).ConfigureAwait(false);
            await o.WriteLineAsync($"{Name(x.Result)}{(x.Code is null ? "" : " " + x.Code)}: V{x.Level}, {x.Snapshots} snapshots, {x.Packs} packs, {x.PacksRead} packs read, {x.Issues} issues").ConfigureAwait(false);
        }
        return Exit(x.Result);
    }

    private static async Task<int> BadNumber(TextWriter err)
    {
        await err.WriteLineAsync("usage: --sample must be 0..100, --rotation an integer").ConfigureAwait(false);
        return ExitUsage;
    }

    /// <summary>Harmonogram zadania (RD-27, AC-03.3): <c>set</c> waliduje wyrażenie po stronie Agenta (KB-0201), <c>off</c> wyłącza (idempotentnie). Strefa i misfire — z zadania.</summary>
    private static async Task<int> ScheduleSet(Args a, IAgentClient c, string job, string? cron, TextWriter o, CancellationToken ct)
    {
        var r = await c.SetScheduleAsync(job, cron, ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "schedule", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                if (r.TriggerId is { } id) w.WriteString("triggerId", id);
            }).ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync($"{Name(r.Result)}{(r.Code is null ? "" : " " + r.Code)}{(r.TriggerId is null ? "" : " trigger " + r.TriggerId)}").ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    private static async Task<int> ScheduleList(Args a, IAgentClient c, TextWriter o, CancellationToken ct)
    {
        var list = await c.ListSchedulesAsync(a.Option("--job"), ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "schedules", w =>
            {
                w.WriteStartArray("schedules");
                foreach (var s in list)
                {
                    w.WriteStartObject();
                    w.WriteString("job", s.Job);
                    w.WriteString("triggerId", s.TriggerId);
                    w.WriteString("cron", s.Cron);
                    w.WriteString("timeZone", s.TimeZone);
                    w.WriteBoolean("enabled", s.Enabled);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }).ConfigureAwait(false);
        }
        else
        {
            foreach (var s in list) await o.WriteLineAsync($"{s.Job}\t{s.Cron}\t{s.TimeZone}\t{(s.Enabled ? "on" : "off")}").ConfigureAwait(false);
        }
        return ExitOk;
    }

    /// <summary>BL-70: <c>--snapshot latest --job &lt;nazwa&gt;</c> → „latest:&lt;nazwa&gt;” (najnowsza kopia TEGO zadania); identyfikator kopii jest jednoznaczny sam w sobie, więc --job go nie zmienia.</summary>
    private static string SnapshotRef(Args a, string snapshot) =>
        a.Option("--job") is { Length: > 0 } job && string.Equals(snapshot.Trim(), "latest", StringComparison.OrdinalIgnoreCase) ? "latest:" + job : snapshot;

    private static async Task<int> Restore(Args a, IAgentClient c, string snap, string? path, string to, TextWriter o, CancellationToken ct)
    {
        if (a.Flag("--dry-run")) return await PlanRestore(a, c, snap, path, to, o, ct).ConfigureAwait(false);
        var r = await c.RestoreAsync(snap, path, to, ct).ConfigureAwait(false);
        await Outcome(a, "restore", r, o).ConfigureAwait(false);
        return Exit(r.Result);
    }

    private static async Task Outcome(Args a, string kind, RunOutcome r, TextWriter o)
    {
        if (a.Flag("--json"))
        {
            await Json(o, kind, w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                if (r.SnapshotId is { } id) w.WriteString("snapshotId", id);
            }).ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync($"{Name(r.Result)}{(r.Code is null ? "" : " " + r.Code)}{(r.SnapshotId is null ? "" : " snapshot " + r.SnapshotId)}").ConfigureAwait(false);
        }
    }

    private static async Task<int> Snapshots(Args a, IAgentClient c, TextWriter o, CancellationToken ct)
    {
        var list = await c.ListSnapshotsAsync(a.Option("--job"), ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "snapshots", w =>
            {
                w.WriteStartArray("snapshots");
                foreach (var s in list)
                {
                    w.WriteStartObject();
                    w.WriteString("id", s.Id);
                    w.WriteString("job", s.Job);
                    w.WriteString("timeUtc", s.TimeUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                    w.WriteString("mode", s.Mode);
                    w.WriteNumber("files", s.Files);
                    w.WriteNumber("bytesRaw", s.BytesRaw);
                    if (s.Damage is not null)
                    {
                        w.WriteString("damage", s.Damage);
                        w.WriteNumber("damagedFiles", s.DamagedFiles);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }).ConfigureAwait(false);
        }
        else
        {
            foreach (var s in list)
                await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{s.Id}  {s.TimeUtc.UtcDateTime:yyyy-MM-dd HH:mm}  {s.Job}  {s.Mode}  {s.Files} files{Damage(s)}")).ConfigureAwait(false);
        }
        return ExitOk;
    }

    /// <summary>it. 115 (RD-70): znacznik szkód z `check --repair` w liście kopii.</summary>
    private static string Damage(SnapshotInfo s) => s.Damage switch
    {
        "unrecoverable" => "  DAMAGED unrecoverable",
        "partial" => string.Create(CultureInfo.InvariantCulture, $"  DAMAGED partial ({s.DamagedFiles} files)"),
        _ => "",
    };

    /// <summary>
    /// Postęp jednego uruchomienia (it. 134, SCR-07): linia przy każdej zmianie stanu/liczników; kod wyjścia = wynik końcowy (0/1/2).
    /// Przerwanie (Ctrl+C) nie wpływa na bieg w Agencie. Nieznany bieg → KB-0107, kod 2.
    /// </summary>
    private static async Task<int> Watch(IAgentClient c, string runId, TextWriter o, CancellationToken ct)
    {
        var exit = ExitFailed;
        await foreach (var p in c.WatchRunAsync(runId, ct).ConfigureAwait(false))
        {
            var r = p.Run;
            await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{r.State}{(r.Files is { } f ? $"  files {f}" : "")}{(r.BytesUploaded is { } up ? $"  uploaded {up} B" : "")}{(p.BytesRead > 0 ? $"  read {p.BytesRead} B" : "")}{(r.Code is null ? "" : "  " + r.Code)}")).ConfigureAwait(false);
            if (p.Final) exit = Exit(r.Result);
        }
        return exit;
    }

    /// <summary>Historia uruchomień (BL-60): także nieudane/pominięte, restore i check. Lista = kod 0 (stan zadań: <c>status</c>).</summary>
    private static async Task<int> Runs(Args a, IAgentClient c, TextWriter o, TextWriter e, CancellationToken ct)
    {
        int? limit = null;
        if (a.Option("--limit") is { } l)
        {
            if (!int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 1000) return Usage(e);
            limit = n;
        }
        var list = await c.ListRunsAsync(a.Option("--job"), limit, ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "runs", w =>
            {
                w.WriteStartArray("runs");
                foreach (var r in list)
                {
                    w.WriteStartObject();
                    w.WriteString("runId", r.RunId);
                    w.WriteString("job", r.Job);
                    w.WriteString("kind", r.Kind);
                    w.WriteString("cause", r.Cause);
                    w.WriteString("state", r.State);
                    w.WriteString("result", Name(r.Result));
                    if (r.StartedUtc is { } st) w.WriteString("startedUtc", Utc(st));
                    if (r.FinishedUtc is { } fi) w.WriteString("finishedUtc", Utc(fi));
                    if (r.Code is { } code) w.WriteString("code", code);
                    if (r.SnapshotId is { } id) w.WriteString("snapshotId", id);
                    if (r.Files is { } files) w.WriteNumber("files", files);
                    if (r.BytesUploaded is { } up) w.WriteNumber("bytesUploaded", up);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }).ConfigureAwait(false);
        }
        else
        {
            foreach (var r in list)
            {
                var when = r.FinishedUtc ?? r.StartedUtc;
                await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{(when is { } w ? w.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "----------------")}  {r.Job}  {r.Kind}  {r.State}{(r.Code is null ? "" : " " + r.Code)}{(r.SnapshotId is null ? "" : " snapshot " + r.SnapshotId)}")).ConfigureAwait(false);
            }
        }
        return ExitOk;
    }

    /// <summary>
    /// Plan prune (BL-25 cz. 3, D3.7) z <c>--dry-run</c> — co zostałoby usunięte po karencji (bez zmian na celu i w katalogu). Bez <c>--dry-run</c> → <see cref="PruneRun"/>.
    /// Wstrzymany prune (KB-1601/1602) = kod 2 (Failed).
    /// </summary>
    private static async Task<int> Prune(Args a, IAgentClient c, string job, TextWriter o, CancellationToken ct)
    {
        var r = await c.PlanPruneAsync(job, ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "prune", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                if (r.BlockedObject is { } b) w.WriteString("blockedObject", b);
                w.WriteBoolean("dryRun", true);
                w.WriteNumber("liveSnapshots", r.LiveSnapshots);
                w.WriteNumber("livePacks", r.LivePacks);
                w.WriteNumber("liveBytes", r.LiveBytes);
                w.WriteNumber("deadSnapshots", r.DeadSnapshots);
                w.WriteNumber("deadIndexes", r.DeadIndexes);
                w.WriteNumber("deadPacks", r.DeadPacks);
                w.WriteNumber("deadBytes", r.DeadBytes);
                w.WriteNumber("graceSeconds", r.GraceSeconds);
            }).ConfigureAwait(false);
        }
        else if (r.Result == OverallState.Failed)
        {
            await o.WriteLineAsync($"failed {r.Code}{(r.BlockedObject is null ? "" : " " + r.BlockedObject)}").ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"dry-run: keep {r.LiveSnapshots} snapshots, {r.LivePacks} packs ({r.LiveBytes} B); remove after {r.GraceSeconds / 3600} h: {r.DeadSnapshots} snapshots, {r.DeadIndexes} indexes, {r.DeadPacks} packs ({r.DeadBytes} B)")).ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    /// <summary>
    /// Prune dwufazowy (BL-25 cz. 4): Agent oznacza nieżywe obiekty i usuwa te oznaczone ≥ 24 h temu (blokada wyłączna, sesja Maintenance).
    /// OK = 0; zatrzymany/zajęte repozytorium/fałszywe blokady = 1 (Warning); błąd = 2.
    /// </summary>
    private static async Task<int> PruneRun(Args a, IAgentClient c, string job, TextWriter o, CancellationToken ct)
    {
        var r = await c.PruneAsync(job, ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "prune", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                if (r.StopObject is { } so) w.WriteString("stopObject", so);
                w.WriteBoolean("dryRun", false);
                w.WriteNumber("marked", r.Marked);
                w.WriteNumber("unmarked", r.Unmarked);
                w.WriteNumber("deletedSnapshots", r.DeletedSnapshots);
                w.WriteNumber("deletedIndexes", r.DeletedIndexes);
                w.WriteNumber("deletedPacks", r.DeletedPacks);
                w.WriteNumber("deletedBytes", r.DeletedBytes);
                w.WriteNumber("pending", r.Pending);
                w.WriteNumber("graceSeconds", r.GraceSeconds);
                w.WriteNumber("locksCleaned", r.LocksCleaned);
                w.WriteNumber("indexesConsolidated", r.IndexesConsolidated);
                w.WriteNumber("indexesWritten", r.IndexesWritten);
                w.WriteNumber("activeIndexes", r.ActiveIndexes);
            }).ConfigureAwait(false);
        }
        else if (r.Result == OverallState.Failed)
        {
            await o.WriteLineAsync($"failed {r.Code}{(r.StopObject is null ? "" : " " + r.StopObject)}").ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{(r.Code is null ? "pruned" : "warning " + r.Code)}: deleted {r.DeletedSnapshots} snapshots, {r.DeletedIndexes} indexes, {r.DeletedPacks} packs ({r.DeletedBytes} B); marked {r.Marked}, waiting {r.Pending} (grace {r.GraceSeconds / 3600} h){(r.IndexesWritten > 0 ? $"; consolidated {r.IndexesConsolidated} indexes into {r.IndexesWritten}" : "")}")).ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    /// <summary>
    /// Zwolnienie wstrzymania retencji po skoku zegara (SEC-17.4, D9.3). Wymaga jawnego <c>--confirm</c> (bez niego — błąd składni 64) i roli Administrator po stronie Agenta.
    /// Przed zwolnieniem sprawdź zegar systemu: po zwolnieniu wiek kopii liczy się znów od bieżącego czasu.
    /// </summary>
    private static async Task<int> RetentionRelease(Args a, IAgentClient c, TextWriter o, CancellationToken ct)
    {
        var r = await c.ReleaseRetentionHoldAsync(ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "retention-release", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                w.WriteBoolean("released", r.Released);
            }).ConfigureAwait(false);
        }
        else if (r.Result == OverallState.Failed)
        {
            await o.WriteLineAsync($"failed {r.Code}").ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync(r.Released ? "ok: retention hold released / wstrzymanie retencji zwolnione" : "ok: retention was not held / retencja nie była wstrzymana").ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    /// <summary>
    /// Podgląd odtworzenia (it. 98, R2.8.1): `kobian restore … --dry-run` — nic nie zapisuje. Kod wyjścia: 0 = można odtworzyć bez pominięć,
    /// 1 = pliki zostaną pominięte (konflikty KB-5105, niedozwolone nazwy, dowiązania), 2 = blokada (KB-5001/5003/5106/5202) albo błąd planu.
    /// </summary>
    private static async Task<int> PlanRestore(Args a, IAgentClient c, string snap, string? path, string to, TextWriter o, CancellationToken ct)
    {
        var p = await c.PlanRestoreAsync(snap, path, to, ct).ConfigureAwait(false);
        var state = p.Code is not null || p.BlockingCode is not null ? OverallState.Failed
            : p.Conflicts + p.InvalidNames + p.Links > 0 ? OverallState.Warning : OverallState.Ok;
        if (a.Flag("--json"))
        {
            await Json(o, "restore-plan", w =>
            {
                w.WriteString("result", Name(state));
                if (p.Code is { } code) w.WriteString("code", code);
                if (p.BlockingCode is { } b) w.WriteString("blockingCode", b);
                w.WriteString("snapshotId", p.SnapshotId);
                w.WriteNumber("files", p.Files);
                w.WriteNumber("bytes", p.Bytes);
                w.WriteNumber("newDirectories", p.NewDirectories);
                w.WriteNumber("conflicts", p.Conflicts);
                w.WriteNumber("conflictBytes", p.ConflictBytes);
                w.WriteStartArray("conflictSample");
                foreach (var s in p.ConflictSample) w.WriteStringValue(s);
                w.WriteEndArray();
                w.WriteNumber("invalidNames", p.InvalidNames);
                w.WriteNumber("links", p.Links);
                w.WriteNumber("missingBlocks", p.MissingBlocks);
                w.WriteNumber("packs", p.Packs);
                w.WriteNumber("readBytes", p.ReadBytes);
                if (p.FreeBytes is { } f) w.WriteNumber("freeBytes", f);
                w.WriteNumber("resumable", p.Resumable);
            }).ConfigureAwait(false);
            return Exit(state);
        }
        if (p.Code is { } err)
        {
            await o.WriteLineAsync($"failed {err}").ConfigureAwait(false);
            return Exit(state);
        }
        var inv = CultureInfo.InvariantCulture;
        await o.WriteLineAsync(string.Create(inv, $"plan: files={p.Files} bytes={p.Bytes} new-dirs={p.NewDirectories} packs={p.Packs} read-bytes={p.ReadBytes}{(p.FreeBytes is { } fb ? string.Create(inv, $" free-bytes={fb}") : "")}")).ConfigureAwait(false);
        if (p.Conflicts > 0)
        {
            await o.WriteLineAsync(string.Create(inv, $"conflicts={p.Conflicts} (KB-5105: existing files are skipped, never overwritten / istniejące pliki zostaną pominięte)")).ConfigureAwait(false);
            foreach (var s in p.ConflictSample) await o.WriteLineAsync("  " + s).ConfigureAwait(false);
        }
        if (p.Resumable > 0) await o.WriteLineAsync(string.Create(inv, $"resumable={p.Resumable} (already restored by an interrupted attempt — skipped without conflict / odtworzone w przerwanej próbie)")).ConfigureAwait(false);
        if (p.InvalidNames + p.Links > 0) await o.WriteLineAsync(string.Create(inv, $"skipped: invalid-names={p.InvalidNames} links={p.Links}")).ConfigureAwait(false);
        await o.WriteLineAsync(p.BlockingCode is { } blk ? $"blocked {blk}" : "ok: nothing written (dry-run) / nic nie zapisano").ConfigureAwait(false);
        return Exit(state);
    }

    /// <summary>Odtworzenie bazy MSSQL do NOWEJ bazy (BL-68): `kobian restore-db --snapshot &lt;id|latest&gt; [--as &lt;nazwa&gt;]`.</summary>
    private static async Task<int> RestoreDb(Args a, IAgentClient c, string snapshot, TextWriter o, CancellationToken ct)
    {
        var r = await c.RestoreDatabaseAsync(snapshot, a.Option("--as"), ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "restore-db", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                w.WriteString("targetDatabase", r.TargetDatabase);
                w.WriteString("snapshotId", r.SnapshotId);
            }).ConfigureAwait(false);
        }
        else if (r.Result == OverallState.Failed)
        {
            await o.WriteLineAsync($"failed {r.Code}").ConfigureAwait(false);
        }
        else
        {
            var tag = r.Result == OverallState.Warning ? $"warning {r.Code}: " : "ok: ";
            await o.WriteLineAsync($"{tag}restored as new database / odtworzono jako nowa baza: {r.TargetDatabase}").ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    /// <summary>
    /// Weryfikacja łańcucha audytu (RD-42, SEC-19.1; it. 99: schemat RD-51, kotwice D9.9): 0 = spójny, 2 = KB-6501/6502/6503 z pierwszym niezgodnym seq.
    /// `--anchor seq:sha256hex` = kotwica zapisana poza serwerem (raport dzienny) — sprawdzenie niezależne od katalogu.
    /// </summary>
    /// <summary>
    /// Tożsamość serwerów celów (BL-35, D4.11, SEC-14.5): <c>list</c> (Operator), <c>approve</c> / <c>reject</c> / <c>require-pin</c> (Administrator).
    /// Zatwierdzić można tylko odcisk oczekujący — skopiowany z <c>list</c> i porównany poza pasmem z odciskiem na serwerze (KB-2141 przy innym).
    /// Składnia sprawdzana lokalnie (64), reguły — w Agencie.
    /// </summary>
    private static async Task<int> Identity(Args a, IAgentClient c, TextWriter o, TextWriter err, CancellationToken ct)
    {
        var sub = a.Positional(2);
        var dest = a.Positional(3);
        if (sub == "list")
        {
            var l = await c.ListServerIdentitiesAsync(a.Option("--destination"), ct).ConfigureAwait(false);
            if (a.Flag("--json"))
            {
                await Json(o, "destination-identities", w =>
                {
                    w.WriteString("result", Name(l.Result));
                    if (l.Code is { } code) w.WriteString("code", code);
                    w.WriteStartArray("identities");
                    foreach (var i in l.Identities)
                    {
                        w.WriteStartObject();
                        w.WriteString("destinationId", i.DestinationId);
                        w.WriteString("endpoint", i.Endpoint);
                        w.WriteString("kind", i.Kind);
                        w.WriteString("fingerprint", i.Fingerprint);
                        w.WriteString("state", i.State);
                        w.WriteBoolean("trustedByCa", i.TrustedByCa);
                        if (i.Display is { } d) w.WriteString("display", d);
                        w.WriteString("firstSeenUtc", Utc(i.FirstSeenUtc));
                        w.WriteString("lastSeenUtc", Utc(i.LastSeenUtc));
                        if (i.DecidedBy is { } by) w.WriteString("decidedBy", by);
                        if (i.DecidedAtUtc is { } at) w.WriteString("decidedAtUtc", Utc(at));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("requirePin");
                    foreach (var d in l.RequirePinDestinations) w.WriteStringValue(d);
                    w.WriteEndArray();
                }).ConfigureAwait(false);
            }
            else if (l.Result == OverallState.Failed)
            {
                await o.WriteLineAsync($"failed {l.Code}").ConfigureAwait(false);
            }
            else
            {
                foreach (var i in l.Identities)
                    await o.WriteLineAsync($"{i.DestinationId}	{i.Endpoint}	{i.Kind}	{i.State}{(i.TrustedByCa ? " (CA)" : "")}	{i.Fingerprint}	{Utc(i.FirstSeenUtc)}").ConfigureAwait(false);
                foreach (var d in l.RequirePinDestinations) await o.WriteLineAsync($"{d}	require-pin").ConfigureAwait(false);
            }
            return Exit(l.Result);
        }
        if (dest is null) return Usage(err);
        IdentityDecisionResult r;
        switch (sub)
        {
            case "approve" or "reject" when a.Positional(4) is { } fp:
                r = await c.DecideServerIdentityAsync(dest, fp, sub == "reject" ? IdentityDecision.Reject : a.Flag("--replace") ? IdentityDecision.Replace : IdentityDecision.Add, ct)
                    .ConfigureAwait(false);
                break;
            case "require-pin" when a.Positional(4) is "on" or "off":
                r = await c.SetServerIdentityPolicyAsync(dest, a.Positional(4) == "on", ct).ConfigureAwait(false);
                break;
            default:
                return Usage(err);
        }
        if (a.Flag("--json"))
        {
            await Json(o, "destination-identity", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                if (r.State is { } st) w.WriteString("state", st);
            }).ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync(r.Result == OverallState.Failed ? $"failed {r.Code}" : $"ok{(r.State is null ? "" : " " + r.State)}").ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    /// <summary>BL-45: aktywne alerty (D9.7) — jedna linia na alert: id, ważność, stan, kod, zakres, KB. Kod wyjścia: 0 brak alertów, 1 są alerty (ostrzeżenie).</summary>
    private static async Task<int> AlertsList(Args a, IAgentClient c, TextWriter o, CancellationToken ct)
    {
        var l = await c.ListAlertsAsync(ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "alerts", w =>
            {
                w.WriteString("result", Name(l.Result));
                w.WriteStartArray("alerts");
                foreach (var x in l.Alerts)
                {
                    w.WriteStartObject();
                    w.WriteString("id", x.Id);
                    w.WriteString("code", x.Code);
                    w.WriteString("severity", x.Severity);
                    w.WriteString("state", x.State);
                    w.WriteString("scopeKind", x.ScopeKind);
                    w.WriteString("subject", x.Subject);
                    if (x.OpenedUtc is { } t) w.WriteString("openedUtc", Utc(t));
                    if (x.Kb is { } kb) w.WriteString("kb", kb);
                    w.WriteNumber("reminders", x.Reminders);
                    w.WriteBoolean("escalated", x.Escalated);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }).ConfigureAwait(false);
        }
        else if (l.Alerts.Count == 0)
        {
            await o.WriteLineAsync("no active alerts").ConfigureAwait(false);
        }
        else
        {
            foreach (var x in l.Alerts)
                await o.WriteLineAsync($"{x.Id}	{x.Severity}	{x.State}	{x.Code}	{x.ScopeKind}:{x.Subject}	{x.Kb ?? "-"}{(x.Escalated ? "	escalated" : "")}").ConfigureAwait(false);
        }
        return l.Alerts.Count == 0 ? Exit(l.Result) : ExitWarning;
    }

    /// <summary>BL-45: <c>alerts ack &lt;id&gt;</c> (minutes = null) albo <c>alerts silence &lt;id&gt; &lt;min&gt;</c>.</summary>
    private static async Task<int> AlertAct(Args a, IAgentClient c, string id, int? minutes, TextWriter o, CancellationToken ct)
    {
        var r = minutes is { } m ? await c.SilenceAlertAsync(id, m, ct).ConfigureAwait(false) : await c.AcknowledgeAlertAsync(id, ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, minutes is null ? "alert-ack" : "alert-silence", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                if (r.State is { } st) w.WriteString("state", st);
            }).ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync(r.Result == OverallState.Failed ? $"failed {r.Code}" : $"ok{(r.State is null ? "" : " " + r.State)}").ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    private static async Task<int> AuditVerify(Args a, IAgentClient c, TextWriter o, CancellationToken ct)
    {
        var r = await c.VerifyAuditAsync(a.Option("--anchor"), ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "audit-verify", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                w.WriteNumber("entries", r.Entries);
                if (r.BrokenSeq is { } b) w.WriteNumber("brokenSeq", b);
                w.WriteString("lastHash", r.LastHash);
                w.WriteNumber("anchorsChecked", r.AnchorsChecked);
                if (r.LastAnchor is { } la) w.WriteString("lastAnchor", la);
                if (r.LastAnchorAtUtc is { } lat) w.WriteString("lastAnchorAtUtc", lat);
            }).ConfigureAwait(false);
        }
        else if (r.Result == OverallState.Failed)
        {
            await o.WriteLineAsync(r.BrokenSeq is { } b ? $"failed {r.Code} seq {b}" : $"failed {r.Code}").ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync($"ok: {r.Entries} audit entries, chain intact / łańcuch audytu spójny, last {r.LastHash}").ConfigureAwait(false);
            await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"anchors checked / sprawdzone kotwice: {r.AnchorsChecked}")).ConfigureAwait(false);
            if (r.LastAnchor is { } la) await o.WriteLineAsync($"last anchor / ostatnia kotwica ({r.LastAnchorAtUtc}): {la}").ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    /// <summary>Retencja (BL-25): podgląd albo logiczne zapomnienie. Brak polityki = OK bez zmian (komunikat). Dane na celu usuwa dopiero prune.</summary>
    private static async Task<int> Forget(Args a, IAgentClient c, string job, TextWriter o, CancellationToken ct)
    {
        var r = await c.ApplyRetentionAsync(job, a.Flag("--dry-run"), ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await Json(o, "forget", w =>
            {
                w.WriteString("result", Name(r.Result));
                if (r.Code is { } code) w.WriteString("code", code);
                w.WriteBoolean("hasPolicy", r.HasPolicy);
                w.WriteBoolean("dryRun", r.DryRun);
                w.WriteNumber("kept", r.Kept);
                w.WriteNumber("forgotten", r.Forgotten);
                w.WriteBoolean("held", r.Held);
                w.WriteStartArray("forget");
                foreach (var id in r.Forget) w.WriteStringValue(id);
                w.WriteEndArray();
            }).ConfigureAwait(false);
        }
        else if (r.Result == OverallState.Failed)
        {
            await o.WriteLineAsync($"failed {r.Code}").ConfigureAwait(false);
        }
        else if (r.Held && !r.DryRun)
        {
            await o.WriteLineAsync($"held {r.Code}: retention paused after a clock jump / retencja wstrzymana po skoku zegara — nic nie zapomniano").ConfigureAwait(false);
        }
        else if (!r.HasPolicy)
        {
            await o.WriteLineAsync("ok: no retention policy / brak polityki retencji — nic nie zapomniano").ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{(r.DryRun ? "dry-run" : "ok")}: keep {r.Kept}, forget {r.Forgotten}")).ConfigureAwait(false);
            foreach (var id in r.Forget) await o.WriteLineAsync("  - " + id).ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    /// <summary>
    /// Eksport zadań (US-29, 6.8): dokument kobian-export v1 bez sekretów. Bez <c>--output</c> dokument idzie na stdout (do przekierowania);
    /// z <c>--output</c> plik tworzony jako nowy (istniejący — tylko z <c>--force</c>), na stdout podsumowanie (tekst albo JSON).
    /// </summary>
    private static async Task<int> Export(Args a, IAgentClient c, TextWriter o, TextWriter err, CancellationToken ct)
    {
        var r = await c.ExportJobsAsync(a.Options("--job"), ct).ConfigureAwait(false);
        var output = a.Option("--output");
        if (r.Result != OverallState.Ok || r.Document is null)
        {
            if (a.Flag("--json")) await TransferJson(o, "export", r.Result, r.Code, r.Issues, null, w => w.WriteNumber("jobs", 0)).ConfigureAwait(false);
            else await WriteIssues(err, r.Code, r.Issues).ConfigureAwait(false);
            return Exit(r.Result);
        }
        if (output is null)
        {
            await o.WriteAsync(System.Text.Encoding.UTF8.GetString(r.Document)).ConfigureAwait(false);
            return ExitOk;
        }
        string full;
        try
        {
            full = Path.GetFullPath(output);
            var opts = new FileStreamOptions { Mode = a.Flag("--force") ? FileMode.Create : FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var fs = new FileStream(full, opts);
            await fs.WriteAsync(r.Document, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await err.WriteLineAsync("cannot write output file (exists? use --force) / nie można zapisać pliku (istnieje? użyj --force)").ConfigureAwait(false);
            return ExitUsage;
        }
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(r.Document));
        if (a.Flag("--json"))
        {
            await TransferJson(o, "export", r.Result, null, [], null, w =>
            {
                w.WriteNumber("jobs", r.Jobs);
                w.WriteString("output", full);
                w.WriteString("sha256", sha);
            }).ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"ok: {r.Jobs} jobs exported / wyeksportowano zadań: {r.Jobs} → {full} (sha256 {sha})")).ConfigureAwait(false);
        }
        return ExitOk;
    }

    /// <summary>
    /// Import zadań (US-29, AC-29.1–29.2, SEC-6.7): <c>--dry-run</c> = plan zmian bez zapisu; konflikt nazw bez <c>--on-conflict</c> = KB-0124 (kod 2).
    /// Plik czytany do limitu (60 KiB) — większy → KB-0121 bez wysyłania. Zaimportowane zadania są wyłączone (włącza Administrator).
    /// </summary>
    private static async Task<int> Import(Args a, IAgentClient c, string file, TextWriter o, TextWriter err, CancellationToken ct)
    {
        var policy = a.Option("--on-conflict") switch
        {
            null => ImportConflict.Fail,
            "skip" => ImportConflict.Skip,
            "overwrite" => ImportConflict.Overwrite,
            "rename" => ImportConflict.Rename,
            _ => (ImportConflict?)null,
        };
        if (policy is null || (a.Flag("--on-conflict") && a.Option("--on-conflict") is null))
        {
            await err.WriteLineAsync("usage: --on-conflict skip|overwrite|rename").ConfigureAwait(false);
            return ExitUsage;
        }
        byte[] doc;
        try
        {
            await using var fs = new FileStream(Path.GetFullPath(file), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > Kobian.Api.Contracts.ApiConventions.MaxJobsDocumentBytes) return await TooLarge(a, o, err).ConfigureAwait(false);
            doc = new byte[fs.Length];
            await fs.ReadExactlyAsync(doc, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await err.WriteLineAsync("cannot read input file / nie można odczytać pliku").ConfigureAwait(false);
            return ExitUsage;
        }
        var dry = a.Flag("--dry-run");
        var r = await c.ImportJobsAsync(doc, dry, policy.Value, ct).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await TransferJson(o, "import", r.Result, r.Code, r.Issues, r.Changes, w =>
            {
                w.WriteBoolean("dryRun", dry);
                w.WriteBoolean("applied", r.Applied);
            }).ConfigureAwait(false);
        }
        else
        {
            foreach (var ch in r.Changes)
                await o.WriteLineAsync($"  {ch.Job}\t{ch.Kind}\t{ch.Action}{(ch.NewName is null ? "" : " → " + ch.NewName)}{(ch.NeedsCredentials ? "\tneeds credentials / brak poświadczeń" : "")}{(ch.MissingKeyMaterial ? "\tkey file missing / brak pliku klucza" : "")}").ConfigureAwait(false);
            if (r.Result != OverallState.Ok) await WriteIssues(err, r.Code, r.Issues).ConfigureAwait(false);
            else await o.WriteLineAsync(dry ? "dry-run: nothing written / nic nie zapisano"
                : r.Applied ? "ok: imported jobs are DISABLED — enable after checking credentials / zaimportowane zadania są WYŁĄCZONE" : "ok: nothing to change / brak zmian").ConfigureAwait(false);
        }
        return Exit(r.Result);
    }

    private static async Task<int> TooLarge(Args a, TextWriter o, TextWriter err)
    {
        if (a.Flag("--json")) await TransferJson(o, "import", OverallState.Failed, KbCode.ImportTooLarge, [new("$", KbCode.ImportTooLarge, "file too large")], null, _ => { }).ConfigureAwait(false);
        else await err.WriteLineAsync($"{KbCode.ImportTooLarge}: file too large / plik za duży").ConfigureAwait(false);
        return ExitFailed;
    }

    private static async Task WriteIssues(TextWriter err, string? code, IReadOnlyList<TransferIssue> issues)
    {
        await err.WriteLineAsync($"failed {code}").ConfigureAwait(false);
        foreach (var i in issues) await err.WriteLineAsync($"  {i.Code} {i.Path}: {i.Reason}").ConfigureAwait(false);
    }

    private static Task TransferJson(TextWriter o, string kind, OverallState result, string? code, IReadOnlyList<TransferIssue> issues, IReadOnlyList<JobImportChange>? changes, Action<Utf8JsonWriter> extra) =>
        Json(o, kind, w =>
        {
            w.WriteString("result", Name(result));
            if (code is not null) w.WriteString("code", code);
            extra(w);
            if (changes is not null)
            {
                w.WriteStartArray("changes");
                foreach (var ch in changes)
                {
                    w.WriteStartObject();
                    w.WriteString("job", ch.Job);
                    w.WriteString("kind", ch.Kind);
                    w.WriteString("action", ch.Action);
                    if (ch.NewName is not null) w.WriteString("newName", ch.NewName);
                    w.WriteBoolean("needsCredentials", ch.NeedsCredentials);
                    w.WriteBoolean("missingKeyMaterial", ch.MissingKeyMaterial);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteStartArray("issues");
            foreach (var i in issues)
            {
                w.WriteStartObject();
                w.WriteString("path", i.Path);
                w.WriteString("code", i.Code);
                w.WriteString("reason", i.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Name(OverallState s) => s switch { OverallState.Ok => "ok", OverallState.Warning => "warning", _ => "failed" };

    private static async Task Json(TextWriter o, string kind, Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("schema", $"kobian.cli.{kind}/{SchemaVersion}");
            body(w);
            w.WriteEndObject();
        }
        await o.WriteLineAsync(System.Text.Encoding.UTF8.GetString(ms.ToArray())).ConfigureAwait(false);
    }

    private sealed class Args(string[] args)
    {
        public string? Verb => args.Length > 0 ? args[0] : null;

        public string? Positional(int i)
        {
            var pos = args.Where((x, idx) => !x.StartsWith("--", StringComparison.Ordinal) && !(idx > 0 && IsValued(args[idx - 1]))).ToList();
            return i < pos.Count ? pos[i] : null;
        }

        public bool Flag(string name) => args.Contains(name, StringComparer.Ordinal);

        /// <summary>Wszystkie wartości powtarzalnej opcji (np. <c>--job a --job b</c>).</summary>
        public List<string> Options(string name)
        {
            var list = new List<string>();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == name && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) list.Add(args[i + 1]);
            return list;
        }

        public string? Option(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        }

        /// <summary>Opcje z wartością. Lista musi obejmować KAŻDĄ opcję czytaną przez <see cref="Option"/>: brak opcji tu powodował, że jej wartość była brana za nazwę zadania (<c>check --sample 50 moje</c>).</summary>
        private static readonly HashSet<string> ValuedOptions = new(StringComparer.Ordinal)
        {
            "--job", "--snapshot", "--path", "--to", "--source", "--dest", "--password-file", "--dest-scheme", "--cron", "--limit", "--secret-ref", "--host", "--port",
            "--host-key", "--source-secret-ref", "--source-host-key", "--destination", "--output", "--on-conflict", "--sample", "--rotation", "--as", "--anchor",
        };

        private static bool IsValued(string a) => ValuedOptions.Contains(a);

        /// <summary>
        /// Pierwsza opcja z wartością podana BEZ wartości (na końcu albo przed następną opcją). Wcześniej była cicho ignorowana: <c>restore --snapshot latest --job $JOB --to D</c> z pustym
        /// <c>$JOB</c> odtwarzał najnowszą kopię DOWOLNEGO zadania, <c>--path</c> bez wartości — całą kopię, <c>export --job</c> — wszystkie zadania.
        /// </summary>
        public string? OptionMissingValue()
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!IsValued(args[i])) continue;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[i];
                i++; // wartość nie jest opcją
            }
            return null;
        }
    }
}
