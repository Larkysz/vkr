using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TransactionalWindows.Service.Processes;

[SupportedOSPlatform("windows")]
internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public KernelHandle() : base(true) { }
    internal KernelHandle(IntPtr raw) : base(true) => SetHandle(raw);
    protected override bool ReleaseHandle() => WindowsProcessNative.CloseHandle(handle);
}

[SupportedOSPlatform("windows")]
internal static class WindowsProcessNative
{
    internal const uint CreateSuspended = 0x00000004;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint KillOnJobClose = 0x00002000;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint Synchronize = 0x00100000;
    internal const uint TokenQuery = 0x0008;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;
    internal const int ErrorMoreData = 234;
    internal const uint NewProcess = 6;
    internal const uint ExitProcess = 7;
    internal const uint AbnormalExitProcess = 8;
    internal const uint ActiveProcessZero = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        internal uint Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, Reserved2Size;
        internal IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimitInformation
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimitInformation
    {
        internal BasicLimitInformation Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CompletionPortAssociation
    {
        internal IntPtr Key, Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccountingInformation
    {
        internal long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        internal uint PageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessBasicInformation
    {
        internal IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern KernelHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(KernelHandle job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(KernelHandle job, int informationClass, IntPtr information, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(KernelHandle job, KernelHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(KernelHandle process, KernelHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(KernelHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(KernelHandle process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(KernelHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern KernelHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(KernelHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(KernelHandle process, out uint code);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(KernelHandle process, uint flags, StringBuilder image, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(KernelHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern KernelHandle CreateIoCompletionPort(IntPtr file, IntPtr existingPort, UIntPtr key, uint concurrency);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetQueuedCompletionStatus(KernelHandle port, out uint message, out UIntPtr key,
        out IntPtr overlapped, uint milliseconds);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(KernelHandle process, int informationClass,
        out ProcessBasicInformation information, int size, IntPtr returnLength);

    internal static void Check(bool result, string operation)
    {
        if (!result) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    internal static void Configure<T>(KernelHandle job, int informationClass, T information) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, memory, false);
            Check(SetInformationJobObject(job, informationClass, memory, (uint)size), "SetInformationJobObject");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static AccountingInformation Accounting(KernelHandle job)
    {
        var size = Marshal.SizeOf<AccountingInformation>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Check(QueryInformationJobObject(job, 1, memory, (uint)size, IntPtr.Zero), "Query job accounting");
            return Marshal.PtrToStructure<AccountingInformation>(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static IReadOnlyList<uint> ActivePids(KernelHandle job)
    {
        for (var capacity = 16; capacity <= 4096; capacity *= 2)
        {
            var size = 8 + IntPtr.Size * capacity;
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(job, 3, memory, (uint)size, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorMoreData) continue;
                    throw new Win32Exception(error, "Query job process IDs");
                }
                var count = Marshal.ReadInt32(memory, 4);
                if (count < 0 || count > capacity) throw new InvalidDataException("Job PID list exceeded its buffer.");
                var result = new uint[count];
                for (var i = 0; i < count; i++) result[i] = checked((uint)Marshal.ReadIntPtr(memory, 8 + IntPtr.Size * i).ToInt64());
                return result;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new ProcessManagementException(ProcessErrorCode.ObservationIncomplete, "Job exceeds the 4096-process observation limit.");
    }

    internal static KernelHandle Own(IntPtr raw)
    {
        // The raw handles returned by CreateProcess are owned exactly once.
        return new KernelHandle(raw);
    }
}
