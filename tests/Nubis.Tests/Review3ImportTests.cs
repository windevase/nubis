using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>가져오기 리뷰(3차) — UNC 우회 차단 · 병합 셀 채우기 범위 · 콜론 하나짜리 주소 · 다단계 점프 일관성 · 등록 Id · AD referral 안내</summary>
public class Review3ImportTests
{
    private static readonly ImportDefaults D = new() { SshPort = 22, RdpPort = 3389 };
    private static readonly string Data = Path.Combine(Path.GetTempPath(), "nubis-r3-data");
    private static readonly string Ssh = Path.Combine(Path.GetTempPath(), "nubis-r3-ssh");

    private static ImportedHost H(string name, string address, int? port = null, string? group = null) =>
        new() { Name = name, Address = address, Port = port, Group = group };

    // ───── MCP 경로 검사: 네트워크 경로(UNC) ─────

    [Theory]
    [InlineData(@"/\attacker.example\share\x")]
    [InlineData(@"\/attacker.example\share\x")]
    [InlineData(@"  \\attacker.example\share\inv.ini")]
    [InlineData(@"\\?\UNC\attacker.example\share\x")]
    public void 따라읽기는_구분자를_섞은_UNC_도_막는다(string path) =>
        Assert.NotNull(McpImport.BlockedFollowPathReason(path, Data, null, Ssh, true));

    [Theory]
    [InlineData(@"\\fileserver.example\share\hosts.csv")]
    [InlineData(@"//fileserver.example/share/hosts.csv")]
    [InlineData(@"/\fileserver.example\share\hosts.csv")]
    public void path_인자도_외부_UNC_는_막는다(string path) =>
        Assert.NotNull(McpImport.BlockedPathReason(path, Data, null, Ssh, false));

    [Fact]
    public void 로컬_경로는_그대로_허용()
    {
        Assert.Null(McpImport.BlockedPathReason(Path.Combine(Path.GetTempPath(), "hosts.csv"), Data, null, Ssh, false));
        Assert.Null(McpImport.BlockedPathReason(Path.GetTempPath().Replace('\\', '/') + "hosts.csv", Data, null, Ssh, false));
        Assert.False(McpImport.IsUncLike(@"C:\work\a.csv"));
        Assert.False(McpImport.IsUncLike("/"));
        Assert.True(McpImport.IsUncLike(@"\/x"));
    }

    // ───── TableImport: 병합 셀 채우기 ─────

    [Fact]
    public void 병합_셀_채우기는_빈_줄에서_끊는다()
    {
        var r = TableImport.Parse("그룹\t이름\tIP\n운영\ta\t10.0.0.1\n\tb\t10.0.0.2\n\n\tc\t10.0.0.3\n");
        Assert.Equal(new string?[] { "운영", "운영", null }, r.Hosts.Select(h => h.Group));
    }

    [Fact]
    public void 주소가_없어_건너뛴_줄의_그룹부터_아래로_채운다()
    {
        // "개발" 줄은 주소가 없어 건너뛰지만, 병합 셀의 첫 줄이므로 아래 빈 그룹 칸은 개발로
        var r = TableImport.Parse("그룹\t이름\tIP\n운영\ta\t10.0.0.1\n개발\t(예정)\t\n\tb\t10.0.0.2\n");
        Assert.Equal(new[] { "운영", "개발" }, r.Hosts.Select(h => h.Group));
    }

