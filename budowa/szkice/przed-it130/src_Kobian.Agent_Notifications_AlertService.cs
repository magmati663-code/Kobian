using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Kobian.Agent.Hosting;
using Kobian.Catalog.Sqlite;
using Kobian.Notifications.Alerts;
using Kobian.Notifications.Alerts.Rules;
using Kobian.Notifications.Events;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Notifications;

/// <summary>Przejście alertu zapisane w jednym takcie (do testów i diagnostyki): kod, zakres, rodzaj powiadomienia (null = bez wysyłki), liczba wpisów outboxa.</summary>
public sealed record AlertTickResult(string Code, string Scope, AlertState State, AlertTransition? Transition, bool Escalated, int OutboxEntries);

/// <summary>
/// Ewaluacja alertów w Agencie (BL-45/BL-82, D9.7, D9.8, R2.9.6): co <see cref="Interval"/> (60 s — pseudokod AlertEvaluator.Tick) w JEDNEJ transakcji
/// katalogu: fakty z katalogu (<see cref="AlertFactsCollector"/>) → <see cref="AlertEvaluator"/> (reguły A01–A17 z danych) → <see cref="AlertStateMachine"/>
/// → zapis stanu w <c>alert_alerts</c> + wpis <c>core_outbox</c> (<c>pl.kobian.alert.*.v1</c>) dla każdego włączonego kanału. Stan trwały: po restarcie
/// Agent wczytuje nierozwiązane alerty i nie wysyła ponownie „opened” (deduplikacja D9.7). Błąd transakcji = stan w pamięci odrzucany i wczytywany
/// ponownie z katalogu w następnym takcie (pamięć nigdy nie wyprzedza katalogu).
/// </summary>
public sealed class AlertService(string catalogPath, CatalogWriter writer, string hostIdHex, TimeProvider time, AlertRuleCatalog? rules = null)
{
    /// <summary>Takt pętli (plan: „co 60 s + na zdarzenia”); reguły okresowe (A03 — 900 s) pilnuje ewaluator.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Kanały (webhook, e-mail) — jak dla run.finished; każdy dostaje własny wpis outboxa w swoim trybie minimalnym. Pusta lista = sam stan (pulpit).</summary>
    public IReadOnlyList<NotifyChannel> Channels { get; init; } = [];

    /// <summary>A14: dostępna aktualizacja bezpieczeństwa runtime (BL-62 RuntimeGuard — KB-7013).</summary>
    public bool SecurityUpdateAvailable { get; init; }

    /// <summary>Cisza nocna (9.4) — dziś bez konfiguracji: PROWIZORYCZNIE Qb95 (brak ciszy; opóźnia tylko Ostrzeżenia).</summary>
    public Func<DateTimeOffset, bool> QuietHours { get; init; } = _ => false;

    /// <summary>
    /// Reguły, których powiadomienia nie są wysyłane natychmiast — tylko stan (pulpit): A02 (9.4: „w raporcie dziennym, nie natychmiast”; wynik
    /// uruchomienia z ostrzeżeniem i tak trafia do kanałów jako run.finished). PROWIZORYCZNIE Qb91.
    /// </summary>
    public static readonly IReadOnlySet<string> StateOnlyRules = new HashSet<string>(StringComparer.Ordinal) { "A02" };

