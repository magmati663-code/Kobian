using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.History;
using Kobian.UI.Localization;
using Kobian.UI.Wizard;

namespace Kobian.UI.Restore;

/// <summary>Wynik RPC RestoreDatabase: stan, kod KB, nazwa utworzonej bazy.</summary>
public sealed record DbRestoreResult(RestoreRunState State, string? Code, string? TargetDatabase);

/// <summary>Port SCR-08 do usługi (RestoreDatabase, Operator).</summary>
public interface IDatabaseRestoreService
{
    /// <summary><paramref name="targetDatabase"/> null = nazwa domyślna Agenta („&lt;źródło&gt;_restore_RRRRMMDD”).</summary>
    Task<DbRestoreResult> RestoreDatabaseAsync(string snapshotId, string? targetDatabase, CancellationToken ct);
}

/// <summary>
/// SCR-08 Odtwarzanie bazy (R2.6.1, AC-32.1; BL-68 cz. 3): kopia trybu Database → NOWA baza na tym samym serwerze SQL. Nazwa docelowa
/// opcjonalna (puste = nazwa domyślna z datą); reguła sysname sprawdzana w UI (≤ 128 znaków, bez znaków sterujących). Istniejąca baza nigdy nie jest
/// nadpisywana (KB-3501) — nadpisanie produkcji wymaga Administratora i step-up (AC-32.2, później).
/// </summary>
public sealed partial class DbRestoreViewModel : ObservableObject
{
    private readonly IDatabaseRestoreService _service;

    public DbRestoreViewModel(IDatabaseRestoreService service, SnapshotEntry snapshot, TimeZoneInfo? zone = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        var c = CultureInfo.CurrentUICulture;
        var when = TimeZoneInfo.ConvertTime(snapshot.TimeUtc, zone ?? TimeZoneInfo.Local).ToString("g", c);
        SnapshotText = string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.SnapshotLine", c), when, snapshot.Job, snapshot.Id.Length > 8 ? snapshot.Id[..8] : snapshot.Id);
    }

    public SnapshotEntry Snapshot { get; }

    public string Title { get; } = Strings.Get("DbRestore.Title");

    public string SnapshotText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameValid), nameof(HasNameError), nameof(CanStart))]
    public partial string TargetName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(IsForm), nameof(IsRunning), nameof(IsDone))]
    public partial RestoreStage Stage { get; private set; } = RestoreStage.Form;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuccess), nameof(IsWarning), nameof(IsFailure), nameof(ResultTitle), nameof(ResultDetail), nameof(HasResultDetail))]
    public partial DbRestoreResult? Result { get; private set; }

    public bool IsForm => Stage == RestoreStage.Form;

    public bool IsRunning => Stage == RestoreStage.Running;

    public bool IsDone => Stage == RestoreStage.Done;

    /// <summary>sysname: 1–128 znaków bez sterujących; puste = nazwa domyślna.</summary>
    public bool NameValid => TargetName.Trim().Length == 0 || (TargetName.Trim().Length <= 128 && !TargetName.Any(char.IsControl));

    public bool HasNameError => !NameValid;

    public bool CanStart => IsForm && NameValid;

    public bool IsSuccess => Result?.State == RestoreRunState.Succeeded;

    public bool IsWarning => Result?.State == RestoreRunState.Warning;

    public bool IsFailure => Result?.State == RestoreRunState.Failed;

    /// <summary>Baza istnieje także przy ostrzeżeniu (KB-3505) — tytuł podaje jej nazwę.</summary>
    public string ResultTitle => Result is null ? "" : IsSuccess || IsWarning
        ? string.Format(CultureInfo.CurrentCulture, Strings.Get("DbRestore.Done"), Result.TargetDatabase)
        : Strings.Get("Restore.Done.Failed");

    public string ResultDetail => Result?.Code is { } c ? KbText.Full(c) : "";

    public bool HasResultDetail => ResultDetail.Length > 0;

    public event EventHandler? Closed;

    public event EventHandler? ShowHistoryRequested;

    [RelayCommand]
    private async Task StartAsync(CancellationToken ct)
    {
        if (!CanStart) return;
        Stage = RestoreStage.Running;
        var name = TargetName.Trim();
        try
        {
            Result = await _service.RestoreDatabaseAsync(Snapshot.Id, name.Length == 0 ? null : name, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Result = new DbRestoreResult(RestoreRunState.Failed, "KB-9001", null);
        }
        catch (InvalidOperationException)
        {
            Result = new DbRestoreResult(RestoreRunState.Failed, "KB-7011", null);
        }
        Stage = RestoreStage.Done;
    }

    [RelayCommand]
    private void Retry()
    {
        if (Stage != RestoreStage.Done) return;
        Result = null;
        Stage = RestoreStage.Form;
    }

    [RelayCommand]
    private void Close()
    {
        if (Stage != RestoreStage.Running) Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowHistory() => ShowHistoryRequested?.Invoke(this, EventArgs.Empty);
}
