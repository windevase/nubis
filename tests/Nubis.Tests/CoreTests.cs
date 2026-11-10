using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>클라우드 동기화 병합 규칙</summary>
public class CloudSyncTests
{
    private static CloudProfile Profile(bool includeStopped = true, bool publicIp = false) =>
        new() { Provider = CloudProvider.Aws, Name = "im", DisplayName = "IM", IncludeStopped = includeStopped, UsePublicIp = publicIp };

    private static CloudInstance Linux(string id, string name = "web", string ip = "10.0.0.1", string state = "running", string pub = "") =>
        new(id, name, "api", state, "ap-northeast-2", ip, pub, false, "Linux/UNIX");

    private static CloudInstance Windows(string id, string ip = "10.0.0.9") =>
        new(id, "win", "db", "running", "ap-northeast-2", ip, "", true, "Windows");

    [Fact]
    public void 새_인스턴스는_OS에_맞는_프로토콜과_프로필_포트로_추가된다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        var (added, _, removed) = CloudSync.Apply(p, new() { Linux("i-1"), Windows("i-2") }, hosts);

        Assert.Equal(2, added);
        Assert.Equal(0, removed);
        var lin = hosts.Single(h => h.CloudInstanceId == "i-1");
        Assert.Equal(Protocol.Ssh, lin.Protocol);
        Assert.Equal(HostEntry.PreferredPort(Protocol.Ssh), lin.Port);   // 프로필에 포트 미지정 → 설정의 기본 포트
        Assert.Equal("10.0.0.1", lin.Address);
        Assert.Equal("api", lin.Purpose);
        var win = hosts.Single(h => h.CloudInstanceId == "i-2");
        Assert.Equal(Protocol.Rdp, win.Protocol);
        Assert.Equal(HostEntry.PreferredPort(Protocol.Rdp), win.Port);
        Assert.NotNull(p.LastSync);
    }

    [Fact]
    public void 프로필에_지정한_포트가_기본_포트보다_우선한다()
    {
        var p = Profile();
        p.SshPort = 50022;
        p.RdpPort = 33890;
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Linux("i-1"), Windows("i-2") }, hosts);
        Assert.Equal(50022, hosts.Single(h => h.CloudInstanceId == "i-1").Port);
        Assert.Equal(33890, hosts.Single(h => h.CloudInstanceId == "i-2").Port);
    }

    [Fact]
    public void 저장된_프로필_포트를_그대로_읽고_없으면_null()
    {
        var withPorts = System.Text.Json.JsonSerializer.Deserialize<CloudProfile>("{\"Name\":\"a\",\"SshPort\":50022,\"RdpPort\":33890}")!;
        Assert.Equal(50022, withPorts.SshPort);
        Assert.Equal(33890, withPorts.EffectiveRdpPort);
        var without = System.Text.Json.JsonSerializer.Deserialize<CloudProfile>("{\"Name\":\"b\"}")!;
        Assert.Null(without.SshPort);
        Assert.Equal(HostEntry.PreferredPort(Protocol.Ssh), without.EffectiveSshPort);
    }

    [Fact]
    public void 재동기화는_클라우드_정보만_갱신하고_사용자_변경값은_유지한다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Linux("i-1") }, hosts);
        var h = hosts[0];
        h.Port = 2222;
        h.Username = "admin";

        CloudSync.Apply(p, new() { Linux("i-1", name: "web-renamed", ip: "10.0.0.50", state: "stopped") }, hosts);

        Assert.Single(hosts);
        Assert.Same(h, hosts[0]);
        Assert.Equal("web-renamed", h.Label);
        Assert.Equal("10.0.0.50", h.Address);
        Assert.Equal("stopped", h.CloudState);
        Assert.True(h.IsStopped);
        Assert.Equal(2222, h.Port);
        Assert.Equal("admin", h.Username);
    }

    [Fact]
    public void 사라진_인스턴스만_제거하고_다른_프로필과_직접_등록한_호스트는_건드리지_않는다()
    {
        var p = Profile();
        var other = Profile();
        var local = new HostEntry { Label = "my-server", Address = "192.168.0.10" };
        var hosts = new List<HostEntry> { local };
        CloudSync.Apply(p, new() { Linux("i-1"), Linux("i-2", ip: "10.0.0.2") }, hosts);
        CloudSync.Apply(other, new() { Linux("x-1", ip: "10.1.0.1") }, hosts);

        var (_, _, removed) = CloudSync.Apply(p, new() { Linux("i-2", ip: "10.0.0.2") }, hosts);

        Assert.Equal(1, removed);
        Assert.Contains(local, hosts);
        Assert.Contains(hosts, h => h.CloudInstanceId == "x-1");
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "i-1");
    }

    [Fact]
    public void 중지_서버_제외_설정이면_중지된_서버는_추가하지_않고_기존_것도_제거한다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Linux("i-1", state: "stopped") }, hosts);
        Assert.Single(hosts);

        p.IncludeStopped = false;
        CloudSync.Apply(p, new() { Linux("i-1", state: "stopped"), Linux("i-2", ip: "10.0.0.2") }, hosts);
        Assert.Equal(new[] { "i-2" }, hosts.Select(h => h.CloudInstanceId));
    }

    [Fact]
    public void 사설과_공인IP를_모두_저장하고_프로필_기본값에_따라_접속주소를_정한다()
    {
        var p = Profile(publicIp: true);
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Linux("i-1", pub: "3.3.3.3"), Linux("i-2", ip: "10.0.0.2"), Linux("i-3", ip: "") }, hosts);

        var withEip = hosts.Single(h => h.CloudInstanceId == "i-1");
        Assert.Equal("10.0.0.1", withEip.Address);
        Assert.Equal("10.0.0.1", withEip.PrivateIp);
        Assert.Equal("3.3.3.3", withEip.PublicIp);
        Assert.True(withEip.UsePublicIp);
        Assert.Equal("3.3.3.3", withEip.EffectiveAddress);
        Assert.Equal(new[] { "10.0.0.1", "3.3.3.3" }, withEip.AddressCandidates());   // 명령 실행은 항상 사설 먼저

        var noEip = hosts.Single(h => h.CloudInstanceId == "i-2");
        Assert.False(noEip.UsePublicIp);
        Assert.Equal("10.0.0.2", noEip.EffectiveAddress);
        Assert.Single(noEip.AddressCandidates());
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "i-3");
    }

    [Fact]
    public void 사용자가_고른_공인IP_사용은_재동기화해도_유지되고_EIP를_떼면_해제된다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Linux("i-1", pub: "3.3.3.3") }, hosts);
        Assert.False(hosts[0].UsePublicIp);
        hosts[0].UsePublicIp = true;

        CloudSync.Apply(p, new() { Linux("i-1", pub: "3.3.3.4") }, hosts);
        Assert.True(hosts[0].UsePublicIp);
        Assert.Equal("3.3.3.4", hosts[0].EffectiveAddress);

        CloudSync.Apply(p, new() { Linux("i-1", pub: "") }, hosts);
        Assert.False(hosts[0].UsePublicIp);
        Assert.Null(hosts[0].PublicIp);
        Assert.Equal("10.0.0.1", hosts[0].EffectiveAddress);
    }

    [Fact]
    public void 접속용_사본은_요청에_따라_공인_사설을_고른다()
    {
        var h = new HostEntry { Address = "10.0.0.1", PrivateIp = "10.0.0.1", PublicIp = "3.3.3.3", Username = "u" };
        h.Password = "p";
        Assert.Equal("10.0.0.1", h.ForConnection(null).Address);
        Assert.Equal("3.3.3.3", h.ForConnection(null, usePublic: true).Address);
        Assert.Equal("10.0.0.1", h.Address);   // 원본은 그대로
        h.UsePublicIp = true;
        Assert.Equal("3.3.3.3", h.ForConnection(null).Address);
        Assert.Equal("10.0.0.1", h.ForConnection(null, usePublic: false).Address);
    }

    [Fact]
    public void 일부_리전_조회가_실패하면_안_보인_서버를_지우지_않는다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Linux("i-1"), Linux("i-2", ip: "10.0.0.2") }, hosts);
        hosts[0].Username = "saved-user";

        // i-1 이 있는 리전이 일시 장애로 응답 안 함
        var (_, _, removed) = CloudSync.Apply(p, new() { Linux("i-2", ip: "10.0.0.2") }, hosts, removeMissing: false);

        Assert.Equal(0, removed);
        Assert.Equal("saved-user", hosts.Single(h => h.CloudInstanceId == "i-1").Username);
    }

    [Theory]
    [InlineData("SOL126SE-LW01:devhub", "SOL126SE-LW01", "devhub")]
    [InlineData("plain-name", "plain-name", "")]
    [InlineData("a:b:c", "a", "b:c")]
    public void 이름태그는_첫_콜론에서_이름과_용도로_나뉜다(string raw, string name, string purpose)
    {
        Assert.Equal((name, purpose), CloudInstance.SplitName(raw));
    }
}

