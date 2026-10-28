using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Sftp;

/// <summary>Klasyfikacja błędów SFTP wg D4.8 (wiersze „SFTP”).</summary>
internal static class SftpErrorMap
{
    public static StorageException Map(Exception ex)
    {
        if (ex is StorageException se) return se;
        if (ex is SftpProtocolException p)
        {
            return p.Status switch
            {
                SftpStatus.NoSuchFile => new(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination path not found.", ex),
                SftpStatus.PermissionDenied => new(ErrorClass.Permanent, KbCode.AccessDenied, "Access denied on destination.", ex),
                SftpStatus.FileAlreadyExists => new(ErrorClass.Permanent, KbCode.ObjectExists, "Object already exists on destination.", ex),
                SftpStatus.NoSpaceOnFilesystem or SftpStatus.QuotaExceeded => new(ErrorClass.Permanent, KbCode.DestinationFull, "No space left on destination.", ex),
                SftpStatus.HostKeyMismatch => new(ErrorClass.Security, KbCode.HostKeyMismatch, "Server host key does not match the pinned key.", ex) { PresentedIdentity = p.PresentedIdentity },
                SftpStatus.AuthRejected => new(ErrorClass.Permanent, KbCode.AuthRejected, "Credentials rejected by destination.", ex),
                SftpStatus.SessionLimit => new(ErrorClass.Transient, KbCode.ServerSessionLimit, "Destination refused a new session (server limit).", ex),
                _ => new(ErrorClass.Transient, KbCode.TransientIo, "Transient SFTP error on destination.", ex),
            };
        }
        return ex switch
        {
            IOException or TimeoutException or System.Net.Sockets.SocketException => new(ErrorClass.Transient, KbCode.TransientIo, "Connection error on destination.", ex),
            UnauthorizedAccessException => new(ErrorClass.Permanent, KbCode.AccessDenied, "Access denied on destination.", ex),
            _ => new(ErrorClass.Transient, KbCode.TransientIo, "Transient SFTP error on destination.", ex),
        };
    }
}
