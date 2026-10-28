using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.Domain;
using Kobian.UI.History;
using Kobian.UI.Localization;
using Kobian.UI.Wizard;

namespace Kobian.UI.Progress;

/// <summary>Etap widoku postępu: łączenie → w toku → (ponawianie po zerwaniu) → koniec / utrata strumienia / zatrzymane przez użytkownika.</summary>
public enum ProgressStage
{
    Connecting,
    Running,
    Reconnecting,
    Done,
    Lost,
    Stopped,
}

/// <summary>
/// SCR-07 widok postępu uruchomienia (RD-79, R2.6.1; WatchRun it. 134). Pasek jest nieokreślony (usługa nie zna całkowitej pracy z góry — liczniki są informacją,
/// nie procentem; nic nie jest przedstawiane jako zmierzone ponad to, co przysłała usługa). Zerwanie połączenia → ponawianie z narastającym odstępem
/// (1, 2, 4, 8, 15 s) do <see cref="MaxReconnects"/> prób z rzędu; udana wiadomość zeruje licznik. „Przestań obserwować” NIE przerywa uruchomienia w usłudze.
/// </summary>
public sealed partial class RunProgressViewModel : ObservableObject
{
    public const int MaxReconnects = 5;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    private readonly IRunProgressSource _source;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _cts;

    public RunProgressViewModel(IRunProgressSource source, string runId, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        RunId = runId;
        _delay = delay ?? Task.Delay;
        Title = Strings.Get("Progress.Title");
        StateText = Strings.Get("Progress.Connecting");
    }

    public string RunId { get; }

    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsFinished), nameof(ShowStop), nameof(HasProblem), nameof(IsProblemBadge))]
    public partial ProgressStage Stage { get; private set; } = ProgressStage.Connecting;

    /// <summary>„Kopia · Dokumenty”.</summary>
    [ObservableProperty]
    public partial string HeadlineText { get; private set; } = "";

    [ObservableProperty]
    public partial string StateText { get; private set; } = "";

    /// <summary>„Odczytano: 1,2 GB · Wysłano do celu: 300 MB · Pliki: 12” — tylko to, co usługa już zna.</summary>
    [ObservableProperty]
    public partial string CountersText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; private set; } = "";

    [ObservableProperty]
    public partial RunOutcomeKind Outcome { get; private set; } = RunOutcomeKind.Unknown;

    public bool IsActive => Stage is ProgressStage.Connecting or ProgressStage.Running or ProgressStage.Reconnecting;

    public bool IsFinished => Stage is ProgressStage.Done or ProgressStage.Lost or ProgressStage.Stopped;

    public bool ShowStop => IsActive;

    public bool HasProblem => ProblemText.Length > 0;

    public bool IsSuccess => Stage == ProgressStage.Done && Outcome == RunOutcomeKind.Ok;

    public bool IsWarning => Stage == ProgressStage.Done && Outcome == RunOutcomeKind.Warning;

    public bool IsFailure => Stage == ProgressStage.Done && Outcome == RunOutcomeKind.Failed;

    /// <summary>Plakietka błędu: uruchomienie zakończone błędem albo utracony strumień (zatrzymanie przez użytkownika nie jest błędem).</summary>
    public bool IsProblemBadge => IsFailure || Stage == ProgressStage.Lost;

    /// <summary>Liczba kolejnych prób ponowienia w bieżącej serii (0 = połączenie działa).</summary>
    public int ReconnectAttempt { get; private set; }

    [RelayCommand]
    private void StopWatching() => _cts?.Cancel();

    /// <summary>Obserwuje uruchomienie do wiadomości końcowej, utraty strumienia albo anulowania; nie rzuca wyjątków transportu.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        try
        {
            while (true)
            {
                try
                {
                    await foreach (var u in _source.WatchAsync(RunId, cts.Token).ConfigureAwait(true))
                    {
                        ReconnectAttempt = 0;
                        Apply(u);
                        if (u.Final) return;
                    }

                    // strumień zakończył się bez wiadomości końcowej (np. restart usługi) — traktujemy jak zerwanie
                    throw new IOException("Stream ended without a final message.");
                }
                catch (IOException)
                {
                    if (ReconnectAttempt >= MaxReconnects)
                    {
                        Fail(KbCode.AgentUnreachable);
                        return;
                    }

                    var wait = Backoff[Math.Min(ReconnectAttempt, Backoff.Length - 1)];
                    ReconnectAttempt++;
                    Stage = ProgressStage.Reconnecting;
                    StateText = string.Format(CultureInfo.CurrentCulture, Strings.Get("Progress.Reconnecting"), ReconnectAttempt, MaxReconnects);
                    await _delay(wait, cts.Token).ConfigureAwait(true);
                }
                catch (InvalidOperationException ex)
                {
                    Fail(ex.InnerException is Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.PermissionDenied or Grpc.Core.StatusCode.Unauthenticated }
                        ? KbCode.ApiAccessDenied : KbCode.OperationNotAvailable); // Unimplemented = starszy Agent bez WatchRun
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsFinished)
            {
                Stage = ProgressStage.Stopped;
                StateText = Strings.Get("Progress.Stopped");
            }
        }
        finally
        {
            _cts = null;
        }
    }

    private void Apply(RunProgressUpdate u)
    {
        if (u.Code == KbCode.RunNotFound && u.Final)
        {
            Fail(KbCode.RunNotFound);
            return;
        }

        Stage = u.Final ? ProgressStage.Done : ProgressStage.Running; // etap przed tekstami: widok nie pokazuje nowego tekstu przy starym etapie
        var c = CultureInfo.CurrentUICulture;
        HeadlineText = $"{Label("Progress.Kind.", u.Kind, "Progress.Kind.other", c)} · {u.Job}";
        StateText = Label("Progress.State.", u.State, "Progress.State.Unknown", c);
        var parts = new List<string>(3);
        if (u.BytesRead > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Get("Progress.BytesRead", c), ByteSize.Format(u.BytesRead, c)));
        if (u.BytesUploaded is > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Get("Progress.BytesUploaded", c), ByteSize.Format(u.BytesUploaded.Value, c)));
        if (u.Files is { } f) parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Get("Progress.Files", c), f.ToString("N0", c)));
        CountersText = string.Join(" · ", parts);
        Outcome = u.Result;
        if (u.Final)
        {
            ProblemText = u.Code is { Length: > 0 } code && u.Result != RunOutcomeKind.Ok ? KbText.Full(code) : "";
            OnPropertyChanged(nameof(IsSuccess));
            OnPropertyChanged(nameof(IsWarning));
            OnPropertyChanged(nameof(IsFailure));
            OnPropertyChanged(nameof(IsProblemBadge));
        }
    }

    private void Fail(string code)
    {
        Stage = ProgressStage.Lost;
        StateText = Strings.Get("Progress.Lost");
        ProblemText = KbText.Full(code);
    }

    private static string Label(string prefix, string value, string fallbackKey, CultureInfo c)
    {
        var key = prefix + value;
        var text = Strings.Get(key, c);
        return text.StartsWith('[') ? Strings.Get(fallbackKey, c) : text;
    }
}