public class HostEntryTests
{
    [Theory]
    [InlineData("running", false)]
    [InlineData("RUNNING", false)]
    [InlineData("stopped", true)]
    [InlineData("TERMINATED", true)]
    [InlineData("deallocated", true)]
    public void 클라우드_호스트의_중지_여부(string state, bool stopped)
    {
        var h = new HostEntry { CloudProfileId = Guid.NewGuid(), CloudState = state };
        Assert.Equal(stopped, h.IsStopped);
    }

    [Fact]
    public void 직접_등록한_호스트는_중지로_보지_않는다() =>
        Assert.False(new HostEntry { CloudState = "stopped" }.IsStopped);

    [Fact]
    public void 프로필_기본계정은_비어_있는_값만_채운다()
    {
        var p = new CloudProfile { DefaultUsername = "example" };
        p.DefaultPassword = "profile-pass";

        var empty = new HostEntry { CloudProfileId = p.Id };
        var filled = empty.WithDefaults(p);
        Assert.Equal("example", filled.Username);
        Assert.Equal("profile-pass", filled.Password);
        Assert.Equal("", empty.Username);   // 원본은 바뀌지 않음

        var own = new HostEntry { CloudProfileId = p.Id, Username = "me" };
        own.Password = "mine";
        Assert.Same(own, own.WithDefaults(p));
    }
}

