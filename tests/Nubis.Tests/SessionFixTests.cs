using System;
using Nubis;
using Renci.SshNet.Common;
using Xunit;

namespace Nubis.Tests;

public class SessionFixTests
{
    [Theory]
    [InlineData("ubuntu@web-01:~$", "web-01")]
    [InlineData("root@db:/var/log#", "db")]
    [InlineData("[ec2-user@ip-10-0-0-5 ~]$", "ip-10-0-0-5")]
    [InlineData("[root@abc123 app]#", "abc123")]
    [InlineData("(venv) deploy@api:~/src$", "api")]
    [InlineData("(base) [user@gpu01 work]$", "gpu01")]
    public void PromptHost_ReadsHostFromPrompt(string lastLine, string expected)
    {
        Assert.Equal(expected, TerminalCwd.PromptHost("ubuntu@web-01:~$ ssh other\n" + lastLine));
    }

    [Theory]
    [InlineData("")]
    [InlineData("total 0")]
    [InlineData("ubuntu@web-01:~$ ls -al")]          // 명령 입력 중
    [InlineData("[root@db ~]# systemctl status")]   // 명령 입력 중
    [InlineData("Password:")]
    [InlineData("PS C:\\Users\\me>")]
    public void PromptHost_NonPromptIsNull(string lastLine)
    {
        Assert.Null(TerminalCwd.PromptHost("ubuntu@web-01:~$ cmd\n" + lastLine));
    }

    [Fact]
    public void PromptHost_DistinguishesOtherServerAndContainer()
    {
        // 로그인 서버의 프롬프트와 ssh·docker exec 로 들어간 프롬프트는 호스트가 다르다 (사용자 전환으로 보지 않음)
        Assert.Equal("web-01", TerminalCwd.PromptHost("ubuntu@web-01:~$"));
        Assert.Equal("other", TerminalCwd.PromptHost("ubuntu@web-01:~$ ssh ec2-user@other\n[ec2-user@other ~]$"));
        Assert.Equal("3f2a9c1b", TerminalCwd.PromptHost("ubuntu@web-01:~$ docker exec -it app bash\nroot@3f2a9c1b:/#"));
        // sudo su - 는 같은 서버라 호스트가 같다
        Assert.Equal("web-01", TerminalCwd.PromptHost("ubuntu@web-01:~$ sudo su -\nroot@web-01:~#"));
        Assert.Equal("root", TerminalCwd.PromptUser("ubuntu@web-01:~$ sudo su -\nroot@web-01:~#"));
    }

    [Theory]
    [InlineData(JumpFailure.Config, true)]
    [InlineData(JumpFailure.HostKey, true)]
    [InlineData(JumpFailure.Auth, true)]
    [InlineData(JumpFailure.Network, false)]
    public void JumpTunnelException_PermanentUnlessNetwork(JumpFailure kind, bool permanent)
    {
        var ex = new JumpTunnelException("x", kind);
        Assert.Equal(permanent, ex.IsPermanent);
        Assert.Equal(kind == JumpFailure.Auth, ex.IsAuth);
        // 기존 호출하는 쪽(InvalidOperationException 을 잡는 SFTP 도구)이 그대로 잡을 수 있어야 한다
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void AuthPromptCancelled_IsAuthenticationException()
    {
        // SSH.NET 이 인증 처리기의 예외를 다시 던질 때 SshAuthenticationException 경로로 전달되도록
        Assert.IsAssignableFrom<SshAuthenticationException>(new AuthPromptCancelledException("취소"));
    }

    [Fact]
    public void AuthPrompter_DetachRemovesTimeoutHandler()
    {
        var ci = new Renci.SshNet.ConnectionInfo("127.0.0.1", 22, "u", new Renci.SshNet.PasswordAuthenticationMethod("u", "p"))
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        using var client = new Renci.SshNet.SshClient(ci);
        var prompter = new AuthPrompter(_ => null);
        prompter.Attach(client);
        prompter.Detach(client);
        Assert.Equal(TimeSpan.FromSeconds(15), ci.Timeout);
        Assert.False(prompter.Cancelled);
    }
}
