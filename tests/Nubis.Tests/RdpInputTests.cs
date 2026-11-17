using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>Claude 의 RDP 화면 조작(rdp_key · rdp_type · 좌표 환산) — RdpKeys</summary>
public class RdpInputTests
{
    private static RdpKeyEvent Down(ushort vk, byte scan, bool ext = false) => new(vk, scan, ext, false);
    private static RdpKeyEvent Up(ushort vk, byte scan, bool ext = false) => new(vk, scan, ext, true);

    // ── ParseCombo: 허용 ──

    [Fact]
    public void win_r_은_Win_누름_R_누름_R_뗌_Win_뗌()
    {
        var ev = RdpKeys.ParseCombo("win+r");
        Assert.Equal(new[] { Down(0x5B, 0x5B, true), Down((ushort)'R', 0x13), Up((ushort)'R', 0x13), Up(0x5B, 0x5B, true) }, ev);
    }

    [Fact]
    public void ctrl_shift_esc_는_수정키를_순서대로_누르고_역순으로_뗀다()
    {
        var ev = RdpKeys.ParseCombo("ctrl+shift+esc");
        Assert.Equal(new[]
        {
            Down(0x11, 0x1D), Down(0x10, 0x2A),
            Down(0x1B, 0x01), Up(0x1B, 0x01),
            Up(0x10, 0x2A), Up(0x11, 0x1D),
        }, ev);
    }

    [Fact]
    public void alt_f4()
    {
        var ev = RdpKeys.ParseCombo("alt+f4");
        Assert.Equal(new[] { Down(0x12, 0x38), Down(0x73, 0x3E), Up(0x73, 0x3E), Up(0x12, 0x38) }, ev);
    }

    [Theory]
    [InlineData("f1", 0x3B)]
    [InlineData("f4", 0x3E)]
    [InlineData("f10", 0x44)]
    [InlineData("f11", 0x57)]
    [InlineData("f12", 0x58)]
    [InlineData("F11", 0x57)]
    public void 기능키_스캔코드(string combo, int scan)
    {
        var ev = RdpKeys.ParseCombo(combo);
        Assert.Equal(2, ev.Count);
        Assert.Equal((byte)scan, ev[0].Scan);
        Assert.False(ev[0].Up);
        Assert.True(ev[1].Up);
    }

    [Theory]
    [InlineData("enter", 0x1C, false)]
    [InlineData("tab", 0x0F, false)]
    [InlineData("esc", 0x01, false)]
    [InlineData("up", 0x48, true)]
    public void 기능키는_단독으로_허용(string combo, int scan, bool ext)
    {
        var ev = RdpKeys.ParseCombo(combo);
        Assert.Equal(2, ev.Count);
        Assert.All(ev, e => { Assert.Equal((byte)scan, e.Scan); Assert.Equal(ext, e.Extended); });
        Assert.False(ev[0].Up);
        Assert.True(ev[1].Up);
    }

    [Fact]
    public void 대소문자를_무시한다()
    {
        Assert.Equal(RdpKeys.ParseCombo("ctrl+alt+delete"), RdpKeys.ParseCombo("Ctrl+Alt+Delete"));
        var ev = RdpKeys.ParseCombo("Ctrl+Alt+Delete");
        Assert.Equal(new[]
        {
            Down(0x11, 0x1D), Down(0x12, 0x38),
            Down(0x2E, 0x53, true), Up(0x2E, 0x53, true),
            Up(0x12, 0x38), Up(0x11, 0x1D),
        }, ev);
    }

    [Fact]
    public void 같은_수정키_중복은_한_번만()
    {
        Assert.Equal(RdpKeys.ParseCombo("ctrl+r"), RdpKeys.ParseCombo("ctrl+ctrl+r"));
        Assert.Equal(RdpKeys.ParseCombo("ctrl+r"), RdpKeys.ParseCombo("control+Ctrl+r"));
        Assert.Equal(4, RdpKeys.ParseCombo("ctrl+ctrl+r").Count);
    }