public class TencentSignatureTests
{
    [Fact]
    public void TC3_서명이_문서_알고리즘_독립_구현값과_같다()
    {
        // 기대값: Tencent Cloud 문서 알고리즘을 Python(hmac/hashlib)으로 따로 구현해 계산한 값
        const string expected = "TC3-HMAC-SHA256 Credential=AKIDtest123/2025-11-10/cvm/tc3_request, " +
                                "SignedHeaders=content-type;host;x-tc-action, " +
                                "Signature=cdc1ddaa5c1acad9d4caf0be9f51898de9a2a81619d118ec472cef3914602ac8";
        var actual = TencentProvider.Authorization("AKIDtest123", "secretKeyForTest456", "DescribeInstances",
            "{\"Offset\":0,\"Limit\":100}", 1762750000);
        Assert.Equal(expected, actual);
    }
}

public class ApprovalDangerTests
{
    [Theory]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("sudo rm -r /var/log/old")]
    [InlineData("rm -f important.db")]
    [InlineData("systemctl restart nginx")]
    [InlineData("shutdown -r now")]
    [InlineData("reboot")]
    [InlineData("Restart-Service W3SVC")]
    [InlineData("Stop-Computer -Force")]
    [InlineData("DROP TABLE users;")]
    [InlineData("kill -9 1234")]
    [InlineData("rm --recursive --force /data")]
    [InlineData("rd /s /q C:\\temp")]
    [InlineData("find /var/log -name '*.gz' -delete")]
    [InlineData("shred -u secret.txt")]
    [InlineData("Clear-Content C:\\app\\config.ini")]
    public void 위험_명령은_경고한다(string cmd) => Assert.True(ApprovalDialog.IsDangerous(cmd));

    [Theory]
    [InlineData("df -h")]
    [InlineData("ls -la /var/log")]
    [InlineData("systemctl status nginx")]
    [InlineData("free -m && uptime")]
    [InlineData("Get-Service | Where-Object Status -eq Running")]
    [InlineData("cat /etc/hosts")]
    [InlineData("grep -r error /var/log/app")]
    public void 조회_명령은_경고하지_않는다(string cmd) => Assert.False(ApprovalDialog.IsDangerous(cmd));
}

