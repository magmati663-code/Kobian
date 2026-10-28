using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.History;
using Kobian.UI.Localization;
using Kobian.UI.Wizard;
using Prog = Kobian.UI.Progress;

namespace Kobian.UI.Restore;

public enum RestoreRunState
{
    Succeeded,
    Warning,
    Failed,
}

/// <summary>Wynik RPC Restore: stan + kod KB (Warning: np. KB-5105 — pliki istniejące nie zostały nadpisane).</summary>
public sealed record RestoreRunResult(RestoreRunState State, string? Code);

/// <summary>
/// Podgląd odtworzenia (RPC PlanRestore, it. 98): <see cref="Code"/> = błąd planu, <see cref="BlockingCode"/> = odtworzenie nie ruszy
/// (KB-5001/5003/5106/5202); <see cref="Conflicts"/> = pliki istniejące, które zostaną pominięte (KB-5105); <see cref="Skipped"/> = niedozwolone nazwy i dowiązania.
/// </summary>
public sealed record RestorePreview(string? Code, long Files, long Bytes, long Conflicts, IReadOnlyList<string> ConflictSample, long Skipped, long? FreeBytes, string? BlockingCode, long Resumable = 0)
{
    public bool Blocks => Code is not null || BlockingCode is not null;
}

/// <summary>Wynik startu odtworzenia w tle (RD-81): <see cref="RunId"/> do śledzenia w <see cref="Prog.IRunProgressSource"/> albo <see cref="Failure"/> (odmowa/walidacja, bez uruchomienia).</summary>
public sealed record DetachedRestoreStart(string? RunId, RestoreRunResult? Failure);

/// <summary>Port SCR-07 do usługi: odtworzenie kopii (RPC Restore, rola Operator).</summary>
public interface IRestoreService
{
    /// <summary>
    /// RD-81: start odtworzenia BEZ czekania na wynik (RPC Restore z detach=true) → run_id do śledzenia postępu. null = usługa nie wspiera trybu w tle
    /// (starszy Agent) — ekran odtwarza jak dotąd, czekając na wynik.
    /// </summary>
    Task<DetachedRestoreStart?> StartDetachedAsync(string snapshotId, string? exactPath, string target, CancellationToken ct) => Task.FromResult<DetachedRestoreStart?>(null);

    /// <summary><paramref name="exactPath"/>: plik albo folder z zawartością (dokładnie, bez wzorców — Qb34); null = cała kopia.</summary>
    Task<RestoreRunResult> RestoreAsync(string snapshotId, string? exactPath, string target, CancellationToken ct);

    /// <summary>Podgląd (R2.8.1 PlanRestore). null = usługa bez podglądu (starszy Agent) — ekran odtwarza jak dotąd, bez planu.</summary>
    Task<RestorePreview?> PlanAsync(string snapshotId, string? exactPath, string target, CancellationToken ct) => Task.FromResult<RestorePreview?>(null);
}

/// <summary>Etap ekranu SCR-07: formularz → sprawdzanie (plan) → w toku → wynik.</summary>
public enum RestoreStage
{
    Form,
    Planning,
    Running,
    Done,
}

/// <summary>
/// SCR-07 Odtwarzanie plików (R2.6.1, AC-07.1–07.3; BL-64): wejście z przeglądarki kopii (kopia + folder/plik), wybór folderu docelowego,
/// uruchomienie RPC Restore i wynik z kodem KB. Zasady pokazywane użytkownikowi wprost: odtworzenie NIGDY nie nadpisuje istniejących plików
/// (KB-5105 → ostrzeżenie), ścieżka względna w kopii jest zachowana w folderze docelowym, dowiązania nie są odtwarzane.
/// Wybór: cała kopia albo DOKŁADNA ścieżka pliku/folderu (RestoreRequest.exact_paths, it. 80 — bez wzorców, zamyka ryzyko z ADR-0022).
/// </summary>
public sealed partial class RestoreViewModel : ObservableObject
{
    private readonly IRestoreService _service;
    private readonly Func<string, bool> _isAbsolute;
    private readonly Prog.IRunProgressSource? _progressSource;

