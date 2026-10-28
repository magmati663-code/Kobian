using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Smb;

/// <summary>
/// Klasyfikacja błędów nawiązania połączenia SMB (Win32) wg D4.8, wiersze „SMB / Win32”. Błędy uwierzytelnienia są trwałe i nigdy nie są ponawiane
/// (blokada konta — 4.7, D4.16 pkt 3). Błędy operacji na plikach po połączeniu mapuje <c>LocalErrorMap</c> (te same kody Win32).
/// Kody spoza tabeli planu (2202, 1331, 1793, 2242, 1385, 1203, 1222, 1311) przypisane do najbliższego wiersza — do potwierdzenia w SP-22 (W-35).
/// </summary>
public static class SmbErrorMap
{
    public static StorageException MapConnect(int win32, Exception? inner = null) => win32 switch
    {
        1219 => new(ErrorClass.Permanent, KbCode.SmbCredentialConflict, "SMB server already has a connection under a different account.", inner),
        1326 or 86 or 2202 => new(ErrorClass.Permanent, KbCode.AuthRejected, "Credentials rejected by destination.", inner),
        1909 or 1330 or 1907 or 1331 or 1793 or 2242 => new(ErrorClass.Permanent, KbCode.AccountLockedOrExpired, "Destination account is locked, disabled or expired.", inner),
        5 or 1385 => new(ErrorClass.Permanent, KbCode.AccessDenied, "Access denied on destination.", inner),
        53 or 67 or 1203 => new(ErrorClass.Permanent, KbCode.DestinationNotFound, "SMB share not found.", inner),
        112 or 39 => new(ErrorClass.Permanent, KbCode.DestinationFull, "No space left on destination.", inner),
        1238 => new(ErrorClass.Transient, KbCode.ServerSessionLimit, "Destination refused a new session (server limit).", inner),
        _ => new(ErrorClass.Transient, KbCode.TransientIo, "Transient SMB connection error.", inner), // 64, 59, 121, 1231, 1222, 1311 i nieznane
    };

    /// <summary>Czy błąd połączenia wolno ponowić automatycznie (tylko klasa przejściowa).</summary>
    public static bool IsRetryable(int win32) => MapConnect(win32).ErrorClass == ErrorClass.Transient;
}