public class ReadOnlyCommandTests
{
    [Theory]
    [InlineData("df -h")]
    [InlineData("free -m && uptime")]
    [InlineData("cat /etc/os-release | grep PRETTY")]
    [InlineData("systemctl status nginx --no-pager")]
    [InlineData("sudo journalctl -u nginx -n 50 --no-pager")]
    [InlineData("ps aux | sort -k3 -rn | head")]
    [InlineData("docker ps -a")]
    [InlineData("kubectl get pods -A")]
    [InlineData("ip addr show")]
    [InlineData("ss -tlnp 2>/dev/null")]
    [InlineData("find /var/log -name '*.log' -mtime -1")]
    [InlineData("awk '{print $1}' /var/log/nginx/access.log | sort | uniq -c")]
    [InlineData("crontab -l")]
    [InlineData("tail -n 100 /var/log/messages; date")]
    [InlineData("grep 'a;b > c' file.txt")]
    public void 리눅스_조회_명령은_읽기_전용(string cmd) => Assert.True(ReadOnlyCommands.IsReadOnly(cmd, CommandShell.Sh));

    [Theory]
    [InlineData("rm -f /tmp/x")]
    [InlineData("echo hi > /etc/motd")]
    [InlineData("cat a >> b")]
    [InlineData("systemctl restart nginx")]
    [InlineData("systemctl stop nginx")]
    [InlineData("docker rm abc")]
    [InlineData("docker exec web sh")]
    [InlineData("kubectl delete pod x")]
    [InlineData("ip link set eth0 down")]
    [InlineData("find / -name x -exec rm {} \\;")]
    [InlineData("find /tmp -delete")]
    [InlineData("ls $(rm -rf /)")]
    [InlineData("ls `reboot`")]
    [InlineData("ls; touch x")]
    [InlineData("sleep 100 & rm x")]
    [InlineData("sort -o out.txt in.txt")]
    [InlineData("awk 'BEGIN{system(\"reboot\")}'")]
    [InlineData("crontab -r")]
    [InlineData("date -s '2020-01-01'")]
    [InlineData("hostname newname")]
    [InlineData("FOO=1 ls")]
    [InlineData("bash -c 'ls'")]
    [InlineData("yum install nginx")]
    [InlineData("apt upgrade")]
    [InlineData("tee /etc/x")]
    [InlineData("sed -i s/a/b/ file")]
    [InlineData("cat <<EOF > x")]
    [InlineData("journalctl --vacuum-size=1M")]
    [InlineData("dmesg -c")]
    [InlineData("top")]
    [InlineData("ping 8.8.8.8")]
    [InlineData("yes")]
    [InlineData("less /var/log/messages")]
    public void 리눅스_쓰기_명령은_승인_필요(string cmd) => Assert.False(ReadOnlyCommands.IsReadOnly(cmd, CommandShell.Sh));

    [Theory]
    [InlineData("Get-Service | Where-Object Status -eq Running")]
    [InlineData("Get-Process | Sort-Object CPU -Descending | Select-Object -First 10")]
    [InlineData("Get-CimInstance Win32_LogicalDisk | Format-Table DeviceID, FreeSpace")]
    [InlineData("Get-EventLog -LogName System -Newest 20")]
    [InlineData("ipconfig /all")]
    [InlineData("Test-NetConnection 10.0.0.1 -Port 443")]
    [InlineData("Get-ChildItem C:\\logs | ForEach-Object { $_.Name }")]
    [InlineData("hostname; whoami")]
    [InlineData("netstat -ano | findstr 3389")]
    public void 파워셸_조회_명령은_읽기_전용(string cmd) => Assert.True(ReadOnlyCommands.IsReadOnly(cmd, CommandShell.PowerShell));

