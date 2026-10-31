using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Kobian.Application.Install;
using Microsoft.Win32;

namespace Kobian.Platform.Windows.Install;

/// <summary>
/// Adapter kroków instalacji (it. 100, D2.12, 2.6) dla custom action MSI uruchamianej jako LocalSystem (Impersonate=no).
/// Grupy: NetLocalGroupAdd (istniejąca = OK). Katalogi: DirectorySecurity bez dziedziczenia, właściciel Administrators, wpisy dziedziczone (CI|OI).
/// Rejestr: HKLM (64-bit). LSA: LsaAddAccountRights/LsaRemoveAccountRights. Każdy krok idempotentny. Testy: W-42 (kolejka Windows).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInstallProvisioner : IInstallProvisioner
{
    public void Apply(InstallStep action)
    {
        ArgumentNullException.ThrowIfNull(action);
        switch (action)
        {
            case EnsureLocalGroup g: EnsureGroup(g); break;
            case EnsureDirectoryAcl d: EnsureDirectory(d); break;
            case SetRegistryDword r: SetDword(r); break;
            case RemoveRegistryValue r: RemoveValue(r); break;
            case GrantAccountRights a: ChangeRights(a.Account, a.Rights, grant: true); break;
            case RevokeAccountRights a: ChangeRights(a.Account, a.Rights, grant: false); break;
            default: throw new InstallStepException("Unknown install step.", 0);
        }
    }

    private static void EnsureGroup(EnsureLocalGroup g)
    {
        var name = Marshal.StringToHGlobalUni(g.Name);
        var comment = Marshal.StringToHGlobalUni(g.Description);
        try
        {
            var info = new InstallNative.LocalGroupInfo1 { Name = name, Comment = comment };
            var rc = InstallNative.NetLocalGroupAdd(IntPtr.Zero, 1, ref info, out _);
            if (rc is 0 or InstallNative.NerrGroupExists or InstallNative.ErrorAliasExists) return;
            throw new InstallStepException("Local group could not be created.", rc);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(comment);
        }
    }

    private static IdentityReference Principal(string p) =>
        p.StartsWith("S-1-", StringComparison.Ordinal) ? new SecurityIdentifier(p) : new NTAccount(p).Translate(typeof(SecurityIdentifier));

    private static void EnsureDirectory(EnsureDirectoryAcl d)
    {
        try
        {
            var info = new DirectoryInfo(d.Path);
            if (info.Exists && info.LinkTarget is not null) throw new InstallStepException("Data directory is a link — refusing to set ACL.", 0);
            info.Create();
            var sec = new DirectorySecurity();
            sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            sec.SetAccessRuleProtection(d.Protect, preserveInheritance: false);
            foreach (var e in d.Allow)
            {
                var rights = e.Right == DirectoryRight.FullControl ? FileSystemRights.FullControl : FileSystemRights.ReadAndExecute;
                sec.AddAccessRule(new FileSystemAccessRule(Principal(e.Principal), rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            info.SetAccessControl(sec);
        }
        catch (IdentityNotMappedException ex)
        {
            throw new InstallStepException("Account for directory ACL does not exist (service not installed?).", 1332, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            throw new InstallStepException("Directory ACL could not be set.", ex.HResult & 0xFFFF, ex);
        }
    }

    private static void SetDword(SetRegistryDword r)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(r.Key, writable: true) ?? throw new InstallStepException("Registry key could not be opened.", 0);
            key.SetValue(r.ValueName, r.Value, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            throw new InstallStepException("Registry value could not be set.", ex.HResult & 0xFFFF, ex);
        }
    }

    private static void RemoveValue(RemoveRegistryValue r)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(r.Key, writable: true);
            key?.DeleteValue(r.ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            throw new InstallStepException("Registry value could not be removed.", ex.HResult & 0xFFFF, ex);
        }
    }

    private static void ChangeRights(string account, IReadOnlyList<string> rights, bool grant)
    {
        SecurityIdentifier sid;
        try
        {
            sid = (SecurityIdentifier)Principal(account);
        }
        catch (IdentityNotMappedException ex)
        {
            if (!grant) return; // konto już nie istnieje (usługa usunięta) — nic do odebrania
            throw new InstallStepException("Service account does not exist (service not installed?).", 1332, ex);
        }
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var attrs = new InstallNative.LsaObjectAttributes { Length = Marshal.SizeOf<InstallNative.LsaObjectAttributes>() };
        var status = InstallNative.LsaOpenPolicy(IntPtr.Zero, ref attrs, InstallNative.PolicyCreateAccount | InstallNative.PolicyLookupNames, out var policy);
        if (status != 0) throw new InstallStepException("LSA policy could not be opened.", InstallNative.LsaNtStatusToWinError(status));
        var buffers = new List<IntPtr>();
        try
        {
            var strings = new InstallNative.LsaUnicodeString[rights.Count];
            for (var i = 0; i < rights.Count; i++)
            {
                var p = Marshal.StringToHGlobalUni(rights[i]);
                buffers.Add(p);
                strings[i] = new InstallNative.LsaUnicodeString { Length = (ushort)(rights[i].Length * 2), MaximumLength = (ushort)(rights[i].Length * 2 + 2), Buffer = p };
            }
            status = grant
                ? InstallNative.LsaAddAccountRights(policy, sidBytes, strings, (uint)strings.Length)
                : InstallNative.LsaRemoveAccountRights(policy, sidBytes, 0, strings, (uint)strings.Length);
            var win32 = status == 0 ? 0 : InstallNative.LsaNtStatusToWinError(status);
            if (win32 is 0 || (!grant && win32 == InstallNative.ErrorFileNotFound)) return; // odebranie: konto bez wpisów LSA = OK
            throw new InstallStepException(grant ? "Account rights could not be granted." : "Account rights could not be revoked.", win32);
        }
        finally
        {
            foreach (var b in buffers) Marshal.FreeHGlobal(b);
            _ = InstallNative.LsaClose(policy);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class InstallNative
{
    public const int NerrGroupExists = 2223;
    public const int ErrorAliasExists = 1379;
    public const int ErrorFileNotFound = 2;
    public const uint PolicyCreateAccount = 0x00000010;
    public const uint PolicyLookupNames = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LocalGroupInfo1
    {
        public IntPtr Name;
        public IntPtr Comment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("netapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int NetLocalGroupAdd(IntPtr serverName, int level, ref LocalGroupInfo1 buf, out int parmErr);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes objectAttributes, uint desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint LsaAddAccountRights(IntPtr policyHandle, byte[] accountSid, LsaUnicodeString[] userRights, uint countOfRights);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint LsaRemoveAccountRights(IntPtr policyHandle, byte[] accountSid, byte allRights, LsaUnicodeString[] userRights, uint countOfRights);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint LsaClose(IntPtr policyHandle);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int LsaNtStatusToWinError(uint status);
}
