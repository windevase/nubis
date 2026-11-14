using System;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class McpFixTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void 숫자_인자는_숫자와_숫자_문자열을_받고_잘못된_값은_null()
    {
        var a = Args("""{"n": 10, "f": 2.5, "s": " 30 ", "sf": "1.5", "bad": "abc", "nul": null, "b": true, "arr": [1], "nan": "NaN", "inf": "Infinity", "big": 1e400}""");
        Assert.Equal(10d, McpHelpers.NumberArg(a, "n"));
        Assert.Equal(2.5d, McpHelpers.NumberArg(a, "f"));
        Assert.Equal(30d, McpHelpers.NumberArg(a, "s"));
        Assert.Equal(1.5d, McpHelpers.NumberArg(a, "sf"));
        Assert.Null(McpHelpers.NumberArg(a, "bad"));
        Assert.Null(McpHelpers.NumberArg(a, "nul"));
        Assert.Null(McpHelpers.NumberArg(a, "b"));
        Assert.Null(McpHelpers.NumberArg(a, "arr"));
        Assert.Null(McpHelpers.NumberArg(a, "nan"));
        Assert.Null(McpHelpers.NumberArg(a, "inf"));
        Assert.Null(McpHelpers.NumberArg(a, "big"));
        Assert.Null(McpHelpers.NumberArg(a, "missing"));
        Assert.Null(McpHelpers.NumberArg(Args("[1,2]"), "n"));
        Assert.Null(McpHelpers.NumberArg(default, "n"));
    }

    [Fact]
    public void 마지막_명령_이후_출력과_프롬프트_줄()
    {
        var screen = "user@web:~$ grep -c error app.log\n3\nDone\nuser@web:~$";
        var recent = McpHelpers.OutputSinceLastCommand(screen);
        Assert.Equal("3\nDone\nuser@web:~$ ", recent);
        // 명령 줄의 글자(error)는 세지 않는다
        Assert.Equal(0, McpHelpers.CountMatches(recent!, "error", false));
        // '$ ' 패턴은 지금 프롬프트 줄(줄 끝 공백이 잘린 화면)에서도 찾는다
        Assert.Equal(1, McpHelpers.CountMatches(recent!, "$ ", false));
        Assert.Equal(1, McpHelpers.CountMatches(recent!, "done", false));

        // 더 앞의 명령 출력은 포함하지 않는다
        var two = "user@web:~$ make\nComplete!\nuser@web:~$ ls\na b\nuser@web:~$";
        Assert.Equal(0, McpHelpers.CountMatches(McpHelpers.OutputSinceLastCommand(two)!, "Complete!", false));

        // PowerShell 프롬프트, 줄 끝 공백·빈 줄
        Assert.Equal("ok\nPS C:\\work> ", McpHelpers.OutputSinceLastCommand("PS C:\\work> echo ok\nok\nPS C:\\work>   \n\n"));

        // 명령 줄을 못 찾으면(cd 로 프롬프트가 바뀜) null → 기존 방식으로 기다린다
        Assert.Null(McpHelpers.OutputSinceLastCommand("user@web:~$ cd /tmp\nuser@web:/tmp$"));
        Assert.Null(McpHelpers.OutputSinceLastCommand("user@web:~$"));
        Assert.Null(McpHelpers.OutputSinceLastCommand(""));
    }

    [Fact]
    public void 새로_붙은_부분에서만_패턴을_센다()
    {
        var prev = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\nDownloading";
        // 창이 밀려 앞 줄이 빠지고, 마지막 줄이 이어지고, 새 줄에 패턴이 나옴
        var cur = string.Join("\n", Enumerable.Range(4, 7).Select(i => $"line {i}")) + "\nDownloading... done\nerror: x\nerror: y";
        Assert.Equal(2, McpHelpers.CountNewMatches(prev, cur, "error", false));

        // 새 출력이 없으면 0
        Assert.Equal(0, McpHelpers.CountNewMatches(prev, prev, "line", false));

        // 앞 화면 마지막 줄에 원래 있던 일치는 다시 세지 않는다
        var prev2 = "l1\nl2\na\nb\nc\nd\nerror at start";
        var cur2 = "l2\na\nb\nc\nd\nerror at start\nok";
        Assert.Equal(0, McpHelpers.CountNewMatches(prev2, cur2, "error", false));
        Assert.Equal(1, McpHelpers.CountNewMatches(prev2, cur2 + "\nerror again", "error", false));

        // 전체 개수는 그대로여도(오래된 일치가 밀려남) 새 일치를 찾는다
        var prev3 = "error 1\na\nb\nc\nd\ne\nz";
        var cur3 = "a\nb\nc\nd\ne\nz\nerror 2";
        Assert.Equal(McpHelpers.CountMatches(prev3, "error", false), McpHelpers.CountMatches(cur3, "error", false));
        Assert.Equal(1, McpHelpers.CountNewMatches(prev3, cur3, "error", false));

        // 기준 줄을 못 찾으면(화면 지움) 0 — 잘못 끝내지 않는다
        Assert.Equal(0, McpHelpers.CountNewMatches(prev, "error everywhere\nerror", "error", false));
        Assert.Equal(0, McpHelpers.CountNewMatches("only", "only\nerror", "error", false));
        Assert.Equal(0, McpHelpers.CountNewMatches(prev, cur, "", false));
    }

    [Theory]
    [InlineData("user@web:~$ apt install -y nginx\nReading package lists...", true)]
    [InlineData("(venv) root@db:/srv/app# make\nbuilding", true)]
    [InlineData("[root@db ~]# yum update\nLoading", true)]
    [InlineData("PS C:\\work> npm ci\nadded 10 packages", true)]
    [InlineData("user@web:~$\nsome output", false)]           // 빈 프롬프트 줄은 명령 줄이 아님
    [InlineData("❯ docker pull nginx\nPulling", false)]       // 앱이 모르는 프롬프트 (zsh 테마)
    [InlineData("Downloading 50%", false)]
    public void 알아보는_프롬프트에서_입력한_명령_줄(string screen, bool expected) =>
        Assert.Equal(expected, McpHelpers.HasPromptCommandLine(screen));

    [Fact]
    public void 로컬_터미널은_읽기_전용이어도_자동_승인하지_않는다()
    {
        static bool ReadOnly(string _) => true;
        Assert.True(McpHelpers.CanAutoApproveTerminalInput(true, "ls -l", true, true, ReadOnly));
        Assert.True(McpHelpers.CanAutoApproveTerminalInput(true, "ls -l", true, true, ReadOnly, localTerminal: false));
        Assert.False(McpHelpers.CanAutoApproveTerminalInput(true, @"gc ~\.ssh\id_rsa", true, true, ReadOnly, localTerminal: true));
    }

    [Fact]
    public void 공통_데이터_폴더도_업로드_금지()
    {
        var root = @"C:\Users\a\AppData\Roaming\Nubis";
        var data = root + @"\users\github_123";
        Assert.NotNull(McpHelpers.BlockedUploadReason(root + @"\login.json", data, root));
        Assert.NotNull(McpHelpers.BlockedUploadReason(root + @"\users\google_999\vault.json", data, root));
        Assert.NotNull(McpHelpers.BlockedUploadReason(data + @"\hosts.json", data, root));
        // rootDir 를 안 주면 예전처럼 dataDir 만 본다
        Assert.Null(McpHelpers.BlockedUploadReason(root + @"\login.json", data));
        Assert.Null(McpHelpers.BlockedUploadReason(@"C:\Users\a\AppData\Roaming\NubisBackup\a.txt", data, root));
    }
}
