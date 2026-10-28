namespace Kobian.Api.Contracts;

/// <summary>Stałe konwencji API kobian.v1 (D2.10, D2.16) współdzielone przez klienta i Agenta.</summary>
public static class ApiConventions
{
    /// <summary>Nagłówek żądania z numerem rewizji klienta (liczba całkowita).</summary>
    public const string ClientRevisionHeader = "kobian-client-revision";

    /// <summary>Trailer odpowiedzi z kodem KB-xxxx (bez danych klienta — SEC-11.5).</summary>
    public const string KbCodeTrailer = "kb-code";

    /// <summary>Rewizja kontraktu, którą mówi ta wersja klienta i Agenta.</summary>
    public const int CurrentRevision = 1;

    /// <summary>Domyślna nazwa named pipe Agenta (D2.4).</summary>
    public const string DefaultPipeName = "Kobian.Agent.v1";

    /// <summary>
    /// US-29: największy dokument kobian-export przyjmowany przez ImportJobs (i produkowany przez ExportJobs) — mieści się w limicie wiadomości hosta 64 KiB (SEC-3.3).
    /// PROWIZORYCZNIE Qb83 (opis w JobExportFormat).
    /// </summary>
    public const int MaxJobsDocumentBytes = 60 * 1024;

    /// <summary>Czas oczekiwania klienta na połączenie (AC-37.1: ≤ 5 s).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ścieżka gniazda Unix zastępującego named pipe poza Windows (Kestrel obsługuje named pipes tylko w Windows) — wyłącznie dev/testy
    /// (PROWIZORYCZNIE ADR-0004). Produkcja = Windows + named pipe.
    /// </summary>
    public static string UnixSocketPath(string pipeName) => Path.Combine(Path.GetTempPath(), pipeName + ".sock");
}
