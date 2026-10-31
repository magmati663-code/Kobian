using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kobian.Application.Storage;

namespace Kobian.Platform.Windows.Smb;

/// <summary>
/// Adapter SMB dla Windows (BL-73, D4.2): <c>WNetAddConnection2W</c> z <c>lpLocalName = NULL</c> (połączenie bez litery dysku, w sesji logowania usługi —
/// niewidoczne w <c>net use</c> użytkownika interaktywnego, AC-04.3) i <c>CONNECT_TEMPORARY</c> (nie zapamiętywane w profilu). Rozłączenie
/// <c>WNetCancelConnection2W</c> bez wymuszania (wymuszenie tylko przy unieważnianiu puli — D4.16 pkt 4, poza tą klasą).
/// Hasło przekazywane w buforze niezarządzanym zerowanym po wywołaniu. Kobian nie zmienia ustawień klienta SMB (podpisywanie, szyfrowanie — D4.15).
/// Niezweryfikowane na Windows — kolejka W-35. Wspólne połączenia dla równoległych sesji: <c>SharedSmbConnector</c> (Providers.Storage).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WNetSmbConnector : ISmbConnector
{
    public ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(credential);
        ct.ThrowIfCancellationRequested();
        var remote = Marshal.StringToHGlobalUni(share.ShareUnc);
        var user = Marshal.StringToHGlobalUni(credential.User);
        var secret = Marshal.StringToHGlobalUni(credential.Password);
        try
        {
            var res = new NativeMethods.NetResource { Type = NativeMethods.ResourceTypeDisk, RemoteName = remote };
            var rc = NativeMethods.WNetAddConnection2W(ref res, secret, user, NativeMethods.ConnectTemporary);
            if (rc != 0) throw new SmbConnectException(rc, "WNetAddConnection2 failed.");
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(secret);
            Marshal.FreeHGlobal(user);
            Marshal.FreeHGlobal(remote);
        }
        return ValueTask.FromResult<ISmbConnection>(new Connection(share.ShareUnc, share.RootUnc));
    }

    private sealed class Connection(string shareUnc, string root) : ISmbConnection
    {
        private int _closed;

        public string LocalRoot { get; } = root;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                var name = Marshal.StringToHGlobalUni(shareUnc);
                try
                {
                    // 2250 ERROR_NOT_CONNECTED — już rozłączone (np. przez unieważnienie puli); inne błędy nie blokują zamknięcia sesji.
                    _ = NativeMethods.WNetCancelConnection2W(name, 0, 0);
                }
                finally
                {
                    Marshal.FreeHGlobal(name);
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    public const int ResourceTypeDisk = 1;
    public const int ConnectTemporary = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public IntPtr LocalName;
        public IntPtr RemoteName;
        public IntPtr Comment;
        public IntPtr Provider;
    }

    [DllImport("mpr.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WNetAddConnection2W(ref NetResource netResource, IntPtr password, IntPtr userName, int flags);

    [DllImport("mpr.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WNetCancelConnection2W(IntPtr name, int flags, int force);
}
