using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Local;

/// <summary>Mapowanie błędów systemu plików na klasy i kody D4.8 (wiersze „SMB / Win32” stosowane też lokalnie).</summary>
internal static class LocalErrorMap
{
    // Win32: 112 ERROR_DISK_FULL, 39 ERROR_HANDLE_DISK_FULL, 80 ERROR_FILE_EXISTS, 183 ERROR_ALREADY_EXISTS.
    // Unix (.NET ustawia HResult = errno): 28 ENOSPC, 17 EEXIST, 122 EDQUOT.
    public static StorageException Map(Exception ex)
    {
        if (ex is StorageException se) return se;
        var code = ex.HResult & 0xFFFF;
        var isWin = OperatingSystem.IsWindows();
        return ex switch
        {
            UnauthorizedAccessException => new(ErrorClass.Permanent, KbCode.AccessDenied, "Access denied on destination.", ex),
            DirectoryNotFoundException => new(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination path not found.", ex),
            IOException when (isWin && code is 112 or 39) || (!isWin && code is 28 or 122) =>
                new(ErrorClass.Permanent, KbCode.DestinationFull, "No space left on destination.", ex),
            IOException when (isWin && code is 80 or 183) || (!isWin && code == 17) =>
                new(ErrorClass.Permanent, KbCode.ObjectExists, "Object already exists on destination.", ex),
            FileNotFoundException => new(ErrorClass.Permanent, KbCode.DestinationNotFound, "Object not found.", ex),
            _ => new(ErrorClass.Transient, KbCode.TransientIo, "Transient I/O error on destination.", ex),
        };
    }
}
