using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kobian.Application.Worker;
using Microsoft.Win32.SafeHandles;

namespace Kobian.Platform.Windows.Process;

/// <summary>
/// Obiekt zadania dla Workera (it. 106; NFR-19, SEC-2.5): KILL_ON_JOB_CLOSE (śmierć Agenta = śmierć Workera, VSS zwalnia migawkę),
/// limit pamięci procesu 150 MB, DIE_ON_UNHANDLED_EXCEPTION (bez okna WER), limit 1 procesu aktywnego (Worker nie tworzy potomków). Test: W-45.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessJob : IProcessJob
{
    public const long MemoryLimitBytes = 150L * 1024 * 1024;
    private const uint LimitActiveProcess = 0x00000008;
    private const uint LimitProcessMemory = 0x00000100;
    private const uint LimitDieOnUnhandledException = 0x00000400;
    private const uint LimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    private readonly SafeFileHandle _job;

    public WindowsProcessJob()
    {
        _job = JobNative.CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var info = new JobNative.ExtendedLimitInformation
        {
            Basic = new JobNative.BasicLimitInformation
            {
                LimitFlags = LimitKillOnJobClose | LimitProcessMemory | LimitDieOnUnhandledException | LimitActiveProcess,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = (UIntPtr)MemoryLimitBytes,
        };
        var size = Marshal.SizeOf<JobNative.ExtendedLimitInformation>();
        if (!JobNative.SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, (uint)size))
        {
            var err = Marshal.GetLastPInvokeError();
            _job.Dispose();
            throw new Win32Exception(err);
        }
    }

    public void Assign(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!JobNative.AssignProcessToJobObject(_job, process.Handle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Dispose() => _job.Dispose();
}

[SupportedOSPlatform("windows")]
internal static class JobNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
