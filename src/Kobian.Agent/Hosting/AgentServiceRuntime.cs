namespace Kobian.Agent.Hosting;

/// <summary>
/// Styk z SCM i Event Log (BL-72). Kompilacja net10.0-windows: Microsoft.Extensions.Hosting.WindowsServices + System.Diagnostics.EventLog;
/// net10.0 (Linux, testy): zawsze konsola, wpisy tylko na stderr. Testy Windows: W-33 (budowa/do-uruchomienia-na-windows.md).
/// </summary>
public static class AgentServiceRuntime
{
    /// <summary>Identyfikator zdarzenia Event Log dla błędu startu (KB-7007, konfiguracja, tryb awaryjny).</summary>
    public const int StartupFailureEventId = 7007;

    /// <summary>Zmienna ustawiana przez jednostkę systemd / plik launchd: <c>KOBIAN_SERVICE=1</c> = tryb usługi Linux/macOS.</summary>
    public const string UnixServiceVariable = "KOBIAN_SERVICE";

    /// <summary>
    /// Usługa Linux/macOS (systemd, launchd): ścieżki systemowe, gniazdo /run/kobian/agent.sock, role z poświadczeń jądra.
    /// Zmienna nie podnosi uprawnień nikomu — w tym trybie role są węższe niż w konsoli (KOBIAN_DEV_ROLE ignorowane).
    /// </summary>
    public static bool IsUnixService(Func<string, string?>? env = null) =>
        !OperatingSystem.IsWindows() && (env ?? Environment.GetEnvironmentVariable)(UnixServiceVariable) == "1";

    public static bool IsWindowsService()
    {
#if KOBIAN_WINDOWS
        return Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
#else
        return false;
#endif
    }

    /// <summary>
    /// Wpis Event Log (źródło <see cref="AgentServiceProfile.ServiceName"/>, rejestrowane przez instalator) — D2.15 pkt 3, AC-37.3, AC-05.4 (alert poza katalogiem).
    /// Brak źródła lub uprawnień nie może zatrzymać ścieżki błędu: wpis jest wtedy pomijany (stderr zostaje).
    /// </summary>
    public static void ReportStartupFailure(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
#if KOBIAN_WINDOWS
        try
        {
            if (!System.Diagnostics.EventLog.SourceExists(AgentServiceProfile.ServiceName)) return; // utworzenie źródła wymaga Administratora — robi to MSI
            System.Diagnostics.EventLog.WriteEntry(AgentServiceProfile.ServiceName, message, System.Diagnostics.EventLogEntryType.Error, StartupFailureEventId);
        }
        catch (System.Security.SecurityException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
#endif
    }
}
