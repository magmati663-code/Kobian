using System.IO.Pipes;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.History;
using Kobian.UI.Jobs;

namespace Kobian.UI.Services;

/// <summary>
/// Adapter SCR-01 → Agent (kobian.v1 po named pipe; poza Windows gniazdo Unix — ADR-0004): GetStatus + ListSnapshots (ostatnia udana kopia).
/// Brak połączenia/odmowa/termin → <see cref="IOException"/> (pulpit: „Usługa Kobian nie działa”, R2.6.4); inne błędy RPC → <see cref="InvalidOperationException"/>.
/// BL-60 (it. 61): następne uruchomienie, cykl, poziom weryfikacji, schemat celu, „suspect” i rola przychodzą z GetStatus; wolne miejsce na celu — jeszcze nie (ProbeDestination, BL-60 cz. 2).
/// </summary>
public sealed class GrpcDashboardSource : IDashboardSource, IJobsSource, IHistorySource, Kobian.UI.Restore.ISnapshotBrowseSource, Kobian.UI.Restore.IRestoreService, Kobian.UI.Restore.IDatabaseRestoreService, Kobian.UI.Settings.IServiceInfoSource, Kobian.UI.Destinations.IDestinationsSource, Kobian.UI.Progress.IRunProgressSource, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly Agent.AgentClient _client;
    private readonly TimeProvider _time;
    private readonly Metadata _headers = new() { { ApiConventions.ClientRevisionHeader, ApiConventions.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };

    public GrpcDashboardSource(string pipeName, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _time = time ?? TimeProvider.System;
        var handler = new SocketsHttpHandler { ConnectCallback = async (_, ct) => await ConnectAsync(pipeName, ct).ConfigureAwait(false) };
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true, MaxReceiveMessageSize = 4 * 1024 * 1024 });
        _client = new Agent.AgentClient(_channel);
    }

    public void Dispose() => _channel.Dispose();

    /// <summary>
    /// RD-79 (SCR-07): strumień WatchRun → <see cref="Kobian.UI.Progress.RunProgressUpdate"/>. Bez terminu (bieg może trwać godzinami; kończy go stan końcowy albo <paramref name="ct"/>).
    /// Błędy transportu → <see cref="IOException"/> (widok ponawia), odmowa/brak metody → <see cref="InvalidOperationException"/> z <see cref="RpcException"/> w InnerException.
    /// </summary>
    public async IAsyncEnumerable<Kobian.UI.Progress.RunProgressUpdate> WatchAsync(string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var call = _client.WatchRun(new WatchRunArgs { RunId = runId }, _headers, null, ct);
        while (await Call(() => call.ResponseStream.MoveNext(ct)).ConfigureAwait(false))
            yield return MapProgress(call.ResponseStream.Current);
    }

    internal static Kobian.UI.Progress.RunProgressUpdate MapProgress(RunProgress p) =>
        new(p.Run.RunId, p.Run.Job, p.Run.Kind, p.Run.State,
            p.Run.Result switch { OverallState.Ok => Kobian.UI.Progress.RunOutcomeKind.Ok, OverallState.Warning => Kobian.UI.Progress.RunOutcomeKind.Warning, OverallState.Failed => Kobian.UI.Progress.RunOutcomeKind.Failed, _ => Kobian.UI.Progress.RunOutcomeKind.Unknown },
            p.Run.HasCode ? p.Run.Code : null, p.Run.HasFiles ? p.Run.Files : null, p.Run.HasBytesUploaded ? p.Run.BytesUploaded : null, p.BytesRead, p.Final);

    /// <summary>Adapter kreatora SCR-03 na tym samym kanale (BL-61).</summary>
    public Wizard.IWizardService CreateWizardService() => new GrpcWizardService(_client, _headers);

    public async Task<DashboardData> LoadAsync(CancellationToken ct)
    {
        var status = await Call(() => _client.GetStatusAsync(new GetStatusRequest(), _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        var snaps = await Call(() => _client.ListSnapshotsAsync(new ListSnapshotsRequest(), _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        var data = Map(status, snaps, _time.GetUtcNow());
        try
        {
            var sched = await Call(() => _client.ListSchedulesAsync(new ListSchedulesRequest(), _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
            return ApplyPausedSchedules(data, sched);
        }
        catch (InvalidOperationException)
        {
            return data; // starszy Agent bez ListSchedules / odmowa roli: pulpit z samego GetStatus, jak dotąd
        }
    }

    /// <summary>
    /// „Wstrzymaj” w SCR-02 wyłącza tylko wyzwalacz harmonogramu — GetStatus nadal ma zadanie włączone, bez cyklu i z ostatnim wynikiem OK, więc pulpit pokazywał zielone „Chronione”,
    /// mimo że kopie automatyczne przestały się robić (SCR-02 mówił „Wstrzymane”). Zadanie z wyłączonym harmonogramem jest dla pulpitu wyłączone — tak samo jak w SCR-02.
    /// </summary>
    internal static DashboardData ApplyPausedSchedules(DashboardData data, ListSchedulesResponse schedules)
    {
        var paused = schedules.Schedules.Where(s => !s.Enabled).Select(s => s.Job).ToHashSet(StringComparer.Ordinal);
        return paused.Count == 0 ? data : data with { Jobs = [.. data.Jobs.Select(j => paused.Contains(j.JobId) ? j with { Enabled = false } : j)] };
    }

    public Task RunJobAsync(string jobId, CancellationToken ct) =>
        Call(() => _client.RunJobAsync(new RunJobRequest { Job = jobId, Wait = false }, _headers, Deadline(), ct).ResponseAsync);

    /// <summary>BL-45: AckAlert (Operator). Odmowa usługi → kod KB z odpowiedzi; starszy Agent bez metody → KB-7012; odmowa roli → KB-7011.</summary>
    public Task<string?> AcknowledgeAlertAsync(string alertId, CancellationToken ct) =>
        AlertAction(() => _client.AckAlertAsync(new AckAlertArgs { AlertId = alertId }, _headers, Deadline(), ct).ResponseAsync);

    /// <summary>BL-45: SilenceAlert (Administrator; 1…10080 min).</summary>
    public Task<string?> SilenceAlertAsync(string alertId, int minutes, CancellationToken ct) =>
        AlertAction(() => _client.SilenceAlertAsync(new SilenceAlertArgs { AlertId = alertId, DurationMinutes = minutes }, _headers, Deadline(), ct).ResponseAsync);

    private static async Task<string?> AlertAction(Func<Task<AlertActionReply>> f)
    {
        try
        {
            return MapAlertAction(await Call(f).ConfigureAwait(false));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.PermissionDenied or StatusCode.Unauthenticated })
        {
            return Kobian.Domain.KbCode.ApiAccessDenied;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.Unimplemented })
        {
            return Kobian.Domain.KbCode.OperationNotAvailable;
        }
    }

    internal static string? MapAlertAction(AlertActionReply r) =>
        r.Result == OverallState.Failed ? (r.HasCode ? r.Code : KbUnknown) : null;

    internal static IReadOnlyList<DashboardAlert> MapAlerts(IEnumerable<AlertInfo> alerts) =>
        [.. alerts.Select(a => new DashboardAlert(a.AlertId, a.Code,
            a.Severity switch { "security" => AlertSeverity.Security, "critical" => AlertSeverity.Critical, _ => AlertSeverity.Warning },
            a.State switch { "notified" => AlertState.Notified, "acknowledged" => AlertState.Acknowledged, "silenced" => AlertState.Silenced, _ => AlertState.Open },
            a.ScopeKind, a.Subject, DateTimeOffset.FromUnixTimeSeconds(a.OpenedUnixSeconds), a.HasKb ? a.Kb : null, a.Reminders, a.Escalated))
            .Where(a => a.Id.Length > 0)]; // alert bez identyfikatora (stary Agent) nie ma jak być potwierdzony — pomijamy zamiast pokazywać martwe przyciski

    /// <summary>SCR-02: stan jak na pulpicie + harmonogramy (ListSchedules — także wyłączone, z zachowanym wyrażeniem).</summary>
    public async Task<JobsData> LoadJobsAsync(CancellationToken ct)
    {
        var dash = await LoadAsync(ct).ConfigureAwait(false);
        var sched = await Call(() => _client.ListSchedulesAsync(new ListSchedulesRequest(), _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return new JobsData(dash.Jobs, MapSchedules(sched), dash.Role, dash.AsOfUtc);
    }

    /// <summary>Uruchomienie bez czekania; odmowa usługi (Failed) → kod KB z odpowiedzi.</summary>
    public async Task<string?> StartJobAsync(string job, CancellationToken ct)
    {
        var r = await Call(() => _client.RunJobAsync(new RunJobRequest { Job = job, Wait = false }, _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return r.Result == OverallState.Failed ? (r.HasCode ? r.Code : KbUnknown) : null;
    }

    /// <summary>cron = null → wyłączenie harmonogramu (SetSchedule bez pola cron); inaczej ustawienie/wznowienie.</summary>
    public async Task<string?> SetScheduleAsync(string job, string? cron, CancellationToken ct)
    {
        var args = new SetScheduleArgs { Job = job };
        if (cron is not null) args.Cron = cron;
        var r = await Call(() => _client.SetScheduleAsync(args, _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return r.Result == OverallState.Failed ? (r.HasCode ? r.Code : KbUnknown) : null;
    }

    /// <summary>SCR-10: kopie z ListSnapshots (opcjonalnie jednego zadania).</summary>
    public async Task<IReadOnlyList<SnapshotEntry>> ListSnapshotsAsync(string? job, CancellationToken ct)
    {
        var req = new ListSnapshotsRequest();
        if (job is not null) req.Job = job;
        var r = await Call(() => _client.ListSnapshotsAsync(req, _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return MapSnapshots(r);
    }

    /// <summary>SCR-10: uruchomienia z ListRuns (500 najnowszych). Starszy Agent bez metody (Unimplemented) → pusta lista (historia tylko z kopii).</summary>
    public async Task<IReadOnlyList<RunEntry>> ListRunsAsync(string? job, CancellationToken ct)
    {
        var req = new ListRunsRequest { Limit = 500 };
        if (job is not null) req.Job = job;
        try
        {
            var r = await _client.ListRunsAsync(req, _headers, Deadline(), ct).ResponseAsync.ConfigureAwait(false);
            return MapRuns(r);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            return [];
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled)
        {
            throw new IOException("Kobian service is not reachable.", ex);
        }
        catch (RpcException ex)
        {
            throw new InvalidOperationException("Kobian service rejected the call.", ex);
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or TimeoutException)
        {
            throw new IOException("Kobian service is not reachable.", ex);
        }
    }

    /// <summary>SCR-06: folder kopii (BrowseTree, BL-63). Starszy Agent bez metody (Unimplemented) → KB-7012 jako wynik (ekran pokazuje komunikat, nie stan offline).</summary>
    public async Task<Kobian.UI.Restore.BrowseResult> BrowseAsync(string snapshotId, string path, CancellationToken ct)
    {
        try
        {
            var r = await Call(() => _client.BrowseTreeAsync(new BrowseTreeArgs { Snapshot = snapshotId, Path = path }, _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
            return MapBrowse(r, path);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.Unimplemented })
        {
            return new Kobian.UI.Restore.BrowseResult(Kobian.Domain.KbCode.OperationNotAvailable, path, [], false);
        }
    }

    /// <summary>SCR-09: cele z definicji zadań (ListDestinations). Starszy Agent (Unimplemented) → pusta lista.</summary>
    public async Task<Kobian.UI.Destinations.DestinationList> ListDestinationsAsync(CancellationToken ct)
    {
        try
        {
            var r = await Call(() => _client.ListDestinationsAsync(new ListDestinationsArgs(), _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
            return MapDestinations(r);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.Unimplemented })
        {
            return new([], 0);
        }
    }

    internal static Kobian.UI.Destinations.DestinationList MapDestinations(ListDestinationsReply r) =>
        new([.. r.Destinations.Select(d => new Kobian.UI.Destinations.DestinationItem(d.Id, d.Scheme, d.Root, [.. d.Jobs]))], r.UnreadableJobs);

    /// <summary>SCR-09: ProbeDestination dla celu z listy (Operator; połączenie na koncie usługi).</summary>
    public async Task<Kobian.UI.Destinations.DestinationCheck> CheckDestinationAsync(Kobian.UI.Destinations.DestinationItem destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var spec = new DestinationSpec { Scheme = destination.Scheme, Root = destination.Root };
        // Cel zdalny: host/port/pin, gdy usługa je podała (ListDestinations v1 — nie; wtedy Agent odpowie kodem KB, jak dotąd dla SFTP).
        if (!string.IsNullOrEmpty(destination.Host)) spec.Host = destination.Host;
        if (destination.Port is { } port) spec.Port = port;
        if (!string.IsNullOrEmpty(destination.HostKey)) spec.HostKey = destination.HostKey;
        var r = await Call(() => _client.ProbeDestinationAsync(new ProbeDestinationArgs { Destination = spec }, _headers, DateTime.UtcNow.AddSeconds(60), ct).ResponseAsync).ConfigureAwait(false);
        return MapCheck(r);
    }

    internal static Kobian.UI.Destinations.DestinationCheck MapCheck(ProbeDestinationReply r) =>
        new(r.Status == ProbeStatus.Ok ? null : r.HasCode ? r.Code : KbUnknown, r.HasFreeBytes ? r.FreeBytes : null, r.HasTotalBytes ? r.TotalBytes : null, r.RepositoryExists);

    /// <summary>SCR-11: wersja usługi, rola i poziom poprawek .NET (GetStatus).</summary>
    public async Task<Kobian.UI.Settings.ServiceInfo> GetServiceInfoAsync(CancellationToken ct)
    {
        var st = await Call(() => _client.GetStatusAsync(new GetStatusRequest(), _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return new(st.AgentVersion, MapRole(st.CallerRole), st.HasRuntimeCode ? st.RuntimeCode : null, st.HasRuntimeVersion ? st.RuntimeVersion : null);
    }

    /// <summary>SCR-11: weryfikacja łańcucha audytu (RD-42). Starszy Agent (Unimplemented) → KB-7012.</summary>
    public async Task<Kobian.UI.Settings.AuditCheckResult> VerifyAuditAsync(CancellationToken ct)
    {
        try
        {
            var r = await Call(() => _client.VerifyAuditAsync(new VerifyAuditArgs(), _headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
            return MapAudit(r);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.Unimplemented })
        {
            return new(false, 0, null, Kobian.Domain.KbCode.OperationNotAvailable);
        }
    }

    internal static Kobian.UI.Settings.AuditCheckResult MapAudit(VerifyAuditReply r) =>
        new(r.Result == OverallState.Ok, r.Entries, r.HasBrokenSeq ? r.BrokenSeq : null, r.HasCode ? r.Code : null,
            r.HasLastAnchor ? r.LastAnchor : null, r.HasLastAnchorAtUtc ? r.LastAnchorAtUtc : null, r.AnchorsChecked);

    /// <summary>SCR-07: RPC Restore (Operator; czeka na wynik — termin 6 h). Odmowa roli → KB-7011 jako wynik, nie wyjątek.</summary>
    public async Task<Kobian.UI.Restore.RestoreRunResult> RestoreAsync(string snapshotId, string? exactPath, string target, CancellationToken ct)
    {
        var req = new RestoreRequest { Snapshot = snapshotId, Target = target };
        if (exactPath is not null) req.ExactPaths.Add(exactPath); // UI i Agent z jednego instalatora; starszy Agent zignorowałby pole (Qb37)
        try
        {
            var r = await Call(() => _client.RestoreAsync(req, _headers, DateTime.UtcNow.AddHours(6), ct).ResponseAsync).ConfigureAwait(false);
            return MapRestore(r);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.PermissionDenied or StatusCode.Unauthenticated })
        {
            return new(Kobian.UI.Restore.RestoreRunState.Failed, Kobian.Domain.KbCode.ApiAccessDenied);
        }
    }

    /// <summary>
    /// RD-81: RPC Restore z detach=true (Operator) — Agent zwraca od razu run_id, odtwarzanie idzie w tle. Odmowa roli → KB-7011; starszy Agent (bez pola detach)
    /// odpowiedziałby po całym odtworzeniu bez run_id — wtedy zwracamy jego wynik jako gotowy (bez śledzenia), więc nic nie jest odtwarzane dwa razy.
    /// </summary>
    public async Task<Kobian.UI.Restore.DetachedRestoreStart?> StartDetachedAsync(string snapshotId, string? exactPath, string target, CancellationToken ct)
    {
        var req = new RestoreRequest { Snapshot = snapshotId, Target = target, Detach = true };
        if (exactPath is not null) req.ExactPaths.Add(exactPath);
        try
        {
            var r = await Call(() => _client.RestoreAsync(req, _headers, DateTime.UtcNow.AddMinutes(5), ct).ResponseAsync).ConfigureAwait(false);
            return r.HasRunId && r.RunId.Length > 0
                ? new(r.RunId, null)
                : new(null, MapRestore(r));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.PermissionDenied or StatusCode.Unauthenticated })
        {
            return new(null, new(Kobian.UI.Restore.RestoreRunState.Failed, Kobian.Domain.KbCode.ApiAccessDenied));
        }
    }

    /// <summary>SCR-07 podgląd (it. 98): RPC PlanRestore (Operator; termin 1 h). Starszy Agent bez metody → null (ekran odtwarza bez podglądu); odmowa roli → KB-7011.</summary>
    public async Task<Kobian.UI.Restore.RestorePreview?> PlanAsync(string snapshotId, string? exactPath, string target, CancellationToken ct)
    {
        var req = new RestoreRequest { Snapshot = snapshotId, Target = target };
        if (exactPath is not null) req.ExactPaths.Add(exactPath);
        try
        {
            var r = await Call(() => _client.PlanRestoreAsync(req, _headers, DateTime.UtcNow.AddHours(1), ct).ResponseAsync).ConfigureAwait(false);
            return MapPlan(r);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.PermissionDenied or StatusCode.Unauthenticated })
        {
            return new(Kobian.Domain.KbCode.ApiAccessDenied, 0, 0, 0, [], 0, null, null);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.Unimplemented })
        {
            return null;
        }
    }

    internal static Kobian.UI.Restore.RestorePreview MapPlan(RestorePlanReply r) =>
        new(r.HasCode ? r.Code : null, r.Files, r.Bytes, r.Conflicts, [.. r.ConflictSample], r.InvalidNames + r.Links,
            r.HasFreeBytes ? r.FreeBytes : null, r.HasBlockingCode ? r.BlockingCode : null, r.Resumable);

    /// <summary>SCR-08: RPC RestoreDatabase (Operator; termin 12 h). Odmowa roli → KB-7011, starszy Agent → KB-7012.</summary>
    public async Task<Kobian.UI.Restore.DbRestoreResult> RestoreDatabaseAsync(string snapshotId, string? targetDatabase, CancellationToken ct)
    {
        var args = new RestoreDatabaseArgs { Snapshot = snapshotId };
        if (targetDatabase is not null) args.TargetDatabase = targetDatabase;
        try
        {
            var r = await Call(() => _client.RestoreDatabaseAsync(args, _headers, DateTime.UtcNow.AddHours(12), ct).ResponseAsync).ConfigureAwait(false);
            return MapDbRestore(r);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.PermissionDenied or StatusCode.Unauthenticated })
        {
            return new(Kobian.UI.Restore.RestoreRunState.Failed, Kobian.Domain.KbCode.ApiAccessDenied, null);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is RpcException { StatusCode: StatusCode.Unimplemented })
        {
            return new(Kobian.UI.Restore.RestoreRunState.Failed, Kobian.Domain.KbCode.OperationNotAvailable, null);
        }
    }

    internal static Kobian.UI.Restore.DbRestoreResult MapDbRestore(RestoreDatabaseReply r) => r.Result switch
    {
        OverallState.Ok => new(Kobian.UI.Restore.RestoreRunState.Succeeded, null, r.TargetDatabase),
        // D5.11: baza została utworzona, ale nie zgadza się z zapytaniami kontrolnymi (KB-3505) — ostrzeżenie z nazwą bazy, nie błąd.
        OverallState.Warning => new(Kobian.UI.Restore.RestoreRunState.Warning, r.HasCode ? r.Code : KbUnknown, r.TargetDatabase),
        _ => new(Kobian.UI.Restore.RestoreRunState.Failed, r.HasCode ? r.Code : KbUnknown, null),
    };

    internal static Kobian.UI.Restore.RestoreRunResult MapRestore(RunOutcome r) => r.Result switch
    {
        OverallState.Ok => new(Kobian.UI.Restore.RestoreRunState.Succeeded, r.HasCode ? r.Code : null),
        OverallState.Warning => new(Kobian.UI.Restore.RestoreRunState.Warning, r.HasCode ? r.Code : null),
        _ => new(Kobian.UI.Restore.RestoreRunState.Failed, r.HasCode ? r.Code : KbUnknown),
    };

    internal static Kobian.UI.Restore.BrowseResult MapBrowse(BrowseTreeReply r, string requestedPath)
    {
        if (r.Result == OverallState.Failed) return new(r.HasCode ? r.Code : KbUnknown, requestedPath, [], false);
        var items = r.Entries.Select(e => new Kobian.UI.Restore.BrowseItem(e.Name,
            e.Kind switch
            {
                BrowseEntryKind.Directory => Kobian.UI.Restore.BrowseItemKind.Directory,
                BrowseEntryKind.File => Kobian.UI.Restore.BrowseItemKind.File,
                _ => Kobian.UI.Restore.BrowseItemKind.Link,
            },
            Math.Max(0, e.Size),
            e.HasModifiedUnixMs && e.ModifiedUnixMs is >= -62135596800000 and <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(e.ModifiedUnixMs) : null)).ToList();
        return new(null, r.Path, items, r.Truncated);
    }

    /// <summary>Czas wiersza = zakończenie, inaczej start, inaczej kolejka; uruchomienie bez żadnego czasu pomijane.</summary>
    internal static IReadOnlyList<RunEntry> MapRuns(ListRunsResponse r) =>
        [.. r.Runs.Select(x => (x, t: x.HasFinishedUnixSeconds ? x.FinishedUnixSeconds : x.HasStartedUnixSeconds ? x.StartedUnixSeconds : x.HasQueuedUnixSeconds ? x.QueuedUnixSeconds : (long?)null))
            .Where(p => p.t is not null)
            .Select(p => new RunEntry(p.x.RunId, p.x.Job, p.x.Kind, p.x.State, DateTimeOffset.FromUnixTimeSeconds(p.t!.Value), p.x.HasCode ? p.x.Code : null, p.x.HasSnapshotId ? p.x.SnapshotId : null))];

    /// <summary>Kod zastępczy, gdy usługa odmówiła bez kodu (nie powinno się zdarzyć — D2.10 wymaga kodu przy Failed).</summary>
    internal const string KbUnknown = "KB-0001";

    internal static IReadOnlyList<JobSchedule> MapSchedules(ListSchedulesResponse r) =>
        r.Schedules.Select(s => new JobSchedule(s.Job, s.Cron, s.Enabled)).ToList();

    internal static IReadOnlyList<SnapshotEntry> MapSnapshots(ListSnapshotsResponse r) =>
        r.Snapshots.Select(s => new SnapshotEntry(s.Id, s.Job, DateTimeOffset.FromUnixTimeSeconds(s.TimeUnixSeconds), s.Mode, s.Files, s.BytesRaw,
            s.Damage is "partial" or "unrecoverable" ? s.Damage : null, Math.Max(0, s.DamagedFiles))).ToList(); // nieznana wartość = bez znacznika

    /// <summary>
    /// Mapowanie kontraktu na model pulpitu (czyste — testowane bez transportu). Identyfikator zadania = nazwa (RunJob przyjmuje nazwę).
    /// Od BL-60 Agent podaje następne uruchomienie, cykl, ostatnią nieuszkodzoną kopię z poziomem weryfikacji, schemat celu, „suspect” i rolę;
    /// wartości zastępcze (ListSnapshots, V2 z wyniku, rola Operator) zostają wyłącznie dla starszego Agenta, który tych pól nie wysyła.
    /// </summary>
    internal static DashboardData Map(GetStatusResponse status, ListSnapshotsResponse snaps, DateTimeOffset now)
    {
        var lastSnap = snaps.Snapshots.GroupBy(s => s.Job, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => DateTimeOffset.FromUnixTimeSeconds(g.Max(s => s.TimeUnixSeconds)), StringComparer.Ordinal);
        var jobs = status.Jobs.Select(j =>
        {
            var result = !j.HasLastRunUnixSeconds ? LastRunResult.None : j.LastResult switch
            {
                OverallState.Ok => LastRunResult.Succeeded,
                OverallState.Warning => LastRunResult.Warning,
                _ => LastRunResult.Failed,
            };
            DateTimeOffset? success = j.HasLastSuccessUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(j.LastSuccessUnixSeconds)
                : lastSnap.TryGetValue(j.Name, out var t) ? t : null;
            var verify = j.LastSuccessVerifyLevel > 0 ? j.LastSuccessVerifyLevel
                : success is not null && result is LastRunResult.Succeeded or LastRunResult.Warning ? 2 : 1;
            return new JobStatusInfo(j.Name, j.Name, !j.Disabled, result,
                j.HasLastRunUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(j.LastRunUnixSeconds) : null, success,
                j.HasNextRunUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(j.NextRunUnixSeconds) : null,
                j.HasIntervalSeconds && j.IntervalSeconds > 0 ? TimeSpan.FromSeconds(j.IntervalSeconds) : null,
                LastSuccessVerifyLevel: verify,
                LastCode: j.HasLastCode ? j.LastCode : null,
                DestinationName: string.IsNullOrEmpty(j.DestinationScheme) ? null : j.DestinationScheme,
                RepositoryDamaged: j.RepositorySuspect,
                Activity: MapActivity(j.ActiveState));
        }).ToList();
        return new DashboardData(jobs, [], MapRole(status.CallerRole), now, RetentionHeld: status.RetentionHold is not null,
            RuntimeCode: status.HasRuntimeCode ? status.RuntimeCode : null, Alerts: MapAlerts(status.Alerts));
    }

    /// <summary>BL-76: „Queued”/„Running” z Agenta; pusty albo nieznany (stary Agent) = brak informacji.</summary>
    internal static ActiveRun MapActivity(string? state) => state switch
    {
        "Queued" => ActiveRun.Queued,
        "Running" => ActiveRun.Running,
        _ => ActiveRun.None,
    };

    /// <summary>Rola z Agenta (D2.9). Brak pola (starszy Agent) → PROWIZORYCZNIE Qb18: Operator — autoryzację i tak egzekwuje Agent.</summary>
    internal static Dashboard.CallerRole MapRole(Api.V1.CallerRole role) => role switch
    {
        Api.V1.CallerRole.Reader => Dashboard.CallerRole.Viewer,
        Api.V1.CallerRole.Administrator => Dashboard.CallerRole.Admin,
        _ => Dashboard.CallerRole.Operator,
    };

    private static DateTime Deadline() => DateTime.UtcNow + ApiConventions.ConnectTimeout + TimeSpan.FromSeconds(5);

    internal static Task<T> CallShared<T>(Func<Task<T>> f) => Call(f);

    private static async Task<T> Call<T>(Func<Task<T>> f)
    {
        try
        {
            return await f().ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled)
        {
            throw new IOException("Kobian service is not reachable.", ex);
        }
        catch (RpcException ex)
        {
            throw new InvalidOperationException("Kobian service rejected the call.", ex);
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or TimeoutException)
        {
            throw new IOException("Kobian service is not reachable.", ex);
        }
    }

    private static async ValueTask<Stream> ConnectAsync(string pipeName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ApiConventions.ConnectTimeout);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(ApiConventions.UnixSocketPath(pipeName)), cts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        // Weryfikacja tożsamości serwera pipe (PID = SCM, podpis — D2.9, KB-7004) — Windows, W-17 w kolejce.
        // BL-17 (D2.9): poziom Identification — usługa odczyta SID i grupy, ale nie może działać jako klient.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)ApiConventions.ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
