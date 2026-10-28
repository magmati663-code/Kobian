#if KOBIAN_WINDOWS
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Adapter Windows SEC-6.3 (it. 101): deskryptor folderu docelowego (właściciel, grupa, DACL — czytany kontem usługi) + token klienta pipe
/// (RunAsClient, poziom Identification wystarcza dla AccessCheck — bez SeImpersonatePrivilege, D2.12/Q44) → AccessCheck(FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY).
/// Dowiązanie jako folder docelowy, brak deskryptora, brak tokenu albo błąd API → Denied. Test: W-43.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeRestoreTargetAuthorizer : IRestoreTargetAuthorizer
{
    private const uint FileAddFile = 0x0002;
    private const uint FileAddSubdirectory = 0x0004;

    public RestoreTargetAccess Check(ServerCallContext context, string target)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe;
        if (pipe is null) return RestoreTargetAccess.Denied;
        var dir = new DirectoryInfo(target);
        if (!dir.Exists || dir.LinkTarget is not null) return RestoreTargetAccess.Denied;
        var sd = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group).GetSecurityDescriptorBinaryForm();
        var allowed = false;
        pipe.RunAsClient(() =>
        {
            using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            if (id.ImpersonationLevel < TokenImpersonationLevel.Identification) return; // token procesu zamiast klienta — fail-closed
            var mapping = new AccessCheckNative.GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF };
            var privileges = new byte[256];
            var privLength = (uint)privileges.Length;
            var desired = FileAddFile | FileAddSubdirectory;
            AccessCheckNative.MapGenericMask(ref desired, ref mapping);
            if (AccessCheckNative.AccessCheck(sd, id.AccessToken.DangerousGetHandle(), desired, ref mapping, privileges, ref privLength, out var granted, out var status)
                && status && (granted & desired) == desired)
                allowed = true;
        });
        return allowed ? RestoreTargetAccess.Allowed : RestoreTargetAccess.Denied;
    }
}

[SupportedOSPlatform("windows")]
internal static class AccessCheckNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GenericMapping
    {
        public uint Read;
        public uint Write;
        public uint Execute;
        public uint All;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AccessCheck(byte[] securityDescriptor, IntPtr clientToken, uint desiredAccess, ref GenericMapping genericMapping,
        byte[] privilegeSet, ref uint privilegeSetLength, out uint grantedAccess, [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void MapGenericMask(ref uint accessMask, ref GenericMapping genericMapping);
}
#endif
