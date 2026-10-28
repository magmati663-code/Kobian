using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Grpc.Core;
using Kobian.Domain;
using Microsoft.AspNetCore.Connections.Features;

namespace Kobian.Agent.Hosting;

/// <summary>Tożsamość klienta gniazda Unix podana przez jądro (nie przez klienta): uid i gid efektywne procesu w chwili połączenia.</summary>
public sealed record UnixPeer(uint Uid, uint Gid);

/// <summary>
/// Odczyt poświadczeń klienta gniazda Unix (Linux: <c>SO_PEERCRED</c>, macOS: <c>LOCAL_PEERCRED</c>/xucred) — odpowiednik tokenu klienta named pipe w Windows (BL-17).
/// Każdy błąd = null (fail-closed: brak tożsamości → odmowa KB-7001).
/// </summary>
public static class UnixPeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int SolLocal = 0;
    private const int LocalPeerCred = 1;

    public static UnixPeer? Read(ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var socket = context.GetHttpContext().Features.Get<IConnectionSocketFeature>()?.Socket;
        return socket is null ? null : Read(socket);
    }

    public static UnixPeer? Read(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (socket.AddressFamily != AddressFamily.Unix) return null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
                var buf = new byte[12];
                if (socket.GetRawSocketOption(SolSocket, SoPeerCred, buf) != 12) return null;
                return new UnixPeer(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8)));
            }
            if (OperatingSystem.IsMacOS())
            {
                // struct xucred { u_int cr_version; uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; } — cr_groups[0] = gid efektywny
                var buf = new byte[76];
                if (socket.GetRawSocketOption(SolLocal, LocalPeerCred, buf) < 16 || BinaryPrimitives.ReadUInt32LittleEndian(buf) != 0) return null;
                if (BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(8)) < 1) return null;
                return new UnixPeer(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12)));
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return null;
        }
        return null;
    }
}

/// <summary>Port: konta i grupy systemu (testowalny bez prawdziwych kont).</summary>
public interface IUnixAccounts
{
    /// <summary>Czy użytkownik (uid, gid główny) należy do grupy o nazwie <paramref name="group"/>; nieznana grupa = false.</summary>
    bool IsMember(uint uid, uint primaryGid, string group);

    /// <summary>Czy użytkownik należy do grupy o identyfikatorze <paramref name="gid"/> (gid główny albo członek dodatkowy).</summary>
    bool IsMember(uint uid, uint primaryGid, uint gid);
}

/// <summary>
/// Konta z bazy systemu (getpwuid/getgrnam/getgrgid — także LDAP/SSSD przez NSS). Funkcje biblioteki C z buforem statycznym, więc wywołania są serializowane.
/// Układ <c>struct passwd</c>/<c>struct group</c>: wskaźniki na początku, gid po dwóch wskaźnikach, gr_mem po nim — taki sam na Linux i macOS (x64/arm64).
/// </summary>
public sealed class SystemUnixAccounts : IUnixAccounts
{
    public static SystemUnixAccounts Instance { get; } = new();

    private static readonly object Gate = new();

    public bool IsMember(uint uid, uint primaryGid, string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        lock (Gate)
        {
            var g = GetGrNam(group);
            return g != IntPtr.Zero && Member(g, uid, primaryGid);
        }
    }

    public bool IsMember(uint uid, uint primaryGid, uint gid)
    {
        if (gid == primaryGid) return true;
        lock (Gate)
        {
            var g = GetGrGid(gid);
            return g != IntPtr.Zero && Member(g, uid, primaryGid);
        }
    }

