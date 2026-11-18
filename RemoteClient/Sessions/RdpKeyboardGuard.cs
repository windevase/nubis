using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nubis;

/// <summary>
/// Claude 가 RDP 에 입력하는 동안 사용자의 실제 키 누름을 막는다 (Nubis 가 앞에 있을 때만).
/// RDP 컨트롤은 키보드 포커스가 있어야 입력을 원격으로 넘기므로 그동안 포커스가 RDP 에 있는데, 그 사이 사용자가 Claude 패널 등에 치던 키가
/// 원격의 PowerShell 같은 창으로 새어 승인하지 않은 입력이 되지 않게 한다.
/// - Claude 의 키는 RDP 프로토콜로 바로 가는 공식 SendKeys 라 이 훅을 거치지 않는다
/// - 누름만 막고 뗌은 통과시킨다 — 뗌을 막으면 그 키가 로컬에서 눌린 채로 남는다 (SendKeys 는 로컬 수정 키 상태를 따라간다)
/// - Esc 누름은 막고 '멈춤' 신호로 쓴다 (onEscape). Win 키는 통과(창 모드에서는 로컬이 처리)
/// - 주입된 키(화상 키보드·자동 입력 도구)는 막지 않는다 — 실제 키보드 입력만
/// 훅은 전용 스레드에서 돈다 — UI 스레드가 바빠도 LowLevelHooksTimeout 으로 키가 새거나 훅이 조용히 제거되지 않게
/// </summary>
internal sealed class RdpKeyboardGuard : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_QUIT = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc;   // 필드로 잡아 GC 가 콜백을 수거하지 않게
    private readonly uint _pid = (uint)Environment.ProcessId;
    private readonly Action _onEscape;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;
    private int _blocked, _disposed;

    /// <summary>막은 키 누름 수</summary>
    public int Blocked => Volatile.Read(ref _blocked);

    /// <summary>훅을 걸었는가 — false 면 막지 못하므로 입력하지 않아야 한다</summary>
    public bool Active { get; }

    public RdpKeyboardGuard(Action onEscape)
    {
        _onEscape = onEscape;
        _proc = Callback;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }   // WM_QUIT 까지 — 훅 콜백은 이 루프 안에서 불린다
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "Nubis RDP key guard" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        Active = _hook != IntPtr.Zero;
    }

    /// <summary>
    /// 막을지 판정 (UI 무관 — 테스트 대상). message = WM_KEYDOWN 등, injected = LLKHF_INJECTED, ours = Nubis 가 앞에 있음.
    /// Block = 삼킴, Stop = Esc (멈춤 신호)
    /// </summary>
    internal static (bool Block, bool Stop) Decide(uint message, uint vk, bool injected, bool ours)
    {
        if (injected || !ours) return (false, false);
        var down = message is 0x100 or 0x104;   // WM_KEYDOWN · WM_SYSKEYDOWN — 뗌은 통과
        if (!down) return (false, false);
        if (vk is 0x5B or 0x5C) return (false, false);   // Win (창 모드에서는 로컬이 처리)
        return (true, vk == 0x1B);
    }

    /// <summary>Ctrl·Shift·Alt 가 지금 실제로 눌려 있는가 — 눌린 채 입력하면 SendKeys 가 그 상태를 따라 Claude 의 글자가 조합키가 된다</summary>
    internal static bool ModifierHeld() =>
        (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var ours = GetWindowThreadProcessId(GetForegroundWindow(), out var pid) != 0 && pid == _pid;
            var (block, stop) = Decide((uint)wParam, k.VkCode, (k.Flags & 0x10) != 0, ours);
            if (block)
            {
                if ((k.Flags & 0x80) == 0) Interlocked.Increment(ref _blocked);
                if (stop) ThreadPool.QueueUserWorkItem(_ => { try { _onEscape(); } catch { } });
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
