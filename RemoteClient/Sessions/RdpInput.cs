using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>입력을 보낼 RDP 입력 창과, 아직 같은 탭·같은 연결인지 확인하는 함수 (UI 스레드에서 부른다)</summary>
internal readonly record struct RdpTarget(IntPtr Hwnd, Func<bool> Alive, Func<bool> CanRelease)
{
    /// <summary>입력 전달 방식 (실험 중 — 실서버에서 통하는 방식을 고른다)</summary>
    public RdpInputMethod Method { get; init; }
    /// <summary>sendkeys 방식의 공식 키 전송 인터페이스</summary>
    public RdpAxHost.IMsRdpClientNonScriptable? Keys { get; init; }
}

/// <summary>
/// RDP 입력 전달 방식. message = 입력 창에 SendMessage · post = PostMessage · focus_post = 컨트롤에 포커스를 준 뒤 PostMessage ·
/// sendkeys = 키는 컨트롤의 공식 SendKeys(마우스는 focus_post) · sendinput = 실제 입력(SendInput — 창이 앞으로 오고 커서가 잠깐 움직였다 돌아옴)
/// </summary>
internal enum RdpInputMethod { Message, Post, FocusPost, SendKeys, SendInput }

/// <summary>원격으로 보낼 키 하나의 누름/뗌 (스캔코드 기준 — RDP 는 스캔코드를 보내고 원격의 자판 배열이 글자를 정한다)</summary>
internal readonly record struct RdpKeyEvent(ushort Vk, byte Scan, bool Extended, bool Up);

/// <summary>
/// Claude 의 RDP 화면 조작(rdp_key · rdp_type)용 키 해석. UI 와 무관 (테스트 대상).
/// 글자는 미국식(US) 배열의 스캔코드로 바꾼다 — 원격 Windows 의 한국어 자판도 영문 자리는 같다.
/// 원격 입력기가 한글 모드면 영문이 한글로 찍히므로, 입력 뒤 화면으로 확인하고 hangul 키로 바꿔야 한다.
/// </summary>
internal static class RdpKeys
{
    private readonly record struct Key(ushort Vk, byte Scan, bool Ext = false);

    // 수정 키 (누르는 순서대로)
    private static readonly Key Ctrl = new(0x11, 0x1D), Shift = new(0x10, 0x2A), Alt = new(0x12, 0x38), Win = new(0x5B, 0x5B, true);