    private static bool Member(IntPtr group, uint uid, uint primaryGid)
    {
        var gid = (uint)Marshal.ReadInt32(group, 2 * IntPtr.Size);
        if (gid == primaryGid) return true;
        var user = UserName(uid);
        if (user is null) return false;
        var mem = Marshal.ReadIntPtr(group, (2 * IntPtr.Size) + IntPtr.Size);
        if (mem == IntPtr.Zero) return false;
        for (var i = 0; i < 65536; i++)
        {
            var p = Marshal.ReadIntPtr(mem, i * IntPtr.Size);
            if (p == IntPtr.Zero) return false;
            if (string.Equals(Marshal.PtrToStringUTF8(p), user, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string? UserName(uint uid)
    {
        var pw = GetPwUid(uid);
        if (pw == IntPtr.Zero) return null;
        var name = Marshal.ReadIntPtr(pw);
        return name == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(name);
    }

#pragma warning disable CA2101 // nazwa grupy jawnie jako LPUTF8Str
    [DllImport("libc", EntryPoint = "getpwuid", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr GetPwUid(uint uid);

    [DllImport("libc", EntryPoint = "getgrnam", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr GetGrNam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libc", EntryPoint = "getgrgid", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr GetGrGid(uint gid);
#pragma warning restore CA2101
}

/// <summary>
/// Mapa klient gniazda → rola (D2.9, odpowiednik <see cref="AgentRoleMap"/>): root albo grupa „kobian-admins” → Administrator; „kobian-operators” → Operator;
/// każdy inny lokalny użytkownik (gniazdo 0666, jak ACL pipe „Authenticated Users”) → Reader. Brak poświadczeń → None.
/// </summary>
public sealed class UnixRoleMap(IUnixAccounts accounts, string adminsGroup = UnixRoleMap.AdminsGroup, string operatorsGroup = UnixRoleMap.OperatorsGroup)
{
    public const string AdminsGroup = "kobian-admins";
    public const string OperatorsGroup = "kobian-operators";

    public AgentRole Map(UnixPeer? peer)
    {
        if (peer is null) return AgentRole.None;
        if (peer.Uid == 0 || accounts.IsMember(peer.Uid, peer.Gid, adminsGroup)) return AgentRole.Administrator;
        return accounts.IsMember(peer.Uid, peer.Gid, operatorsGroup) ? AgentRole.Operator : AgentRole.Reader;
    }
}

/// <summary>Rola z poświadczeń jądra (usługa Linux/macOS). Każdy wyjątek → brak tożsamości (KB-7001), nigdy rola domyślna.</summary>
public sealed class UnixPeerCallerResolver(Func<ServerCallContext, UnixPeer?> read, UnixRoleMap map) : ICallerRoleResolver
{
    public AgentRole Resolve(ServerCallContext context) => ResolveCaller(context).Role;

    public CallerInfo ResolveCaller(ServerCallContext context)
    {
        UnixPeer? peer;
        AgentRole role;
        try
        {
            peer = read(context);
            role = map.Map(peer);
        }
#pragma warning disable CA1031 // fail-closed: dowolny błąd odczytu = brak tożsamości (odmowa)
        catch (Exception)
#pragma warning restore CA1031
        {
            return CallerInfo.Unauthenticated;
        }
        return role == AgentRole.None || peer is null ? CallerInfo.Unauthenticated : new CallerInfo(role, ActorOf(peer));
    }

    /// <summary>Aktor w dzienniku audytu: „unix-uid:1000” (odpowiednik SID w Windows).</summary>
    public static string ActorOf(UnixPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return "unix-uid:" + peer.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// SEC-6.3 na Linux/macOS: Agent działa jako root, więc Operator odtwarza tylko tam, gdzie SAM mógłby tworzyć pliki — prawo przeszukiwania (x) każdego
/// katalogu na ścieżce i zapisu (w+x) w folderze docelowym wg bitów właściciel/grupa/inni. Dowiązanie symboliczne na ścieżce = odmowa (podaj ścieżkę rzeczywistą);
/// brak lstat = odmowa (fail-closed). ACL rozszerzone (setfacl) nie są brane pod uwagę — mogą tylko zawęzić wynik do odmowy po stronie bitów.
/// </summary>
public sealed class UnixRestoreTargetAuthorizer(Func<ServerCallContext, UnixPeer?> read, IUnixAccounts accounts, Func<string, UnixStat?>? lstat = null) : IRestoreTargetAuthorizer
{
    private readonly Func<string, UnixStat?> _lstat = lstat ?? UnixFileStat.TryLStat;

    public RestoreTargetAccess Check(ServerCallContext context, string target) => Check(read(context), target);

    public RestoreTargetAccess Check(UnixPeer? peer, string target)
    {
        if (peer is null || string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return RestoreTargetAccess.Denied;
        if (peer.Uid == 0) return RestoreTargetAccess.Allowed;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = "/";
        for (var i = 0; i <= parts.Length; i++)
        {
            if (i > 0) path = path == "/" ? "/" + parts[i - 1] : path + "/" + parts[i - 1];
            if (_lstat(path) is not { Type: UnixFileType.Directory } st) return RestoreTargetAccess.Denied;
            var last = i == parts.Length;
            if (!Has(peer, st, execute: true, write: last)) return RestoreTargetAccess.Denied;
        }
        return RestoreTargetAccess.Allowed;
    }

    private bool Has(UnixPeer peer, UnixStat st, bool execute, bool write)
    {
        // POSIX: właściciel → bity właściciela (bez przechodzenia do grupy/innych), członek grupy → bity grupy, reszta → bity innych.
        var bits = st.Uid == peer.Uid ? (st.Mode >> 6) & 7 : accounts.IsMember(peer.Uid, peer.Gid, st.Gid) ? (st.Mode >> 3) & 7 : st.Mode & 7;
        return (!execute || (bits & 1) != 0) && (!write || (bits & 2) != 0);
    }
}
