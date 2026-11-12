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

    // OSC 7 이 없는 셸: 명령 실행 뒤 화면으로 현재 폴더 추정 (현재 폴더 /home/ec2-user/app 기준)
    [Theory]
    [InlineData("ubuntu@web-01:~$ cd /var/log\nubuntu@web-01:/var/log$ ", "/var/log")]          // 경로가 보이는 프롬프트
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd logs\n[ec2-user@ip-10-0-0-1 logs]$ ", "/home/ec2-user/app/logs")]
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd ..\n[ec2-user@ip-10-0-0-1 ec2-user]$ ", "/home/ec2-user")]
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd /etc/nginx/\n[ec2-user@ip-10-0-0-1 nginx]$", "/etc/nginx")]
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd \"my dir\"\n[ec2-user@ip-10-0-0-1 my dir]$ ", "/home/ec2-user/app/my dir")]
    [InlineData("[root@web app]# cd /\n[root@web /]# ", "/")]
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd\n[ec2-user@ip-10-0-0-1 ~]$ ", "/home/ec2-user")]
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ ls\na.txt b.txt\n[ec2-user@ip-10-0-0-1 app]$ ", "/home/ec2-user/app")]
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd nope\n-bash: cd: nope: No such file or directory\n[ec2-user@ip-10-0-0-1 app]$ ", "/home/ec2-user/app")]   // cd 실패: 그대로
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd -\n/tmp\n[ec2-user@ip-10-0-0-1 tmp]$ ", null)]       // 이전 폴더는 알 수 없음
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd logs\n[ec2-user@ip-10-0-0-1 logs]$ vi a", null)]    // 명령을 입력 중
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ cd x\n[ec2-user@ip-10-0-0-1 y]$ ", null)]               // 이름이 맞지 않으면 믿지 않음
    public void 프롬프트와_cd_명령으로_현재_폴더를_추정한다(string screen, string? expected) =>
        Assert.Equal(expected, TerminalCwd.Resolve(screen, "/home/ec2-user", "/home/ec2-user/app"));

    [Fact]
    public void 이미_옮긴_뒤_같은_화면을_다시_읽어도_cd_를_두_번_적용하지_않는다()
    {
        const string screen = "[ec2-user@ip-10-0-0-1 app]$ cd logs\n[ec2-user@ip-10-0-0-1 logs]$ ";
        var first = TerminalCwd.Resolve(screen, "/home/ec2-user", "/home/ec2-user/app");
        Assert.Equal("/home/ec2-user/app/logs", first);
        Assert.Equal(first, TerminalCwd.Resolve(screen, "/home/ec2-user", first));
    }

    [Fact]
    public void 앞_명령의_프롬프트가_오기_전에_친_cd_도_마지막_프롬프트로_따라간다()
    {
        // 감시 화면은 첫 Enter 줄부터 — 프롬프트가 여러 개 보여도 마지막 것
        const string screen = "demo@test:/$ cd pub\ndemo@test:/pub$ cd example\ndemo@test:/pub/example$ cd ..\ndemo@test:/pub$ ";
        Assert.Equal("/pub", TerminalCwd.Resolve(screen, "/", "/pub/example"));
    }

    [Theory]
    [InlineData("/a/b", "../c", "/a/c")]
    [InlineData("/a/b", "./c/./d/", "/a/b/c/d")]
    [InlineData("/a", "../../..", "/")]
    [InlineData("/a", "~/x", "/home/u/x")]
    [InlineData("/a", "~other", null)]
    [InlineData("/a", "$HOME", null)]
    public void cd_인자를_절대_경로로_바꾼다(string current, string arg, string? expected) =>
        Assert.Equal(expected, TerminalCwd.Combine(current, arg, "/home/u"));

    [Fact]
    public void SFTP_편집기_경로는_Claude_가_바꿀_수_없다()
    {
        Assert.Null(SettingsCatalog.Find("SftpEditor"));
        Assert.NotNull(SettingsCatalog.Find("SftpAutoOpen"));
        Assert.NotNull(SettingsCatalog.Find("SftpFollowTerminal"));
    }
}