    private static readonly Dictionary<string, Key> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = new(0x0D, 0x1C), ["return"] = new(0x0D, 0x1C),
        ["tab"] = new(0x09, 0x0F),
        ["esc"] = new(0x1B, 0x01), ["escape"] = new(0x1B, 0x01),
        ["backspace"] = new(0x08, 0x0E),
        ["space"] = new(0x20, 0x39),
        ["delete"] = new(0x2E, 0x53, true), ["del"] = new(0x2E, 0x53, true),
        ["insert"] = new(0x2D, 0x52, true), ["ins"] = new(0x2D, 0x52, true),
        ["home"] = new(0x24, 0x47, true), ["end"] = new(0x23, 0x4F, true),
        ["pageup"] = new(0x21, 0x49, true), ["pgup"] = new(0x21, 0x49, true),
        ["pagedown"] = new(0x22, 0x51, true), ["pgdn"] = new(0x22, 0x51, true),
        ["up"] = new(0x26, 0x48, true), ["down"] = new(0x28, 0x50, true),
        ["left"] = new(0x25, 0x4B, true), ["right"] = new(0x27, 0x4D, true),
        ["apps"] = new(0x5D, 0x5D, true), ["menu"] = new(0x5D, 0x5D, true),
        ["capslock"] = new(0x14, 0x3A),
        ["printscreen"] = new(0x2C, 0x37, true),
        // 한/영 전환: 한국어 자판의 한/영 키(VK_HANGUL), 오른쪽 Alt 를 한/영으로 쓰는 배열(101 종류 1)용 ralt 도 둔다
        ["hangul"] = new(0x15, 0x72), ["ralt"] = new(0xA5, 0x38, true),
        ["win"] = Win, ["ctrl"] = Ctrl, ["shift"] = Shift, ["alt"] = Alt,
    };

    private static readonly Dictionary<string, Key> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = Ctrl, ["control"] = Ctrl,
        ["shift"] = Shift,
        ["alt"] = Alt,
        ["win"] = Win, ["windows"] = Win, ["meta"] = Win, ["super"] = Win,
    };

    // US 배열: 글자 → (스캔코드, Shift 필요)
    private static readonly Dictionary<char, (byte Scan, bool Shift)> Chars = BuildChars();

    private static Dictionary<char, (byte, bool)> BuildChars()
    {
        var d = new Dictionary<char, (byte, bool)>();
        void Row(string plain, string shifted, byte first)
        {
            for (var i = 0; i < plain.Length; i++)
            {
                d[plain[i]] = ((byte)(first + i), false);
                if (shifted[i] != ' ') d[shifted[i]] = ((byte)(first + i), true);
            }
        }
        Row("1234567890-=", "!@#$%^&*()_+", 0x02);
        Row("qwertyuiop[]", "QWERTYUIOP{}", 0x10);
        Row("asdfghjkl;'`", "ASDFGHJKL:\"~", 0x1E);
        Row("\\zxcvbnm,./", "|ZXCVBNM<>?", 0x2B);
        d[' '] = (0x39, false);
        return d;
    }

    private static ushort VkOfChar(char c) => c switch
    {
        >= 'a' and <= 'z' => (ushort)char.ToUpperInvariant(c),
        >= 'A' and <= 'Z' => c,
        >= '0' and <= '9' => c,
        ' ' => 0x20,
        // 기호는 VK 가 배열마다 다르다 — mstscax 는 lParam 의 스캔코드를 보내므로 VK 는 참고값 (0 이면 MapVirtualKey 로 채운다)
        _ => 0,
    };

    /// <summary>win+글자 허용 목록: 실행 · 탐색기 · 바탕 화면 · 빠른 링크 메뉴 · 잠금 · 설정 · 검색 · 모두 최소화 · 작업 표시줄 앱</summary>
    private const string WinLetters = "redxlism1234567890";

    /// <summary>한 번에 보낼 수 있는 키 조합 수 · 글자 수</summary>
    public const int MaxCombos = 20, MaxText = 4000;

    /// <summary>
    /// "win+r", "ctrl+shift+esc", "alt+f4", "enter" 같은 키 조합 하나를 누름·뗌 순서로 바꾼다.
    /// 수정 키 없이 글자 키만 누르는 것(예: "a", "shift+a")은 거부한다 — 글자는 매번 승인을 받는 rdp_type 으로만 입력하게.
    /// 붙여넣기(ctrl+v, shift+insert)는 사용자 PC 의 클립보드 내용이 원격에 들어가므로 거부한다.
    /// </summary>
    public static List<RdpKeyEvent> ParseCombo(string combo)
    {
        // '+' 키 자체는 쓸 수 없다 ("ctrl++" 는 형식 오류 — 확대는 ctrl+= 와 같은 자리)
        var parts = (combo ?? "").Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0)) throw new ArgumentException($"키 조합 형식이 잘못됐습니다: '{combo}' (예: win+r, ctrl+shift+esc, enter)");

        var mods = new List<Key>();
        foreach (var m in parts[..^1])
        {
            if (!Modifiers.TryGetValue(m, out var mk)) throw new ArgumentException($"'{m}' 은 수정 키(ctrl·shift·alt·win)가 아닙니다: '{combo}'");
            if (!mods.Contains(mk)) mods.Add(mk);
        }

        var last = parts[^1];
        Key key;
        var isChar = false;
        if (Named.TryGetValue(last, out var named)) key = named;
        else if (FunctionKey(last) is { } fk) key = fk;
        else if (last.Length == 1 && Chars.TryGetValue(char.ToLowerInvariant(last[0]), out var ch) && !ch.Shift)
        {
            key = new Key(VkOfChar(char.ToLowerInvariant(last[0])), ch.Scan);
            isChar = true;
        }
        else throw new ArgumentException($"알 수 없는 키 '{last}' — 글자 1개, enter·tab·esc·backspace·delete·home·end·pageup·pagedown·up·down·left·right·f1~f12·win·apps·hangul 등");

        // 공백도 글자 — 단독 space 는 rdp_type 으로 (ctrl+space 같은 조합은 허용)
        if (key == Named["space"]) isChar = true;
        var hasCommandMod = mods.Contains(Ctrl) || mods.Contains(Alt) || mods.Contains(Win);
        if (isChar && !hasCommandMod)
            throw new ArgumentException($"'{combo}' 는 글자 입력입니다 — 글자는 rdp_type 으로 입력하세요 (rdp_key 는 ctrl·alt·win 조합과 기능 키용).");
        // AltGr 배열(독일어 등)에서는 ctrl+alt+글자가 글자(@, € 등)를 찍는다 — 글자 승인을 거치지 않는 입력이 되므로 막는다
        if (isChar && mods.Contains(Ctrl) && mods.Contains(Alt))
            throw new ArgumentException($"'{combo}' 는 일부 자판에서 글자를 입력하므로(AltGr) 쓸 수 없습니다. 글자는 rdp_type 으로 입력하세요.");
        var letter = isChar ? char.ToLowerInvariant(last[0]) : '\0';
        if (letter == 'v' && (mods.Contains(Ctrl) || mods.Contains(Win)) || key == Named["insert"] && mods.Contains(Shift))
            throw new ArgumentException("붙여넣기(ctrl+v · shift+insert · win+v 클립보드 기록)는 사용자 PC 의 클립보드 내용이 원격에 들어갈 수 있어 쓸 수 없습니다. 넣을 글자는 rdp_type 으로 입력하세요.");
        // 화상 키보드(ctrl+win+o)는 클릭만으로 글자를 승인 없이 입력하는 길이 된다
        if (letter == 'o' && mods.Contains(Ctrl) && mods.Contains(Win))
            throw new ArgumentException("화상 키보드(ctrl+win+o)는 열 수 없습니다. 글자는 rdp_type 으로 입력하세요.");
        // win+글자는 허용 목록만 — win+. / win+; (이모지·기호·클립보드 기록 패널), win+h (음성 입력) 처럼
        // 클릭만으로 글자를 넣거나 붙여넣는 패널을 여는 조합이 있다
        if (isChar && key != Named["space"] && mods.Contains(Win) && !WinLetters.Contains(letter))
            throw new ArgumentException($"'{combo}' 는 쓸 수 없습니다 — win+글자는 {string.Join(", ", WinLetters.Select(c => "win+" + c))} 만 허용합니다.");

        var ev = new List<RdpKeyEvent>();
        foreach (var m in mods) ev.Add(new(m.Vk, m.Scan, m.Ext, false));
        ev.Add(new(key.Vk, key.Scan, key.Ext, false));
        ev.Add(new(key.Vk, key.Scan, key.Ext, true));
        for (var i = mods.Count - 1; i >= 0; i--) ev.Add(new(mods[i].Vk, mods[i].Scan, mods[i].Ext, true));
        return ev;
    }

    private static Key? FunctionKey(string s)
    {
        if (s.Length is < 2 or > 3 || (s[0] != 'f' && s[0] != 'F') || !int.TryParse(s[1..], out var n) || n is < 1 or > 12) return null;
        var scan = n <= 10 ? (byte)(0x3A + n) : (byte)(0x57 + n - 11);
        return new Key((ushort)(0x6F + n), scan);
    }

    /// <summary>
    /// 글자를 누름·뗌 순서로 바꾼다. 줄바꿈(\n, \r\n)은 Enter, 탭은 Tab.
    /// US 배열로 칠 수 없는 글자(한글 등)는 ArgumentException — 무엇이 안 되는지 알려 준다.
    /// </summary>
    public static List<RdpKeyEvent> TextToEvents(string text)
    {
        text ??= "";
        if (text.Length > MaxText) throw new ArgumentException($"한 번에 입력할 수 있는 글자는 {MaxText}자까지입니다.");
        // 코드포인트(rune) 단위로 모은다 — 이모지처럼 서로게이트 쌍인 글자가 반쪽씩 깨져 보이지 않게
        var bad = text.EnumerateRunes()
            .Where(r => !(r.IsBmp && ((char)r.Value is '\n' or '\r' or '\t' || Chars.ContainsKey((char)r.Value))))
            .Distinct().Take(5).ToList();
        if (bad.Count > 0)
            throw new ArgumentException("영문 자판으로 입력할 수 없는 글자가 있습니다: " + string.Join(" ", bad.Select(Describe)) +
                                        " — RDP 입력은 영문·숫자·기호만 가능합니다 (한글 경로 등은 와일드카드·환경 변수로 피해 가세요).");
        var ev = new List<RdpKeyEvent>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') continue; c = '\n'; }
            if (c == '\n') { Tap(ev, Named["enter"]); continue; }
            if (c == '\t') { Tap(ev, Named["tab"]); continue; }
            var (scan, shift) = Chars[c];
            if (shift) ev.Add(new(Shift.Vk, Shift.Scan, false, false));
            Tap(ev, new Key(VkOfChar(c), scan));
            if (shift) ev.Add(new(Shift.Vk, Shift.Scan, false, true));
        }
        return ev;
    }

    private static void Tap(List<RdpKeyEvent> ev, Key k)
    {
        ev.Add(new(k.Vk, k.Scan, k.Ext, false));
        ev.Add(new(k.Vk, k.Scan, k.Ext, true));
    }

    private static string Describe(Rune r) => Rune.IsControl(r) || Rune.IsWhiteSpace(r) ? $"\\u{r.Value:X4}" : $"'{r}'";

    /// <summary>Enter 키 누름·뗌 (rdp_type 의 press_enter)</summary>
    public static IEnumerable<RdpKeyEvent> EnterEvents() => new[] { new RdpKeyEvent(0x0D, 0x1C, false, false), new RdpKeyEvent(0x0D, 0x1C, false, true) };

    /// <summary>
    /// 캡처 이미지 좌표 → 탭 화면의 물리 픽셀 좌표. 캡처 뒤 탭 크기가 바뀌었으면(창 크기 조절) null — 다시 캡처해야 한다.
    /// </summary>
    public static (int X, int Y)? ImageToTabPixels(double x, double y, int imageW, int imageH, int capturedW, int capturedH, int currentW, int currentH)
    {
        if (imageW < 1 || imageH < 1 || capturedW < 1 || capturedH < 1 || x < 0 || y < 0 || x >= imageW || y >= imageH) return null;
        if (Math.Abs(currentW - capturedW) > 2 || Math.Abs(currentH - capturedH) > 2) return null;
        var px = (int)Math.Floor(x * capturedW / imageW);
        var py = (int)Math.Floor(y * capturedH / imageH);
        return (Math.Clamp(px, 0, capturedW - 1), Math.Clamp(py, 0, capturedH - 1));
    }
}