    [Theory]
    [InlineData("Restart-Service W3SVC")]
    [InlineData("Stop-Process -Id 1")]
    [InlineData("Get-Service | Stop-Service")]
    [InlineData("Get-ChildItem C:\\x | Remove-Item")]
    [InlineData("Set-Content C:\\a.txt hi")]
    [InlineData("Get-Process > C:\\p.txt")]
    [InlineData("Get-Process | Out-File C:\\p.txt")]
    [InlineData("Invoke-Expression 'rm x'")]
    [InlineData("iex (Get-Content a.ps1 -Raw)")]
    [InlineData("[IO.File]::Delete('C:\\a')")]
    [InlineData("(Get-Service W3SVC).Stop()")]
    [InlineData("Get-ChildItem | ForEach-Object { $_.Delete() }")]
    [InlineData("& 'C:\\tools\\x.exe'")]
    [InlineData("ipconfig /release")]
    [InlineData("sc stop W3SVC")]
    [InlineData("reg add HKLM\\Software\\X /v a /d 1")]
    [InlineData("net user bob pass /add")]
    [InlineData("shutdown /r /t 0")]
    [InlineData("notepad.exe")]
    [InlineData("Start-Process cmd")]
    [InlineData("Get-ChildItem C:\\tmp | % { rm $_ }")]
    [InlineData("Get-ChildItem | ForEach-Object { del $_.FullName }")]
    public void 파워셸_쓰기_명령은_승인_필요(string cmd) => Assert.False(ReadOnlyCommands.IsReadOnly(cmd, CommandShell.PowerShell));

    [Theory]
    [InlineData("ipconfig /all", true)]
    [InlineData("dir C:\\logs & systeminfo", true)]
    [InlineData("sc query W3SVC", true)]
    [InlineData("reg query HKLM\\Software\\Microsoft", true)]
    [InlineData("tasklist | findstr w3wp", true)]
    [InlineData("netstat -ano > C:\\n.txt", false)]
    [InlineData("del C:\\a.txt", false)]
    [InlineData("dir & del x", false)]
    [InlineData("sc config W3SVC start= disabled", false)]
    [InlineData("schtasks /create /tn x /tr y", false)]
    [InlineData("powershell -c Get-Process", false)]
    public void cmd_판별(string cmd, bool expected) => Assert.Equal(expected, ReadOnlyCommands.IsReadOnly(cmd, CommandShell.Cmd));
}

public class WinRmTests
{
    [Theory]
    [InlineData(@"CORP\admin", "admin", "CORP")]
    [InlineData("admin@corp.local", "admin", "corp.local")]
    [InlineData("example", "example", "")]
    public void 계정_형식별_도메인_분리(string input, string user, string domain)
    {
        var c = WinRm.ToCredential(input, "pw");
        Assert.Equal(user, c.UserName);
        Assert.Equal(domain, c.Domain);
    }

    [Fact]
    public void PowerShell_CLIXML_오류를_읽을_수_있는_텍스트로_바꾼다()
    {
        const string clixml = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
                              "<S S=\"Error\">Get-Item : Cannot find path_x000D__x000A_</S>" +
                              "<S S=\"Error\">'C:\\nope' &amp; more_x000D__x000A_</S></Objs>";
        Assert.Equal("Get-Item : Cannot find path\n'C:\\nope' & more", WinRm.CleanClixml(clixml));
        Assert.Equal("plain error", WinRm.CleanClixml("plain error"));
    }

    [Fact]
    public void SOAP_봉투는_올바른_XML이고_셸ID를_포함한다()
    {
        var xml = WinRm.Envelope("https://10.0.0.1:5986/wsman", "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete", "SHELL-123", null, "");
        var doc = XDocument.Parse(xml);
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "Selector" && e.Value == "SHELL-123");
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "To" && e.Value == "https://10.0.0.1:5986/wsman");
    }
}

public class ChildEnvironmentTests
{
    [Fact]
    public void 세션에서_상속된_Claude_변수만_뺀다()
    {
        var persistent = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLAUDE_CODE_USE_BEDROCK", "PATH" };
        Assert.True(ChildEnvironment.ShouldDrop("CLAUDECODE", persistent));
        Assert.True(ChildEnvironment.ShouldDrop("CLAUDE_CODE_SESSION_ID", persistent));
        Assert.True(ChildEnvironment.ShouldDrop("MCP_CONNECTION_NONBLOCKING", persistent));
        Assert.False(ChildEnvironment.ShouldDrop("CLAUDE_CODE_USE_BEDROCK", persistent));   // 사용자가 영구 설정한 값
        Assert.False(ChildEnvironment.ShouldDrop("PATH", persistent));
        Assert.False(ChildEnvironment.ShouldDrop("USERPROFILE", persistent));
    }

