using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>가져오기 리뷰(2차)에서 고친 동작 — 엑셀 붙여넣기 변형 · 주소의 포트 · 그룹 갱신 · Include/group_vars 차단 · 인코딩</summary>
public class Review2ImportTests
{
    private static readonly ImportDefaults D = new() { SshPort = 22, RdpPort = 3389 };

    private static ImportedHost H(string name, string address, int? port = null, string? group = null) =>
        new() { Name = name, Address = address, Port = port, Group = group };

    // ───── TableImport ─────

    [Fact]
    public void 전각_IP_와_천단위_쉼표_포트를_읽는다()
    {
        var r = TableImport.Parse("이름\tIP주소\t접속포트\nweb\t１０．０．０．５\t3,389\n");
        var h = Assert.Single(r.Hosts);
        Assert.Equal("10.0.0.5", h.Address);
        Assert.Equal(3389, h.Port);
        Assert.Equal(Protocol.Rdp, h.Protocol);
    }

    [Fact]
    public void IP_칸의_공백과_설명을_정리한다()
    {
        var r = TableImport.Parse("name\tip\na\t10. 0. 0. 1\nb\t10.0.0.2 (VIP)\n");
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, r.Hosts.Select(h => h.Address));
    }

    [Fact]
    public void 병합된_그룹_칸은_위_줄의_그룹으로_채우고_경고()
    {
        var r = TableImport.Parse("구분\t서버 이름\tIP\n운영\ta\t10.0.0.1\n\tb\t10.0.0.2\n개발\tc\t10.0.0.3\n");
        Assert.Equal(new[] { "운영", "운영", "개발" }, r.Hosts.Select(h => h.Group));
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void OS_종류_와_IP_사설_머리글()
    {
        var r = TableImport.Parse("서버 이름,IP(사설),OS 종류\nw1,10.0.0.9,Windows Server 2022\n");
        var h = Assert.Single(r.Hosts);
        Assert.Equal("10.0.0.9", h.Address);
        Assert.Equal("Windows Server 2022", h.Os);
        Assert.Equal(Protocol.Rdp, h.Protocol);
    }

    // ───── ImportMerge ─────

    [Fact]
    public void 주소에_붙은_포트를_나눈다()
    {
        var e = ImportMerge.ToHostEntry(H("a", "host.example.com:2222"), D);
        Assert.Equal("host.example.com", e.Address);
        Assert.Equal(2222, e.Port);
        // 포트가 따로 있으면 그것이 우선
        Assert.Equal(2200, ImportMerge.ToHostEntry(H("a", "host:2222", port: 2200), D).Port);
        // IPv6 는 그대로, 대괄호는 벗긴다
        Assert.Equal("fe80::1", ImportMerge.ToHostEntry(H("a", "fe80::1"), D).Address);
        var v6 = ImportMerge.ToHostEntry(H("a", "[2001:db8::1]:2022"), D);
        Assert.Equal("2001:db8::1", v6.Address);
        Assert.Equal(2022, v6.Port);
        // 3389 가 주소에 붙어 있으면 RDP 로 추정
        Assert.Equal(Protocol.Rdp, ImportMerge.ToHostEntry(H("w", "10.0.0.1:3389"), D).Protocol);
    }

    [Fact]
    public void 주소_포트_형태도_기존_호스트와_대조한다()
    {
        var hosts = new List<HostEntry> { new() { Label = "old", Address = "10.0.0.1", Port = 2222, Protocol = Protocol.Ssh } };
        var plan = ImportMerge.Plan(new[] { H("x", "10.0.0.1:2222") }, hosts, D);
        Assert.Equal(ImportStatus.Existing, plan.Rows[0].Status);
    }

    [Fact]
    public void 갱신할_때_앞에_붙일_이름만으로_기존_그룹을_바꾸지_않는다()
    {
        var old = new HostEntry { Label = "old", Address = "10.0.0.1", Port = 22, Protocol = Protocol.Ssh, Group = "g0" };
        var hosts = new List<HostEntry> { old };
        var plan = ImportMerge.Plan(new[] { H("renamed", "10.0.0.1") }, hosts, D with { GroupPrefix = "가져옴" });
        ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: true);
        Assert.Equal("g0", old.Group);

        // 가져온 그룹이 있으면 앞에 붙여 갱신, "모두 이 그룹으로" 는 항상 그 값
        var plan2 = ImportMerge.Plan(new[] { H("renamed", "10.0.0.1", group: "web") }, hosts, D with { GroupPrefix = "가져옴" });
        ImportMerge.Apply(plan2, plan2.Rows, hosts, updateExisting: true);
        Assert.Equal("가져옴 / web", old.Group);
        var plan3 = ImportMerge.Plan(new[] { H("renamed", "10.0.0.1") }, hosts, D with { GroupOverride = "" });
        ImportMerge.Apply(plan3, plan3.Rows, hosts, updateExisting: true);
        Assert.Equal("", old.Group);
    }

    [Fact]
    public void 큰_가져오기도_색인으로_대조한다()
    {
        var hosts = Enumerable.Range(0, 3000)
            .Select(i => new HostEntry { Label = "e" + i, Address = $"10.1.{i / 256}.{i % 256}", Port = 22, Protocol = Protocol.Ssh, PrivateIp = $"172.16.{i / 256}.{i % 256}" })
            .ToList();
        var imported = Enumerable.Range(0, 3000).Select(i => H("n" + i, $"172.16.{i / 256}.{i % 256}")).ToList();
        imported.Add(H("new", "192.168.0.1"));
        var plan = ImportMerge.Plan(imported, hosts, D);
        Assert.Equal(3000, plan.Rows.Count(r => r.Status == ImportStatus.Existing));
        Assert.Same(hosts[1234], plan.Rows[1234].Existing);
        var r = ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: false);
        Assert.Equal(1, r.Added);
        Assert.Equal(3001, hosts.Count);
    }

    // ───── ssh_config ─────

    [Fact]
    public void 다단계_ProxyJump_는_첫_홉과_경고()
    {
        var r = SshConfigImport.Parse("Host t\n  HostName 10.0.0.1\n  ProxyJump ssh://a.example.com,b\n");
        Assert.Equal("a.example.com", r.Hosts[0].JumpHost);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Include_검사에서_막으면_파일을_읽지_않는다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-r2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "inc"), "Host secret-host\n  HostName 10.9.9.9\n");
            var r = SshConfigImport.Parse("Include inc\nHost a\n  HostName 10.0.0.1\n", dir, _ => "blocked");
            Assert.DoesNotContain(r.Hosts, h => h.Name == "secret-host");
            Assert.Contains(r.Warnings, w => w.Contains("blocked"));
            // 검사가 없으면 읽는다
            Assert.Contains(SshConfigImport.Parse("Include inc\n", dir).Hosts, h => h.Name == "secret-host");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MCP_따라읽기는_네트워크_경로를_막는다()
    {
        var data = Path.Combine(Path.GetTempPath(), "nubis-r2-data");
        var ssh = Path.Combine(Path.GetTempPath(), "nubis-r2-ssh");
        Assert.NotNull(McpImport.BlockedFollowPathReason(@"\\attacker.example\share\x", data, null, ssh, true));
        Assert.NotNull(McpImport.BlockedFollowPathReason("//attacker.example/share/x", data, null, ssh, true));
        Assert.NotNull(McpImport.BlockedFollowPathReason(Path.Combine(data, "vault.json"), data, null, ssh, true));
        Assert.Null(McpImport.BlockedFollowPathReason(Path.Combine(Path.GetTempPath(), "inv.ini"), data, null, ssh, false));
    }

    // ───── Ansible group_vars · host_vars ─────

    [Fact]
    public void 인벤토리_옆의_group_vars_와_host_vars_를_읽는다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-r2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "group_vars", "web"));
        Directory.CreateDirectory(Path.Combine(dir, "host_vars"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "hosts.ini"), "[web]\nweb1 ansible_host=10.0.0.1\nweb2\n");
            File.WriteAllText(Path.Combine(dir, "group_vars", "all.yml"), "ansible_user: base\n");
            File.WriteAllText(Path.Combine(dir, "group_vars", "web", "main.yml"), "ansible_port: 2222\nansible_password: secret\n");
            File.WriteAllText(Path.Combine(dir, "host_vars", "web2.yaml"), "ansible_host: 10.0.0.2\nansible_user: web2user\n");
            File.WriteAllText(Path.Combine(dir, "host_vars", "web1"), "$ANSIBLE_VAULT;1.1;AES256\n6162\n");

            var r = AnsibleInventoryImport.ParseFile(Path.Combine(dir, "hosts.ini"));
            var w1 = r.Hosts.Single(h => h.Name == "web1");
            var w2 = r.Hosts.Single(h => h.Name == "web2");
            Assert.Equal("10.0.0.1", w1.Address);
            Assert.Equal(2222, w1.Port);
            Assert.Equal("base", w1.Username);
            Assert.Equal("10.0.0.2", w2.Address);
            Assert.Equal("web2user", w2.Username);
            Assert.DoesNotContain(r.Hosts, h => (h.Note ?? "").Contains("secret"));
            Assert.Contains(r.Warnings, w => w.Contains("web1"));   // vault 파일은 건너뜀

            // 검사에서 막으면 읽지 않는다
            var blocked = AnsibleInventoryImport.Parse(File.ReadAllText(Path.Combine(dir, "hosts.ini")), dir, _ => "no");
            Assert.Null(blocked.Hosts.Single(h => h.Name == "web1").Port);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ───── 인코딩 ─────

    [Fact]
    public void 파일_인코딩_판별()
    {
        Assert.Equal("이름", ImportText.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("이름")).ToArray()));
        Assert.Equal("이름", ImportText.Decode(Encoding.UTF8.GetBytes("이름")));
        Assert.Equal("이름", ImportText.Decode(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("이름")).ToArray()));
        // UTF-8 이 아닌 바이트도 예외 없이 읽는다 (시스템 코드 페이지)
        Assert.NotNull(ImportText.Decode(new byte[] { 0xC0, 0xCC, 0xB8, 0xA7 }));
    }

    // ───── mRemoteNG ─────

    [Fact]
    public void mRemoteNG_RDP_도메인을_사용자명에_합친다()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.8">
              <Node Name="dc" Type="Connection" Protocol="RDP" Hostname="10.0.0.1" Port="0" Username="admin" Domain="CORP" />
              <Node Name="ssh" Type="Connection" Protocol="SSH2" Hostname="10.0.0.2" Port="22" Username="root" Domain="CORP" />
            </mrng:Connections>
            """;
        var r = MRemoteNgImport.Parse(xml);
        Assert.Equal(@"CORP\admin", r.Hosts.Single(h => h.Name == "dc").Username);
        Assert.Equal(3389, r.Hosts.Single(h => h.Name == "dc").Port);
        Assert.Equal("root", r.Hosts.Single(h => h.Name == "ssh").Username);
    }
}
