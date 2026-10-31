namespace Kobian.Application.Storage;

/// <summary>
/// Port regulatora równoległości wysyłek do celu (D4.16 pkt 2; it. 118, RD-73) — implementacja AIMD w Providers.Storage (AimdParallelism).
/// Silnik pyta o bieżący limit przed każdą nową wysyłką i zgłasza wynik zakończonej.
/// </summary>
public interface IUploadParallelism
{
    int Current { get; }

    bool OnSuccess();

    bool OnLimitSignal();
}
