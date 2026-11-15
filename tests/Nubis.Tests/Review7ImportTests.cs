using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>가져오기 리뷰(7차) — 주소 이름으로 기존 이름 덮기 · 점프 서버 색인 · RDP 이름 점프 · Include 순환 · host_vars 목록 · 검색 텍스트</summary>
public class Review7ImportTests
{
    private static readonly ImportDefaults D = new() { SshPort = 22, RdpPort = 3389 };

    private static ImportedHost H(string name, string address, int? port = null, string? jump = null, Protocol? proto = null, string? user = null) =>
        new() { Name = name, Address = address, Port = port, JumpHost = jump, Protocol = proto, Username = user };

    // ───── 갱신: 주소로 채운 이름 ─────

    [Fact]
    public void 이름_칸이_없어_주소가_이름인_줄은_기존_이름을_주소로_덮지_않는다()
    {
        var old = new HostEntry { Label = "web01", Address = "10.0.0.1", Port = 22, Protocol = Protocol.Ssh, Group = "g0" };
        var hosts = new List<HostEntry> { old };
        // 표 가져오기는 이름 칸이 없으면 Name = 주소 (TableImport)
        var parsed = TableImport.Parse("IP\t그룹\n10.0.0.1\tprod\n").Hosts;
        Assert.Equal("10.0.0.1", parsed.Single().Name);
        var plan = ImportMerge.Plan(parsed, hosts, D);
        var r = ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: true);