/// <summary>
/// mstscax 의 입력 창(IHWindowClass)에 마우스·키 메시지를 보내 원격 세션에 입력한다.
/// 사용자의 실제 마우스 커서·키보드 포커스는 건드리지 않는다. UI 스레드에서 호출.
/// SendMessage 로 입력 창의 창 프로시저를 바로 부른다 — PostMessage 는 메시지 루프를 거치며 WPF·WinForms 의
/// 사전 처리(Tab·방향키 키보드 탐색 등)에 가로채일 수 있다.
/// 중간에 취소·실패해도 누른 키·버튼은 반드시 뗀다 (원격에 Ctrl·Win·마우스 버튼이 눌린 채 남지 않게).
/// </summary>
internal static partial class RdpInputSender
{
    private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const uint WM_MOUSEMOVE = 0x200, WM_MOUSEWHEEL = 0x20A;
    private const int MK_LBUTTON = 0x1, MK_RBUTTON = 0x2, MK_MBUTTON = 0x10;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rc);
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>RDP 컨트롤 안의 입력 창 (UIMainClass › UIContainerClass › IHWindowClass). 없으면 Zero</summary>
    public static IntPtr FindInputWindow(IntPtr control)
    {
        var found = IntPtr.Zero;
        var sb = new StringBuilder(64);
        EnumChildWindows(control, (h, _) =>
        {
            sb.Clear();
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "IHWindowClass") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>화면(물리 픽셀) 좌표 → 입력 창 안 좌표. 입력 창 밖이면 null</summary>
    public static (int X, int Y)? ScreenToInput(IntPtr ih, int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        if (!ScreenToClient(ih, ref pt) || !GetClientRect(ih, out var rc)) return null;
        if (pt.X < 0 || pt.Y < 0 || pt.X >= rc.Right || pt.Y >= rc.Bottom) return null;
        return (pt.X, pt.Y);
    }

    /// <summary>
    /// 키 이벤트 하나의 창 메시지 (UI 무관, 테스트 대상). altHeld/ctrlHeld = 이 이벤트를 보내기 전 Alt·Ctrl 이 눌려 있었는가.
    /// Alt 를 누른 동안(Ctrl 없이)과 Alt 키 자체는 실제 키보드처럼 WM_SYSKEY* — 그래야 Alt+F4 등이 시스템 키로 처리된다.
    /// lParam: 반복 1 · 비트 16-23 스캔코드 · 24 확장 키 · 29 Alt(컨텍스트) · 30 이전 상태 · 31 전환(뗌)
    /// </summary>
    internal static (uint Msg, IntPtr WParam, IntPtr LParam) BuildKeyMessage(RdpKeyEvent e, bool altHeld, bool ctrlHeld)
    {
        var isAlt = e.Vk == 0x12;
        // 비트 29: Alt 를 누른 동안의 키, Alt 누름 자체는 1 · Alt 를 뗄 때는 0 (실제 키보드: Alt 뗌 = 0xC0380001)
        var altContext = isAlt ? !e.Up : altHeld;
        var sys = (altHeld || isAlt) && !ctrlHeld;
        long l = 1 | ((long)e.Scan << 16) | (e.Extended ? 1L << 24 : 0) | (altContext ? 1L << 29 : 0);
        if (e.Up) l |= (1L << 30) | (1L << 31);
        var vk = e.Vk != 0 ? e.Vk : 0u;
        var msg = e.Up ? (sys ? WM_SYSKEYUP : WM_KEYUP) : (sys ? WM_SYSKEYDOWN : WM_KEYDOWN);
        return (msg, (IntPtr)vk, (IntPtr)unchecked((int)l));
    }

    private static IntPtr XY(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    /// <summary>
    /// 메시지 하나마다 대상이 아직 같은 탭·같은 연결인지 확인한다 — 긴 입력 도중 탭을 닫거나(컨트롤은 풀로 돌아가 다른 탭이 다시 쓸 수 있음)
    /// 다시 연결하면 남은 입력을 다른 서버·새 세션에 보내지 않고 멈춘다.
    /// release = 이미 누른 키·버튼을 떼는 메시지 — 같은 탭·같은 연결이면(자동 재연결 중·안내 화면이어도) 보낸다 (눌린 채 남지 않게)
    /// </summary>
    private static void Send(RdpTarget ih, uint msg, IntPtr w, IntPtr l, bool release = false)
    {
        if (!IsWindow(ih.Hwnd) || !(release ? ih.CanRelease() : ih.Alive()))
            throw new InvalidOperationException("탭이 닫혔거나 연결이 바뀌어 입력을 멈췄습니다.");
        if (ih.Method == RdpInputMethod.Message) SendMessage(ih.Hwnd, msg, w, l);
        else if (!PostMessage(ih.Hwnd, msg, w, l)) throw new InvalidOperationException("RDP 입력 창에 보내지 못했습니다.");
    }

    private static void SendKey(RdpTarget ih, RdpKeyEvent e, ref bool alt, ref bool ctrl, bool release = false)
    {
        // sendkeys · sendinput 은 창 메시지가 아니라 스캔코드로 보낸다 (Alt·Ctrl 상태는 원격이 따라간다)
        if (ih.Method is RdpInputMethod.SendKeys or RdpInputMethod.SendInput)
        {
            NativeKey(ih, e, release);
            if (e.Vk == 0x12) alt = !e.Up;
            if (e.Vk == 0x11) ctrl = !e.Up;
            return;
        }
        var (msg, w, l) = BuildKeyMessage(e, alt, ctrl);
        if (w == IntPtr.Zero) w = (IntPtr)MapVirtualKey(e.Scan, 1 /* MAPVK_VSC_TO_VK */);
        Send(ih, msg, w, l, release);
        if (e.Vk == 0x12) alt = !e.Up;
        if (e.Vk == 0x11) ctrl = !e.Up;
    }

    public static async Task KeysAsync(RdpTarget ih, IReadOnlyList<RdpKeyEvent> events, CancellationToken ct)
    {
        bool alt = false, ctrl = false;
        var held = new List<RdpKeyEvent>();   // 눌렀지만 아직 안 뗀 키 (누른 순서)
        try
        {
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                SendKey(ih, e, ref alt, ref ctrl);
                if (e.Up) held.RemoveAll(h => h.Scan == e.Scan && h.Extended == e.Extended);
                else held.Add(e);
                // 너무 빨리 몰아 보내면 원격(특히 실행 창·UAC 전환 직후)이 키를 놓친다
                await Task.Delay(e.Up ? 12 : 8, ct);
            }
        }
        finally
        {
            // 취소·실패로 중간에 끝났으면 누른 키를 역순으로 뗀다 (토큰 없이)
            for (var i = held.Count - 1; i >= 0; i--)
            {
                var h = held[i];
                try { SendKey(ih, h with { Up = true }, ref alt, ref ctrl, release: true); } catch { break; }
            }
        }
    }

    public static async Task ClickAsync(RdpTarget ih, int x, int y, string button, int count, CancellationToken ct)
    {
        if (ih.Method == RdpInputMethod.SendInput) { await NativeClickAsync(ih, x, y, button, count, ct); return; }
        var (down, up, mk) = button switch
        {
            "right" => (0x204u, 0x205u, MK_RBUTTON),
            "middle" => (0x207u, 0x208u, MK_MBUTTON),
            _ => (0x201u, 0x202u, MK_LBUTTON),
        };
        Send(ih, WM_MOUSEMOVE, IntPtr.Zero, XY(x, y));
        await Task.Delay(40, ct);
        for (var i = 0; i < count; i++)
        {
            Send(ih, down, (IntPtr)mk, XY(x, y));
            try { await Task.Delay(30, ct); }
            finally { try { Send(ih, up, IntPtr.Zero, XY(x, y), release: true); } catch { } }   // 취소돼도 버튼은 뗀다
            if (i + 1 < count) await Task.Delay(60, ct);   // 더블 클릭 간격 안
        }
    }

    public static async Task DragAsync(RdpTarget ih, (int X, int Y) from, (int X, int Y) to, CancellationToken ct)
    {
        if (ih.Method == RdpInputMethod.SendInput) { await NativeDragAsync(ih, from, to, ct); return; }
        Send(ih, WM_MOUSEMOVE, IntPtr.Zero, XY(from.X, from.Y));
        await Task.Delay(40, ct);
        Send(ih, 0x201, (IntPtr)MK_LBUTTON, XY(from.X, from.Y));
        var last = from;
        try
        {
            const int steps = 12;
            for (var i = 1; i <= steps; i++)
            {
                await Task.Delay(20, ct);
                last = (from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps);
                Send(ih, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, XY(last.X, last.Y));
            }
            await Task.Delay(40, ct);
        }
        finally
        {
            // 취소·실패해도 왼쪽 버튼은 뗀다 (원격에 끌기 상태가 남지 않게) — 그때까지 옮긴 위치에서
            try { Send(ih, 0x202, IntPtr.Zero, XY(last.X, last.Y), release: true); } catch { }
        }
    }

    /// <summary>휠: notches 양수 = 위로, 음수 = 아래로. WM_MOUSEWHEEL 의 좌표는 화면 좌표</summary>
    public static async Task ScrollAsync(RdpTarget ih, (int X, int Y) client, (int X, int Y) screen, int notches, CancellationToken ct)
    {
        if (ih.Method == RdpInputMethod.SendInput) { await NativeScrollAsync(ih, client, notches, ct); return; }
        Send(ih, WM_MOUSEMOVE, IntPtr.Zero, XY(client.X, client.Y));
        await Task.Delay(30, ct);
        var dir = Math.Sign(notches);
        for (var i = 0; i < Math.Abs(notches); i++)
        {
            Send(ih, WM_MOUSEWHEEL, (IntPtr)((120 * dir) << 16), XY(screen.X, screen.Y));
            await Task.Delay(30, ct);
        }
    }
}
