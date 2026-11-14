using Nubis;
using Xunit;

namespace Nubis.Tests;

public class SudoSftpTests
{
    [Theory]
    [InlineData("root@web-01:~# ", "root")]
    [InlineData("ubuntu@web-01:/var/log$", "ubuntu")]
    [InlineData("[root@db ~]# ", "root")]
    [InlineData("[ec2-user@ip-10-0-0-1 app]$ ", "ec2-user")]
    [InlineData("(venv) dev@box:/srv$ ", "dev")]
    [InlineData("ubuntu@web-01:~$ sudo su -", null)]        // 명령 입력 중
    [InlineData("[sudo] password for ubuntu: ", null)]       // sudo 비밀번호 질문
    [InlineData("Last login: Mon Nov 10 10:00:00 2026", null)]
    public void 프롬프트의_사용자를_읽는다(string lastLine, string? expected) =>
        Assert.Equal(expected, TerminalCwd.PromptUser("ubuntu@web-01:~$ sudo su -\n" + lastLine));

    [Fact]
    public void root_로_전환하면_물결표는_root_홈이다() =>
        Assert.Equal("/root", TerminalCwd.Resolve("root@web-01:~# ", "/root", "/root"));

    [Theory]
    [InlineData("root", true)]
    [InlineData("www-data", true)]
    [InlineData("svc.app", true)]
    [InlineData("machine$", true)]
    [InlineData("root;rm -rf /", false)]
    [InlineData("a b", false)]
    [InlineData("$(id)", false)]
    [InlineData("", false)]
    public void 사용자_이름은_엄격하게_검사한다(string user, bool ok) => Assert.Equal(ok, SudoSftp.IsValidUser(user));

    [Fact]
    public void 작은따옴표로_인용한다() => Assert.Equal("'a'\\''b'", SudoSftp.Quote("a'b"));

    [Fact]
    public void 비밀번호_방식은_캐시를_무시하고_표준입력에서_읽는다()
    {
        Assert.Equal("sudo -k -S -p '' -u 'root' '/usr/lib/openssh/sftp-server'",
            SudoSftp.ServerCommand("/usr/lib/openssh/sftp-server", "root", withPassword: true));
        Assert.Equal("sudo -n -u 'root' '/usr/lib/openssh/sftp-server'",
            SudoSftp.ServerCommand("/usr/lib/openssh/sftp-server", "root", withPassword: false));
    }

    [Theory]
    [InlineData("sudo: a password is required\n", true)]
    [InlineData("sudo: sorry, you must have a tty to run sudo\n", false)]
    public void 비밀번호가_필요한지_구분한다(string stderr, bool expected) => Assert.Equal(expected, SudoSftp.NeedsPassword(stderr));

    [Theory]
    [InlineData("Sorry, try again.\nsudo: no password was provided\nsudo: 1 incorrect password attempt\n", true)]
    [InlineData("ubuntu is not in the sudoers file.  This incident will be reported.\n", false)]
    public void 비밀번호_오류를_구분한다(string stderr, bool expected) => Assert.Equal(expected, SudoSftp.IsWrongPassword(stderr));

    [Theory]
    [InlineData("ubuntu is not in the sudoers file.  This incident will be reported.", "sudo")]
    [InlineData("sudo: sorry, you must have a tty to run sudo", "requiretty")]
    [InlineData("Sorry, user ubuntu is not allowed to execute '/usr/lib/openssh/sftp-server' as root on web-01.", "sftp-server")]
    [InlineData("sudo: something odd", "something odd")]
    public void sudo_오류를_설명한다(string stderr, string expectedPart) => Assert.Contains(expectedPart, SudoSftp.Explain(stderr));
}