    [Fact]
    public void 환경블록은_널문자로_끝난다()
    {
        var block = ChildEnvironment.Build();
        Assert.EndsWith("\0\0", block);
        Assert.DoesNotContain("CLAUDECODE=", block);
    }
}

public class SftpPathTests
{
    [Theory]
    [InlineData("app-10:00.log", "app-10_00.log")]
    [InlineData("..\\..\\evil.bat", ".._.._evil.bat")]
    [InlineData("..", "_")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("report?.csv", "report_.csv")]
    [InlineData("trailing. ", "trailing")]
    public void 원격_이름을_안전한_Windows_파일명으로(string remote, string expected) =>
        Assert.Equal(expected, SftpPanel.SafeLocalName(remote));

    [Theory]
    [InlineData("/home/user", "a.txt", "/home/user/a.txt")]
    [InlineData("/", "etc", "/etc")]
    [InlineData("/var/", "log", "/var/log")]
    public void 경로_결합(string dir, string name, string expected) => Assert.Equal(expected, SftpPanel.Combine(dir, name));

    [Theory]
    [InlineData("/home/user", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/", "/")]
    [InlineData("/var/log/", "/var")]
    public void 상위_경로(string path, string expected) => Assert.Equal(expected, SftpPanel.ParentPath(path));

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    public void 크기_표시(long bytes, string expected) => Assert.Equal(expected, SftpPanel.HumanSize(bytes));
}

public class RdpDefaultsTests
{
    [Fact]
    public void Default_rdp_값을_읽는다()
    {
        var d = RdpDefaults.Parse("session bpp:i:24\r\nconnection type:i:6\r\nnetworkautodetect:i:0\r\nbandwidthautodetect:i:0\r\n" +
                                  "allow font smoothing:i:1\r\ndisable wallpaper:i:1\r\nredirectprinters:i:0\r\nkeyboardhook:i:1\r\n" +
                                  "audiomode:i:2\r\ndrivestoredirect:s:*\r\nfull address:s:10.0.0.1:3389\r\n");
        Assert.Equal(24, d.ColorDepth);
        Assert.Equal(6, d.ConnectionType);
        Assert.False(d.BandwidthDetection);
        Assert.Equal(0x80 | 0x1, d.PerformanceFlags);
        Assert.False(d.RedirectPrinters);
        Assert.True(d.RedirectDrives);
        Assert.Equal(1, d.KeyboardHook);
        Assert.Equal(2, d.AudioMode);
    }

    [Fact]
    public void 자동_감지가_켜져_있으면_연결_종류는_자동()
    {
        var d = RdpDefaults.Parse("connection type:i:2\nnetworkautodetect:i:1\n");
        Assert.Equal(7, d.ConnectionType);
    }

    [Fact]
    public void 파일이_없으면_mstsc_기본값()
    {
        var d = RdpDefaults.Load(@"Z:\없는\경로\Default.rdp");
        Assert.False(d.FromFile);
        Assert.Equal(32, d.ColorDepth);
        Assert.Equal(7, d.ConnectionType);
        Assert.Equal(2, d.KeyboardHook);
    }

    [Fact]
    public void 이_PC의_Default_rdp를_읽을_수_있다()
    {
        if (!System.IO.File.Exists(RdpDefaults.DefaultPath)) return;
        var d = RdpDefaults.Load();
        Assert.True(d.FromFile);
        Assert.InRange(d.ColorDepth, 8, 32);
    }
}

public class ThemeTests
{
    [Fact]
    public void 모든_테마는_16색과_올바른_색상값을_가진다()
    {
        foreach (var t in TerminalThemes.All)
        {
            Assert.Equal(16, t.Ansi.Length);
            var x = t.ToXterm();
            Assert.Equal(21, x.Count);
            Assert.All(x.Values, v => Assert.Matches("^#[0-9A-Fa-f]{6}$", v));
        }
        Assert.Equal(TerminalThemes.DefaultId, TerminalThemes.Get("없는-테마").Id);
    }
}
