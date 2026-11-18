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
/// - Esc 누름은 막고 '멈춤' 신호로 쓴다 (onEscape). Win 도 막는다 (RDP 키보드 훅 설정에 따라 원격으로 갈 수 있음)
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
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc;   // 필드로 잡아 GC 가 콜백을 수거하지 않게
    private readonly uint _pid = (uint)Environment.ProcessId;
    private readonly Action _onEscape;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;
    private int _blocked, _disposed, _abandoned;
    private readonly System.Collections.Generic.HashSet<uint> _heldBlocked = new();   // 훅 스레드에서만 — 막은 누름 중 아직 안 뗀 키 (자동 반복을 한 번만 세게)

    /// <summary>막은 키 누름 수</summary>
    public int Blocked => Volatile.Read(ref _blocked);

    /// <summary>훅을 걸었는가 — false 면 막지 못하므로 입력하지 않아야 한다</summary>
    public bool Active { get; }

    public RdpKeyboardGuard(Action onEscape)
    {
        _onEscape = onEscape;
        _proc = Callback;
        // ready 는 Dispose 하지 않는다 — 시간 초과 뒤 스레드가 늦게 Set 할 수 있다 (GC 가 정리)
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // 메시지 큐를 먼저 만든다 — 그래야 Dispose 의 WM_QUIT 이 언제든 도착한다
            _threadId = GetCurrentThreadId();
            var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            // 생성자가 기다리다 포기했으면(시간 초과)·이미 Dispose 됐으면 걸린 훅을 바로 풀고 끝낸다 — 키 차단이 영구히 남지 않게
            if (hook != IntPtr.Zero && Volatile.Read(ref _abandoned) == 1) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            _hook = hook;
            ready.Set();
            if (hook == IntPtr.Zero) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }   // WM_QUIT 까지 — 훅 콜백은 이 루프 안에서 불린다
            UnhookWindowsHookEx(hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "Nubis RDP key guard" };
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(3))) Volatile.Write(ref _abandoned, 1);
        Active = Volatile.Read(ref _abandoned) == 0 && _hook != IntPtr.Zero;
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
        // Win 도 막는다 — RDP 키보드 훅 설정이 "원격"(keyboardhook:i:1)이면 포커스를 가진 RDP 컨트롤이 Win 을 원격으로 넘겨 시작 메뉴가 열린다
        return (true, vk == 0x1B);
    }

    /// <summary>Ctrl·Shift·Alt·Win 이 지금 실제로 눌려 있는가 — 눌린 채 입력하면 SendKeys 가 그 상태를 따라 Claude 의 글자가 조합키가 된다</summary>
    internal static bool ModifierHeld() =>
        (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;

    /// <summary>이 PC 의 CapsLock 이 켜져 있는가 — RDP 는 포커스를 받을 때 이 상태를 원격에 맞춰 글자 대소문자가 뒤집힌다</summary>
    internal static bool CapsLockOn() => (GetKeyState(0x14) & 1) != 0;

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var ours = GetWindowThreadProcessId(GetForegroundWindow(), out var pid) != 0 && pid == _pid;
            var (block, stop) = Decide((uint)wParam, k.VkCode, (k.Flags & 0x10) != 0, ours);
            if (block)
            {
                // 꾹 눌러 생기는 자동 반복은 한 번만 센다 (뗄 때까지 같은 키)
                if (_heldBlocked.Add(k.VkCode))
                {
                    Interlocked.Increment(ref _blocked);
                    if (stop) ThreadPool.QueueUserWorkItem(_ => { try { _onEscape(); } catch { } });
                }
                return (IntPtr)1;
            }
            if ((uint)wParam is 0x101 or 0x105) _heldBlocked.Remove(k.VkCode);   // 뗌 — 다음 누름은 새로 센다
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Volatile.Write(ref _abandoned, 1);   // 스레드가 아직 훅을 걸기 전이면 걸자마자 스스로 푼다
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
