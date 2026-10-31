using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Kobian.Domain;

public enum UnixFileType
{
    Unknown = 0,
    Regular = 1,
    Directory = 2,
    Symlink = 3,
    Fifo = 4,
    Socket = 5,
    CharDevice = 6,
    BlockDevice = 7,
}

/// <summary>Wynik lstat: typ, bity uprawnień (07777), właściciel.</summary>
public readonly record struct UnixStat(UnixFileType Type, uint Mode, uint Uid, uint Gid);

/// <summary>
/// lstat bez śledzenia dowiązań na Linuksie (<c>statx</c>, układ struktury niezależny od architektury) i macOS (<c>lstat</c>, 64-bitowy i-węzeł).
/// System.IO nie mówi, czy wpis jest FIFO/gniazdem/urządzeniem — a otwarcie FIFO do odczytu blokuje się na zawsze, urządzenie znakowe daje strumień bez końca.
/// Brak funkcji w bibliotece C (stara glibc &lt; 2.28, inna platforma) → null; wywołujący zachowuje się jak dotąd.
/// </summary>
public static class UnixFileStat
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasic = 0x1 | 0x2 | 0x8 | 0x10; // TYPE | MODE | UID | GID
    private static bool _unavailable;

    public static UnixStat? TryLStat(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_unavailable || OperatingSystem.IsWindows()) return null;
        var buf = new byte[256];
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxBasic, buf) != 0) return null;
                // struct statx: stx_uid @20, stx_gid @24, stx_mode (u16) @28
                return Make(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(28)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(20)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(24)));
            }
            if (OperatingSystem.IsMacOS())
            {
                var rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStatMacX64(path, buf) : LStatMac(path, buf);
                if (rc != 0) return null;
                // struct stat (darwin, _DARWIN_FEATURE_64_BIT_INODE): st_mode (u16) @4, st_uid @16, st_gid @20
                return Make(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(20)));
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _unavailable = true;
        }
        return null;
    }

    /// <summary>lchown (bez śledzenia dowiązań); false przy błędzie albo poza Linux/macOS.</summary>
    public static bool TryLChown(string path, uint uid, uint gid)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            return LChown(path, uid, gid) == 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Czy proces działa z efektywnym uid 0 (usługa Linux/macOS).</summary>
    public static bool IsRoot()
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            return GetEUid() == 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    public static UnixStat Make(uint mode, uint uid, uint gid)
    {
        var type = (mode & 0xF000) switch
        {
            0x1000 => UnixFileType.Fifo,
            0x2000 => UnixFileType.CharDevice,
            0x4000 => UnixFileType.Directory,
            0x6000 => UnixFileType.BlockDevice,
            0x8000 => UnixFileType.Regular,
            0xA000 => UnixFileType.Symlink,
            0xC000 => UnixFileType.Socket,
            _ => UnixFileType.Unknown,
        };
        return new UnixStat(type, mode & 0xFFF, uid, gid);
    }

#pragma warning disable CA2101 // ścieżka jawnie jako LPUTF8Str (UTF-8 jak w libc); CA2101 zna tylko CharSet.Unicode/LPWStr
    // DllImport, nie LibraryImport: generator LibraryImport wymaga AllowUnsafeBlocks (zakazane bez ADR — BannedApiTests). Bufor byte[] jest przypinany przez marshaller.
    [DllImport("libc", EntryPoint = "statx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buf);

    [DllImport("libc", EntryPoint = "lstat", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LStatMac([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buf);

    [DllImport("libc", EntryPoint = "lchown", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LChown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);

    [DllImport("libc", EntryPoint = "geteuid", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEUid();

    [DllImport("libc", EntryPoint = "lstat$INODE64", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LStatMacX64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buf);
#pragma warning restore CA2101
}
