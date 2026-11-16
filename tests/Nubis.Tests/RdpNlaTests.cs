using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>RDP NLA(CredSSP) 끄기 — Entra ID(Azure AD) 가입 PC 접속 (.rdp 의 enablecredsspsupport:i:0)</summary>
[Collection("settings")]
public class RdpNlaTests
{
    [Fact]
    public void Default_rdp_의_enablecredsspsupport_를_읽는다()
    {
        Assert.True(RdpDefaults.Parse("full address:s:10.0.0.5\r\n").CredSsp);   // 없으면 켬 (mstsc 기본)
        Assert.False(RdpDefaults.Parse("username:s:AzureAD\\user@example.com\r\nenablecredsspsupport:i:0\r\n").CredSsp);
        Assert.True(RdpDefaults.Parse("enablecredsspsupport:i:1").CredSsp);
    }

    [Fact]
    public void 호스트에서_끄거나_Default_rdp_에서_끄면_NLA_를_쓰지_않는다()
    {
        var on = RdpDefaults.Parse("");
        var off = RdpDefaults.Parse("enablecredsspsupport:i:0");
        var host = new HostEntry { Protocol = Protocol.Rdp };
        Assert.True(on.CredSspFor(host));
        Assert.False(off.CredSspFor(host));
        host.RdpDisableNla = true;
        Assert.False(on.CredSspFor(host));
        // 복제·접속용 사본에도 따라간다
        Assert.True(host.Clone().RdpDisableNla);
    }

    [Theory]
    [InlineData(@"AzureAD\user@example.com", true)]
    [InlineData(@"azuread\user@example.com", true)]
    [InlineData(@" AzureAD\user@example.com", true)]
    [InlineData(@"CORP\user", false)]
    [InlineData("user@example.com", false)]
    [InlineData(null, false)]
    public void Entra_사용자명_판별(string? user, bool expected) => Assert.Equal(expected, RdpDefaults.IsEntraUser(user));

    [Fact]
    public void Claude_가_NLA_를_바꾸면_위험_변경으로_경고한다()
    {
        Assert.True(McpHostRules.IsRisky($"{McpHostRules.NlaLabel}: 켬 → 끔"));
        Assert.False(McpHostRules.IsFavoriteOnly(new List<string> { $"{McpHostRules.NlaLabel}: 켬 → 끔" }));
    }
}