        Assert.Equal(1, r.Updated);   // 그룹은 바뀐다
        Assert.Equal("web01", old.Label);
        Assert.Equal("prod", old.Group);
    }

    [Fact]
    public void 포트가_붙은_주소_이름도_주소로_본다()
    {
        var old = new HostEntry { Label = "bastion", Address = "jump.example.com", Port = 2222, Protocol = Protocol.Ssh };
        var hosts = new List<HostEntry> { old };
        var plan = ImportMerge.Plan(new[] { H("jump.example.com:2222", "jump.example.com:2222") }, hosts, D);
        ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: true);
        Assert.Equal("bastion", old.Label);
    }

    // ───── 점프 서버 ─────

    [Fact]
    public void 점프_서버는_목록의_이름_주소_포트로_찾고_먼저_나온_호스트가_이긴다()
    {
        var a = new HostEntry { Label = "bastion", Address = "10.9.0.1", Port = 22, Protocol = Protocol.Ssh };
        var b = new HostEntry { Label = "other", Address = "10.9.0.1", Port = 2222, Protocol = Protocol.Ssh };
        var rdp = new HostEntry { Label = "win-jump", Address = "10.9.0.5", Port = 3389, Protocol = Protocol.Rdp };
        var hosts = new List<HostEntry> { a, b, rdp };
        var plan = ImportMerge.Plan(new[]
        {
            H("x1", "10.0.0.1", jump: " BASTION "),
            H("x2", "10.0.0.2", jump: "admin@10.9.0.1:2222"),
            H("x3", "10.0.0.3", jump: "10.9.0.1"),
            H("x4", "10.0.0.4", jump: "win-jump"),
        }, hosts, D);
        var r = ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: false);

        HostEntry Added(string label) => hosts.Single(h => h.Label == label);
        Assert.Equal(a.Id, Added("x1").JumpHostId);
        Assert.Equal(b.Id, Added("x2").JumpHostId);
        Assert.Equal(a.Id, Added("x3").JumpHostId);
        Assert.Null(Added("x4").JumpHostId);   // RDP 호스트는 점프 서버가 될 수 없다
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void 가져온_RDP_항목_이름은_점프_서버로_찾았다고_미리_보이지_않는다()
    {
        var plan = ImportMerge.Plan(new[]
        {
            H("win", "10.0.0.5", proto: Protocol.Rdp),
            H("lin", "10.0.0.6", jump: "win"),
        }, new List<HostEntry>(), D);
        Assert.Contains("win", plan.Rows[1].Message);
    }

    [Fact]
    public void 점프_서버를_쓰는_호스트_수천_대도_빠르게_연결한다()
    {
        var existing = Enumerable.Range(0, 3000)
            .Select(i => new HostEntry { Label = "e" + i, Address = $"10.1.{i / 250}.{i % 250}", Port = 22, Protocol = Protocol.Ssh })
            .ToList();
        existing.Add(new HostEntry { Label = "bastion", Address = "10.9.9.9", Port = 22, Protocol = Protocol.Ssh });
        var imported = Enumerable.Range(0, 3000).Select(i => H("n" + i, $"10.2.{i / 250}.{i % 250}", jump: "10.9.9.9")).ToList();
        var sw = Stopwatch.StartNew();
        var plan = ImportMerge.Plan(imported, existing, D);
        var r = ImportMerge.Apply(plan, plan.Rows, existing, updateExisting: false);
        sw.Stop();
        Assert.Equal(3000, r.Added);
        Assert.Empty(r.Warnings);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
    }

    // ───── 미리보기 검색 ─────

    [Fact]
    public void 검색_텍스트에_사용자와_메모가_들어간다()
    {
        var plan = ImportMerge.Plan(new[] { new ImportedHost { Name = "web", Address = "10.0.0.1", Username = "deploy", Note = "결제 서버", Group = "prod" } },
            new List<HostEntry>(), D);
        var s = plan.Rows.Single().SearchText;
        Assert.Contains("deploy", s);
        Assert.Contains("결제 서버", s);
        Assert.Contains("prod", s);
        Assert.Same(s, plan.Rows.Single().SearchText);   // 한 번만 만든다
    }

    // ───── ssh config Include 순환 ─────

    [Fact]
    public void 서로_포함하는_glob_Include_는_한_번씩만_읽는다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-r7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "conf.d"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "config"), "Include conf.d/*\n");
            for (int i = 0; i < 6; i++)
                File.WriteAllText(Path.Combine(dir, "conf.d", $"h{i}"), $"Include conf.d/*\nHost h{i}\n    HostName 10.0.0.{i + 1}\n");
            var sw = Stopwatch.StartNew();
            var r = SshConfigImport.ParseFile(Path.Combine(dir, "config"));
            sw.Stop();
            Assert.Equal(6, r.Hosts.Count);
            Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
            Assert.NotEmpty(r.Warnings);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 같은_파일을_순환_없이_두_번_포함하는_것은_허용한다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-r7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "common"), "User shared\n");
            var r = SshConfigImport.Parse("Host a\n    Include common\nHost b\n    Include common\n", dir);
            Assert.All(r.Hosts, h => Assert.Equal("shared", h.Username));
            Assert.Empty(r.Warnings);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ───── Ansible host_vars ─────

    [Fact]
    public void host_vars_는_폴더에_있는_이름만_읽는다_대소문자_무시()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-r7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "host_vars", "h2"));
        Directory.CreateDirectory(Path.Combine(dir, "group_vars"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "host_vars", "H1.yml"), "ansible_port: 2201\n");
            File.WriteAllText(Path.Combine(dir, "host_vars", "h2", "main.yml"), "ansible_user: deploy\n");
            File.WriteAllText(Path.Combine(dir, "group_vars", "web.yaml"), "ansible_user: webuser\n");
            var r = AnsibleInventoryImport.Parse("[web]\nh1\nh2\nh3\n", dir);
            var byName = r.Hosts.ToDictionary(h => h.Name);
            Assert.Equal(2201, byName["h1"].Port);
            Assert.Equal("webuser", byName["h1"].Username);
            Assert.Equal("deploy", byName["h2"].Username);
            Assert.Null(byName["h3"].Port);
            Assert.Equal("webuser", byName["h3"].Username);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ───── 표: 구분자 ─────

    [Fact]
    public void 앞쪽_빈_줄이_많아도_구분자를_찾는다()
    {
        var r = TableImport.Parse("\n\n\n\n\n\n이름;주소;포트\nweb;10.0.0.1;2222\n");
        var h = Assert.Single(r.Hosts);
        Assert.Equal("web", h.Name);
        Assert.Equal(2222, h.Port);
    }
}
