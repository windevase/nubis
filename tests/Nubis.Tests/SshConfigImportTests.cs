using System;
using System.IO;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class SshConfigImportTests
{
    private static ImportedHost Get(ImportResult r, string name) => Assert.Single(r.Hosts, h => h.Name == name);

    [Fact]
    public void Host_한_줄의_여러_별칭은_각각_서버가_된다()
    {
        var r = SshConfigImport.Parse("""
            Host web1 web2
                User deploy
            """);
        Assert.Equal(new[] { "web1", "web2" }, r.Hosts.Select(h => h.Name).ToArray());
        Assert.All(r.Hosts, h =>
        {
            Assert.Equal("deploy", h.Username);
            Assert.Equal(Protocol.Ssh, h.Protocol);
            Assert.Equal("SSH config", h.Group);
            Assert.Equal(h.Name, h.Address); // HostName 이 없으면 별칭이 주소
        });
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void 와일드카드_블록은_서버가_되지_않고_기본값만_준다_처음_값_우선()
    {
        var r = SshConfigImport.Parse("""
            Host db
                HostName 10.0.0.5
                User dbadmin

            Host *
                User root
                Port 2222
            """);
        var db = Assert.Single(r.Hosts);
        Assert.Equal("db", db.Name);
        Assert.Equal("10.0.0.5", db.Address);
        Assert.Equal("dbadmin", db.Username); // 앞 블록 값이 우선
        Assert.Equal(2222, db.Port);          // Host * 가 빈 값을 채움
    }

    [Fact]
    public void 앞쪽_Host_별표가_먼저_오면_그_값이_우선한다()
    {
        var r = SshConfigImport.Parse("""
            Host *
                User first
            Host a
                User second
            """);
        Assert.Equal("first", Get(r, "a").Username);
    }

    [Fact]
    public void 첫_Host_앞의_전역_설정은_모든_호스트에_적용된다()
    {
        var r = SshConfigImport.Parse("""
            User globaluser
            Host a
                User mine
            Host b
            """);
        Assert.Equal("globaluser", Get(r, "a").Username);
        Assert.Equal("globaluser", Get(r, "b").Username);
    }

    [Fact]
    public void 부정_패턴은_일치에서_제외한다()
    {
        var r = SshConfigImport.Parse("""
            Host bastion
                HostName 1.2.3.4
            Host app1 app2
            Host app* !app2
                ProxyJump bastion
            """);
        Assert.Equal("bastion", Get(r, "app1").JumpHost);
        Assert.Null(Get(r, "app2").JumpHost);
        Assert.Null(Get(r, "bastion").JumpHost);
        Assert.DoesNotContain(r.Hosts, h => h.Name.Contains('*') || h.Name.Contains('!'));
    }

    [Fact]
    public void HostName_의_퍼센트h_는_별칭으로_바뀐다()
    {
        var r = SshConfigImport.Parse("""
            Host srv1 srv2
            Host srv*
                HostName %h.example.com
            """);
        Assert.Equal("srv1.example.com", Get(r, "srv1").Address);
        Assert.Equal("srv2.example.com", Get(r, "srv2").Address);
    }

    [Fact]
    public void Port_User_ProxyJump_첫_홉()
    {
        var r = SshConfigImport.Parse("""
            Host target
                HostName 10.1.1.1
                Port 2200
                User ops
                ProxyJump jump@gw.example.com:2022,second
            """);
        var h = Get(r, "target");
        Assert.Equal(2200, h.Port);
        Assert.Equal("ops", h.Username);
        Assert.Equal("jump@gw.example.com:2022", h.JumpHost);
    }

    [Fact]
    public void 잘못된_포트는_경고하고_무시한다()
    {
        var r = SshConfigImport.Parse("""
            Host a
                Port 99999
            """);
        Assert.Null(Get(r, "a").Port);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Match_블록은_건너뛰고_경고는_한_번()
    {
        var r = SshConfigImport.Parse("""
            Match host foo
                User matched
            Match all
                Port 1
            Host foo
            """);
        var foo = Get(r, "foo");
        Assert.Null(foo.Username);
        Assert.Null(foo.Port);
        Assert.Single(r.Warnings, w => w.Contains("Match"));
    }

    [Fact]
    public void ProxyCommand_는_경고하고_점프_서버를_넣지_않는다()
    {
        var r = SshConfigImport.Parse("""
            Host a
                ProxyCommand ssh -W %h:%p gw
                ProxyJump gw
            Host b
                ProxyCommand none
            """);
        Assert.Null(Get(r, "a").JumpHost);
        Assert.Null(Get(r, "b").JumpHost);
        var w = Assert.Single(r.Warnings);
        Assert.Contains("ProxyCommand", w);
        Assert.Contains("a", w);
    }

    [Fact]
    public void IdentityFile_은_메모에만_남긴다()
    {
        var r = SshConfigImport.Parse("""
            Host a
                IdentityFile ~/.ssh/id_a
            Host *
                IdentityFile C:\keys\default.pem
            """);
        var note = Get(r, "a").Note;
        Assert.NotNull(note);
        Assert.Contains("IdentityFile: ~/.ssh/id_a", note);
        Assert.Contains(@"IdentityFile: C:\keys\default.pem", note); // Windows 경로의 역슬래시는 그대로
    }

    [Fact]
    public void 따옴표와_등호_형식_대소문자_무시()
    {
        var r = SshConfigImport.Parse("""
            # 주석
            host "my server"
                hostname=10.9.9.9
                USER = "svc user"
                Port=22 # 줄 끝 주석
            """);
        var h = Get(r, "my server");
        Assert.Equal("10.9.9.9", h.Address);
        Assert.Equal("svc user", h.Username);
        Assert.Equal(22, h.Port);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void 닫히지_않은_따옴표_줄은_경고하고_건너뛴다()
    {
        var r = SshConfigImport.Parse("Host a\n  User \"broken\n  Port 23\n");
        var h = Get(r, "a");
        Assert.Null(h.Username);
        Assert.Equal(23, h.Port);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Include_상대_경로와_glob()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-sshcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "conf.d"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "config"), """
                Include conf.d/*.conf
                Include extra
                Include missing-file

                Host main
                    HostName 10.0.0.1
                Host *
                    User fallback
                """);
            File.WriteAllText(Path.Combine(dir, "conf.d", "a.conf"), """
                Host alpha
                    HostName 10.0.0.2
                """);
            File.WriteAllText(Path.Combine(dir, "conf.d", "b.conf"), """
                Host beta
                    Port 2022
                """);
            File.WriteAllText(Path.Combine(dir, "conf.d", "ignored.txt"), "Host notme\n");
            File.WriteAllText(Path.Combine(dir, "extra"), "Host gamma\n    User g\n");

            var r = SshConfigImport.ParseFile(Path.Combine(dir, "config"));

            Assert.Equal(new[] { "alpha", "beta", "gamma", "main" }, r.Hosts.Select(h => h.Name).ToArray());
            Assert.Equal("10.0.0.2", Get(r, "alpha").Address);
            Assert.Equal(2022, Get(r, "beta").Port);
            Assert.Equal("g", Get(r, "gamma").Username);
            Assert.Equal("fallback", Get(r, "main").Username);
            Assert.Equal("fallback", Get(r, "alpha").Username);
            Assert.Single(r.Warnings, w => w.Contains("missing-file"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Include_뒤의_줄은_원래_블록에_이어서_적용된다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-sshcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "inc"), "Host other\n    User o\n");
            var r = SshConfigImport.Parse("Host main\n    Include inc\n    User m\n", dir);
            Assert.Equal("m", Get(r, "main").Username);
            Assert.Equal("o", Get(r, "other").Username);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 자기_자신을_포함해도_깊이_한도에서_멈춘다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-sshcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config"), "Host loop\nInclude config\n");
            var r = SshConfigImport.ParseFile(Path.Combine(dir, "config"));
            Assert.Single(r.Hosts);
            Assert.Single(r.Warnings);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 없는_파일은_경고만()
    {
        var r = SshConfigImport.ParseFile(Path.Combine(Path.GetTempPath(), "nubis-none-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(r.Hosts);
        Assert.Single(r.Warnings);
    }
}