    public RestoreViewModel(IRestoreService service, RestoreSelection selection, TimeZoneInfo? zone = null, Func<string, bool>? isAbsolutePath = null,
        Prog.IRunProgressSource? progressSource = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _progressSource = progressSource;
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _isAbsolute = isAbsolutePath ?? Path.IsPathFullyQualified;
        var c = CultureInfo.CurrentUICulture;
        var when = TimeZoneInfo.ConvertTime(selection.Snapshot.TimeUtc, zone ?? TimeZoneInfo.Local).ToString("g", c);
        SnapshotText = string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.SnapshotLine", c), when, selection.Snapshot.Job,
            selection.Snapshot.Id.Length > 8 ? selection.Snapshot.Id[..8] : selection.Snapshot.Id);
        WhatText = selection.Path.Length == 0
            ? Strings.Get("Restore.WhatAll", c)
            : string.Format(CultureInfo.CurrentCulture, Strings.Get(selection.IsDirectory ? "Restore.WhatFolder" : "Restore.WhatFile", c), selection.Path);
    }

    public RestoreSelection Selection { get; }

    public string Title { get; } = Strings.Get("Restore.Title");

    public string SnapshotText { get; }

    public string WhatText { get; }

    /// <summary>Dokładna ścieżka dla RPC Restore (null = cała kopia).</summary>
    public string? ExactPath => Selection.Path.Length == 0 ? null : Selection.Path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(TargetError), nameof(HasTargetError), nameof(PreviewText), nameof(ShowHint))]
    public partial string Target { get; set; } = "";

    /// <summary>Zmiana folderu unieważnia podgląd (konflikty i miejsce zależą od folderu).</summary>
    partial void OnTargetChanged(string value) => Plan = null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(IsForm), nameof(IsPlanning), nameof(IsRunning), nameof(IsDone), nameof(ShowHint))]
    public partial RestoreStage Stage { get; private set; } = RestoreStage.Form;

    /// <summary>Wynik PlanRestore dla bieżącego folderu (null = jeszcze nie sprawdzono albo usługa bez podglądu).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan), nameof(PlanSummary), nameof(PlanConflictsText), nameof(HasPlanConflicts), nameof(PlanConflictSample),
        nameof(PlanBlockText), nameof(HasPlanBlock), nameof(StartText), nameof(CanStart), nameof(HasPlanResumable), nameof(PlanResumableText), nameof(RunningText))]
    public partial RestorePreview? Plan { get; private set; }

    public bool IsPlanning => Stage == RestoreStage.Planning;

    public bool HasPlan => Plan is { Code: null };

    /// <summary>„3 pliki (8,8 KB) · wolne 1,2 GB”.</summary>
    public string PlanSummary
    {
        get
        {
            if (Plan is not { Code: null } p) return "";
            var c = CultureInfo.CurrentUICulture;
            var text = Plural.Text("Plural.File", p.Files, c) + " (" + ByteSize.Format(p.Bytes, c) + ")";
            if (p.FreeBytes is { } f) text += " · " + string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.Plan.Free", c), ByteSize.Format(f, c));
            return string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.Plan.Summary", c), text);
        }
    }

    public bool HasPlanConflicts => Plan is { Code: null } p && p.Conflicts + p.Skipped > 0;

    /// <summary>D8.8 (it. 108): „N plików odtworzono w przerwanej próbie — zostaną pominięte” (informacja, nie ostrzeżenie).</summary>
    public bool HasPlanResumable => Plan is { Code: null, Resumable: > 0 };

    public string PlanResumableText => Plan is { Code: null, Resumable: > 0 } p
        ? string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.Plan.Resumable"), Plural.Text("Plural.File", p.Resumable, CultureInfo.CurrentUICulture))
        : "";

    /// <summary>RD-60: w trakcie odtwarzania — skala z podglądu („Odtwarzanie 12 plików (8,6 MB)…”).</summary>
    public string RunningText => Plan is { Code: null } p
        ? string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.RunningPlan"), Plural.Text("Plural.File", p.Files - p.Resumable, CultureInfo.CurrentUICulture), ByteSize.Format(p.Bytes, CultureInfo.CurrentCulture))
        : Strings.Get("Restore.Running");

    public string PlanConflictsText
    {
        get
        {
            if (Plan is not { Code: null } p || p.Conflicts + p.Skipped == 0) return "";
            var c = CultureInfo.CurrentUICulture;
            return string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.Plan.Conflicts", c), Plural.Text("Plural.File", p.Conflicts + p.Skipped, c));
        }
    }

    /// <summary>Do 5 przykładowych ścieżek konfliktów (reszta: „i jeszcze n”).</summary>
    public IReadOnlyList<string> PlanConflictSample
    {
        get
        {
            if (Plan is not { Code: null } p) return [];
            var list = p.ConflictSample.Take(MaxShownConflicts).ToList();
            var more = p.Conflicts - list.Count;
            if (more > 0) list.Add(string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.Plan.More"), more.ToString("N0", CultureInfo.CurrentCulture)));
            return list;
        }
    }

    public const int MaxShownConflicts = 5;

    public bool HasPlanBlock => Plan?.Blocks == true;

    public string PlanBlockText => Plan is { } p && p.Blocks ? KbText.Full(p.Code ?? p.BlockingCode!) : "";

    /// <summary>„Odtwórz” albo po podglądzie z konfliktami „Odtwórz mimo to” (świadome potwierdzenie pominięć).</summary>
    public string StartText => HasPlanConflicts && !HasPlanBlock ? Strings.Get("Restore.StartAnyway") : Strings.Get("Restore.Start");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultTitle), nameof(ResultDetail), nameof(IsSuccess), nameof(IsWarning), nameof(IsFailure), nameof(HasResultDetail))]
    public partial RestoreRunResult? Result { get; private set; }

    /// <summary>RD-81: widok postępu odtwarzania w tle (WatchRun); null = odtworzenie czeka na wynik jak dotąd (bez paska postępu z liczników).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunProgress), nameof(HasNoRunProgress))]
    public partial Prog.RunProgressViewModel? RunProgress { get; private set; }

    public bool HasRunProgress => RunProgress is not null;

    public bool HasNoRunProgress => RunProgress is null;

    public bool IsForm => Stage == RestoreStage.Form;

    public bool IsRunning => Stage == RestoreStage.Running;

    public bool IsDone => Stage == RestoreStage.Done;

    public bool TargetValid => Target.Trim().Length > 0 && _isAbsolute(Target.Trim()) && Target.IndexOf('\0', StringComparison.Ordinal) < 0;

    public bool HasTargetError => Target.Length > 0 && !TargetValid;

    public bool ShowHint => IsForm && !HasTargetError;

    public string TargetError => HasTargetError ? Strings.Get("Restore.TargetInvalid") : "";

    public bool CanStart => Stage == RestoreStage.Form && TargetValid && !HasPlanBlock;

    /// <summary>Podgląd miejsca: „…\cel\Faktury\2026\…” — ścieżka z kopii jest zachowana.</summary>
    public string PreviewText
    {
        get
        {
            if (!TargetValid) return "";
            var t = Target.Trim().TrimEnd('/', '\\');
            var sep = t.Contains('\\', StringComparison.Ordinal) ? "\\" : "/";
            var rel = Selection.Path.Replace("/", sep, StringComparison.Ordinal);
            var where = rel.Length == 0 ? t + sep + "…" : t + sep + rel + (Selection.IsDirectory ? sep + "…" : "");
            return string.Format(CultureInfo.CurrentCulture, Strings.Get("Restore.Preview"), where);
        }
    }

    public bool IsSuccess => Result?.State == RestoreRunState.Succeeded;

    public bool IsWarning => Result?.State == RestoreRunState.Warning;

    public bool IsFailure => Result?.State == RestoreRunState.Failed;

    public string ResultTitle => Result?.State switch
    {
        RestoreRunState.Succeeded => Strings.Get("Restore.Done.Ok"),
        RestoreRunState.Warning => Strings.Get("Restore.Done.Warning"),
        RestoreRunState.Failed => Strings.Get("Restore.Done.Failed"),
        _ => "",
    };

    public string ResultDetail => Result?.Code is { } code ? KbText.Full(code) : "";

    public bool HasResultDetail => ResultDetail.Length > 0;

    /// <summary>Powrót do przeglądarki (anulowanie albo „Gotowe”).</summary>
    public event EventHandler? Closed;

    /// <summary>„Pokaż w historii” po zakończeniu.</summary>
    public event EventHandler? ShowHistoryRequested;

    /// <summary>
    /// „Odtwórz”: najpierw podgląd (PlanRestore). Blokada → zostaje formularz z komunikatem KB; konflikty → pokazane i drugi klik „Odtwórz mimo to”;
    /// czysto albo brak podglądu w usłudze (starszy Agent) → od razu odtworzenie.
    /// </summary>
    [RelayCommand]
    private async Task StartAsync(CancellationToken ct)
    {
        if (!CanStart) return;
        if (Plan is null)
        {
            Stage = RestoreStage.Planning;
            var target = Target.Trim();
            RestorePreview? plan;
            try
            {
                plan = await _service.PlanAsync(Selection.Snapshot.Id, ExactPath, target, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                plan = new RestorePreview("KB-9001", 0, 0, 0, [], 0, null, null);
            }
            catch (InvalidOperationException ex)
            {
                plan = new RestorePreview(Kobian.UI.Services.UiFailure.CodeOf(ex), 0, 0, 0, [], 0, null, null);
            }
            Stage = RestoreStage.Form;
            Plan = plan;
            if (plan is not null && (plan.Blocks || plan.Conflicts + plan.Skipped > 0)) return; // użytkownik widzi plan i decyduje
        }
        Stage = RestoreStage.Running;
        try
        {
            var started = _progressSource is null
                ? null
                : await _service.StartDetachedAsync(Selection.Snapshot.Id, ExactPath, Target.Trim(), ct).ConfigureAwait(true);
            if (started is null)
            {
                // starszy Agent albo UI bez źródła postępu: odtworzenie czeka na wynik (jak przed RD-81)
                Result = await _service.RestoreAsync(Selection.Snapshot.Id, ExactPath, Target.Trim(), ct).ConfigureAwait(true);
            }
            else if (started.RunId is null)
            {
                Result = started.Failure ?? new RestoreRunResult(RestoreRunState.Failed, "KB-0001");
            }
            else if (!await WatchAsync(started.RunId, ct).ConfigureAwait(true))
            {
                return; // użytkownik przestał obserwować — odtwarzanie trwa w usłudze (wynik w historii)
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Result = new RestoreRunResult(RestoreRunState.Failed, "KB-9001");
        }
        catch (InvalidOperationException ex)
        {
            Result = new RestoreRunResult(RestoreRunState.Failed, Kobian.UI.Services.UiFailure.CodeOf(ex));
        }
        Stage = RestoreStage.Done;
    }

    /// <summary>
    /// RD-81: śledzi odtwarzanie w tle przez WatchRun i zamienia stan końcowy na <see cref="Result"/>. false = obserwację przerwał użytkownik
    /// (wracamy do formularza; odtwarzanie w usłudze NIE jest przerywane).
    /// </summary>
    private async Task<bool> WatchAsync(string runId, CancellationToken ct)
    {
        var watch = new Prog.RunProgressViewModel(_progressSource!, runId);
        RunProgress = watch;
        await watch.RunAsync(ct).ConfigureAwait(true);
        if (watch.Stage == Prog.ProgressStage.Stopped)
        {
            RunProgress = null;
            Plan = null; // stan folderu po częściowym odtworzeniu jest inny — ponowny podgląd
            Stage = RestoreStage.Form;
            return false;
        }
        Result = watch.Stage == Prog.ProgressStage.Done
            ? new RestoreRunResult(watch.Outcome switch
            {
                Prog.RunOutcomeKind.Ok => RestoreRunState.Succeeded,
                Prog.RunOutcomeKind.Warning => RestoreRunState.Warning,
                _ => RestoreRunState.Failed,
            }, watch.FinalCode)
            : new RestoreRunResult(RestoreRunState.Failed, watch.FinalCode ?? "KB-9001"); // utracony strumień: wynik nieznany, odtwarzanie mogło się zakończyć — szczegóły w historii
        RunProgress = null;
        return true;
    }

    /// <summary>Po błędzie „Spróbuj ponownie” wraca do formularza z zachowanym folderem.</summary>
    [RelayCommand]
    private void Retry()
    {
        if (Stage != RestoreStage.Done) return;
        Result = null;
        Plan = null; // stan folderu po próbie jest inny — ponowny podgląd
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
