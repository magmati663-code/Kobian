namespace Kobian.Agent.Hosting;

/// <summary>Akcja odzyskiwania SCM (D2.12 pkt 4): restart po <see cref="Delay"/>.</summary>
public sealed record ServiceRecoveryAction(TimeSpan Delay);

/// <summary>
/// Profil usługi Windows Agenta — jedno źródło prawdy dla instalatora (MSI, BL-MSI) i kolejki testów Windows (W-33).
/// R2.2.1 / D2.5: usługa „Automatic (Delayed Start)”, konto wirtualne <c>NT SERVICE\KobianAgent</c> (bez Administrators/Backup Operators);
/// D2.12: token ograniczony do uprawnień backupu (bez SeImpersonate/SeDebug/SeTcb), akcje odzyskiwania 1 min ×2, potem 5 min (NFR-09),
/// ServiceSidType = Unrestricted (SP-13 do potwierdzenia), wpis VssAccessControl = 2 (K12.2).
/// Ten typ nie wykonuje niczego na systemie — instalator i testy Windows czytają z niego wartości.
/// </summary>
public static class AgentServiceProfile
{
    public const string ServiceName = "KobianAgent";
    public const string DisplayName = "Kobian Backup Agent";
    public const string Description = "Kobian Backup: harmonogram i wykonywanie kopii zapasowych, API lokalne (named pipe).";
    public const string Account = @"NT SERVICE\" + ServiceName;
    public const string StartMode = "delayed-auto";
    public const string SidType = "unrestricted";

    /// <summary>D2.15 pkt 3: mutex pojedynczej instancji (ACL konto usługi + SYSTEM — instalator/W-33).</summary>
    public const string InstanceMutexName = @"Global\Kobian.Agent.Instance";

    /// <summary>K12.2/D2.12 pkt 3: wartość rejestru dla requestera VSS spoza Administrators.</summary>
    public const string VssAccessControlKey = @"HKLM\SYSTEM\CurrentControlSet\Services\VSS\VssAccessControl";
    public const int VssAccessControlValue = 2;

    /// <summary>D2.12 pkt 1: SERVICE_CONFIG_REQUIRED_PRIVILEGES_INFO (lista zamknięta).</summary>
    public static IReadOnlyList<string> RequiredPrivileges { get; } =
        ["SeBackupPrivilege", "SeRestorePrivilege", "SeChangeNotifyPrivilege", "SeShutdownPrivilege"];

    /// <summary>
    /// D2.12 pkt 2: uprawnienia LSA nadawane kontu wirtualnemu — KAŻDE z <see cref="RequiredPrivileges"/>, którego konto wirtualne nie ma domyślnie
    /// (SeChangeNotify ma każdy). Bez SeShutdownPrivilege SCM odmawia startu: zdarzenie 7000 „A privilege that the service requires… does not exist”.
    /// </summary>
    public static IReadOnlyList<string> LsaRights { get; } = ["SeBackupPrivilege", "SeRestorePrivilege", "SeShutdownPrivilege"];

    /// <summary>Uprawnienia, których token usługi nie może mieć (SEC-1.4; SeImpersonate — D2.9/Q44).</summary>
    public static IReadOnlyList<string> ForbiddenPrivileges { get; } =
        ["SeImpersonatePrivilege", "SeDebugPrivilege", "SeTcbPrivilege", "SeTakeOwnershipPrivilege", "SeLoadDriverPrivilege", "SeAssignPrimaryTokenPrivilege"];

    /// <summary>D2.12 pkt 4 / NFR-09: restart po 1 min ×2, potem co 5 min; licznik awarii zerowany po dobie.</summary>
    public static IReadOnlyList<ServiceRecoveryAction> RecoveryActions { get; } =
        [new(TimeSpan.FromMinutes(1)), new(TimeSpan.FromMinutes(1)), new(TimeSpan.FromMinutes(5))];

    public static TimeSpan RecoveryResetPeriod { get; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Argumenty <c>sc.exe</c> (lista, nie wiersz poleceń — bez powłoki, SEC: zakaz składania poleceń) do instalacji deweloperskiej w kolejce Windows (W-33).
    /// Produkcja: MSI ServiceInstall + custom action (D2.12). <paramref name="binaryPath"/> musi być ścieżką bezwzględną do Kobian.Agent.exe.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ScCommands(string binaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        if (binaryPath.Contains('"', StringComparison.Ordinal) || binaryPath.AsSpan().IndexOfAny('\r', '\n') >= 0 || !IsWindowsAbsolute(binaryPath))
            throw new ArgumentException("Binary path must be an absolute local Windows path without quotes.", nameof(binaryPath));
        var actions = string.Join('/', RecoveryActions.Select(a => "restart/" + ((long)a.Delay.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return
        [
            ["create", ServiceName, "binPath=", "\"" + binaryPath + "\"", "start=", StartMode, "obj=", Account, "DisplayName=", DisplayName],
            ["description", ServiceName, Description],
            ["privs", ServiceName, string.Join('/', RequiredPrivileges)],
            ["sidtype", ServiceName, SidType],
            ["failure", ServiceName, "reset=", ((long)RecoveryResetPeriod.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "actions=", actions],
            ["failureflag", ServiceName, "1"],
        ];
    }

    /// <summary>„C:\…” — litera dysku + separator; UNC (\\host\…), ścieżki urządzeń (\\?\, \\.\) i względne odrzucane.</summary>
    internal static bool IsWindowsAbsolute(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
}
