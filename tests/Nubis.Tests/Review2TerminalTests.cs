using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>2차 리뷰 — 터미널(XtermView) 보조 로직</summary>
public class Review2TerminalTests
{
    [Theory]
    [InlineData("file://web01/home/a%20b", "/home/a b", "web01")]
    [InlineData("file:///etc", "/etc", "")]
    [InlineData("file://db.example.com/", "/", "db.example.com")]
    [InlineData("http://x/y", null, "")]
    [InlineData("file://nohost", null, "")]
    public void OSC7_호스트와_경로를_읽는다(string data, string? path, string host)
    {
        Assert.Equal(path, XtermView.ParseOsc7(data, out var h));
        Assert.Equal(host, h);
    }

    [Theory]
    [InlineData("Welcome", "Welcome")]
    [InlineData("line1\nline2", "line1\r\nline2")]
    [InlineData("line1\r\nline2", "line1\r\nline2")]
    [InlineData("a\rb", "a\r\nb")]
    [InlineData("배너\x1b]0;evil\x07끝", "배너]0;evil끝")]          // 제목 바꾸기 시도
    [InlineData("x\x1b[2Jy", "x[2Jy")]                             // 화면 지우기 시도
    [InlineData("tab\there", "tab\there")]
    public void 안내_문구의_제어_문자를_빼고_줄바꿈을_맞춘다(string input, string expected) =>
        Assert.Equal(expected, XtermView.SanitizeInfo(input));

    [Fact]
    public void 한도_안이면_자르지_않는다() => Assert.Equal(0, XtermView.PendingCut("abc", 10));

    [Fact]
    public void 줄_경계에서_자른다()
    {
        var text = "\x1b[31mAAAA\nBBBB\nCC";   // 길이 17 → 자를 위치 5 (ESC 시퀀스 가운데) 대신 다음 줄 시작
        var cut = XtermView.PendingCut(text, 12);
        Assert.Equal("BBBB\nCC", text[cut..]);
    }

    [Fact]
    public void 줄바꿈이_없으면_서로게이트_쌍을_가르지_않는다()
    {
        var text = "ab\U0001F600cd";   // a b [hi lo] c d
        // 한도 3 → 자를 위치 3 은 서로게이트 뒤쪽 — 한 칸 더 뒤에서 자른다
        var cut = XtermView.PendingCut(text, 3);
        Assert.Equal("cd", text[cut..]);
    }
}
