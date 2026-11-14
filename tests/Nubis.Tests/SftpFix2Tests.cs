using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class SftpFix2Tests
{
    [Fact]
    public void 한_폴더의_로컬_이름은_대소문자_치환_결과가_겹치지_않는다()
    {
        var dir = @"C:\tmp\dl";
        var result = SftpPanel.UniqueTargets(new[]
        {
            ("a:b.log", false), ("a_b.log", false), ("README", false), ("readme", false), ("logs", true), ("Logs", true),
        }, dir);
        Assert.Equal(new[]
        {
            @"C:\tmp\dl\a_b.log", @"C:\tmp\dl\a_b (1).log", @"C:\tmp\dl\README", @"C:\tmp\dl\readme (1)", @"C:\tmp\dl\logs", @"C:\tmp\dl\Logs (1)",
        }, result);
    }

    [Fact]
    public void 폴더_이름은_점이_있어도_확장자로_나누지_않는다()
    {
        var result = SftpPanel.UniqueTargets(new[] { ("conf.d", true), ("CONF.D", true) }, @"C:\x");
        Assert.Equal(new[] { @"C:\x\conf.d", @"C:\x\CONF.D (1)" }, result);
    }

    [Fact]
    public void 이미_있는_파일과도_겹치지_않게_한다()
    {
        var existing = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { @"C:\x\app.log", @"C:\x\app (1).log" };
        var result = SftpPanel.UniqueTargets(new[] { ("app.log", false), ("other.txt", false) }, @"C:\x", existing.Contains);
        Assert.Equal(new[] { @"C:\x\app (2).log", @"C:\x\other.txt" }, result);
    }

    [Fact]
    public void x_와_x_part_는_서로_다른_이름이다()
    {
        var result = SftpPanel.UniqueTargets(new[] { ("x", false), ("x.part", false) }, @"C:\x");
        Assert.Equal(2, result.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(@"C:\t\run.bat", true)]
    [InlineData(@"C:\t\SETUP.EXE", true)]
    [InlineData(@"C:\t\page.hta", true)]
    [InlineData(@"C:\t\x.vbs", true)]
    [InlineData(@"C:\t\app.js", true)]
    [InlineData(@"C:\t\a.ps1", true)]
    [InlineData(@"C:\t\link.lnk", true)]
    [InlineData(@"C:\t\pkg.msi", true)]
    [InlineData(@"C:\t\s.scr", true)]
    [InlineData(@"C:\t\deploy.sh", true)]
    [InlineData(@"C:\t\evil.cmd. ", true)]   // 끝의 점·공백은 Windows 가 무시
    [InlineData(@"C:\t\nginx.conf", false)]
    [InlineData(@"C:\t\app.log", false)]
    [InlineData(@"C:\t\config.yml", false)]
    [InlineData(@"C:\t\Makefile", false)]
    [InlineData(@"C:\t\.bashrc", false)]
    public void 실행될_수_있는_형식을_구분한다(string path, bool risky) => Assert.Equal(risky, SftpPanel.IsRiskyToOpen(path));

    [Fact]
    public void 업로드_임시_이름은_매번_다르고_같은_폴더에_있다()
    {
        var a = SftpPanel.UploadTempName("/etc/app/config.yml");
        var b = SftpPanel.UploadTempName("/etc/app/config.yml");
        Assert.NotEqual(a, b);
        Assert.StartsWith("/etc/app/config.yml.", a);
        Assert.EndsWith(".nubis-upload", a);
        Assert.DoesNotContain("/", a["/etc/app/".Length..]);
    }

    [Fact]
    public void sudo_확인은_sftp_server_로_한다()
    {
        Assert.Equal("sudo -n -u 'root' '/usr/lib/openssh/sftp-server' </dev/null",
            SudoSftp.ProbeCommand("root", "/usr/lib/openssh/sftp-server"));
        Assert.Equal("sudo -k -S -p '' -u 'root' '/usr/lib/openssh/sftp-server'",
            SudoSftp.CheckPasswordCommand("root", "/usr/lib/openssh/sftp-server"));
    }
}
