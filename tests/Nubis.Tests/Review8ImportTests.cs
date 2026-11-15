using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>가져오기 리뷰(8차) — 7차 수정분 재검토: IPv6 괄호 이름 · 머리글 없는 표의 이름 판정 경계</summary>
public class Review8ImportTests
{
    private static readonly ImportDefaults D = new() { SshPort = 22, RdpPort = 3389 };

    [Theory]
    [InlineData("[fe80::1]", "[fe80::1]:2222")]
    [InlineData("fe80::1", "[fe80::1]:2222")]
    [InlineData("[fe80::1]:2222", "[fe80::1]:2222")]
    public void 괄호_포트가_붙은_IPv6_이름도_주소로_본다(string name, string address)
    {
        var old = new HostEntry { Label = "v6-box", Address = "fe80::1", Port = 2222, Protocol = Protocol.Ssh };
        var hosts = new List<HostEntry> { old };
        var plan = ImportMerge.Plan(new[] { new ImportedHost { Name = name, Address = address } }, hosts, D);
        ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: true);
        Assert.Equal("v6-box", old.Label);
    }

    [Fact]
    public void 주소가_아닌_이름은_기존_이름을_갱신한다()
    {
        var old = new HostEntry { Label = "old", Address = "10.0.0.1", Port = 22, Protocol = Protocol.Ssh };
        var hosts = new List<HostEntry> { old };
        var plan = ImportMerge.Plan(new[] { new ImportedHost { Name = "web01", Address = "10.0.0.1" } }, hosts, D);
        ImportMerge.Apply(plan, plan.Rows, hosts, updateExisting: true);
        Assert.Equal("web01", old.Label);
    }

    [Theory]
    [InlineData("10.0.0.1\tdb-01.prod", "db-01.prod")]
    [InlineData("10.0.0.1\tweb_01", "web_01")]
    [InlineData("Web01\t10.0.0.1", "Web01")]          // 주소 앞 칸은 대소문자 · 숫자가 섞여도 이름
    public void 머리글_없는_표_이름_칸(string line, string name)
    {
        var h = Assert.Single(TableImport.Parse(line + "\n").Hosts);
        Assert.Equal(name, h.Name);
    }

    /// <summary>
    /// 주소 뒤 "Web01" 같은 대소문자+숫자 이름은 "Welcome1" · "Summer2024" 같은 흔한 비밀번호와 구별할 수 없어 이름으로 쓰지 않는다
    /// (비밀번호가 호스트 이름으로 저장 · 동기화되는 것보다 이름이 주소로 보이는 쪽을 택함 — 이름 열을 앞에 두거나 머리글을 붙이면 된다)
    /// </summary>
    [Theory]
    [InlineData("10.0.0.1\tWeb01")]
    [InlineData("10.0.0.1\tWelcome1")]
    [InlineData("10.0.0.1\tSummer2024")]
    public void 주소_뒤_대소문자_숫자_혼합_칸은_이름으로_쓰지_않는다(string line)
    {
        var h = Assert.Single(TableImport.Parse(line + "\n").Hosts);
        Assert.Equal("10.0.0.1", h.Name);
    }

    [Fact]
    public void 머리글이_있으면_대소문자_숫자_혼합_이름도_그대로()
    {
        var h = Assert.Single(TableImport.Parse("IP\t이름\n10.0.0.1\tWeb01\n").Hosts);
        Assert.Equal("Web01", h.Name);
    }
}
