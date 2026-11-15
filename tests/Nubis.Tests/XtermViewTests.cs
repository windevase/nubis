using Nubis;
using Xunit;

namespace Nubis.Tests;

public class XtermViewTests
{
    [Theory]
    [InlineData("ls -al", "ls -al", false)]
    [InlineData("a\tb\r\nc\n", "a\tb\r\nc\n", false)]                    // 탭·CR·LF 는 그대로
    [InlineData("한글 경로/파일", "한글 경로/파일", false)]
    [InlineData("echo hi\x1b[201~rm -rf ~\r", "echo hi[201~rm -rf ~\r", true)]   // bracketed paste 탈출 시도
    [InlineData("\x1b]0;title\x07x", "]0;titlex", true)]                // OSC·BEL
    [InlineData("a\u0000b\u0003c\u007Fd", "abcd", true)]                        // NUL·Ctrl+C·DEL
    [InlineData("x\u009b201~y", "x201~y", true)]                        // C1 CSI (0x9B)
    [InlineData("\x1b", "", true)]
    [InlineData("", "", false)]
    public void 붙여넣기에서_제어_문자를_뺀다(string input, string expected, bool removed)
    {
        Assert.Equal(expected, XtermView.SanitizePaste(input, out var r));
        Assert.Equal(removed, r);
    }

    [Theory]
    [InlineData("https://app.local/terminal.html", true)]
    [InlineData("https://APP.LOCAL/terminal.html", true)]
    [InlineData("http://app.local/terminal.html", false)]
    [InlineData("https://app.local:8443/terminal.html", false)]
    [InlineData("https://app.local.evil.com/terminal.html", false)]
    [InlineData("https://evil.com/", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 앱_페이지_출처만_인정한다(string? uri, bool expected) => Assert.Equal(expected, XtermView.IsAppPage(uri));

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/Windows/System32/calc.exe", null)]
    [InlineData("ms-settings:", null)]
    [InlineData("about:blank", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void http_https_링크만_연다(string? uri, string? expected) => Assert.Equal(expected, XtermView.WebLink(uri));
}
