using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>AD 가져오기의 순수 로직 (LDAP 연결 없음): 필터 · DN → 그룹 · 도메인 → 기준 DN · 속성 → 호스트</summary>
public class ActiveDirectoryImportTests
{
    private static Dictionary<string, string?> Attrs(string? name, string? dns, string? os, string? ver = null, string? desc = null, string? dn = null) =>
        new()
        {
            ["name"] = name, ["dNSHostName"] = dns, ["operatingSystem"] = os,
            ["operatingSystemVersion"] = ver, ["description"] = desc, ["distinguishedName"] = dn,
        };

    // ── BuildFilter ──

    [Fact]
    public void 필터_조건없음은_컴퓨터만()
    {
        Assert.Equal("(&(objectCategory=computer))", ActiveDirectoryImport.BuildFilter(false, false, null));
    }

    [Fact]
    public void 필터_서버만은_WindowsServer_또는_비Windows()
    {
        Assert.Equal("(&(objectCategory=computer)(|(operatingSystem=*Server*)(!(operatingSystem=*Windows*))))",
            ActiveDirectoryImport.BuildFilter(true, false, null));
    }

    [Fact]
    public void 필터_사용안함_제외()
    {
        Assert.Equal("(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))",
            ActiveDirectoryImport.BuildFilter(false, true, null));
    }

    [Fact]
    public void 필터_최근로그온()
    {
        Assert.Equal("(&(objectCategory=computer)(lastLogonTimestamp>=133000000000000000))",
            ActiveDirectoryImport.BuildFilter(false, false, 133000000000000000));
    }

    [Fact]
    public void 필터_모든조건_조합()
    {
        Assert.Equal(
            "(&(objectCategory=computer)(|(operatingSystem=*Server*)(!(operatingSystem=*Windows*)))(!(userAccountControl:1.2.840.113556.1.4.803:=2))(lastLogonTimestamp>=42))",
            ActiveDirectoryImport.BuildFilter(true, true, 42));
    }

    // ── GroupFromDn ──

    [Fact]
    public void 그룹은_OU경로를_위에서부터()
    {
        Assert.Equal("Servers / Web", ActiveDirectoryImport.GroupFromDn("CN=web01,OU=Web,OU=Servers,DC=corp,DC=com"));
    }

    [Fact]
    public void 그룹_EntraDS_기본OU()
    {
        Assert.Equal("AADDC Computers", ActiveDirectoryImport.GroupFromDn("CN=vm01,OU=AADDC Computers,DC=aaddscontoso,DC=com"));
    }

    [Fact]
    public void 그룹_기본Computers_컨테이너()
    {
        Assert.Equal("Computers", ActiveDirectoryImport.GroupFromDn("CN=pc01,CN=Computers,DC=corp,DC=com"));
    }

    [Fact]
    public void 그룹_이스케이프된_쉼표()
    {
        Assert.Equal("Seoul, KR / Web", ActiveDirectoryImport.GroupFromDn(@"CN=web\,01,OU=Web,OU=Seoul\, KR,DC=corp,DC=com"));
    }

    [Fact]
    public void 그룹_16진_이스케이프()
    {
        // \2C = ',' , \EC\84\9C\EB\B2\84 = "서버" (UTF-8)
        Assert.Equal("A,B / 서버", ActiveDirectoryImport.GroupFromDn(@"CN=x,OU=\EC\84\9C\EB\B2\84,OU=A\2CB,DC=corp,DC=com"));
    }

    [Fact]
    public void 그룹_도메인바로아래면_null()
    {
        Assert.Null(ActiveDirectoryImport.GroupFromDn("CN=web01,DC=corp,DC=com"));
        Assert.Null(ActiveDirectoryImport.GroupFromDn(""));
    }

    // ── DomainToBaseDn ──

    [Fact]
    public void 도메인을_기준DN으로()
    {
        Assert.Equal("DC=corp,DC=example,DC=com", ActiveDirectoryImport.DomainToBaseDn("corp.example.com"));
        Assert.Equal("DC=corp,DC=com", ActiveDirectoryImport.DomainToBaseDn(" corp.com. "));
    }

    [Fact]
    public void 이미_DN이면_그대로()
    {
        Assert.Equal("OU=Servers,DC=corp,DC=com", ActiveDirectoryImport.DomainToBaseDn("OU=Servers,DC=corp,DC=com"));
    }

    // ── MapComputer ──

    [Fact]
    public void WindowsServer는_RDP()
    {
        var h = ActiveDirectoryImport.MapComputer(Attrs("WEB01", "web01.corp.com", "Windows Server 2022 Datacenter", "10.0 (20348)",
            "웹 서버", "CN=WEB01,OU=Web,OU=Servers,DC=corp,DC=com"));
        Assert.NotNull(h);
        Assert.Equal("WEB01", h!.Name);
        Assert.Equal("web01.corp.com", h.Address);
        Assert.Equal(Protocol.Rdp, h.Protocol);
        Assert.Equal("Windows Server 2022 Datacenter 10.0 (20348)", h.Os);
        Assert.Equal("Servers / Web", h.Group);
        Assert.Equal("웹 서버", h.Note);
        Assert.Null(h.Port);
    }

    [Fact]
    public void 리눅스는_SSH()
    {
        var h = ActiveDirectoryImport.MapComputer(Attrs("app01", "app01.corp.com", "Linux", "Rocky 9"));
        Assert.Equal(Protocol.Ssh, h!.Protocol);
        Assert.Equal("Linux Rocky 9", h.Os);
        Assert.Null(h.Group);
        Assert.Null(h.Note);
    }

    [Fact]
    public void OS가_없으면_SSH와_메모()
    {
        var h = ActiveDirectoryImport.MapComputer(Attrs("db01", "db01.corp.com", "", desc: "DB"));
        Assert.Equal(Protocol.Ssh, h!.Protocol);
        Assert.Null(h.Os);
        Assert.StartsWith("DB · ", h.Note!);
        Assert.Contains("OS", h.Note!);
    }

    [Fact]
    public void DNS이름이_없으면_이름을_주소로()
    {
        var h = ActiveDirectoryImport.MapComputer(Attrs("PC01", null, "Windows 11 Enterprise"));
        Assert.Equal("PC01", h!.Address);
        Assert.Equal(Protocol.Rdp, h.Protocol);
    }

    [Fact]
    public void 이름이_없으면_DNS첫부분()
    {
        var h = ActiveDirectoryImport.MapComputer(Attrs(null, "host9.corp.com", "Windows Server 2019"));
        Assert.Equal("host9", h!.Name);
        Assert.Equal("host9.corp.com", h.Address);
    }

    [Fact]
    public void 이름도_DNS도_없으면_null()
    {
        Assert.Null(ActiveDirectoryImport.MapComputer(Attrs(null, " ", "Linux")));
    }

    [Fact]
    public void 속성이름_대소문자_무시()
    {
        var h = ActiveDirectoryImport.MapComputer(new Dictionary<string, string?>
        {
            ["NAME"] = "x1", ["dnshostname"] = "x1.corp.com", ["OperatingSystem"] = "Windows Server 2016",
        });
        Assert.Equal("x1.corp.com", h!.Address);
        Assert.Equal(Protocol.Rdp, h.Protocol);
    }
}