    [Fact]
    public void 그룹_열이_없으면_채우지_않는다()
    {
        var r = TableImport.Parse("이름\tIP\na\t10.0.0.1\nb\t10.0.0.2\n");
        Assert.All(r.Hosts, h => Assert.Null(h.Group));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("병합"));
    }

    // ───── ImportMerge ─────

    [Theory]
    [InlineData("host:99999")]
    [InlineData("host:ssh")]
    [InlineData("10.0.0.1:0")]
    public void 콜론이_하나인데_포트가_아니면_오류(string address)
    {
        var plan = ImportMerge.Plan(new[] { H("x", address) }, new List<HostEntry>(), D);
        Assert.Equal(ImportStatus.Invalid, Assert.Single(plan.Rows).Status);
    }

    [Theory]
    [InlineData("fe80::1", "fe80::1", 22)]
    [InlineData("::1", "::1", 22)]
    [InlineData("[::1]", "::1", 22)]
    [InlineData("[fe80::1%eth0]:2200", "fe80::1%eth0", 2200)]
    [InlineData("web01:2222", "web01", 2222)]
    public void IPv6_와_host_port(string address, string host, int port)
    {
        var plan = ImportMerge.Plan(new[] { H("x", address) }, new List<HostEntry>(), D);
        var row = Assert.Single(plan.Rows);
        Assert.Equal(ImportStatus.New, row.Status);
        Assert.Equal(host, row.Host!.Address);
        Assert.Equal(port, row.Host.Port);
    }

    [Fact]
    public void 등록_결과에_추가_갱신한_호스트_Id()
    {
        var old = new HostEntry { Label = "old", Address = "10.0.0.1", Port = 22, Protocol = Protocol.Ssh };
        var hosts = new List<HostEntry> { old };
        var plan = ImportMerge.Plan(new[] { H("renamed", "10.0.0.1"), H("new", "10.0.0.2"), H("same", "10.0.0.1") }, hosts, D);
        var r = ImportMerge.Apply(plan, plan.Rows.Where(x => x.IsSelectable), hosts, updateExisting: true);
        Assert.Equal(1, r.Added);
        Assert.Equal(new[] { hosts.Single(h => h.Address == "10.0.0.2").Id }, r.AddedIds);
        Assert.Equal(new[] { old.Id }, r.UpdatedIds);
        // 갱신하지 않으면 Id 도 없다
        var none = ImportMerge.Apply(plan, Array.Empty<ImportRow>(), hosts, updateExisting: true);
        Assert.Empty(none.AddedIds);
        Assert.Empty(none.UpdatedIds);
    }

    // ───── Ansible ─────

    [Fact]
    public void Ansible_다단계_점프는_ssh_config_와_같이_첫_홉()
    {
        var (jump, multi) = AnsibleInventoryImport.FindJumpHost("-o ProxyJump=ssh://a.example.com,b.example.com");
        Assert.Equal("a.example.com", jump);
        Assert.True(multi);
        Assert.Equal("first", AnsibleInventoryImport.FindJumpHost("-J first,second").Jump);
    }

    [Fact]
    public void 점으로_끝나는_그룹_이름은_group_vars_폴더를_통째로_읽지_않는다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-r3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "group_vars"));
        try
        {
            // "web." 은 Windows 에서 끝의 점이 떼어져 group_vars/web 파일을 가리킨다 — 다른 그룹 변수가 섞이면 안 됨
            File.WriteAllText(Path.Combine(dir, "group_vars", "web"), "ansible_port: 2222\n");
            var r = AnsibleInventoryImport.Parse("[web.]\nh1 ansible_host=10.0.0.1\n", dir);
            Assert.Null(r.Hosts.Single().Port);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ───── 인코딩 ─────

    [Fact]
    public void ACP_가_UTF8_이어도_레거시_코드_페이지로_읽는다()
    {
        // 65001 은 건너뛰고 설치된 Windows 언어의 코드 페이지(없으면 Latin-1) — 어떤 경우에도 UTF-8 로 다시 읽지 않는다
        var e = ImportText.AnsiEncoding(65001);
        Assert.NotEqual(65001, e.CodePage);
        Assert.Equal(949, ImportText.AnsiEncoding(949).CodePage);
        // CP949 "이름" 바이트 → 949 로 읽으면 한글
        Assert.Equal("이름", ImportText.AnsiEncoding(949).GetString(new byte[] { 0xC0, 0xCC, 0xB8, 0xA7 }));
    }

    // ───── Active Directory ─────

    [Fact]
    public void AD_referral_안내는_검색_기준과_도메인을_짚는다()
    {
        var outside = ActiveDirectoryImport.ReferralMessage("OU=Servers,DC=other,DC=com", "corp.example.com");
        Assert.Contains("DC=corp,DC=example,DC=com", outside);
        Assert.Contains("OU=Servers,DC=other,DC=com", outside);
        var child = ActiveDirectoryImport.ReferralMessage("OU=x, DC=child,DC=corp, DC=example,DC=com", "corp.example.com");
        Assert.DoesNotContain("(DC=corp", child);
        Assert.Contains("OU=x", child);
    }
}
