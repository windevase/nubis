using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>get_terminal_info · send_to_terminal 읽기 전용 자동 승인 판단</summary>
public class TerminalInfoTests
{
    [Theory]
    [InlineData("PS C:\\Users\\me> ", "C:\\Users\\me")]
    [InlineData("PS D:\\work\\repo>", "D:\\work\\repo")]
    [InlineData("PS \\\\server\\share\\x> ", "\\\\server\\share\\x")]
    [InlineData("PS HKLM:\\SOFTWARE> ", null)]
    [InlineData("PS C:\\Users\\me> Get-ChildItem", null)]   // 명령 입력 중
    [InlineData("ubuntu@web-01:~$ ", null)]
    public void PowerShell_프롬프트의_폴더를_읽는다(string lastLine, string? expected) =>
        Assert.Equal(expected, TerminalCwd.PowerShellCwd("Windows PowerShell\n" + lastLine));

    [Fact]
    public void 홈을_모르면_물결표_그대로_돌려준다()
    {
        Assert.Equal("~/app/logs", TerminalCwd.Resolve("ubuntu@web-01:~/app/logs$ ", "~", null));
        Assert.Equal("~", TerminalCwd.Resolve("[ec2-user@ip-1 ~]$ ", "~", null));
        Assert.Equal("/var/log", TerminalCwd.Resolve("root@web-01:/var/log# ", "~", null));
    }

    private static bool ReadOnly(string t) => ReadOnlyCommands.IsReadOnly(t, CommandShell.Sh);

    [Theory]
    [InlineData(true, "pwd", true, true, true)]
    [InlineData(true, "ls -la /var/log", true, true, true)]
    [InlineData(false, "pwd", true, true, false)]           // 설정 꺼짐
    [InlineData(true, "pwd", true, false, false)]           // 프롬프트 아님 (비밀번호 질문·vim 등)
    [InlineData(true, "pwd", false, true, false)]           // Enter 없음
    [InlineData(true, "rm -rf /tmp/x", true, true, false)]  // 쓰기 명령
    [InlineData(true, "pwd\nrm -rf /", true, true, false)]  // 여러 줄
    [InlineData(true, "   ", true, true, false)]
    public void 터미널_입력_자동_승인_조건(bool setting, string text, bool enter, bool atPrompt, bool expected) =>
        Assert.Equal(expected, McpHelpers.CanAutoApproveTerminalInput(setting, text, enter, atPrompt, ReadOnly));
}
