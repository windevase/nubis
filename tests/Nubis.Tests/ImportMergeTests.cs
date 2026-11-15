using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class ImportMergeTests
{
    private static readonly ImportDefaults D = new() { SshPort = 22, RdpPort = 3389 };

    private static ImportedHost H(string name, string address, int? port = null, Protocol? proto = null, string? group = null,
                                  string? user = null, string? jump = null, string? os = null) =>
        new() { Name = name, Address = address, Port = port, Protocol = proto, Group = group, Username = user, JumpHost = jump, Os = os };

    private static HostEntry Existing(string label, string address, int port = 22, Protocol proto = Protocol.Ssh, string group = "") =>
        new() { Label = label, Address = address, Port = port, Protocol = proto, Group = group };

    [Fact]
    public void 종류는_OS_나_3389_포트로_추정하고_기본은_SSH()
    {
        Assert.Equal(Protocol.Rdp, ImportMerge.ToHostEntry(H("w", "10.0.0.1", os: "Windows Server 2022 Datacenter"), D).Protocol);
        Assert.Equal(Protocol.Rdp, ImportMerge.ToHostEntry(H("w", "10.0.0.1", port: 3389), D).Protocol);
        Assert.Equal(Protocol.Ssh, ImportMerge.ToHostEntry(H("l", "10.0.0.1"), D).Protocol);
        // 출처에 종류가 있으면 그대로
        Assert.Equal(Protocol.Ssh, ImportMerge.ToHostEntry(H("x", "10.0.0.1", port: 3389, proto: Protocol.Ssh), D).Protocol);
    }

    [Fact]
    public void 포트가_없으면_설정의_기본_포트()
    {
        var d = D with { SshPort = 2222, RdpPort = 13389 };
        Assert.Equal(2222, ImportMerge.ToHostEntry(H("l", "a"), d).Port);
        Assert.Equal(13389, ImportMerge.ToHostEntry(H("w", "b", os: "Windows 10"), d).Port);
        Assert.Equal(2200, ImportMerge.ToHostEntry(H("l", "a", port: 2200), d).Port);
    }

    [Fact]
    public void 이름이_없으면_주소를_이름으로()
    {
        var h = ImportMerge.ToHostEntry(H("  ", " web01.example.com "), D);
        Assert.Equal("web01.example.com", h.Label);
        Assert.Equal("web01.example.com", h.Address);
    }

    [Fact]
    public void 그룹_접두어와_모두_이_그룹으로()
    {
        Assert.Equal("가져옴 / web", ImportMerge.ToHostEntry(H("a", "a", group: "web"), D with { GroupPrefix = "가져옴" }).Group);
        Assert.Equal("가져옴", ImportMerge.ToHostEntry(H("a", "a"), D with { GroupPrefix = "가져옴" }).Group);
        Assert.Equal("web", ImportMerge.ToHostEntry(H("a", "a", group: "web"), D).Group);
        Assert.Equal("ops", ImportMerge.ToHostEntry(H("a", "a", group: "web"), D with { GroupPrefix = "x", GroupOverride = "ops" }).Group);
        Assert.Equal("", ImportMerge.ToHostEntry(H("a", "a", group: "web"), D with { GroupOverride = "" }).Group);
    }

    [Fact]
    public void Vault_를_고르면_사용자명은_넣지_않고_SSH_RDP_따로()
    {
        var ssh = Guid.NewGuid();
        var rdp = Guid.NewGuid();
        var d = D with { SshVaultId = ssh, RdpVaultId = rdp };
        var l = ImportMerge.ToHostEntry(H("l", "a", user: "root"), d);
        var w = ImportMerge.ToHostEntry(H("w", "b", user: "admin", os: "Windows"), d);
        Assert.Equal(ssh, l.VaultId);
        Assert.Equal("", l.Username);
        Assert.Equal(rdp, w.VaultId);
        Assert.Equal("", w.Username);

        var noVault = ImportMerge.ToHostEntry(H("l", "a", user: "root"), D with { RdpVaultId = rdp });
        Assert.Null(noVault.VaultId);
        Assert.Equal("root", noVault.Username);
        Assert.Null(noVault.ProtectedPassword);
    }

    [Fact]
    public void 상태_새호스트_이미있음_중복_오류()
    {
        var existing = new List<HostEntry> { Existing("old", "10.0.0.1") };
        var plan = ImportMerge.Plan(new[]
        {
            H("a", "10.0.0.1"),             // 이미 있음
            H("b", "10.0.0.2"),             // 새 호스트
            H("c", "10.0.0.2"),             // 중복 (앞 줄과 같음)
            H("d", "10.0.0.2", port: 2222), // 포트가 달라 새 호스트
            H("e", ""),                     // 오류
            H("f", "bad host"),             // 오류
            H("g", "10.0.0.3", port: 70000),// 오류
            H("h", "10.0.0.1", os: "Windows"), // RDP 라 다른 호스트
        }, existing, D);

        Assert.Equal(new[]
        {
            ImportStatus.Existing, ImportStatus.New, ImportStatus.Duplicate, ImportStatus.New,
            ImportStatus.Invalid, ImportStatus.Invalid, ImportStatus.Invalid, ImportStatus.New,
        }, plan.Rows.Select(r => r.Status));
        Assert.Same(existing[0], plan.Rows[0].Existing);
        Assert.False(plan.Rows[2].IsSelectable);
        Assert.False(plan.Rows[4].IsSelected);
        Assert.True(plan.Rows[1].IsSelected);
    }

    [Fact]
    public void 기존_호스트_비교는_주소_대소문자_무시()
    {
        var existing = new List<HostEntry> { Existing("old", "Web01.Example.com") };
        var plan = ImportMerge.Plan(new[] { H("a", "web01.example.COM") }, existing, D);
        Assert.Equal(ImportStatus.Existing, plan.Rows[0].Status);
    }

    [Fact]
    public void 등록하면_새_호스트만_추가하고_기본은_기존_건너뜀()
    {
        var old = Existing("old", "10.0.0.1", group: "g0");
        var hosts = new List<HostEntry> { old };
        var plan = ImportMerge.Plan(new[] { H("new-name", "10.0.0.1", group: "g1"), H("b", "10.0.0.2", user: "ec2-user"), H("c", "") }, hosts, D);
        var r = ImportMerge.Apply(plan, plan.Rows.Where(x => x.IsSelected), hosts, updateExisting: false);

        Assert.Equal(1, r.Added);
        Assert.Equal(0, r.Updated);
        Assert.Equal(2, r.Skipped);
        Assert.Equal(2, hosts.Count);
        Assert.Equal("old", old.Label);
        Assert.Equal("g0", old.Group);
        var added = hosts[1];
        Assert.Equal("b", added.Label);
        Assert.Equal("ec2-user", added.Username);
        Assert.Null(added.ProtectedPassword);
    }

    [Fact]
    public void 갱신은_이름_그룹_사용자명만_바꾸고_Vault_는_그대로()
    {
        var vault = Guid.NewGuid();
        var old = Existing("old", "10.0.0.1", group: "g0");
        old.VaultId = vault;
        old.Username = "keep";
        old.KeyPath = @"C:\keys\id";
        var hosts = new List<HostEntry> { old };
        var plan = ImportMerge.Plan(new[] { H("renamed", "10.0.0.1", group: "g1", user: "admin") }, hosts, D with { SshVaultId = Guid.NewGuid() });
        var r = ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: true);

        Assert.Equal(0, r.Added);
        Assert.Equal(1, r.Updated);
        Assert.Single(hosts);
        Assert.Equal("renamed", old.Label);
        Assert.Equal("g1", old.Group);
        Assert.Equal("admin", old.Username);
        Assert.Equal(vault, old.VaultId);
        Assert.Equal(@"C:\keys\id", old.KeyPath);
        Assert.Equal(22, old.Port);
    }

    [Fact]
    public void 갱신할_때_가져온_그룹이_없으면_기존_그룹을_지우지_않는다()
    {
        var old = Existing("old", "10.0.0.1", group: "g0");
        var hosts = new List<HostEntry> { old };
        var plan = ImportMerge.Plan(new[] { H("renamed", "10.0.0.1") }, hosts, D);
        ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: true);
        Assert.Equal("g0", old.Group);
        Assert.Equal("renamed", old.Label);
    }

    [Fact]
    public void 클라우드_호스트는_갱신하지_않는다()
    {
        var cloud = new HostEntry
        {
            Label = "cloud", Address = "10.0.0.1", PrivateIp = "10.0.0.1", PublicIp = "3.3.3.3", Port = 22, Protocol = Protocol.Ssh,
            CloudProfileId = Guid.NewGuid(), CloudInstanceId = "i-1", Group = "",
        };
        var hosts = new List<HostEntry> { cloud };
        // 공인 IP 로 적어도 같은 호스트로 본다
        var plan = ImportMerge.Plan(new[] { H("x", "10.0.0.1", group: "g"), H("y", "3.3.3.3") }, hosts, D);
        Assert.All(plan.Rows, row => Assert.Equal(ImportStatus.Existing, row.Status));
        Assert.All(plan.Rows, row => Assert.False(row.IsSelectable));

        var r = ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: true);
        Assert.Equal(0, r.Added + r.Updated);
        Assert.Equal("cloud", cloud.Label);
        Assert.Equal("", cloud.Group);
        Assert.Single(hosts);
    }

    [Fact]
    public void 미리보기_뒤에_목록이_바뀌어도_중복_등록하지_않는다()
    {
        var hosts = new List<HostEntry>();
        var plan = ImportMerge.Plan(new[] { H("a", "10.0.0.1") }, hosts, D);
        hosts.Add(Existing("synced", "10.0.0.1"));
        var r = ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: false);
        Assert.Equal(0, r.Added);
        Assert.Single(hosts);

        // 같은 계획을 두 번 적용해도 한 번만
        var hosts2 = new List<HostEntry>();
        var plan2 = ImportMerge.Plan(new[] { H("a", "10.0.0.1") }, hosts2, D);
        ImportMerge.Apply(plan2, plan2.Rows, hosts2, false);
        ImportMerge.Apply(plan2, plan2.Rows, hosts2, false);
        Assert.Single(hosts2);
    }

    [Fact]
    public void 체크하지_않은_줄은_추가하지_않는다()
    {
        var hosts = new List<HostEntry>();
        var plan = ImportMerge.Plan(new[] { H("a", "10.0.0.1"), H("b", "10.0.0.2") }, hosts, D);
        plan.Rows[1].IsSelected = false;
        var r = ImportMerge.Apply(plan, plan.Rows.Where(x => x.IsSelected), hosts, false);
        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("a", Assert.Single(hosts).Label);
    }

    [Fact]
    public void 점프_서버는_가져온_이름_또는_기존_호스트로_연결()
    {
        var bastionOld = Existing("bastion-old", "1.1.1.1");
        var hosts = new List<HostEntry> { bastionOld };
        var plan = ImportMerge.Plan(new[]
        {
            H("app", "10.0.0.5", jump: "bastion"),           // 뒤에 오는 가져온 이름
            H("bastion", "2.2.2.2"),
            H("db", "10.0.0.6", jump: "bastion-old"),        // 기존 호스트 이름
            H("cache", "10.0.0.7", jump: "ops@1.1.1.1:22"),  // user@host:port
            H("lost", "10.0.0.8", jump: "nowhere"),          // 못 찾음
        }, hosts, D);
        Assert.Contains("nowhere", plan.Rows[4].Message);
        Assert.True(string.IsNullOrEmpty(plan.Rows[0].Message));

        var r = ImportMerge.Apply(plan, plan.Rows.Where(x => x.IsSelected), hosts, false);
        HostEntry Get(string label) => hosts.Single(h => h.Label == label);
        Assert.Equal(Get("bastion").Id, Get("app").JumpHostId);
        Assert.Equal(bastionOld.Id, Get("db").JumpHostId);
        Assert.Equal(bastionOld.Id, Get("cache").JumpHostId);
        Assert.Null(Get("lost").JumpHostId);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void 점프_서버를_체크하지_않으면_비워_두고_경고()
    {
        var hosts = new List<HostEntry>();
        var plan = ImportMerge.Plan(new[] { H("app", "10.0.0.5", jump: "bastion"), H("bastion", "2.2.2.2") }, hosts, D);
        plan.Rows[1].IsSelected = false;
        var r = ImportMerge.Apply(plan, plan.Rows.Where(x => x.IsSelected), hosts, false);
        Assert.Null(Assert.Single(hosts).JumpHostId);
        Assert.Single(r.Warnings);
    }

    [Theory]
    [InlineData("bastion", "bastion", null)]
    [InlineData("ops@bastion:2222", "bastion", 2222)]
    [InlineData("a@b,ops@jump2:22", "jump2", 22)]
    [InlineData("[fe80::1]:2200", "fe80::1", 2200)]
    [InlineData("fe80::1", "fe80::1", null)]
    public void 점프_서버_표기_해석(string spec, string host, int? port)
    {
        var (h, p) = ImportMerge.ParseJumpSpec(spec);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }
}
