using Grpc.Core;
using Kobian.Api.V1;

namespace Kobian.Agent.Hosting;

/// <summary>Adapter kobian.v1.Agent → <see cref="IAgentBackend"/>. Bez logiki biznesowej; jedynie rola wywołującego w GetStatus (BL-60) z tego samego resolvera co autoryzacja.</summary>
public sealed class AgentGrpcService(IAgentBackend backend, ICallerRoleResolver roles, IRestoreTargetAuthorizer restoreTargets) : Api.V1.Agent.AgentBase
{
    /// <summary>SEC-6.3 (it. 101): Operator — folder docelowy musi być zapisywalny dla KLIENTA (AccessCheck); Administrator bez kontroli.</summary>
    private string? DenyTarget(ServerCallContext context, string target) =>
        RestoreTargetGate.Deny(roles.ResolveCaller(context).Role, target, t => restoreTargets.Check(context, t));

    /// <summary>Lokalny cel repozytorium: Operator tylko tam, gdzie sam może zapisywać (usługa Linux/macOS jako root — inaczej np. /etc). Inne schematy bez zmian.</summary>
    private string? DenyDestination(ServerCallContext context, string? scheme, string? root) =>
        scheme == "local"
            ? RestoreTargetGate.DenyPath(roles.ResolveCaller(context).Role, root, r => restoreTargets.CheckDestination(context, r), Kobian.Domain.KbCode.AccessDenied)
            : null;

    public override async Task<GetStatusResponse> GetStatus(GetStatusRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = await backend.GetStatusAsync(context.CancellationToken).ConfigureAwait(false);
        response.CallerRole = ToProto(roles.Resolve(context));
        return response;
    }

    public override Task<ListRunsResponse> ListRuns(ListRunsRequest request, ServerCallContext context) =>
        backend.ListRunsAsync(request.HasJob ? request.Job : null, request.HasLimit ? request.Limit : null, context.CancellationToken);

    /// <summary>it. 134: strumień zmian jednego uruchomienia; autoryzację zapewnia <see cref="AgentCallInterceptor"/> (ServerStreamingServerHandler).</summary>
    public override async Task WatchRun(WatchRunArgs request, IServerStreamWriter<RunProgress> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        await foreach (var progress in backend.WatchRunAsync(request.RunId, context.CancellationToken).ConfigureAwait(false))
            await responseStream.WriteAsync(progress, context.CancellationToken).ConfigureAwait(false);
    }

    public override Task<ProbeDestinationReply> ProbeDestination(ProbeDestinationArgs request, ServerCallContext context) =>
        backend.ProbeDestinationAsync(request, context.CancellationToken);

    public override Task<DiscoverDatabasesReply> DiscoverDatabases(DiscoverDatabasesArgs request, ServerCallContext context) =>
        backend.DiscoverDatabasesAsync(request, context.CancellationToken);

    public override Task<FetchSqlCertificateReply> FetchSqlCertificate(FetchSqlCertificateArgs request, ServerCallContext context) =>
        backend.FetchSqlCertificateAsync(request, context.CancellationToken);

    public override Task<ApproveSqlCertificateReply> ApproveSqlCertificate(ApproveSqlCertificateArgs request, ServerCallContext context) =>
        backend.ApproveSqlCertificateAsync(request, context.CancellationToken);

    public override Task<ExportJobsReply> ExportJobs(ExportJobsArgs request, ServerCallContext context) =>
        backend.ExportJobsAsync(request, context.CancellationToken);

    public override Task<ImportJobsReply> ImportJobs(ImportJobsArgs request, ServerCallContext context) =>
        backend.ImportJobsAsync(request, context.CancellationToken);

    public override Task<UpsertJobReply> UpsertJob(UpsertJobArgs request, ServerCallContext context) =>
        DenyDestination(context, request.Destination?.Scheme, request.Destination?.Root) is { } denied
            ? Task.FromResult(new UpsertJobReply { Result = OverallState.Failed, Code = denied })
            : backend.UpsertJobAsync(request, context.CancellationToken);

    public override Task<InitRepositoryReply> InitRepository(InitRepositoryArgs request, ServerCallContext context) =>
        DenyDestination(context, request.Destination?.Scheme, request.Destination?.Root) is { } denied
            ? Task.FromResult(new InitRepositoryReply { Result = OverallState.Failed, Code = denied })
            : backend.InitRepositoryAsync(request, context.CancellationToken);

    public override Task<ApplyRetentionReply> ApplyRetention(ApplyRetentionArgs request, ServerCallContext context) =>
        backend.ApplyRetentionAsync(request, context.CancellationToken);

    public override Task<PreviewRetentionReply> PreviewRetention(PreviewRetentionArgs request, ServerCallContext context) =>
        backend.PreviewRetentionAsync(request, context.CancellationToken);

    public override Task<PlanPruneReply> PlanPrune(PlanPruneArgs request, ServerCallContext context) =>
        backend.PlanPruneAsync(request, context.CancellationToken);

    public override Task<PruneReply> Prune(PruneArgs request, ServerCallContext context) =>
        backend.PruneAsync(request, context.CancellationToken);

    public override Task<ReleaseRetentionHoldReply> ReleaseRetentionHold(ReleaseRetentionHoldArgs request, ServerCallContext context) =>
        backend.ReleaseRetentionHoldAsync(context.CancellationToken);

    public override Task<BrowseTreeReply> BrowseTree(BrowseTreeArgs request, ServerCallContext context) =>
        backend.BrowseTreeAsync(request, context.CancellationToken);

    public override Task<VerifyAuditReply> VerifyAudit(VerifyAuditArgs request, ServerCallContext context) =>
        backend.VerifyAuditAsync(request, context.CancellationToken);

