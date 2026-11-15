using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Nubis;

/// <summary>
/// 로컬 터미널(PowerShell·Claude 패널) 자식 프로세스 환경변수.
/// 앱을 다른 Claude Code 세션 안에서 띄웠을 때 상속되는 세션 전용 변수(CLAUDECODE, CLAUDE_CODE_SESSION_ID 등)는 뺀다.
/// 사용자가 영구 환경변수로 설정한 값(예: CLAUDE_CODE_USE_BEDROCK)은 그대로 둔다.
/// </summary>
public static class ChildEnvironment
{
    private static readonly string[] SessionPrefixes = { "CLAUDE", "ANTHROPIC", "MCP_" };

    /// <summary>제외할 변수인지 (테스트 가능하도록 영구 변수 목록을 인자로 받음)</summary>
    public static bool ShouldDrop(string name, ICollection<string> persistentNames) =>
        SessionPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !persistentNames.Contains(name);

    public static HashSet<string> PersistentNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment")) set.UnionWith(k?.GetValueNames() ?? Array.Empty<string>());
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                set.UnionWith(k?.GetValueNames() ?? Array.Empty<string>());
        }
        catch { }
        return set;
    }

    /// <summary>CreateProcess 용 유니코드 환경 블록 ("이름=값\0...\0\0", 이름순 정렬)</summary>
    public static string Build()
    {
        var persistent = PersistentNames();
        var vars = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => ((string)e.Key, (string?)e.Value ?? ""))
            .Where(v => !ShouldDrop(v.Item1, persistent))
            .OrderBy(v => v.Item1, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }
}

/// <summary>
/// Windows ConPTY(의사 콘솔)로 로컬 프로세스(PowerShell, claude 등)를 실행한다.
/// 출력은 VT 시퀀스가 포함된 UTF-8 이라 xterm.js 에 그대로 넘기면 된다.
/// </summary>
public sealed class PseudoConsoleProcess : IDisposable
{
    private IntPtr _hpc;
    private SafeFileHandle? _inputWrite, _outputRead;
    private FileStream? _input;
    private Process? _process;
    private IntPtr _hProcess, _hThread;
    private IntPtr _job;   // KILL_ON_JOB_CLOSE Job (PTY 수명 동안 유지, KillTreeOnClose 일 때만)
    // 읽기 스레드·입력 작업 줄에서도 읽는다
    private volatile bool _disposed;
    /// <summary>WaitExitAsync 가 시작됨 — 그때부터 프로세스 핸들은 그쪽이 닫는다</summary>
    private volatile bool _waitStarted;

    /// <summary>
    /// 닫을 때 하위 프로세스 트리까지 모두 끝낼지 (Claude 패널: cmd → node(claude) → MCP 자식 등이 남지 않게 KILL_ON_JOB_CLOSE Job 사용).
    /// false(로컬 PowerShell 탭)면 셸 프로세스만 끝내고 의사 콘솔을 닫는다 — 'code .'·Start-Process 로 띄운 GUI 앱은 탭을 닫아도 남는다.
    /// Start 전에 정한다.
    /// </summary>
    public bool KillTreeOnClose { get; init; }

    /// <summary>출력 (백그라운드 스레드에서 호출됨)</summary>
    public event Action<string>? Output;
    /// <summary>프로세스 종료 (종료 코드)</summary>
    public event Action<int>? Exited;

