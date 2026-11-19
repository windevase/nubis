using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nubis;

/// <summary>
/// 앱이 끝나면 함께 끝나야 하는 장시간 자식 프로세스(kubectl port-forward 등)를 넣는 KILL_ON_JOB_CLOSE Job.
/// 핸들은 앱 수명 동안 열어 두고 닫지 않는다 — 앱이 정상 종료하든 비정상 종료하든 OS 가 핸들을 닫을 때 안의 프로세스가 모두 끝난다
/// (ConPty 의 PseudoConsoleProcess 와 같은 방식). Job 을 만들 수 없으면(드문 환경) 넣지 않고 false — 호출자가 직접 끝내야 한다
/// </summary>
public static class ProcessJob
{
    private static readonly object Gate = new();
    private static IntPtr _job;
    private static bool _failed;

    public static bool Add(Process p)
    {
        try
        {
            var job = Job();
            return job != IntPtr.Zero && AssignProcessToJobObject(job, p.Handle);
        }
        catch { return false; }
    }

    private static IntPtr Job()
    {
        lock (Gate)
        {
            if (_job != IntPtr.Zero || _failed) return _job;
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) { _failed = true; return IntPtr.Zero; }
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len)) return _job = job;
            }
            finally { Marshal.FreeHGlobal(ptr); }
            CloseHandle(job);
            _failed = true;
            return IntPtr.Zero;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}
