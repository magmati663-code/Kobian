using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Ftp;

/// <summary>Klasyfikacja błędów FTP/FTPS wg D4.8 (wiersze „FTP”, „FTP / FTPS”) i D4.16 (421 = limit połączeń).</summary>
internal static class FtpErrorMap
{
    public static StorageException Map(Exception ex)
    {
        if (ex is StorageException se) return se;
        if (ex is FtpProtocolException p)
        {
            return p.Failure switch
            {
                FtpFailure.AuthRejected => new(ErrorClass.Permanent, KbCode.AuthRejected, "Credentials rejected by destination.", ex),
                FtpFailure.NoSpace => new(ErrorClass.Permanent, KbCode.DestinationFull, "No space left on destination.", ex),
                FtpFailure.NotFound => new(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination path not found.", ex),
                FtpFailure.PermissionDenied => new(ErrorClass.Permanent, KbCode.AccessDenied, "Access denied on destination.", ex),
                FtpFailure.NameNotAllowed => new(ErrorClass.Permanent, KbCode.InvalidObjectName, "Destination refused the object name.", ex),
                FtpFailure.AlreadyExists => new(ErrorClass.Permanent, KbCode.ObjectExists, "Object already exists on destination; never overwritten.", ex),
                FtpFailure.TlsIdentityRejected => new(ErrorClass.Security, KbCode.TlsIdentityMismatch, "FTPS server certificate is not trusted and its key is not pinned.", ex) { PresentedIdentity = p.PresentedIdentity },
                FtpFailure.TlsNotSupported => new(ErrorClass.Permanent, KbCode.FtpsNotSupported, "FTP server refused TLS (FTPS required).", ex),
                FtpFailure.InsecureNotConfirmed => new(ErrorClass.Security, KbCode.InsecureTransportNotConfirmed, "Unencrypted FTP requires explicit confirmation.", ex),
                FtpFailure.SessionLimit => new(ErrorClass.Transient, KbCode.ServerSessionLimit, "Destination refused a new session (server limit).", ex),
                _ => new(ErrorClass.Transient, KbCode.TransientIo, "Transient FTP error on destination.", ex),
            };
        }
        return ex switch
        {
            IOException or TimeoutException or System.Net.Sockets.SocketException => new(ErrorClass.Transient, KbCode.TransientIo, "Connection error on destination.", ex),
            UnauthorizedAccessException => new(ErrorClass.Permanent, KbCode.AccessDenied, "Access denied on destination.", ex),
            _ => new(ErrorClass.Transient, KbCode.TransientIo, "Transient FTP error on destination.", ex),
        };
    }

    /// <summary>
    /// Kod odpowiedzi → klasa (D4.8). <paramref name="writeContext"/>: 550 przy STOR/MKD/RNTO = brak uprawnień (KB-2105), w pozostałych = brak obiektu (KB-2106).
    /// 421 z wzmianką o liczbie połączeń → limit serwera (KB-2112, D4.16 pkt 1). Pozostałe 4xx przejściowe, pozostałe 5xx trwałe (KB-2105).
    /// </summary>
    public static FtpFailure Classify(int code, string? message, bool writeContext)
    {
        if (code == 421 && message is not null && LooksLikeConnectionLimit(message)) return FtpFailure.SessionLimit;
        return code switch
        {
            530 or 332 => FtpFailure.AuthRejected,
            452 or 552 => FtpFailure.NoSpace,
            550 => writeContext ? FtpFailure.PermissionDenied : FtpFailure.NotFound,
            553 => FtpFailure.NameNotAllowed,
            >= 400 and < 500 => FtpFailure.Transient,
            >= 500 and < 600 => FtpFailure.PermissionDenied,
            _ => FtpFailure.Transient,
        };
    }

    private static bool LooksLikeConnectionLimit(string message) =>
        message.Contains("too many", StringComparison.OrdinalIgnoreCase)
        || message.Contains("connections", StringComparison.OrdinalIgnoreCase)
        || message.Contains("limit", StringComparison.OrdinalIgnoreCase);
}
