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
    /// Ścieżka gniazda Unix Agenta uruchomionego w konsoli (dev/testy, gniazdo tylko dla bieżącego użytkownika — PROWIZORYCZNIE ADR-0004).
    /// Usługa Linux/macOS używa <see cref="SystemSocketPath"/>.
    /// </summary>
    public static string UnixSocketPath(string pipeName) => UnixSocketPath(pipeName, Path.GetTempPath(), Environment.UserName);

    /// <summary>Najdłuższa ścieżka gniazda w bajtach UTF-8 (sun_path: macOS 104, Linux 108 — z kończącym NUL; zapas na obu).</summary>
    public const int MaxUnixSocketPathBytes = 100;

    /// <summary>
    /// Gniazdo w katalogu tymczasowym; zbyt długi katalog (np. TMPDIR w głębokim folderze) → <c>/tmp/&lt;nazwa&gt;-&lt;użytkownik&gt;.sock</c>
    /// zamiast awarii Agenta przy starcie (UnixDomainSocketEndPoint odrzuca dłuższe ścieżki). Klient liczy ścieżkę tak samo.
    /// </summary>
    public static string UnixSocketPath(string pipeName, string tempDirectory, string userName)
    {
        var path = Path.Combine(tempDirectory, pipeName + ".sock");
        if (System.Text.Encoding.UTF8.GetByteCount(path) <= MaxUnixSocketPathBytes) return path;
        var safeUser = new string([.. userName.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')]);
        var fallback = "/tmp/" + pipeName + "-" + (safeUser.Length > 0 ? safeUser : "user") + ".sock";
        return System.Text.Encoding.UTF8.GetByteCount(fallback) <= MaxUnixSocketPathBytes ? fallback : "/tmp/" + pipeName + ".sock";
    }

    /// <summary>Zmienna środowiskowa z jawną ścieżką gniazda Agenta (klient i usługa Linux/macOS).</summary>
    public const string SocketVariable = "KOBIAN_SOCKET";

    /// <summary>Gniazdo usługi Linux (systemd RuntimeDirectory=kobian) / macOS (launchd).</summary>
    public static string SystemSocketPath => OperatingSystem.IsMacOS() ? "/var/run/kobian/agent.sock" : "/run/kobian/agent.sock";

    /// <summary>
    /// Gniazdo, z którym łączy się klient (CLI, UI) poza Windows: <c>KOBIAN_SOCKET</c>, potem Agent konsolowy bieżącego użytkownika (jeśli jego gniazdo istnieje),
    /// potem usługa systemowa; gdy nic nie istnieje — ścieżka konsolowa (komunikat „usługa niedostępna” jak dotąd).
    /// </summary>
    public static string ClientSocketPath(string pipeName)
    {
        var env = Environment.GetEnvironmentVariable(SocketVariable);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var dev = UnixSocketPath(pipeName);
        if (Path.Exists(dev) || pipeName != DefaultPipeName) return dev;
        return Path.Exists(SystemSocketPath) ? SystemSocketPath : dev;
    }
}
