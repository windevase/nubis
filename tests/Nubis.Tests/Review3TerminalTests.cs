using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>3차 리뷰 — 터미널 출력 흐름 제어 조각 나누기 · 프롬프트 오탐 방지</summary>
public class Review3TerminalTests
{
    [Fact]
    public void 한도_안이면_전부_보낸다() =>
        Assert.Equal(5, XtermView.ChunkLength(new StringBuilder("abcde"), 10));

    [Fact]
    public void 한도에서_자른다() =>
        Assert.Equal(4, XtermView.ChunkLength(new StringBuilder("abcdefgh"), 4));

    [Fact]
    public void 서로게이트_쌍_가운데서_자르지_않는다()
    {
        // "abc" + 😀(서로게이트 쌍 2글자) + "d" — 4 에서 자르면 쌍의 앞 글자만 남으므로 3 에서 자른다
        var sb = new StringBuilder("abc\U0001F600d");
        Assert.Equal(3, XtermView.ChunkLength(sb, 4));
        Assert.Equal(5, XtermView.ChunkLength(sb, 5));
    }

    [Fact]
    public void 한글은_그대로_자른다() =>
        Assert.Equal(2, XtermView.ChunkLength(new StringBuilder("한글터미널"), 2));

    [Theory]
    [InlineData("$ cd /etc\nroot@db:/etc#", true)]                  // 새 프롬프트가 커서 줄
    [InlineData("$ cat notes.txt\nroot@db:~#\n", false)]            // 출력 줄에 프롬프트 모양 글자 — 커서는 다음 빈 줄
    [InlineData("$ echo 'root@db:~#'\nroot@db:~#\n   ", false)]     // 커서 줄이 공백뿐
    [InlineData("$ sudo su -\r\n[root@db ~]#", true)]
    [InlineData("", false)]
    public void 커서_줄에_글자가_있을_때만_새_프롬프트로_본다(string screen, bool expected) =>
        Assert.Equal(expected, TerminalCwd.CursorLineHasText(screen));

    [Fact]
    public void 출력_뒤_빈_커서_줄이면_사용자_전환으로_보지_않는다()
    {
        // 예전에는 마지막 "빈 줄이 아닌" 줄을 봐서 cat 출력의 "root@db:~#" 를 root 전환으로 오해했다
        var screen = "$ cat log\nroot@db:~#\n";
        Assert.Equal("root", TerminalCwd.PromptUser(screen));   // PromptUser 자체는 그대로
        Assert.False(TerminalCwd.CursorLineHasText(screen));      // 그래서 호출 전에 이 조건으로 거른다
    }
}
