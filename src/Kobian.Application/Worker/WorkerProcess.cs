using Kobian.Domain;

namespace Kobian.Application.Worker;

/// <summary>Polecenie uruchomienia Workera: plik wykonywalny + stałe argumenty poprzedzające rolę (produkcja: Kobian.Worker.exe bez prefiksu; testy: dotnet + Kobian.Worker.dll).</summary>
public sealed record WorkerCommand(string FileName, IReadOnlyList<string> PrefixArguments, string VerifyPath);

/// <summary>Port weryfikacji podpisu pliku Workera przed startem (SEC-2.7). Adapter Windows: Authenticode (WinVerifyTrust).</summary>
public interface ISignatureVerifier
{
    bool IsTrusted(string path);
}

/// <summary>Tryb deweloperski/konsoli: bez weryfikacji (PROWIZORYCZNIE Qb55 — usługa zawsze weryfikuje).</summary>
public sealed class NoSignatureCheck : ISignatureVerifier
{
    public static NoSignatureCheck Instance { get; } = new();

    public bool IsTrusted(string path) => true;
}

/// <summary>Port obiektu zadania (job object, NFR-19): proces potomny ginie razem z uchwytem rodzica, limit pamięci. Null = brak (Linux/testy).</summary>
public interface IProcessJob : IDisposable
{
    void Assign(System.Diagnostics.Process process);
}

/// <summary>Uruchomiony Worker: stdin/stdout jako strumienie protokołu, zabicie drzewa procesów, czekanie na wyjście.</summary>
public interface IWorkerProcess : IAsyncDisposable
{
    Stream Input { get; }

    Stream Output { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    Task WaitForExitAsync(CancellationToken ct);

    void Kill();
}

/// <summary>Błąd startu Workera z kodem KB: KB-7008 (polityka systemu), KB-7015 (podpis), KB-7016 (pozostałe).</summary>
public sealed class WorkerStartException : Exception
{
    public WorkerStartException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    public WorkerStartException()
        : this(KbCode.WorkerStartFailed, "Worker start failed")
    {
    }

    public WorkerStartException(string message)
        : this(KbCode.WorkerStartFailed, message)
    {
    }

    public WorkerStartException(string message, Exception inner)
        : this(KbCode.WorkerStartFailed, message, inner)
    {
    }

    public string Code { get; }
}