    private readonly AlertRuleCatalog _rules = rules ?? AlertRuleCatalog.LoadEmbedded();
    private AlertEvaluator? _evaluator;
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal); // dedupKey → alert_id (tylko nierozwiązane)
    private AlertFactSet _facts = AlertFactSet.Empty;

    /// <summary>Jeden takt w jednej transakcji katalogu. Zwraca zmiany stanu (puste = nic się nie zmieniło).</summary>
    public async Task<IReadOnlyList<AlertTickResult>> TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var quiet = QuietHours(now);
        try
        {
            return await writer.ExecuteControlAsync((c, tx) =>
            {
                _evaluator ??= Load(c, tx);
                _facts = AlertFactsCollector.Collect(c, tx, now, SecurityUpdateAvailable);
                var updates = _evaluator.Tick(now, quiet);
                var results = new List<AlertTickResult>(updates.Count);
                foreach (var u in updates) results.Add(Persist(c, tx, u, now));
                return (IReadOnlyList<AlertTickResult>)results;
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Transakcja wycofana (albo nie wiadomo, czy zatwierdzona) — stan w pamięci mógł wyprzedzić katalog: wczytać ponownie.
            _evaluator = null;
            _ids.Clear();
            throw;
        }
    }

    /// <summary>Aktywne (nierozwiązane) alerty z katalogu — ten sam odczyt co GetStatus (pulpit SCR-01).</summary>
    public IReadOnlyList<Kobian.Api.V1.AlertInfo> Active()
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        var list = new Google.Protobuf.Collections.RepeatedField<Kobian.Api.V1.AlertInfo>();
        CatalogStatusReader.ReadAlerts(c, list);
        return list;
    }

    /// <summary>Pętla do zatrzymania hosta; błąd katalogu lub danych nie zatrzymuje usługi (następna próba w kolejnym takcie).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or ArgumentException or FormatException)
                {
                    // następna próba w kolejnym takcie
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private AlertEvaluator Load(SqliteConnection c, SqliteTransaction tx)
    {
        _ids.Clear();
        var existing = new List<Alert>();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT alert_id, code, scope, severity, state, opened_at, last_notified_at, reminders, escalated, acknowledged_at, silenced_until
            FROM alert_alerts WHERE state <> 'resolved';
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Wiersz spoza formatu zakresu (wstawiony poza Agentem) nie może zablokować taktów — pomijany (nie jest oceniany ani wysyłany).
            if (AlertEvent.ParseScope(r.GetString(2)) is null) continue;
            var a = new Alert(r.GetString(1), r.GetString(2), ParseSeverity(r.GetString(3)), ParseState(r.GetString(4)), Time(r.GetString(5)),
                r.IsDBNull(6) ? null : Time(r.GetString(6)), r.GetInt32(7), r.IsDBNull(9) ? null : Time(r.GetString(9)),
                r.IsDBNull(10) ? null : Time(r.GetString(10)), r.GetInt64(8) != 0);
            existing.Add(a);
            _ids[a.DedupKey] = r.GetString(0);
        }
        return new AlertEvaluator(_rules, rule => _facts.For(rule.Code), existing);
    }

    private AlertTickResult Persist(SqliteConnection c, SqliteTransaction tx, AlertUpdate u, DateTimeOffset now)
    {
        var a = u.Alert ?? throw new InvalidOperationException("Alert update without state.");
        var key = a.DedupKey;
        var resolved = a.State == AlertState.Resolved;
        var detail = _facts.Detail(key);
        var payload = DetailJson(detail);
        if (!_ids.TryGetValue(key, out var id))
        {
            if (resolved) return new AlertTickResult(a.Code, a.Scope, a.State, null, false, 0); // nic do zamknięcia w katalogu
            id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            Exec(c, tx, """
                INSERT INTO alert_alerts(alert_id, code, scope, severity, state, opened_at, last_notified_at, reminders, escalated, acknowledged_at, silenced_until, resolved_at, last_payload)
                VALUES ($i, $c, $s, $sv, $st, $o, $n, $r, $e, $ack, $sil, NULL, $p);
                """, ("$i", id), ("$c", a.Code), ("$s", a.Scope), ("$sv", AlertEvent.SeverityText(a.Severity)), ("$st", StateText(a.State)), ("$o", OutboxDispatcher.Iso(a.OpenedAt)),
                ("$n", Opt(a.LastNotifiedAt)), ("$r", a.Reminders), ("$e", a.Escalated ? 1 : 0), ("$ack", Opt(a.AcknowledgedAt)), ("$sil", Opt(a.SilencedUntil)), ("$p", payload));
            _ids[key] = id;
        }
        else
        {
            Exec(c, tx, """
                UPDATE alert_alerts SET severity=$sv, state=$st, last_notified_at=$n, reminders=$r, escalated=$e, acknowledged_at=$ack, silenced_until=$sil,
                  resolved_at=$res, last_payload=COALESCE($p, last_payload)
                WHERE alert_id=$i;
                """, ("$i", id), ("$sv", AlertEvent.SeverityText(a.Severity)), ("$st", StateText(a.State)), ("$n", Opt(a.LastNotifiedAt)), ("$r", a.Reminders),
                ("$e", a.Escalated ? 1 : 0), ("$ack", Opt(a.AcknowledgedAt)), ("$sil", Opt(a.SilencedUntil)), ("$res", resolved ? OutboxDispatcher.Iso(now) : null), ("$p", payload));
            if (resolved) _ids.Remove(key);
        }

        AlertTransition? transition = u.Outbox switch
        {
            AlertOutbox.Opened => AlertTransition.Opened,
            AlertOutbox.Reminder => AlertTransition.Reminder,
            AlertOutbox.Resolved => AlertTransition.Resolved,
            _ => u.Escalate ? AlertTransition.Reminder : null,
        };
        if (transition is null || StateOnlyRules.Contains(a.Code)) return new AlertTickResult(a.Code, a.Scope, a.State, transition, u.Escalate, 0);

        // Jedno zdarzenie (wspólny id — deduplikacja u odbiorcy), ładunek per kanał w jego trybie minimalnym (SEC-9.6).
        var notice = new AlertNotice(id, a.Code, a.Severity, u.Rule.Category, u.Rule.Kb, a.Scope, transition.Value, a.OpenedAt, now, a.Reminders, u.Escalate,
            _facts.JobName(a.Scope), detail?.LastSuccess, detail?.Details);
        var eventId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var entries = 0;
        foreach (var ch in Channels)
        {
            OutboxDispatcher.Enqueue(c, tx, ch.Name, AlertEvent.Build(eventId, hostIdHex, notice, ch.Minimal), now);
            entries++;
        }
        return new AlertTickResult(a.Code, a.Scope, a.State, transition, u.Escalate, entries);
    }

    private static byte[]? DetailJson(AlertDetail? d)
    {
        if (d is null || (d.LastSuccess is null && d.Details.Count == 0)) return null;
        var o = new SortedDictionary<string, object>(StringComparer.Ordinal);
        if (d.LastSuccess is { } ls) o["lastSuccessUtc"] = OutboxDispatcher.Iso(ls);
        foreach (var (k, v) in d.Details) o[k] = v;
        return JsonSerializer.SerializeToUtf8Bytes(o);
    }

    private static string? Opt(DateTimeOffset? t) => t is { } v ? OutboxDispatcher.Iso(v) : null;

    internal static string StateText(AlertState s) => s switch
    {
        AlertState.Open => "open",
        AlertState.Notified => "notified",
        AlertState.Acknowledged => "acknowledged",
        AlertState.Silenced => "silenced",
        _ => "resolved",
    };

    private static AlertState ParseState(string s) => s switch
    {
        "open" => AlertState.Open,
        "notified" => AlertState.Notified,
        "acknowledged" => AlertState.Acknowledged,
        "silenced" => AlertState.Silenced,
        "resolved" => AlertState.Resolved,
        _ => throw new FormatException("Unknown alert state in catalog."),
    };

    private static AlertSeverity ParseSeverity(string s) => s switch
    {
        "warning" => AlertSeverity.Warning,
        "critical" => AlertSeverity.Critical,
        "security" => AlertSeverity.Security,
        _ => throw new FormatException("Unknown alert severity in catalog."),
    };

    internal static DateTimeOffset Time(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Dodatkowe dane zakresu do powiadomienia (z zamkniętej listy kluczy — <see cref="AlertEvent.DetailKeys"/>).</summary>
public sealed record AlertDetail(DateTimeOffset? LastSuccess, IReadOnlyDictionary<string, long> Details);

/// <summary>Fakty jednego taktu: zakresy per reguła, szczegóły per klucz deduplikacji, nazwy zadań (tylko do trybu nieminimalnego).</summary>
public sealed class AlertFactSet(
    IReadOnlyDictionary<string, IReadOnlyList<ScopedFacts>> byRule,
    IReadOnlyDictionary<string, AlertDetail> details,
    IReadOnlyDictionary<string, string> jobNames)
{
    public static readonly AlertFactSet Empty = new(new Dictionary<string, IReadOnlyList<ScopedFacts>>(), new Dictionary<string, AlertDetail>(), new Dictionary<string, string>());

    public IReadOnlyList<ScopedFacts> For(string code) => byRule.TryGetValue(code, out var l) ? l : [];

    public AlertDetail? Detail(string dedupKey) => details.GetValueOrDefault(dedupKey);

    public string? JobName(string scope) => scope.StartsWith("job:", StringComparison.Ordinal) ? jobNames.GetValueOrDefault(scope[4..]) : null;
}

/// <summary>
/// Zbieranie faktów dla reguł z katalogu (D9.8). Reguły z danymi w katalogu: <see cref="ActiveRules"/>; pozostałe nie mają jeszcze źródła danych
/// w Agencie i są jawnie nieaktywne (<see cref="InactiveRules"/> — puste zakresy, nie udajemy wyniku). Zakresy = nieprzejrzyste identyfikatory
/// (<c>job:&lt;id&gt;</c>, <c>dest:&lt;id&gt;</c>, <c>repo:&lt;id&gt;</c>, <c>host</c>) — bez ścieżek, hostów i nazw plików (SEC-9.6).
/// </summary>
public static class AlertFactsCollector
{
    public static readonly IReadOnlySet<string> ActiveRules = new HashSet<string>(StringComparer.Ordinal) { "A01", "A02", "A03", "A04", "A09", "A14", "A15", "A17" };

    /// <summary>Reguła → brakujące źródło danych (dokumentacja admina i notatki; test pilnuje, że ActiveRules ∪ InactiveRules = A01–A17).</summary>
    public static readonly IReadOnlyDictionary<string, string> InactiveRules = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["A05"] = "restore test V4 (no results in the catalog yet)",
        ["A06"] = "destination free space (probe results are not persisted)",
        ["A07"] = "repository reconciliation (not implemented)",
        ["A08"] = "audit-triggered one-shot alerts (needs acknowledge flow)",
        ["A10"] = "destination capability probe D4.5 (not persisted)",
        ["A11"] = "ransomware heuristics (Planner scan not implemented)",
        ["A12"] = "external heartbeat (by design evaluated outside the Agent)",
        ["A13"] = "certificate/secret expiry dates (not collected)",
        ["A16"] = "OS/SQL patch level (WMI/Windows Update reader not implemented)",
    };

    /// <summary>A01: ponowienia uznane za wyczerpane, gdy numer próby ≥ limitu (RunConditions.RetryLimit = 3) albo po tym czasie bez nowej próby.</summary>
    public static readonly TimeSpan RetryGrace = TimeSpan.FromMinutes(2); // PROWIZORYCZNIE Qb90

    public const int RetryLimit = 3;

    /// <summary>A15: okno zliczania odmów API (PROWIZORYCZNIE Qb92: 1 h; próg z reguły — 5).</summary>
    public static readonly TimeSpan DenialWindow = TimeSpan.FromHours(1);

    public const int MaxJobs = 10_000;

    public static AlertFactSet Collect(SqliteConnection c, SqliteTransaction? tx, DateTimeOffset now, bool securityUpdateAvailable)
    {
        ArgumentNullException.ThrowIfNull(c);
        var byRule = new Dictionary<string, IReadOnlyList<ScopedFacts>>(StringComparer.Ordinal);
        var details = new Dictionary<string, AlertDetail>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        List<ScopedFacts> a01 = [], a02 = [], a03 = [], a04 = [], a09 = [];

        // Zadania włączone: ostatni rozstrzygnięty wynik (Succeeded/Warning/Failed), aktywne uruchomienie, ostatnia nieuszkodzona kopia, harmonogram cron.
        using (var cmd = Cmd(c, tx, """
            SELECT j.job_id, j.name, j.time_zone, j.created_at,
                   (SELECT r.state FROM run_runs r WHERE r.job_id = j.job_id AND r.finished_at IS NOT NULL AND r.state IN ('Succeeded','Warning','Failed') ORDER BY r.finished_at DESC, r.rowid DESC LIMIT 1),
                   (SELECT r.attempt FROM run_runs r WHERE r.job_id = j.job_id AND r.finished_at IS NOT NULL AND r.state IN ('Succeeded','Warning','Failed') ORDER BY r.finished_at DESC, r.rowid DESC LIMIT 1),
                   (SELECT r.finished_at FROM run_runs r WHERE r.job_id = j.job_id AND r.finished_at IS NOT NULL AND r.state IN ('Succeeded','Warning','Failed') ORDER BY r.finished_at DESC, r.rowid DESC LIMIT 1),
                   EXISTS (SELECT 1 FROM run_runs r WHERE r.job_id = j.job_id AND r.state IN ('Queued','Running','Paused')),
                   (SELECT s.time_utc FROM snap_snapshots s WHERE s.job_id = j.job_id AND s.forgotten_at IS NULL AND s.suspect = 0 ORDER BY s.time_utc DESC, s.rowid DESC LIMIT 1),
                   (SELECT t.spec FROM sched_triggers t WHERE t.job_id = j.job_id AND t.kind = 'cron' AND t.enabled = 1 ORDER BY t.trigger_id LIMIT 1)
            FROM job_jobs j WHERE j.enabled = 1 ORDER BY j.job_id LIMIT $n;
            """, ("$n", MaxJobs)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var jobId = r.GetString(0);
                if (!AlertEventIds.IsId(jobId)) continue; // identyfikator spoza formatu (wiersz wstawiony poza Agentem) — pomijany, jak w raporcie dziennym
                names[jobId] = r.GetString(1);
                var scope = "job:" + jobId;
                RunOutcome? outcome = r.IsDBNull(4) ? null : r.GetString(4) switch
                {
                    "Succeeded" => RunOutcome.Success,
                    "Warning" => RunOutcome.Warning,
                    _ => RunOutcome.Failed,
                };
                var active = r.GetInt64(7) != 0;
                var exhausted = outcome == RunOutcome.Failed && !active
                    && (r.GetInt64(5) >= RetryLimit || (TryTime(r, 6) is { } fin && now - fin >= RetryGrace));
                var runFacts = new AlertFacts { LastOutcome = outcome, RetriesExhausted = exhausted };
                a01.Add(new ScopedFacts(scope, runFacts));
                a02.Add(new ScopedFacts(scope, runFacts));

                // A03 tylko dla zadań z włączonym harmonogramem (zadanie ręczne nie ma oczekiwanego cyklu — plan 9.4: „1,5 × interwał harmonogramu”).
                if (!r.IsDBNull(9) && CatalogStatusReader.Timing((byte[])r[9], r.GetString(2), now) is { Interval: { } interval })
                {
                    var lastSuccess = TryTime(r, 8);
                    a03.Add(new ScopedFacts(scope, new AlertFacts { LastSuccessAt = lastSuccess, Since = TryTime(r, 3), ScheduleInterval = interval }));
                    if (lastSuccess is not null) details["A03:" + scope] = new AlertDetail(lastSuccess, new Dictionary<string, long>());
                }
            }
        }

        // A04: repozytorium uszkodzone — stan 'corrupt', kopia z oceną szkód (check --repair) albo najnowsza kopia zadania oznaczona suspect (V2/V3).
        using (var cmd = Cmd(c, tx, """
            SELECT p.repo_id, p.state,
                   (SELECT COUNT(*) FROM snap_snapshots s WHERE s.repo_id = p.repo_id AND s.forgotten_at IS NULL AND s.damage IS NOT NULL),
                   (SELECT COUNT(*) FROM snap_snapshots s WHERE s.repo_id = p.repo_id AND s.forgotten_at IS NULL AND s.suspect = 1
                      AND NOT EXISTS (SELECT 1 FROM snap_snapshots n WHERE n.repo_id = s.repo_id AND n.job_id = s.job_id AND n.forgotten_at IS NULL AND n.time_utc > s.time_utc))
            FROM repo_repositories p ORDER BY p.repo_id LIMIT $n;
            """, ("$n", MaxJobs)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var repoId = r.GetString(0);
                if (!AlertEventIds.IsId(repoId)) continue;
                var (damaged, suspect) = (r.GetInt64(2), r.GetInt64(3));
                var failed = r.GetString(1) == "corrupt" || damaged > 0 || suspect > 0;
                var scope = "repo:" + repoId;
                a04.Add(new ScopedFacts(scope, new AlertFacts { Flags = failed ? Flag("verificationFailed") : new HashSet<string>() }));
                if (failed)
                    details["A04:" + scope] = new AlertDetail(null, new Dictionary<string, long>(StringComparer.Ordinal) { ["damagedSnapshots"] = damaged, ["suspectSnapshots"] = suspect });
            }
        }

        // A09: tożsamość serwera celu czeka na decyzję Administratora (BL-35, it. 121: repo_dest_identities.state = 'pending') — nowy lub zmieniony klucz.
        using (var cmd = Cmd(c, tx, "SELECT destination_id, COUNT(*) FROM repo_dest_identities WHERE state = 'pending' GROUP BY destination_id ORDER BY destination_id LIMIT $n;", ("$n", MaxJobs)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var dest = r.GetString(0);
                if (!AlertEventIds.IsId(dest)) continue;
                var scope = "dest:" + dest;
                a09.Add(new ScopedFacts(scope, new AlertFacts { Flags = Flag("hostKeyMismatch") }));
                details["A09:" + scope] = new AlertDetail(null, new Dictionary<string, long>(StringComparer.Ordinal) { ["pendingIdentities"] = r.GetInt64(1) });
            }
        }

        // A15: odmowy API w oknie (łańcuch audytu RD-42; at_utc w formacie ISO z milisekundami — porównanie tekstowe).
        long denied;
        using (var cmd = Cmd(c, tx, "SELECT COUNT(*) FROM audit_log WHERE outcome = 'denied' AND at_utc >= $f;", ("$f", OutboxDispatcher.Iso(now - DenialWindow))))
            denied = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        details["A15:host"] = new AlertDetail(null, new Dictionary<string, long>(StringComparer.Ordinal) { ["deniedCalls"] = denied });

        // A17: wstrzymanie retencji po skoku zegara (SEC-17.4, core_retention_hold) — aktywny do zwolnienia przez Administratora.
        TimeSpan? delta = null;
        using (var cmd = Cmd(c, tx, "SELECT delta_sec FROM core_retention_hold WHERE id = 1;"))
            if (cmd.ExecuteScalar() is long d) delta = TimeSpan.FromSeconds(d);
        if (delta is { } dd) details["A17:host"] = new AlertDetail(null, new Dictionary<string, long>(StringComparer.Ordinal) { ["clockDeltaSeconds"] = (long)dd.TotalSeconds });

        byRule["A01"] = a01;
        byRule["A02"] = a02;
        byRule["A03"] = a03;
        byRule["A04"] = a04;
        byRule["A09"] = a09;
        byRule["A14"] = [new ScopedFacts("host", new AlertFacts { Flags = securityUpdateAvailable ? Flag("securityUpdateAvailable") : new HashSet<string>() })];
        byRule["A15"] = [new ScopedFacts("host", new AlertFacts { DenialsInWindow = (int)Math.Min(denied, int.MaxValue) })];
        byRule["A17"] = [new ScopedFacts("host", new AlertFacts { ClockDelta = delta })];
        return new AlertFactSet(byRule, details, names);
    }

    private static HashSet<string> Flag(string name) => new(StringComparer.Ordinal) { name };

    private static DateTimeOffset? TryTime(SqliteDataReader r, int i) =>
        !r.IsDBNull(i) && DateTimeOffset.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }
}

/// <summary>Format identyfikatorów w zakresach alertów: 1–64 znaki ASCII (litery, cyfry, '-') — jak w run.finished i raporcie dziennym.</summary>
internal static class AlertEventIds
{
    public static bool IsId(string id) => DailyReportService.IsReportableJobId(id);
}
