using System.IO.Pipes;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using Kobian.Domain;

namespace Kobian.Cli.Commands;

/// <summary>
/// Klient gRPC Agenta po named pipe (D2.4, BL-14). Błędy transportu, odmowa i przekroczenie czasu (≤ 5 s, AC-37.1) → <see cref="AgentUnavailableException"/>;
/// KB-7006 z trailera → <see cref="ClientTooOldException"/> (AC-37.2). Do każdego wywołania dołączana jest rewizja klienta (D2.16).
/// </summary>
public sealed class GrpcAgentClient : IAgentClient, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly Api.V1.Agent.AgentClient _client;
    private readonly Metadata _headers = new() { { ApiConventions.ClientRevisionHeader, ApiConventions.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };

    public GrpcAgentClient(string pipeName, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var timeout = connectTimeout ?? ApiConventions.ConnectTimeout;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) => await Connect(pipeName, timeout, ct).ConfigureAwait(false),
        };
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true, MaxReceiveMessageSize = 4 * 1024 * 1024 });
        _client = new Api.V1.Agent.AgentClient(_channel);
    }

    private static async ValueTask<Stream> Connect(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            // poza Windows: gniazdo Unix zamiast named pipe (usługa: /run/kobian/agent.sock, konsola: katalog tymczasowy — ADR-0004)
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(ApiConventions.ClientSocketPath(pipeName)), cts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        // BL-17 (D2.9): poziom Identification — usługa odczyta SID i grupy, ale nie może działać jako klient.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AgentStatus> GetStatusAsync(CancellationToken ct)
    {
        var r = await Call(() => _client.GetStatusAsync(new GetStatusRequest(), _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new AgentStatus(Map(r.State), r.AgentVersion,
            [.. r.Jobs.Select(j => new Commands.JobStatus(j.Name, Map(j.LastResult),
                j.HasLastRunUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(j.LastRunUnixSeconds) : null,
                j.HasLastCode ? j.LastCode : null))],
            r.RetentionHold is { } h ? new RetentionHoldStatus(h.Code, h.HeldAtUnixSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(h.HeldAtUnixSeconds) : null, h.ClockDeltaSeconds) : null,
            r.HasRuntimeCode ? r.RuntimeCode : null, r.HasRuntimeVersion ? r.RuntimeVersion : null,
            AlertSummary.From(r.Alerts.Select(a => new AlertItem(a.AlertId, a.Code, a.Severity, a.State, a.ScopeKind, a.Subject, null, null, a.Reminders, a.Escalated))));
    }

    public async Task<RetentionReleaseResult> ReleaseRetentionHoldAsync(CancellationToken ct)
    {
        var r = await Call(() => _client.ReleaseRetentionHoldAsync(new ReleaseRetentionHoldArgs(), _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new RetentionReleaseResult(Map(r.Result), r.HasCode ? r.Code : null, r.Released);
    }

    public async Task<DatabaseRestoreResult> RestoreDatabaseAsync(string snapshot, string? targetDatabase, CancellationToken ct)
    {
        var args = new RestoreDatabaseArgs { Snapshot = snapshot };
        if (targetDatabase is not null) args.TargetDatabase = targetDatabase;
        var r = await Call(() => _client.RestoreDatabaseAsync(args, _headers, DateTime.UtcNow.AddHours(12), ct).ResponseAsync).ConfigureAwait(false);
        return new DatabaseRestoreResult(Map(r.Result), r.HasCode ? r.Code : null, r.TargetDatabase, r.SnapshotId);
    }

    public Task<AuditVerifyResult> VerifyAuditAsync(CancellationToken ct) => VerifyAuditAsync(null, ct);

    public async Task<AuditVerifyResult> VerifyAuditAsync(string? anchor, CancellationToken ct)
    {
        var args = new VerifyAuditArgs();
        if (anchor is not null) args.Anchor = anchor;
        var r = await Call(() => _client.VerifyAuditAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new AuditVerifyResult(Map(r.Result), r.HasCode ? r.Code : null, r.Entries, r.HasBrokenSeq ? r.BrokenSeq : null, r.LastHash,
            r.AnchorsChecked, r.HasLastAnchor ? r.LastAnchor : null, r.HasLastAnchorAtUtc ? r.LastAnchorAtUtc : null);
    }

    public async Task<RunOutcome> RunJobAsync(string job, bool wait, CancellationToken ct)
    {
        // wait = true: operacja może trwać długo — bez terminu, kończy ją tylko ct (Ctrl+C).
        var call = _client.RunJobAsync(new RunJobRequest { Job = job, Wait = wait }, _headers, wait ? null : DeadlineFromNow(), ct);
        return Map(await Call(() => call.ResponseAsync).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<Commands.SnapshotInfo>> ListSnapshotsAsync(string? job, CancellationToken ct)
    {
        var req = new ListSnapshotsRequest();
        if (job is not null) req.Job = job;
        var r = await Call(() => _client.ListSnapshotsAsync(req, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return [.. r.Snapshots.Select(s => new Commands.SnapshotInfo(s.Id, s.Job, DateTimeOffset.FromUnixTimeSeconds(s.TimeUnixSeconds), s.Mode, s.Files, s.BytesRaw,
            s.Damage.Length == 0 ? null : s.Damage, s.DamagedFiles))];
    }

    public async Task<IReadOnlyList<RunItem>> ListRunsAsync(string? job, int? limit, CancellationToken ct)
    {
        var req = new ListRunsRequest();
        if (job is not null) req.Job = job;
        if (limit is not null) req.Limit = limit.Value;
        var r = await Call(() => _client.ListRunsAsync(req, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return [.. r.Runs.Select(MapRun)];
    }

    /// <summary>
    /// it. 134 (SCR-07): strumień WatchRun. Bez terminu (bieg może trwać godzinami; przerywa go <paramref name="ct"/> albo stan końcowy). Błędy transportu/odmowa → te same wyjątki co <see cref="Call{T}"/>;
    /// <c>yield</c> poza blokiem try, bo C# nie pozwala na yield w try z catch.
    /// </summary>
    public async IAsyncEnumerable<RunProgressItem> WatchRunAsync(string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var call = _client.WatchRun(new WatchRunArgs { RunId = runId }, _headers, null, ct);
        while (await Call(() => call.ResponseStream.MoveNext(ct)).ConfigureAwait(false))
        {
            var p = call.ResponseStream.Current;
            yield return new RunProgressItem(MapRun(p.Run), p.BytesRead, p.Final);
        }
    }

    public async Task<RetentionResult> ApplyRetentionAsync(string job, bool dryRun, CancellationToken ct)
    {
        var r = await Call(() => _client.ApplyRetentionAsync(new ApplyRetentionArgs { Job = job, DryRun = dryRun }, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new RetentionResult(Map(r.Result), r.HasCode ? r.Code : null, r.HasPolicy, r.DryRun, r.Kept, r.Forgotten, [.. r.Items.Where(i => !i.Keep).Select(i => i.SnapshotId)], r.Held);
    }

    public async Task<PrunePlanResult> PlanPruneAsync(string job, CancellationToken ct)
    {
        var r = await Call(() => _client.PlanPruneAsync(new PlanPruneArgs { Job = job }, _headers, null, ct).ResponseAsync).ConfigureAwait(false); // spis całego repo może trwać — bez krótkiego terminu
        return new PrunePlanResult(Map(r.Result), r.HasCode ? r.Code : null, r.HasBlockedObject ? r.BlockedObject : null, r.LiveSnapshots, r.LivePacks, r.LiveBytes,
            r.DeadSnapshots, r.DeadIndexes, r.DeadPacks, r.DeadBytes, r.GraceSeconds);
    }

    public async Task<PruneRunResult> PruneAsync(string job, CancellationToken ct)
    {
        var r = await Call(() => _client.PruneAsync(new PruneArgs { Job = job }, _headers, null, ct).ResponseAsync).ConfigureAwait(false); // spis + odczekanie blokady wyłącznej (≈ 10 min) — bez krótkiego terminu
        return new PruneRunResult(Map(r.Result), r.HasCode ? r.Code : null, r.HasStopObject ? r.StopObject : null, r.Marked, r.Unmarked, r.DeletedSnapshots, r.DeletedIndexes,
            r.DeletedPacks, r.DeletedBytes, r.Pending, r.GraceSeconds, r.LocksCleaned, r.IndexesConsolidated, r.IndexesWritten, r.ActiveIndexes);
    }

    internal static RunItem MapRun(RunInfo x) => new(x.RunId, x.Job, x.Kind, x.Cause, x.State, Map(x.Result),
        x.HasStartedUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(x.StartedUnixSeconds) : null,
        x.HasFinishedUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(x.FinishedUnixSeconds) : null,
        x.HasCode ? x.Code : null, x.HasSnapshotId ? x.SnapshotId : null, x.HasFiles ? x.Files : null, x.HasBytesUploaded ? x.BytesUploaded : null);

    public async Task<RunOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct)
    {
        var req = new RestoreRequest { Snapshot = snapshot, Target = target };
        if (pathFilter is not null) req.PathFilter = pathFilter;
        return Map(await Call(() => _client.RestoreAsync(req, _headers, null, ct).ResponseAsync).ConfigureAwait(false));
    }

    public async Task<RunOutcome> RestoreDetachedAsync(string snapshot, string? pathFilter, string target, CancellationToken ct)
    {
        var req = new RestoreRequest { Snapshot = snapshot, Target = target, Detach = true };
        if (pathFilter is not null) req.PathFilter = pathFilter;
        return Map(await Call(() => _client.RestoreAsync(req, _headers, DateTime.UtcNow.AddMinutes(2), ct).ResponseAsync).ConfigureAwait(false));
    }

    public async Task<RestorePlanResult> PlanRestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct)
    {
        var req = new RestoreRequest { Snapshot = snapshot, Target = target };
        if (pathFilter is not null) req.PathFilter = pathFilter;
        var r = await Call(() => _client.PlanRestoreAsync(req, _headers, DateTime.UtcNow.AddHours(1), ct).ResponseAsync).ConfigureAwait(false);
        return MapPlan(r);
    }

    internal static RestorePlanResult MapPlan(RestorePlanReply r) => new(r.HasCode ? r.Code : null, r.SnapshotId, r.Files, r.Bytes, r.NewDirectories, r.Conflicts,
        r.ConflictBytes, [.. r.ConflictSample], r.InvalidNames, r.Links, r.MissingBlocks, r.Packs, r.ReadBytes, r.HasFreeBytes ? r.FreeBytes : null,
        r.HasBlockingCode ? r.BlockingCode : null, r.Resumable);

    public async Task<(string? Code, string? SecretRef)> SaveCredentialAsync(string name, string user, string password, CancellationToken ct)
    {
        var r = await Call(() => _client.SaveCredentialAsync(new SaveCredentialArgs { Name = name, User = user, Password = password }, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return r.Result == Api.V1.OverallState.Failed ? (r.HasCode ? r.Code : KbCode.Internal, null) : (null, r.SecretRef);
    }

    public async Task<InitResult> InitJobAsync(InitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var args = new InitJobArgs
        {
            JobName = request.JobName,
            Source = request.Source,
            DestinationScheme = request.DestinationScheme,
            DestinationRoot = request.DestinationRoot,
            PasswordFile = request.PasswordFile,
        };
        if (request.PackTargetSize is { } t) args.PackTargetSize = t;
        if (!string.IsNullOrEmpty(request.SecretRef)) args.SecretRef = request.SecretRef;
        if (!string.IsNullOrEmpty(request.Host)) args.Host = request.Host;
        if (request.Port is { } port) args.Port = port;
        if (!string.IsNullOrEmpty(request.HostKey)) args.HostKey = request.HostKey;
        args.ExtraSources.AddRange(request.ExtraSources);
        foreach (var s in request.SourceServers)
        {
            var spec = new DestinationSpec { Scheme = s.Scheme, Root = s.Root, Host = s.Host };
            if (s.Port is { } sp) spec.Port = sp;
            if (!string.IsNullOrEmpty(s.SecretRef)) spec.SecretRef = s.SecretRef;
            if (!string.IsNullOrEmpty(s.HostKey)) spec.HostKey = s.HostKey;
            args.SourceServers.Add(spec);
        }
        var r = await Call(() => _client.InitJobAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new InitResult(Map(r.Result), r.HasCode ? r.Code : null, r.HasJobId ? r.JobId : null, r.HasRepoId ? r.RepoId : null, r.HasKeyId ? r.KeyId : null);
    }

    public async Task<IReadOnlyList<ScheduleItem>> ListSchedulesAsync(string? job, CancellationToken ct)
    {
        var req = new ListSchedulesRequest();
        if (job is not null) req.Job = job;
        var r = await Call(() => _client.ListSchedulesAsync(req, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return [.. r.Schedules.Select(s => new ScheduleItem(s.Job, s.TriggerId, s.Cron, s.TimeZone, s.Enabled))];
    }

    public async Task<ScheduleSetResult> SetScheduleAsync(string job, string? cron, CancellationToken ct)
    {
        var args = new SetScheduleArgs { Job = job };
        if (cron is not null) args.Cron = cron;
        var r = await Call(() => _client.SetScheduleAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new ScheduleSetResult(Map(r.Result), r.HasCode ? r.Code : null, r.HasTriggerId ? r.TriggerId : null);
    }

    public Task<CheckResult> CheckAsync(string job, int? samplePercent, long? rotation, CancellationToken ct) => CheckAsync(job, samplePercent, rotation, false, ct);

    public async Task<CheckResult> CheckAsync(string job, int? samplePercent, long? rotation, bool repair, CancellationToken ct)
    {
        var args = new CheckArgs { Job = job, Repair = repair };
        if (samplePercent is { } p) args.SamplePercent = p;
        if (rotation is { } r) args.Rotation = r;
        // V3 pobiera packi — bez terminu, kończy tylko ct (Ctrl+C), jak restore.
        var x = await Call(() => _client.CheckAsync(args, _headers, null, ct).ResponseAsync).ConfigureAwait(false);
        return new CheckResult(Map(x.Result), x.HasCode ? x.Code : null, x.Level, x.Snapshots, x.Packs, x.PacksRead, x.BlocksVerified,
            [.. x.DamagedSnapshots], x.Issues, [.. x.Findings.Select(f => (f.Code, f.Target))],
            [.. x.Damage.Select(d => new DamageItem(d.SnapshotId, d.State, d.DamagedFiles, d.DamagedDirectories, d.IntactFiles, d.IndexRecovered))], x.RecoveredPacks, x.UnreadablePacks);
    }

    public async Task<ServerIdentityList> ListServerIdentitiesAsync(string? destinationId, CancellationToken ct)
    {
        var args = new ListServerIdentitiesArgs();
        if (destinationId is not null) args.DestinationId = destinationId;
        var r = await Call(() => _client.ListServerIdentitiesAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new ServerIdentityList(Map(r.Result), r.HasCode ? r.Code : null,
            [.. r.Identities.Select(i => new ServerIdentityItem(i.DestinationId, i.Kind, i.Fingerprint, StateName(i.State), i.Endpoint, i.HasDisplay ? i.Display : null,
                DateTimeOffset.FromUnixTimeSeconds(i.FirstSeenUnixSeconds), DateTimeOffset.FromUnixTimeSeconds(i.LastSeenUnixSeconds),
                i.HasDecidedBy ? i.DecidedBy : null, i.HasDecidedAtUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(i.DecidedAtUnixSeconds) : null, i.TrustedByCa))],
            [.. r.RequirePinDestinations]);
    }

    public async Task<IdentityDecisionResult> DecideServerIdentityAsync(string destinationId, string fingerprint, IdentityDecision decision, CancellationToken ct)
    {
        var args = new ApproveServerIdentityArgs
        {
            DestinationId = destinationId,
            Fingerprint = fingerprint,
            Decision = decision switch
            {
                IdentityDecision.Add => ServerIdentityDecision.Add,
                IdentityDecision.Replace => ServerIdentityDecision.Replace,
                _ => ServerIdentityDecision.Reject,
            },
        };
        var r = await Call(() => _client.ApproveServerIdentityAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new IdentityDecisionResult(Map(r.Result), r.HasCode ? r.Code : null, r.Result == Api.V1.OverallState.Ok ? StateName(r.State) : null);
    }

    public async Task<IdentityDecisionResult> SetServerIdentityPolicyAsync(string destinationId, bool requirePin, CancellationToken ct)
    {
        var args = new SetServerIdentityPolicyArgs { DestinationId = destinationId, RequirePin = requirePin };
        var r = await Call(() => _client.SetServerIdentityPolicyAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new IdentityDecisionResult(Map(r.Result), r.HasCode ? r.Code : null, null);
    }

    public async Task<AlertListResult> ListAlertsAsync(CancellationToken ct)
    {
        var r = await Call(() => _client.GetStatusAsync(new GetStatusRequest(), _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new AlertListResult(OverallState.Ok, null,
            [.. r.Alerts.Select(a => new AlertItem(a.AlertId, a.Code, a.Severity, a.State, a.ScopeKind, a.Subject,
                a.OpenedUnixSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(a.OpenedUnixSeconds) : null, a.HasKb ? a.Kb : null, a.Reminders, a.Escalated))]);
    }

    public async Task<AlertActionResult> AcknowledgeAlertAsync(string alertId, CancellationToken ct)
    {
        var args = new AckAlertArgs { AlertId = alertId };
        var r = await Call(() => _client.AckAlertAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new AlertActionResult(Map(r.Result), r.HasCode ? r.Code : null, r.HasState ? r.State : null);
    }

    public async Task<AlertActionResult> SilenceAlertAsync(string alertId, int minutes, CancellationToken ct)
    {
        var args = new SilenceAlertArgs { AlertId = alertId, DurationMinutes = minutes };
        var r = await Call(() => _client.SilenceAlertAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new AlertActionResult(Map(r.Result), r.HasCode ? r.Code : null, r.HasState ? r.State : null);
    }

    private static string StateName(Api.V1.ServerIdentityState s) => s switch
    {
        Api.V1.ServerIdentityState.Pending => "pending",
        Api.V1.ServerIdentityState.Pinned => "pinned",
        Api.V1.ServerIdentityState.Rejected => "rejected",
        Api.V1.ServerIdentityState.Retired => "retired",
        Api.V1.ServerIdentityState.Expired => "expired",
        _ => "unknown",
    };

    public async Task<JobsExportResult> ExportJobsAsync(IReadOnlyList<string> jobs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var args = new ExportJobsArgs();
        args.Jobs.AddRange(jobs);
        var r = await Call(() => _client.ExportJobsAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new JobsExportResult(Map(r.Result), r.HasCode ? r.Code : null, r.Document.IsEmpty ? null : r.Document.ToByteArray(), r.Jobs,
            [.. r.Issues.Select(i => new TransferIssue(i.Path, i.Code, i.Reason))]);
    }

    public async Task<JobsImportResult> ImportJobsAsync(byte[] document, bool dryRun, ImportConflict onConflict, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        var args = new ImportJobsArgs
        {
            Document = Google.Protobuf.ByteString.CopyFrom(document),
            ValidateOnly = dryRun,
            OnConflict = onConflict switch
            {
                ImportConflict.Skip => ImportConflictMode.Skip,
                ImportConflict.Overwrite => ImportConflictMode.Overwrite,
                ImportConflict.Rename => ImportConflictMode.Rename,
                _ => ImportConflictMode.Fail,
            },
        };
        var r = await Call(() => _client.ImportJobsAsync(args, _headers, DeadlineFromNow(), ct).ResponseAsync).ConfigureAwait(false);
        return new JobsImportResult(Map(r.Result), r.HasCode ? r.Code : null, [.. r.Issues.Select(i => new TransferIssue(i.Path, i.Code, i.Reason))],
            [.. r.Changes.Select(c => new JobImportChange(c.Job, c.Kind, c.Action, c.HasNewName ? c.NewName : null, c.NeedsCredentials, c.MissingKeyMaterial))], r.Applied);
    }

    public void Dispose() => _channel.Dispose();

    private static DateTime DeadlineFromNow() => DateTime.UtcNow + TimeSpan.FromSeconds(30);

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.FailedPrecondition && e.Trailers.GetValue(ApiConventions.KbCodeTrailer) == KbCode.ClientRevisionTooOld)
        {
            throw new ClientTooOldException(KbCode.ClientRevisionTooOld, e);
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated)
        {
            // Odmowa roli to nie „usługa niedostępna”: kod z trailera Agenta (KB-7011/KB-7001) trafia do komunikatu; kod wyjścia bez zmian (3).
            var served = e.Trailers.GetValue(ApiConventions.KbCodeTrailer);
            throw new AgentUnavailableException(served is KbCode.ApiAccessDenied or KbCode.CallerUnauthenticated ? served : KbCode.AgentUnreachable, e);
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Internal)
        {
            throw new AgentUnavailableException(KbCode.AgentUnreachable, e);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled)
        {
            throw new OperationCanceledException("The call was cancelled.", e); // Ctrl+C: wyjście obsługuje CliApp, nie nieobsłużony wyjątek gRPC
        }
        catch (RpcException e)
        {
            throw new AgentInternalException(KbCode.Internal, e); // Unknown (wyjątek w obsłudze), NotFound, ResourceExhausted…: czytelny błąd zamiast stosu wywołań
        }
    }

    private static OverallState Map(Api.V1.OverallState s) => s switch
    {
        Api.V1.OverallState.Ok => OverallState.Ok,
        Api.V1.OverallState.Warning => OverallState.Warning,
        _ => OverallState.Failed,
    };

    private static RunOutcome Map(Api.V1.RunOutcome o) => new(Map(o.Result), o.HasCode ? o.Code : null, o.HasSnapshotId ? o.SnapshotId : null, o.HasRunId ? o.RunId : null);
}
