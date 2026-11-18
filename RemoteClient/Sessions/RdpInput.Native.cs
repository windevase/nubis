using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>
/// RDP 입력 전달 — 창 메시지가 아닌 방식 (실서버 실험용): sendkeys = 컨트롤의 공식 IMsRdpClientNonScriptable.SendKeys,
/// sendinput = Windows 실제 입력(SendInput). sendinput 은 RDP 컨트롤이 포커스를 가진 채 Nubis 창이 앞에 있어야 하고,
/// 마우스는 실제 커서를 옮겼다가 끝나면 원래 자리로 되돌린다
/// </summary>
internal static partial class RdpInputSender
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT Mi; [FieldOffset(0)] public KEYBDINPUT Ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10,
        MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;

    private static void Check(RdpTarget ih, bool release)
    {
        if (!IsWindow(ih.Hwnd) || !(release ? ih.CanRelease() : ih.Alive()))
            throw new InvalidOperationException("탭이 닫혔거나 연결이 바뀌어 입력을 멈췄습니다.");
    }

    /// <summary>키 하나 (sendkeys: 공식 SendKeys, 확장 키는 스캔코드에 0x100 · sendinput: 스캔코드 실제 입력)</summary>
    private static void NativeKey(RdpTarget ih, RdpKeyEvent e, bool release)
    {
        Check(ih, release);
        if (ih.Method == RdpInputMethod.SendKeys)
        {
            var keys = ih.Keys ?? throw new InvalidOperationException("이 RDP 컨트롤은 SendKeys 를 지원하지 않습니다.");
            keys.SendKeys(1, new short[] { (short)(e.Up ? -1 : 0) }, new[] { e.Scan | (e.Extended ? 0x100 : 0) });
            return;
        }
        var flags = KEYEVENTF_SCANCODE | (e.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (e.Up ? KEYEVENTF_KEYUP : 0);
        Input(new INPUT { Type = INPUT_KEYBOARD, U = new INPUTUNION { Ki = new KEYBDINPUT { Scan = e.Scan, Flags = flags } } });
    }

    private static void Input(INPUT input)
    {
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException($"SendInput 실패 (오류 {Marshal.GetLastWin32Error()}) — Nubis 창이 앞에 있어야 합니다.");
    }

    private static (int X, int Y) ToScreen(RdpTarget ih, int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        ClientToScreen(ih.Hwnd, ref pt);
        return (pt.X, pt.Y);
    }

    /// <summary>화면 좌표로 커서 이동 (가상 데스크톱 전체 기준 절대 좌표)</summary>
    private static void MoveTo(int sx, int sy, uint extra = 0)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = Math.Max(1, GetSystemMetrics(78)), vh = Math.Max(1, GetSystemMetrics(79));
        var nx = (int)Math.Round((sx - vx) * 65535.0 / Math.Max(1, vw - 1));
        var ny = (int)Math.Round((sy - vy) * 65535.0 / Math.Max(1, vh - 1));
        Input(new INPUT { Type = INPUT_MOUSE, U = new INPUTUNION { Mi = new MOUSEINPUT { Dx = nx, Dy = ny, Flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | extra } } });
    }

    private static void MouseButton(uint flag) =>
        Input(new INPUT { Type = INPUT_MOUSE, U = new INPUTUNION { Mi = new MOUSEINPUT { Flags = flag } } });

    private static async Task NativeClickAsync(RdpTarget ih, int x, int y, string button, int count, CancellationToken ct)
    {
        var (down, up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        GetCursorPos(out var saved);
        try
        {
            Check(ih, false);
            var (sx, sy) = ToScreen(ih, x, y);
            MoveTo(sx, sy);
            await Task.Delay(60, ct);
            for (var i = 0; i < count; i++)
            {
                Check(ih, false);
                MouseButton(down);
                try { await Task.Delay(40, ct); }
                finally { try { MouseButton(up); } catch { } }
                if (i + 1 < count) await Task.Delay(60, ct);
            }
            await Task.Delay(60, ct);
        }
        finally { SetCursorPos(saved.X, saved.Y); }   // 사용자 커서를 원래 자리로
    }

    private static async Task NativeDragAsync(RdpTarget ih, (int X, int Y) from, (int X, int Y) to, CancellationToken ct)
    {
        GetCursorPos(out var saved);
        var downSent = false;
        try
        {
            Check(ih, false);
            var a = ToScreen(ih, from.X, from.Y);
            var b = ToScreen(ih, to.X, to.Y);
            MoveTo(a.X, a.Y);
            await Task.Delay(60, ct);
            MouseButton(MOUSEEVENTF_LEFTDOWN);
            downSent = true;
            const int steps = 12;
            for (var i = 1; i <= steps; i++)
            {
                await Task.Delay(20, ct);
                Check(ih, false);
                MoveTo(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps);
            }
            await Task.Delay(60, ct);
        }
        finally
        {
            if (downSent) { try { MouseButton(MOUSEEVENTF_LEFTUP); } catch { } }
            SetCursorPos(saved.X, saved.Y);
        }
    }

    private static async Task NativeScrollAsync(RdpTarget ih, (int X, int Y) client, int notches, CancellationToken ct)
    {
        GetCursorPos(out var saved);
        try
        {
            Check(ih, false);
            var (sx, sy) = ToScreen(ih, client.X, client.Y);
            MoveTo(sx, sy);
            await Task.Delay(60, ct);
            var dir = Math.Sign(notches);
            for (var i = 0; i < Math.Abs(notches); i++)
            {
                Check(ih, false);
                Input(new INPUT { Type = INPUT_MOUSE, U = new INPUTUNION { Mi = new MOUSEINPUT { MouseData = unchecked((uint)(120 * dir)), Flags = MOUSEEVENTF_WHEEL } } });
                await Task.Delay(40, ct);
            }
        }
        finally { SetCursorPos(saved.X, saved.Y); }
    }
}
