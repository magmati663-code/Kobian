using System.Runtime.InteropServices;

namespace Kobian.UI.Services;

/// <summary>
/// RD-57 (it. 96, 6.7): czy bieżący proces UI działa na tokenie ograniczonym UAC konta administratora — wtedy rola „Podgląd” wynika z braku podniesienia
/// i pulpit podpowiada „Uruchom jako administrator” zamiast „poproś administratora”. Rola zawsze pochodzi z Agenta (D2.9); ta informacja tylko dobiera tekst.
/// UI nie uruchamia samo siebie z podniesieniem (zakaz Process.Start przez powłokę — lista kontrolna rola 11) — wyłącznie instrukcja słowna.
/// </summary>
public interface IElevationInfo
{
    bool CanElevate { get; }
}

/// <summary>Brak informacji o UAC (inne systemy, testy) — podpowiedź „poproś administratora”.</summary>
public sealed class NoElevation : IElevationInfo
{
    public static readonly NoElevation Instance = new();

    public bool CanElevate => false;
}

/// <summary>
/// Adapter Windows: <c>GetTokenInformation(TokenElevationType)</c> tokenu własnego procesu; <c>TokenElevationTypeLimited</c> (3) = administrator bez podniesienia.
/// Każdy błąd → false (tekst domyślny). Test: W-38 w budowa/do-uruchomienia-na-windows.md.
/// </summary>
public sealed class TokenElevationInfo : IElevationInfo
{
    private readonly Lazy<bool> _value = new(Read);

    public bool CanElevate => _value.Value;

    private static bool Read()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!ElevationNativeMethods.OpenProcessToken(ElevationNativeMethods.GetCurrentProcess(), ElevationNativeMethods.TokenQuery, out var token)) return false;
        try
        {
            return ElevationNativeMethods.GetTokenInformation(token, ElevationNativeMethods.TokenElevationTypeClass, out var type, sizeof(int), out _)
                && type == ElevationNativeMethods.TokenElevationTypeLimited;
        }
        finally
        {
            _ = ElevationNativeMethods.CloseHandle(token);
        }
    }
}

internal static class ElevationNativeMethods
{
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevationTypeClass = 18;
    internal const int TokenElevationTypeLimited = 3;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
