using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kobian.Application.Worker;

namespace Kobian.Platform.Windows.Process;

/// <summary>
/// Weryfikacja Authenticode pliku Workera przed startem (it. 106; SEC-2.7): WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2), bez UI,
/// bez sprawdzania odwołań przez sieć (PROWIZORYCZNIE Qb55 — usługa może nie mieć Internetu; łańcuch i znacznik czasu sprawdzane lokalnie).
/// Zgodność wydawcy z podpisem Agenta — do zrobienia (RD-67). Test: W-45 (plik podpisany, niepodpisany, zmieniony bajt).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public bool IsTrusted(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new TrustNative.FileInfo { Size = (uint)Marshal.SizeOf<TrustNative.FileInfo>(), FilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<TrustNative.FileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new TrustNative.Data
            {
                Size = (uint)Marshal.SizeOf<TrustNative.Data>(),
                UiChoice = 2,          // WTD_UI_NONE
                RevocationChecks = 0,  // WTD_REVOKE_NONE
                UnionChoice = 1,       // WTD_CHOICE_FILE
                File = filePtr,
                StateAction = 0,       // WTD_STATEACTION_IGNORE
                ProvFlags = 0x00001000, // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = GenericVerifyV2;
            return TrustNative.WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0; // INVALID_HANDLE_VALUE = bez okna
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class TrustNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Data
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref Data data);
}