    [Theory]
    [InlineData("ctrl+c")]
    [InlineData("ctrl+a")]
    [InlineData("win+e")]
    [InlineData("alt+tab")]
    public void 명령_조합은_허용(string combo)
    {
        var ev = RdpKeys.ParseCombo(combo);
        Assert.Equal(4, ev.Count);
    }

    // ── ParseCombo: 거부 ──

    [Theory]
    [InlineData("a")]
    [InlineData("shift+a")]
    [InlineData("shift+1")]
    [InlineData("")]
    [InlineData("ctrl+")]
    [InlineData("foo+r")]
    [InlineData("ctrl+xyz")]
    [InlineData("ctrl+v")]
    [InlineData("CTRL+V")]
    [InlineData("ctrl+shift+v")]
    [InlineData("shift+insert")]
    public void 거부되는_조합(string combo) => Assert.Throws<ArgumentException>(() => RdpKeys.ParseCombo(combo));

    [Fact]
    public void null_은_거부() => Assert.Throws<ArgumentException>(() => RdpKeys.ParseCombo(null!));

    // ── TextToEvents ──

    [Fact]
    public void aB1_느낌표_는_Shift_로_감싼다()
    {
        var ev = RdpKeys.TextToEvents("aB1!");
        RdpKeyEvent ShD() => Down(0x10, 0x2A);
        RdpKeyEvent ShU() => Up(0x10, 0x2A);
        var expected = new[]
        {
            Down((ushort)'A', 0x1E), Up((ushort)'A', 0x1E),                 // a
            ShD(), Down((ushort)'B', 0x30), Up((ushort)'B', 0x30), ShU(),   // B
            Down((ushort)'1', 0x02), Up((ushort)'1', 0x02),                 // 1
            ShD(), Down(0, 0x02), Up(0, 0x02), ShU(),                       // ! (기호는 VK 0)
        };
        Assert.Equal(expected, ev);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void 줄바꿈은_Enter_한_번(string text)
    {
        var ev = RdpKeys.TextToEvents(text);
        Assert.Equal(new[] { Down(0x0D, 0x1C), Up(0x0D, 0x1C) }, ev);
    }

    [Fact]
    public void 여러_줄의_CRLF_는_줄마다_Enter_한_번()
    {
        var ev = RdpKeys.TextToEvents("a\r\nb\r\n");
        Assert.Equal(2, ev.Count(e => e.Scan == 0x1C && !e.Up));
    }

    [Fact]
    public void 탭과_공백()
    {
        Assert.Equal(new[] { Down(0x09, 0x0F), Up(0x09, 0x0F) }, RdpKeys.TextToEvents("\t"));
        Assert.Equal(new[] { Down(0x20, 0x39), Up(0x20, 0x39) }, RdpKeys.TextToEvents(" "));
    }

    [Fact]
    public void 출력_이벤트는_키마다_누름_뗌_짝이_맞는다()
    {
        var sb = new StringBuilder();
        for (var c = 0x20; c <= 0x7E; c++) sb.Append((char)c);
        sb.Append("\r\n\t\nHello, World! ~{}|\"<>?");
        var ev = RdpKeys.TextToEvents(sb.ToString());
        foreach (var g in ev.GroupBy(e => (e.Scan, e.Extended)))
            Assert.Equal(g.Count(e => !e.Up), g.Count(e => e.Up));

        // 순서상으로도 뗌이 누름보다 앞서거나 같은 키를 두 번 누른 채로 있지 않는다
        var held = new HashSet<(byte, bool)>();
        foreach (var e in ev)
        {
            if (e.Up) Assert.True(held.Remove((e.Scan, e.Extended)));
            else Assert.True(held.Add((e.Scan, e.Extended)));
        }
        Assert.Empty(held);
    }

    [Theory]
    [InlineData("가")]
    [InlineData("abc가def")]
    [InlineData("Ａ")]      // 전각 A
    [InlineData("！")]      // 전각 느낌표
    public void 영문_자판에_없는_글자는_거부하고_메시지에_그_글자를_넣는다(string text)
    {
        var bad = text.First(c => c > 0x7E).ToString();
        var ex = Assert.Throws<ArgumentException>(() => RdpKeys.TextToEvents(text));
        Assert.Contains(bad, ex.Message);
    }

    [Fact]
    public void 이모지는_거부하고_메시지에_그_글자를_넣는다()
    {
        const string emoji = "\U0001F600";   // 😀 (서로게이트 쌍)
        var ex = Assert.Throws<ArgumentException>(() => RdpKeys.TextToEvents("hi " + emoji));
        Assert.Contains(emoji, ex.Message);
    }

    [Fact]
    public void 최대_길이()
    {
        Assert.Equal(RdpKeys.MaxText * 2, RdpKeys.TextToEvents(new string('a', RdpKeys.MaxText)).Count);
        Assert.Throws<ArgumentException>(() => RdpKeys.TextToEvents(new string('a', RdpKeys.MaxText + 1)));
    }

    [Fact]
    public void 출력_가능한_ASCII_는_전부_변환된다()
    {
        for (var c = 0x20; c <= 0x7E; c++)
        {
            var ev = RdpKeys.TextToEvents(((char)c).ToString());
            Assert.True(ev.Count is 2 or 4, $"0x{c:X2}");
        }
    }

    [Fact]
    public void 빈_문자열과_null_은_이벤트_없음()
    {
        Assert.Empty(RdpKeys.TextToEvents(""));
        Assert.Empty(RdpKeys.TextToEvents(null!));
    }

    // ── ImageToTabPixels ──

    [Fact]
    public void 축소_캡처_좌표를_탭_픽셀로_환산()
    {
        Assert.Equal((200, 100), RdpKeys.ImageToTabPixels(100, 50, 800, 450, 1600, 900, 1600, 900));
        Assert.Equal((0, 0), RdpKeys.ImageToTabPixels(0, 0, 800, 450, 1600, 900, 1600, 900));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(800, 10)]
    [InlineData(10, 450)]
    [InlineData(-0.1, 10)]
    public void 이미지_밖_좌표는_null(double x, double y) =>
        Assert.Null(RdpKeys.ImageToTabPixels(x, y, 800, 450, 1600, 900, 1600, 900));

    [Theory]
    [InlineData(1603, 900)]
    [InlineData(1597, 900)]
    [InlineData(1600, 903)]
    [InlineData(1600, 897)]
    public void 캡처_뒤_크기가_3px_이상_바뀌면_null(int curW, int curH) =>
        Assert.Null(RdpKeys.ImageToTabPixels(100, 50, 800, 450, 1600, 900, curW, curH));

    [Theory]
    [InlineData(1602, 900)]
    [InlineData(1598, 898)]
    [InlineData(1601, 902)]
    public void 크기_차이_2px_이내는_허용(int curW, int curH) =>
        Assert.Equal((200, 100), RdpKeys.ImageToTabPixels(100, 50, 800, 450, 1600, 900, curW, curH));

    [Fact]
    public void 결과는_캡처_크기_안으로_클램프()
    {
        var r = RdpKeys.ImageToTabPixels(799.9999, 449.9999, 800, 450, 1600, 900, 1600, 900);
        Assert.NotNull(r);
        Assert.True(r!.Value.X <= 1599);
        Assert.True(r.Value.Y <= 899);
        Assert.Equal((1599, 899), r.Value);

        // 확대 캡처(이미지가 탭보다 큼)에서도 마지막 픽셀을 넘지 않는다
        var big = RdpKeys.ImageToTabPixels(1999.5, 999.5, 2000, 1000, 1000, 500, 1000, 500);
        Assert.Equal((999, 499), big);
    }

    [Fact]
    public void 이미지_크기가_0_이면_null() =>
        Assert.Null(RdpKeys.ImageToTabPixels(0, 0, 0, 0, 1600, 900, 1600, 900));

    [Fact]
    public void 가로_세로_비율이_다른_축소도_축마다_환산()
    {
        // 이미지 800x600 ← 캡처 1600x900 : 가로 2배, 세로 1.5배
        Assert.Equal((200, 300), RdpKeys.ImageToTabPixels(100, 200, 800, 600, 1600, 900, 1600, 900));
        Assert.Equal((1599, 899), RdpKeys.ImageToTabPixels(799.9, 599.9, 800, 600, 1600, 900, 1600, 900));
        Assert.Equal((1, 1), RdpKeys.ImageToTabPixels(0.5, 0.9, 800, 600, 1600, 900, 1600, 900));
    }

    [Fact]
    public void 캡처_크기가_0_이면_예외_없이_null()
    {
        (int, int)? r = null;
        var ex = Record.Exception(() => r = RdpKeys.ImageToTabPixels(10, 10, 800, 450, 0, 0, 0, 0));
        Assert.Null(ex);
        Assert.Null(r);
        ex = Record.Exception(() => r = RdpKeys.ImageToTabPixels(10, 10, 800, 450, 0, 900, 0, 900));
        Assert.Null(ex);
        Assert.Null(r);
    }

    // ── TextToEvents 경계 ──

    [Fact]
    public void 중간의_CR_단독은_Enter_한_번()
    {
        var ev = RdpKeys.TextToEvents("a\rb");
        Assert.Equal(1, ev.Count(e => e.Scan == 0x1C && !e.Up));
        Assert.Equal(new[]
        {
            Down((ushort)'A', 0x1E), Up((ushort)'A', 0x1E),
            Down(0x0D, 0x1C), Up(0x0D, 0x1C),
            Down((ushort)'B', 0x30), Up((ushort)'B', 0x30),
        }, ev);
    }

    [Fact]
    public void CR_CR_LF_는_Enter_두_번()
    {
        var ev = RdpKeys.TextToEvents("\r\r\n");
        Assert.Equal(new[] { Down(0x0D, 0x1C), Up(0x0D, 0x1C), Down(0x0D, 0x1C), Up(0x0D, 0x1C) }, ev);
    }

    // ── ParseCombo: 추가 거부 ──

    [Theory]
    // 공백은 글자
    [InlineData("space")]
    [InlineData("SPACE")]
    [InlineData("shift+space")]
    // AltGr (ctrl+alt+글자)
    [InlineData("ctrl+alt+a")]
    [InlineData("alt+ctrl+a")]
    [InlineData("ctrl+shift+alt+a")]
    [InlineData("ctrl+alt+1")]
    [InlineData("ctrl+alt+;")]
    [InlineData("ctrl+alt+space")]
    // 클립보드 기록
    [InlineData("win+v")]
    [InlineData("WIN+V")]
    [InlineData("win+shift+v")]
    [InlineData("ctrl+win+v")]
    // 화상 키보드
    [InlineData("ctrl+win+o")]
    [InlineData("win+ctrl+O")]
    // 형식 오류
    [InlineData("ctrl++")]
    [InlineData("+")]
    [InlineData("ctrl+ +r")]
    // 기능 키 범위
    [InlineData("f0")]
    [InlineData("f13")]
    [InlineData("f1a")]
    // Shift 가 필요한 기호
    [InlineData("ctrl+!")]
    // 붙여넣기
    [InlineData("shift+ins")]
    public void 추가로_거부되는_조합(string combo) => Assert.Throws<ArgumentException>(() => RdpKeys.ParseCombo(combo));

    [Theory]
    [InlineData("win+.")]   // 이모지 패널
    [InlineData("win+;")]   // 이모지 패널
    [InlineData("win+h")]   // 음성 입력
    [InlineData("win+v")]   // 클립보드 기록
    [InlineData("win+c")]
    [InlineData("win+k")]
    [InlineData("win+w")]
    public void win_글자는_허용_목록_밖이면_거부(string combo) => Assert.Throws<ArgumentException>(() => RdpKeys.ParseCombo(combo));

    // ── ParseCombo: 추가 허용 ──

    [Theory]
    [InlineData("ctrl+space")]
    [InlineData("win+space")]
    [InlineData("alt+space")]
    [InlineData("ctrl+alt+f1")]
    [InlineData("ctrl+alt+tab")]
    [InlineData("ctrl+insert")]
    [InlineData("shift+tab")]
    [InlineData("shift+delete")]
    [InlineData("alt+v")]
    [InlineData("ctrl+shift+esc")]
    [InlineData("hangul")]
    [InlineData("ralt")]
    [InlineData("win")]
    [InlineData("capslock")]
    [InlineData("win+up")]
    [InlineData("win+down")]
    [InlineData("win+left")]
    [InlineData("win+right")]
    [InlineData("win+tab")]
    public void 추가로_허용되는_조합(string combo)
    {
        var ev = RdpKeys.ParseCombo(combo);
        Assert.NotEmpty(ev);
        Assert.Equal(ev.Count(e => !e.Up), ev.Count(e => e.Up));
    }

    [Theory]
    [InlineData("win+r")]
    [InlineData("win+e")]
    [InlineData("win+d")]
    [InlineData("win+x")]
    [InlineData("win+l")]
    [InlineData("win+i")]
    [InlineData("win+s")]
    [InlineData("win+m")]
    [InlineData("win+1")]
    [InlineData("win+2")]
    [InlineData("win+3")]
    [InlineData("win+4")]
    [InlineData("win+5")]
    [InlineData("win+6")]
    [InlineData("win+7")]
    [InlineData("win+8")]
    [InlineData("win+9")]
    [InlineData("win+0")]
    public void win_글자_허용_목록(string combo) => Assert.Equal(4, RdpKeys.ParseCombo(combo).Count);

    // ── RdpInputSender.BuildKeyMessage ──

    private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;

    private static uint Bits(IntPtr p) => unchecked((uint)p.ToInt64());
    private static bool Bit(IntPtr p, int n) => (Bits(p) & (1u << n)) != 0;

    [Fact]
    public void 일반_키_누름은_KEYDOWN_반복1_스캔코드_나머지_비트0()
    {
        var (msg, w, l) = RdpInputSender.BuildKeyMessage(Down((ushort)'A', 0x1E), false, false);
        Assert.Equal(WM_KEYDOWN, msg);
        Assert.Equal((IntPtr)'A', w);
        Assert.Equal(1u | (0x1Eu << 16), Bits(l));
        Assert.False(Bit(l, 24));
        Assert.False(Bit(l, 29));
        Assert.False(Bit(l, 30));
        Assert.False(Bit(l, 31));
    }

    [Fact]
    public void 일반_키_뗌은_KEYUP_비트30_31()
    {
        var (msg, _, l) = RdpInputSender.BuildKeyMessage(Up((ushort)'A', 0x1E), false, false);
        Assert.Equal(WM_KEYUP, msg);
        Assert.True(Bit(l, 30));
        Assert.True(Bit(l, 31));
        Assert.False(Bit(l, 29));
        Assert.True(l.ToInt64() < 0);
        Assert.Equal(0x1Eu, (Bits(l) >> 16) & 0xFF);
    }

    [Fact]
    public void 확장_키는_비트24()
    {
        Assert.True(Bit(RdpInputSender.BuildKeyMessage(Down(0x5B, 0x5B, true), false, false).LParam, 24));
        Assert.True(Bit(RdpInputSender.BuildKeyMessage(Up(0x5B, 0x5B, true), false, false).LParam, 24));
        var del = RdpKeys.ParseCombo("ctrl+alt+delete").First(e => e.Vk == 0x2E);
        Assert.True(Bit(RdpInputSender.BuildKeyMessage(del, false, false).LParam, 24));
    }

    [Fact]
    public void Alt_누름은_SYSKEYDOWN_비트29()
    {
        var (msg, w, l) = RdpInputSender.BuildKeyMessage(Down(0x12, 0x38), false, false);
        Assert.Equal(WM_SYSKEYDOWN, msg);
        Assert.Equal((IntPtr)0x12, w);
        Assert.True(Bit(l, 29));
    }

    [Fact]
    public void Alt_뗌은_SYSKEYUP_비트29_0()
    {
        var (msg, _, l) = RdpInputSender.BuildKeyMessage(Up(0x12, 0x38), true, false);
        Assert.Equal(WM_SYSKEYUP, msg);
        Assert.False(Bit(l, 29));   // 실제 키보드처럼 Alt 자체를 뗄 때는 컨텍스트 비트가 꺼진다
        Assert.True(Bit(l, 30));
        Assert.True(Bit(l, 31));
    }

    [Fact]
    public void Alt_누른_채_F4_는_SYSKEY_비트29()
    {
        var down = RdpInputSender.BuildKeyMessage(Down(0x73, 0x3E), true, false);
        Assert.Equal(WM_SYSKEYDOWN, down.Msg);
        Assert.True(Bit(down.LParam, 29));
        var up = RdpInputSender.BuildKeyMessage(Up(0x73, 0x3E), true, false);
        Assert.Equal(WM_SYSKEYUP, up.Msg);
        Assert.True(Bit(up.LParam, 29));
    }

    [Fact]
    public void Ctrl_누른_채_Alt_는_시스템_키가_아니다()
    {
        var (msg, _, l) = RdpInputSender.BuildKeyMessage(Down(0x12, 0x38), false, true);
        Assert.Equal(WM_KEYDOWN, msg);
        Assert.True(Bit(l, 29));
        var del = RdpInputSender.BuildKeyMessage(Down(0x2E, 0x53, true), true, true);
        Assert.Equal(WM_KEYDOWN, del.Msg);
    }

    [Fact]
    public void 기호_VK_0_은_WParam_0()
    {
        var (_, w, l) = RdpInputSender.BuildKeyMessage(Down(0, 0x02), false, false);
        Assert.Equal(IntPtr.Zero, w);
        Assert.Equal(0x02u, (Bits(l) >> 16) & 0xFF);
    }

    /// <summary>KeysAsync 와 같은 방식으로 Alt·Ctrl 상태를 따라가며 메시지 종류를 만든다 (상태는 메시지를 만든 뒤 갱신)</summary>
    private static uint[] Messages(string combo)
    {
        bool alt = false, ctrl = false;
        var r = new List<uint>();
        foreach (var e in RdpKeys.ParseCombo(combo))
        {
            r.Add(RdpInputSender.BuildKeyMessage(e, alt, ctrl).Msg);
            if (e.Vk == 0x12) alt = !e.Up;
            if (e.Vk == 0x11) ctrl = !e.Up;
        }
        return r.ToArray();
    }

    [Fact]
    public void alt_f4_조합은_전부_SYSKEY() =>
        Assert.Equal(new[] { WM_SYSKEYDOWN, WM_SYSKEYDOWN, WM_SYSKEYUP, WM_SYSKEYUP }, Messages("alt+f4"));

    [Fact]
    public void ctrl_alt_delete_조합은_전부_일반_KEY() =>
        Assert.Equal(new[] { WM_KEYDOWN, WM_KEYDOWN, WM_KEYDOWN, WM_KEYUP, WM_KEYUP, WM_KEYUP }, Messages("ctrl+alt+delete"));

    [Fact]
    public void win_r_조합은_전부_일반_KEY() =>
        Assert.Equal(new[] { WM_KEYDOWN, WM_KEYDOWN, WM_KEYUP, WM_KEYUP }, Messages("win+r"));
}
