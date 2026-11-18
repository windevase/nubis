using System;
using System.Runtime.InteropServices;

namespace Nubis;

/// <summary>
/// Claude 가 RDP 에 입력하는 동안 사용자의 실제 키 입력을 막는다 (Nubis 가 앞에 있을 때만).
/// 그동안 RDP 컨트롤이 키보드 포커스를 가져야 입력이 원격으로 넘어가는데, 그 사이 사용자가 Claude 패널 등에 치던 키가
/// 원격의 PowerShell 같은 창으로 새어 승인하지 않은 입력이 되지 않게 한다.
/// Claude 의 키는 RDP 프로토콜로 바로 가는 공식 SendKeys 라 이 훅을 거치지 않는다 — 막히는 것은 실제 키보드 입력뿐이다.
/// 저수준 키보드 훅(WH_KEYBOARD_LL) — 설치한 UI 스레드가 메시지를 돌리는 동안만 불린다. Dispose 로 바로 해제
/// </summary>
internal sealed class RdpKeyboardGuard : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc;   // 필드로 잡아 GC 가 콜백을 수거하지 않게
    private IntPtr _hook;
    private readonly uint _pid = (uint)Environment.ProcessId;

    /// <summary>막은 키 수 (사용자에게 알릴 때)</summary>
    public int Blocked { get; private set; }

    public RdpKeyboardGuard()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    /// <summary>훅을 걸었는가 (실패하면 막지 못한다)</summary>
    public bool Active => _hook != IntPtr.Zero;

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // 실제 키(주입되지 않은 키)이고 Nubis 가 앞에 있으면 막는다 — 다른 앱에서 치는 키는 그대로
            if ((k.Flags & LLKHF_INJECTED) == 0 && GetWindowThreadProcessId(GetForegroundWindow(), out var pid) != 0 && pid == _pid)
            {
                Blocked++;
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