    public override Task<ListDestinationsReply> ListDestinations(ListDestinationsArgs request, ServerCallContext context) =>
        backend.ListDestinationsAsync(context.CancellationToken);

    public override Task<ListServerIdentitiesReply> ListServerIdentities(ListServerIdentitiesArgs request, ServerCallContext context) =>
        backend.ListServerIdentitiesAsync(request, context.CancellationToken);

    /// <summary>BL-35: decyzję zapisuje się z SID wywołującego (ten sam resolver co autoryzacja i audyt).</summary>
    public override Task<ApproveServerIdentityReply> ApproveServerIdentity(ApproveServerIdentityArgs request, ServerCallContext context) =>
        backend.ApproveServerIdentityAsync(request, roles.ResolveCaller(context).ActorSid, context.CancellationToken);

    public override Task<AlertActionReply> AckAlert(AckAlertArgs request, ServerCallContext context) =>
        backend.AckAlertAsync(request, context.CancellationToken);

    public override Task<AlertActionReply> SilenceAlert(SilenceAlertArgs request, ServerCallContext context) =>
        backend.SilenceAlertAsync(request, context.CancellationToken);

    public override Task<SetServerIdentityPolicyReply> SetServerIdentityPolicy(SetServerIdentityPolicyArgs request, ServerCallContext context) =>
        backend.SetServerIdentityPolicyAsync(request, roles.ResolveCaller(context).ActorSid, context.CancellationToken);

    public override Task<RestoreDatabaseReply> RestoreDatabase(RestoreDatabaseArgs request, ServerCallContext context) =>
        backend.RestoreDatabaseAsync(request, context.CancellationToken);

    internal static CallerRole ToProto(AgentRole role) => role switch
    {
        AgentRole.Reader => CallerRole.Reader,
        AgentRole.Operator => CallerRole.Operator,
        AgentRole.Administrator => CallerRole.Administrator,
        _ => CallerRole.Unspecified,
    };

    public override Task<ListSnapshotsResponse> ListSnapshots(ListSnapshotsRequest request, ServerCallContext context) =>
        backend.ListSnapshotsAsync(request.HasJob ? request.Job : null, context.CancellationToken);

    public override Task<RunOutcome> RunJob(RunJobRequest request, ServerCallContext context) =>
        backend.RunJobAsync(request.Job, request.Wait, context.CancellationToken);

    public override Task<RunOutcome> Restore(RestoreRequest request, ServerCallContext context) =>
        DenyTarget(context, request.Target) is { } denied
            ? Task.FromResult(new RunOutcome { Result = OverallState.Failed, Code = denied })
            : request.Detach && request.ExactPaths.Count > 0 && request.HasPathFilter
            ? Task.FromResult(new RunOutcome { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.JobDefinitionInvalid })
            : request.Detach
            ? backend.RestoreDetachedAsync(request.Snapshot, request.HasPathFilter ? request.PathFilter : null, [.. request.ExactPaths], request.Target, context.CancellationToken)
            : request.ExactPaths.Count == 0
            ? backend.RestoreAsync(request.Snapshot, request.HasPathFilter ? request.PathFilter : null, request.Target, context.CancellationToken)
            : request.HasPathFilter
                ? Task.FromResult(new RunOutcome { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.JobDefinitionInvalid }) // filtr i dokładne ścieżki naraz — niejednoznaczne
                : backend.RestoreExactAsync(request.Snapshot, [.. request.ExactPaths], request.Target, context.CancellationToken);

    public override Task<RestorePlanReply> PlanRestore(RestoreRequest request, ServerCallContext context) =>
        request.ExactPaths.Count > 0 && request.HasPathFilter
            ? Task.FromResult(new RestorePlanReply { Code = Kobian.Domain.KbCode.JobDefinitionInvalid }) // jak Restore: filtr i dokładne ścieżki naraz — niejednoznaczne
            : DenyTarget(context, request.Target) is { } denied
                ? Task.FromResult(new RestorePlanReply { Code = denied })
                : backend.PlanRestoreAsync(request, context.CancellationToken);

    public override Task<SaveCredentialReply> SaveCredential(SaveCredentialArgs request, ServerCallContext context) =>
        backend.SaveCredentialAsync(request, context.CancellationToken);

    public override Task<InitJobReply> InitJob(InitJobArgs request, ServerCallContext context) =>
        (DenyDestination(context, request.DestinationScheme, request.DestinationRoot)
         ?? RestoreTargetGate.DenyPath(roles.ResolveCaller(context).Role, request.PasswordFile, p => restoreTargets.CheckReadableFile(context, p), Kobian.Domain.KbCode.AccessDenied)) is { } denied
            ? Task.FromResult(new InitJobReply { Result = OverallState.Failed, Code = denied })
            : backend.InitJobAsync(request, context.CancellationToken);

    public override Task<ListSchedulesResponse> ListSchedules(ListSchedulesRequest request, ServerCallContext context) =>
        backend.ListSchedulesAsync(request.HasJob ? request.Job : null, context.CancellationToken);

    public override Task<SetScheduleReply> SetSchedule(SetScheduleArgs request, ServerCallContext context) =>
        backend.SetScheduleAsync(request, context.CancellationToken);

    /// <summary>check; z <c>repair</c> (D8.10: „akcje tylko za zgodą Admin”) — wymagana rola Administrator, inaczej KB-7011 (it. 109).</summary>
    public override Task<CheckReply> Check(CheckArgs request, ServerCallContext context) =>
        request.Repair && roles.ResolveCaller(context).Role < AgentRole.Administrator
            ? throw AgentCallInterceptor.Reject(StatusCode.PermissionDenied, Kobian.Domain.KbCode.ApiAccessDenied)
            : backend.CheckAsync(request, context.CancellationToken);
}
