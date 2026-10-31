namespace Kobian.UI.Progress;

/// <summary>Wynik uruchomienia w widoku postępu (bez typów kontraktu gRPC — UI zależy od portu, nie od transportu).</summary>
public enum RunOutcomeKind
{
    Unknown,
    Ok,
    Warning,
    Failed,
}

/// <summary>
/// Jedna zmiana uruchomienia ze strumienia WatchRun (it. 134) — tylko metadane (NFR-11): stan maszyny uruchomień, liczniki, kod KB.
/// <c>Final</c> = ostatnia wiadomość strumienia. RD-79 (it. 136).
/// </summary>
public sealed record RunProgressUpdate(string RunId, string Job, string Kind, string State, RunOutcomeKind Result, string? Code, long? Files, long? BytesUploaded, long BytesRead, bool Final);

/// <summary>
/// Port SCR-07 do strumienia postępu (RPC WatchRun, rola Viewer+). Strumień kończy się po wiadomości <c>Final</c>.
/// Utrata połączenia = <see cref="IOException"/> (widok ponawia z odstępami); odmowa usługi = <see cref="InvalidOperationException"/> (widok się poddaje).
/// </summary>
public interface IRunProgressSource
{
    IAsyncEnumerable<RunProgressUpdate> WatchAsync(string runId, CancellationToken ct);
}
