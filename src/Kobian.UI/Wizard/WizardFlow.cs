namespace Kobian.UI.Wizard;

/// <summary>Stany kreatora zadania (D6.7, R2.6.2).</summary>
public enum WizardState
{
    S1Sources,
    S2Destination,
    S2Probe,
    S2Identity,
    S3Schedule,
    S4Security,
    S4RecoveryKey,
    S5Retention,
    S6Summary,
    Saved,
    FirstBackup,
    MiniTest,
    Done,
    Cancelled,
}

public enum WizardEvent
{
    Next,
    Back,
    ProbeOk,
    ProbeUnknownIdentity,
    ProbePermanentError,
    IdentityApproved,
    RepositoryCreated,
    ExistingRepositoryUnlocked,
    RecoveryKeyConfirmed,
    SaveSucceeded,
    FirstBackupSucceeded,
    FirstBackupFailed,
    MiniTestFinished,
    Cancel,
}

/// <summary>Kontekst przejść: wynik walidacji kroku (UpsertJob validate_only) i efekty uboczne po stronie usługi.</summary>
public sealed record WizardContext(bool StepValid, bool RepositoryCreated, bool RunNow);

/// <summary>
/// Maszyna stanów kreatora jako czysta funkcja (D6.7). Reguły: (1) „Wstecz” nie cofa efektów ubocznych — po utworzeniu
/// repo krok S4 jest pomijany w obu kierunkach; (2) przerwanie po S4 nie tworzy zadania (repo „niepowiązane”);
/// (3) „Dalej” wymaga pozytywnej walidacji kroku.
/// </summary>
public static class WizardFlow
{
    public static WizardState Next(WizardState s, WizardEvent e, WizardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (e == WizardEvent.Cancel)
            return s is WizardState.Saved or WizardState.FirstBackup or WizardState.MiniTest or WizardState.Done ? s : WizardState.Cancelled;

        return (s, e) switch
        {
            (WizardState.S1Sources, WizardEvent.Next) when ctx.StepValid => WizardState.S2Destination,
            (WizardState.S2Destination, WizardEvent.Next) when ctx.StepValid => WizardState.S2Probe,
            (WizardState.S2Destination, WizardEvent.Back) => WizardState.S1Sources,
            (WizardState.S2Probe, WizardEvent.ProbeOk) => WizardState.S3Schedule,
            (WizardState.S2Probe, WizardEvent.ProbeUnknownIdentity) => WizardState.S2Identity,
            (WizardState.S2Probe, WizardEvent.ProbePermanentError) => WizardState.S2Destination,
            (WizardState.S2Identity, WizardEvent.IdentityApproved) => WizardState.S2Probe,
            (WizardState.S2Identity, WizardEvent.Back) => WizardState.S2Destination,
            (WizardState.S3Schedule, WizardEvent.Next) when ctx.StepValid => ctx.RepositoryCreated ? WizardState.S5Retention : WizardState.S4Security,
            (WizardState.S3Schedule, WizardEvent.Back) => WizardState.S2Destination,
            (WizardState.S4Security, WizardEvent.RepositoryCreated) => WizardState.S4RecoveryKey,
            (WizardState.S4Security, WizardEvent.ExistingRepositoryUnlocked) => WizardState.S5Retention,
            (WizardState.S4Security, WizardEvent.Back) when !ctx.RepositoryCreated => WizardState.S3Schedule,
            (WizardState.S4RecoveryKey, WizardEvent.RecoveryKeyConfirmed) => WizardState.S5Retention,
            (WizardState.S5Retention, WizardEvent.Next) when ctx.StepValid => WizardState.S6Summary,
            (WizardState.S5Retention, WizardEvent.Back) => ctx.RepositoryCreated ? WizardState.S3Schedule : WizardState.S4Security,
            (WizardState.S6Summary, WizardEvent.Back) => WizardState.S5Retention,
            (WizardState.S6Summary, WizardEvent.SaveSucceeded) => WizardState.Saved,
            (WizardState.Saved, WizardEvent.Next) => ctx.RunNow ? WizardState.FirstBackup : WizardState.Done,
            (WizardState.FirstBackup, WizardEvent.FirstBackupSucceeded) => WizardState.MiniTest,
            (WizardState.FirstBackup, WizardEvent.FirstBackupFailed) => WizardState.Done,
            (WizardState.MiniTest, WizardEvent.MiniTestFinished) => WizardState.Done,
            _ => s, // zdarzenie niedozwolone w stanie: brak zmiany (UI wyszarza akcję)
        };
    }

    /// <summary>Czy wolno pokazać „Wstecz” (RecoveryKey: nie — klucz musi zostać potwierdzony, AC-28.1).</summary>
    public static bool CanGoBack(WizardState s, WizardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return s switch
        {
            WizardState.S2Destination or WizardState.S2Identity or WizardState.S3Schedule or WizardState.S5Retention or WizardState.S6Summary => true,
            WizardState.S4Security => !ctx.RepositoryCreated,
            _ => false,
        };
    }

    /// <summary>Numer kroku 1–6 do paska postępu.</summary>
    public static int StepNumber(WizardState s) => s switch
    {
        WizardState.S1Sources => 1,
        WizardState.S2Destination or WizardState.S2Probe or WizardState.S2Identity => 2,
        WizardState.S3Schedule => 3,
        WizardState.S4Security or WizardState.S4RecoveryKey => 4,
        WizardState.S5Retention => 5,
        _ => 6,
    };
}
