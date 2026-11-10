using Nubis;
using Xunit;

namespace Nubis.Tests;

public class TerminalCwdTests
{
    [Theory]
    [InlineData("file://web-01/home/ubuntu/app", "/home/ubuntu/app")]
    [InlineData("file:///var/log", "/var/log")]
    [InlineData("file://h/home/a%20b/%ED%95%9C%EA%B8%80", "/home/a b/한글")]
    [InlineData("http://x/y", null)]
    [InlineData("file://hostonly", null)]
    public void OSC7_경로를_읽는다(string data, string? expected) => Assert.Equal(expected, XtermView.ParseOsc7(data));

    [Theory]
    [InlineData("ubuntu@web-01:~$ ", "/home/ubuntu")]
    [InlineData("ubuntu@web-01:~/app/logs$", "/home/ubuntu/app/logs")]
    [InlineData("root@db:/etc/nginx# ", "/etc/nginx")]
    [InlineData("(venv) dev@box:/srv$ ", "/srv")]
    [InlineData("last output\nubuntu@web-01:/tmp$ \n\n", "/tmp")]
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ ", null)]   // 경로를 알 수 없는 프롬프트
    [InlineData("ubuntu@web-01:~$ ls -al", null)]       // 명령을 입력 중
    [InlineData("", null)]
    public void 프롬프트에서_현재_폴더를_읽는다(string screen, string? expected) =>
        Assert.Equal(expected, XtermView.PromptPath(screen, "/home/ubuntu"));

    [Fact]
    public void SFTP_편집기_경로는_Claude_가_바꿀_수_없다()
    {
        Assert.Null(SettingsCatalog.Find("SftpEditor"));
        Assert.NotNull(SettingsCatalog.Find("SftpAutoOpen"));
        Assert.NotNull(SettingsCatalog.Find("SftpFollowTerminal"));
    }
}
