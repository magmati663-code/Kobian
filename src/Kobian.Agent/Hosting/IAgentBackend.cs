using Kobian.Api.V1;

namespace Kobian.Agent.Hosting;

/// <summary>Port logiki Agenta wywoływany przez usługę gRPC (autoryzacja i rewizja są już sprawdzone przez interceptor).</summary>
public interface IAgentBackend
{
    Task<GetStatusResponse> GetStatusAsync(CancellationToken ct);

    Task<ListSnapshotsResponse> ListSnapshotsAsync(string? job, CancellationToken ct);

    Task<RunOutcome> RunJobAsync(string job, bool wait, CancellationToken ct);

    Task<RunOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct);

    Task<InitJobReply> InitJobAsync(InitJobArgs args, CancellationToken ct);

    Task<ListSchedulesResponse> ListSchedulesAsync(string? job, CancellationToken ct);

    Task<SetScheduleReply> SetScheduleAsync(SetScheduleArgs args, CancellationToken ct);

    Task<CheckReply> CheckAsync(CheckArgs args, CancellationToken ct);

    Task<ListRunsResponse> ListRunsAsync(string? job, int? limit, CancellationToken ct);

    /// <summary>Postęp jednego uruchomienia (it. 134, SCR-07): kolejne zmiany do stanu końcowego. Domyślnie jedna wiadomość końcowa z KB-7012 (backend bez katalogu).</summary>
    IAsyncEnumerable<RunProgress> WatchRunAsync(string runId, CancellationToken ct) => UnavailableWatch();

    private static async IAsyncEnumerable<RunProgress> UnavailableWatch()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return new RunProgress { Final = true, Run = new RunInfo { State = "Failed", Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable } };
    }

    Task<ProbeDestinationReply> ProbeDestinationAsync(ProbeDestinationArgs args, CancellationToken ct);

    Task<UpsertJobReply> UpsertJobAsync(UpsertJobArgs args, CancellationToken ct);

    Task<InitRepositoryReply> InitRepositoryAsync(InitRepositoryArgs args, CancellationToken ct);

    /// <summary>Login i hasło z kreatora → magazyn sekretów Agenta (secret_ref „file:&lt;nazwa&gt;”). Bez implementacji → KB-7012.</summary>
    Task<SaveCredentialReply> SaveCredentialAsync(SaveCredentialArgs args, CancellationToken ct) =>
        Task.FromResult(new SaveCredentialReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Podfoldery na serwerze (wybór folderu w kreatorze). Bez implementacji → KB-7012.</summary>
    Task<BrowseServerReply> BrowseServerAsync(BrowseServerArgs args, CancellationToken ct) =>
        Task.FromResult(new BrowseServerReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    Task<ApplyRetentionReply> ApplyRetentionAsync(ApplyRetentionArgs args, CancellationToken ct);

    Task<PreviewRetentionReply> PreviewRetentionAsync(PreviewRetentionArgs args, CancellationToken ct);

    /// <summary>Plan prune (BL-25 cz. 3) — tylko podgląd. Domyślnie KB-7012 (backend bez dostępu do celu).</summary>
    Task<PlanPruneReply> PlanPruneAsync(PlanPruneArgs args, CancellationToken ct) =>
        Task.FromResult(new PlanPruneReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Prune dwufazowy (BL-25 cz. 4). Domyślnie KB-7012.</summary>
    Task<PruneReply> PruneAsync(PruneArgs args, CancellationToken ct) =>
        Task.FromResult(new PruneReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Zwolnienie wstrzymania retencji (SEC-17.4). Domyślnie KB-7012.</summary>
    Task<ReleaseRetentionHoldReply> ReleaseRetentionHoldAsync(CancellationToken ct) =>
        Task.FromResult(new ReleaseRetentionHoldReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Discovery MSSQL dla kreatora S1-db (BL-40 cz. 3). Domyślnie KB-7012.</summary>
    Task<DiscoverDatabasesReply> DiscoverDatabasesAsync(DiscoverDatabasesArgs args, CancellationToken ct) =>
        Task.FromResult(new DiscoverDatabasesReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Certyfikat TLS serwera SQL do zatwierdzenia (BL-40 cz. 5). Domyślnie KB-7012.</summary>
    Task<FetchSqlCertificateReply> FetchSqlCertificateAsync(FetchSqlCertificateArgs args, CancellationToken ct) =>
        Task.FromResult(new FetchSqlCertificateReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Zatwierdzenie certyfikatu serwera SQL i zapis pinu (BL-40 cz. 5). Domyślnie KB-7012.</summary>
    Task<ApproveSqlCertificateReply> ApproveSqlCertificateAsync(ApproveSqlCertificateArgs args, CancellationToken ct) =>
        Task.FromResult(new ApproveSqlCertificateReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Przeglądarka kopii (BL-63, SCR-06): jeden folder snapshotu. Domyślnie KB-7012.</summary>
    Task<BrowseTreeReply> BrowseTreeAsync(BrowseTreeArgs args, CancellationToken ct) =>
        Task.FromResult(new BrowseTreeReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Weryfikacja łańcucha audytu (RD-42). Domyślnie KB-7012.</summary>
    Task<VerifyAuditReply> VerifyAuditAsync(CancellationToken ct) =>
        Task.FromResult(new VerifyAuditReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Weryfikacja z kotwicą zewnętrzną (it. 99). Domyślnie bez kotwicy.</summary>
    Task<VerifyAuditReply> VerifyAuditAsync(VerifyAuditArgs args, CancellationToken ct) => VerifyAuditAsync(ct);

    /// <summary>RD-81: restore w tle — zwraca run_id od razu (wynik: WatchRun / run_runs). Domyślnie KB-7012.</summary>
    Task<RunOutcome> RestoreDetachedAsync(string snapshot, string? pathFilter, IReadOnlyList<string> exactPaths, string target, CancellationToken ct) =>
        Task.FromResult(new RunOutcome { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Restore dokładnych ścieżek (it. 80, Qb34). Domyślnie KB-7012.</summary>
    Task<RunOutcome> RestoreExactAsync(string snapshot, IReadOnlyList<string> exactPaths, string target, CancellationToken ct) =>
        Task.FromResult(new RunOutcome { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Cele z definicji zadań (SCR-09). Domyślnie pusta lista.</summary>
    Task<ListDestinationsReply> ListDestinationsAsync(CancellationToken ct) => Task.FromResult(new ListDestinationsReply());

    /// <summary>Podgląd odtworzenia (it. 98, R2.8.1). Domyślnie KB-7012.</summary>
    Task<RestorePlanReply> PlanRestoreAsync(RestoreRequest request, CancellationToken ct) =>
        Task.FromResult(new RestorePlanReply { Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Tożsamości serwerów celów (BL-35, SCR-09). Domyślnie KB-7012.</summary>
    Task<ListServerIdentitiesReply> ListServerIdentitiesAsync(ListServerIdentitiesArgs args, CancellationToken ct) =>
        Task.FromResult(new ListServerIdentitiesReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Zatwierdzenie/odrzucenie oczekującego odcisku (BL-35, D4.11); <paramref name="actorSid"/> = SID Administratora. Domyślnie KB-7012.</summary>
    Task<ApproveServerIdentityReply> ApproveServerIdentityAsync(ApproveServerIdentityArgs args, string actorSid, CancellationToken ct) =>
        Task.FromResult(new ApproveServerIdentityReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>„Wysokie ryzyko” celu (SEC-14.5). Domyślnie KB-7012.</summary>
    Task<SetServerIdentityPolicyReply> SetServerIdentityPolicyAsync(SetServerIdentityPolicyArgs args, string actorSid, CancellationToken ct) =>
        Task.FromResult(new SetServerIdentityPolicyReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Potwierdzenie alertu (BL-45, D9.7). Domyślnie KB-7012.</summary>
    Task<AlertActionReply> AckAlertAsync(AckAlertArgs args, CancellationToken ct) =>
        Task.FromResult(new AlertActionReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Wyciszenie alertu (BL-45, D9.7). Domyślnie KB-7012.</summary>
    Task<AlertActionReply> SilenceAlertAsync(SilenceAlertArgs args, CancellationToken ct) =>
        Task.FromResult(new AlertActionReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Odtworzenie bazy MSSQL do nowej bazy (BL-68). Domyślnie KB-7012.</summary>
    Task<RestoreDatabaseReply> RestoreDatabaseAsync(RestoreDatabaseArgs args, CancellationToken ct) =>
        Task.FromResult(new RestoreDatabaseReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Eksport zadań (US-29). Domyślnie KB-7012.</summary>
    Task<ExportJobsReply> ExportJobsAsync(ExportJobsArgs args, CancellationToken ct) =>
        Task.FromResult(new ExportJobsReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });

    /// <summary>Import zadań (US-29, SEC-6.7). Domyślnie KB-7012.</summary>
    Task<ImportJobsReply> ImportJobsAsync(ImportJobsArgs args, CancellationToken ct) =>
        Task.FromResult(new ImportJobsReply { Result = OverallState.Failed, Code = Kobian.Domain.KbCode.OperationNotAvailable });
}
