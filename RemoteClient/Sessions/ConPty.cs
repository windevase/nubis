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
    private bool _disposed;

    /// <summary>출력 (백그라운드 스레드에서 호출됨)</summary>
    public event Action<string>? Output;
    /// <summary>프로세스 종료 (종료 코드)</summary>
    public event Action<int>? Exited;

    public void Start(string commandLine, string workingDir, short cols, short rows)
    {
        if (!CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out _outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inputRead, outputWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr, L.T("CreatePseudoConsole 실패"));
        // 의사 콘솔이 복제해 가졌으므로 이쪽 끝은 닫는다
        inputRead.Dispose();
        outputWrite.Dispose();

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
                if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, env, workingDir, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), L.F("프로세스 실행 실패: {0}", commandLine));
                _hProcess = pi.hProcess;
                _hThread = pi.hThread;
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
        _ = WaitExitAsync();
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
            while ((n = output.Read(buf, 0, buf.Length)) > 0)
            {
                var c = decoder.GetChars(buf, 0, n, chars, 0);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
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
            using var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_hProcess, ownsHandle: false) };
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

    public void Write(string text)
    {
        if (_input == null || _disposed) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
        catch { }
    }

    public void Resize(short cols, short rows)
    {
        if (_hpc != IntPtr.Zero) ResizePseudoConsole(_hpc, new COORD { X = cols, Y = rows });
    }

    private void ClosePc()
    {
        var h = Interlocked.Exchange(ref _hpc, IntPtr.Zero);
        if (h != IntPtr.Zero) ClosePseudoConsole(h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            else if (_hProcess != IntPtr.Zero) TerminateProcess(_hProcess, 1);
        }
        catch { }
        ClosePc();
        try { _input?.Dispose(); } catch { }
        try { _outputRead?.Dispose(); } catch { }
        // _hProcess 는 WaitExitAsync 가 종료 확인 후 닫는다
        if (_hThread != IntPtr.Zero) CloseHandle(_hThread);
        _process?.Dispose();
    }

    // ---------------- Win32 ----------------
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

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