    public void Start(string commandLine, string workingDir, short cols, short rows)
    {
        // 실패하면 만든 파이프 끝을 바로 닫는다 (Dispose 가 _inputWrite·_outputRead 를 닫고, 의사 콘솔 쪽 끝은 여기서)
        SafeFileHandle? inputRead = null, outputWrite = null;
        try
        {
            if (!CreatePipe(out inputRead, out _inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            if (!CreatePipe(out _outputRead, out outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();

            var hr = CreatePseudoConsole(new COORD { X = Math.Max((short)1, cols), Y = Math.Max((short)1, rows) }, inputRead, outputWrite, 0, out _hpc);
            if (hr != 0) throw new Win32Exception(hr, L.T("CreatePseudoConsole 실패"));
        }
        finally
        {
            // 의사 콘솔이 복제해 가졌으므로(또는 실패) 이쪽 끝은 닫는다
            inputRead?.Dispose();
            outputWrite?.Dispose();
        }

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        si.lpAttributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();

            var env = Marshal.StringToHGlobalUni(ChildEnvironment.Build());
            try
            {
                // 일시 정지 상태로 만들어 Job 에 넣은 뒤 재개한다 — 넣기 전에 손자 프로세스(node 등)가 먼저 생기지 않게
                if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED, env, workingDir, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), L.F("프로세스 실행 실패: {0}", commandLine));
                _hProcess = pi.hProcess;
                _hThread = pi.hThread;
                try { if (KillTreeOnClose) _job = CreateKillOnCloseJob(pi.hProcess); }
                finally { ResumeThread(pi.hThread); }
                // 하위 프로세스까지 정리하기 위한 Process 객체 (이미 끝났으면 null — 종료 대기는 핸들로 함)
                try { _process = Process.GetProcessById(pi.dwProcessId); } catch { _process = null; }
            }
            finally { Marshal.FreeHGlobal(env); }
        }
        finally
        {
            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
        }

        _input = new FileStream(_inputWrite, FileAccess.Write);
        new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read" }.Start();
        _waitStarted = true;
        _ = WaitExitAsync();
    }

    /// <summary>
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE Job 을 만들어 프로세스를 넣는다. Job 핸들이 닫히면(Dispose, 앱이 비정상 종료돼 OS 가 핸들을 닫을 때도)
    /// 그 안의 프로세스 트리(cmd → node(claude) 등)가 모두 끝난다 — PID 재조회가 실패해 cmd 만 죽고 손자가 남는 일을 막는다.
    /// 실패하면(드물게 Job 중첩이 막힌 환경) IntPtr.Zero — 예전처럼 Kill(entireProcessTree) 로만 정리한다.
    /// </summary>
    private static IntPtr CreateKillOnCloseJob(IntPtr hProcess)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len) && AssignProcessToJobObject(job, hProcess))
                return job;
        }
        finally { Marshal.FreeHGlobal(ptr); }
        CloseHandle(job);
        return IntPtr.Zero;
    }

    private void ReadLoop()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var buf = new byte[16384];
        var chars = new char[16384 + 4];
        try
        {
            using var output = new FileStream(_outputRead!, FileAccess.Read);
            int n;
            var gate = OutputGate;
            while ((n = output.Read(buf, 0, buf.Length)) > 0)
            {
                var c = decoder.GetChars(buf, 0, n, chars, 0);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
                // 화면이 한참 밀려 있으면 다음 읽기를 미룬다 (닫히면 바로 빠져나온다)
                if (gate != null)
                    while (!_disposed && !gate(250)) { }
            }
        }
        catch { /* 파이프 닫힘 */ }
    }

    private async Task WaitExitAsync()
    {
        var code = 0;
        try
        {
            // CreateProcess 가 준 핸들로 직접 대기 (PID 재조회 경쟁 없음)
            // ManualResetEvent 의 SafeWaitHandle 을 바꿔 끼우면 생성자가 만든 이벤트 핸들이 GC 때까지 남는다 → 핸들만 감싸는 WaitHandle
            using var wait = new ProcessWaitHandle(_hProcess);
            var tcs = new TaskCompletionSource();
            var reg = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => tcs.TrySetResult(), null, Timeout.Infinite, true);
            await tcs.Task;
            reg.Unregister(null);
            if (GetExitCodeProcess(_hProcess, out var exit)) code = unchecked((int)exit);
        }
        catch { }
        // 프로세스가 끝나면 의사 콘솔을 닫아야 출력 파이프가 EOF 가 된다
        ClosePc();
        // 프로세스 핸들은 대기가 끝난 여기서 닫는다 (Dispose 가 먼저 닫으면 대기 중인 핸들이 무효가 됨)
        var h = Interlocked.Exchange(ref _hProcess, IntPtr.Zero);
        if (h != IntPtr.Zero) CloseHandle(h);
        Exited?.Invoke(code);
    }

    /// <summary>프로세스 핸들을 소유하지 않고 감싸는 대기 핸들 (닫기는 WaitExitAsync 가 직접)</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr hProcess) => SafeWaitHandle = new SafeWaitHandle(hProcess, ownsHandle: false);
    }

    /// <summary>키 입력을 순서대로 보내는 백그라운드 작업 줄 (프로세스가 입력을 읽지 않아 파이프가 가득 차도 UI 스레드가 멈추지 않게)</summary>
    private Task _writeChain = Task.CompletedTask;
    private readonly object _writeLock = new();

    /// <summary>입력 보내기 (어느 스레드에서나). 들어온 순서대로 백그라운드에서 쓴다</summary>
    public void Write(string text)
    {
        var input = _input;
        if (input == null || _disposed || text.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_writeLock)
            _writeChain = _writeChain.ContinueWith(_ =>
            {
                if (_disposed) return;
                try
                {
                    input.Write(bytes, 0, bytes.Length);
                    input.Flush();
                }
                catch { }
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// 출력을 넘기기 전에 화면이 따라올 때까지 기다리는 함수 (최대 대기 ms → 받아도 되면 true). 없으면 기다리지 않는다.
    /// 기다리는 동안 읽지 않으므로 파이프가 차서 프로세스가 출력을 잠시 멈춘다 (화면이 못 따라갈 만큼 쏟아질 때의 흐름 제어)
    /// </summary>
    public Func<int, bool>? OutputGate { get => _outputGate; init => _outputGate = value; }
    // Dispose 가 놓는다 (게이트는 화면(XtermView)의 메서드라, 프로세스 종료 대기가 남아 있는 동안 화면을 붙잡지 않게)
    private Func<int, bool>? _outputGate;

    /// <summary>
    /// Resize(UI 스레드)와 ClosePc(프로세스 종료 대기 스레드)가 겹치면 이미 닫힌(해제된) 의사 콘솔 핸들로
    /// ResizePseudoConsole 을 불러 앱이 죽을 수 있다 — 같은 잠금으로 묶는다
    /// </summary>
    private readonly object _pcLock = new();

    public void Resize(short cols, short rows)
    {
        if (cols <= 0 || rows <= 0) return;
        lock (_pcLock)
            if (_hpc != IntPtr.Zero) ResizePseudoConsole(_hpc, new COORD { X = cols, Y = rows });
    }

    private void ClosePc()
    {
        IntPtr h;
        lock (_pcLock)
        {
            h = _hpc;
            _hpc = IntPtr.Zero;
        }
        // ClosePseudoConsole 은 남은 출력이 다 읽힐 때까지 기다릴 수 있어 잠금 밖에서
        if (h != IntPtr.Zero) ClosePseudoConsole(h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // 트리 정리는 Claude 패널만. PowerShell 탭은 셸만 끝낸다 — 콘솔에 붙은 자식은 아래 ClosePc(의사 콘솔 닫기)로 끝나고,
            // 따로 창을 띄운 GUI 앱(code ., Start-Process notepad 등)은 남는다
            // 이미 끝난 프로세스는 건드리지 않는다 — 그 사이 WaitExitAsync 가 핸들을 닫아 값이 다른 핸들로 재사용됐을 수 있다
            if (_process != null) { if (!_process.HasExited) _process.Kill(entireProcessTree: KillTreeOnClose); }
            else if (_hProcess != IntPtr.Zero) TerminateProcess(_hProcess, 1);
        }
        catch { }
        // Job 을 닫으면 남은 하위 프로세스(Kill 이 놓친 손자 포함)가 모두 끝난다
        var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (job != IntPtr.Zero) CloseHandle(job);
        ClosePc();
        try { _input?.Dispose(); } catch { }
        // Start 가 FileStream 을 만들기 전에 실패했으면 _input 이 없어 입력 파이프 핸들을 직접 닫는다
        try { _inputWrite?.Dispose(); } catch { }
        try { _outputRead?.Dispose(); } catch { }
        // _hProcess 는 WaitExitAsync 가 종료 확인 후 닫는다. 프로세스는 만들었는데 Start 가 그 전에 실패했으면(대기가 시작되지 않음) 여기서 닫는다
        if (!_waitStarted)
        {
            var hp = Interlocked.Exchange(ref _hProcess, IntPtr.Zero);
            if (hp != IntPtr.Zero) CloseHandle(hp);
        }
        var ht = Interlocked.Exchange(ref _hThread, IntPtr.Zero);
        if (ht != IntPtr.Zero) CloseHandle(ht);
        _process?.Dispose();
        // 구독자(탭의 람다)를 놓는다 — 종료 대기가 끝나기 전까지 이 객체가 살아 있어도 화면을 붙잡지 않게.
        // Exited 는 WaitExitAsync 가 아직 부를 수 있지만, 닫는 쪽(LocalTerminalView)은 닫힌 뒤의 종료 알림을 쓰지 않는다
        Output = null;
        Exited = null;
        _outputGate = null;
    }

    // ---------------- Win32 ----------------
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
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
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
