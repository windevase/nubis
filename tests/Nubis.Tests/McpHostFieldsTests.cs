using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>add_host / update_host 의 즐겨찾기 · 점프 서버 · RD 게이트웨이 · 기억한 답 판정 (McpHostRules)</summary>
public class McpHostFieldsTests
{
    private static HostEntry Ssh(string name) => new() { Label = name, Address = name + ".example", Protocol = Protocol.Ssh, Port = 22 };
    private static HostEntry Rdp(string name) => new() { Label = name, Address = name + ".example", Protocol = Protocol.Rdp, Port = 3389 };

    [Fact]
    public void JumpError_AllowsPlainSshJump()
    {
        Assert.Null(McpHostRules.JumpError(Ssh("app"), Ssh("bastion")));
    }

    [Fact]
    public void JumpError_RejectsSelf()
    {
        var h = Ssh("app");
        Assert.NotNull(McpHostRules.JumpError(h, h));
        // 같은 Id 의 사본(update_host 의 Clone)도 자기 자신
        Assert.NotNull(McpHostRules.JumpError(h.Clone(), h));
    }

    [Fact]
    public void JumpError_RejectsNonSshTargetOrJump()
    {
        Assert.NotNull(McpHostRules.JumpError(Rdp("win"), Ssh("bastion")));
        Assert.NotNull(McpHostRules.JumpError(Ssh("app"), Rdp("win")));
    }

    [Fact]
    public void JumpError_OneHopOnly()
    {
        var outer = Ssh("outer");
        var inner = Ssh("inner");
        inner.JumpHostId = outer.Id;
        var app = Ssh("app");
        Assert.NotNull(McpHostRules.JumpError(app, inner));
        // 서로를 점프 서버로 (순환) 도 막힌다
        var a = Ssh("a");
        var b = Ssh("b");
        b.JumpHostId = a.Id;
        Assert.NotNull(McpHostRules.JumpError(a, b));
        // 이미 지정돼 있던 점프 서버는 편집 창처럼 그대로 허용
        app.JumpHostId = inner.Id;
        Assert.Null(McpHostRules.JumpError(app, inner));
    }

    [Theory]
    [InlineData("gw.example.com", "gw.example.com")]
    [InlineData("  gw.example.com:443 ", "gw.example.com:443")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("[2001:db8::1]:443", "[2001:db8::1]:443")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]")]
    public void NormalizeGateway_AcceptsHostAndPort(string input, string expected)
    {
        Assert.Equal(expected, McpHostRules.NormalizeGateway(input, out var err));
        Assert.Null(err);
    }

    [Fact]
    public void NormalizeGateway_EmptyClears()
    {
        Assert.Null(McpHostRules.NormalizeGateway("   ", out var err));
        Assert.Null(err);
    }

    [Theory]
    [InlineData("gw.example.com:0")]
    [InlineData("gw.example.com:70000")]
    [InlineData("gw.example.com:+443")]
    [InlineData("gw example.com")]
    [InlineData("user@gw.example.com")]
    [InlineData("gw.example.com;evil")]
    [InlineData("\\\\server\\share")]
    [InlineData("https://gw.example.com")]
    [InlineData("2001:db8::1")]
    [InlineData("[not-ipv6]:443")]
    [InlineData("[2001:db8::1]x")]
    [InlineData(":443")]
    [InlineData("-gw")]
    [InlineData("gw\u202Eexample")]
    [InlineData("gw\u200Bexample")]
    [InlineData("gw\u2028example")]
    [InlineData("gw\texample")]
    [InlineData("gw.ex\u0430mple.com")]
    [InlineData("\uFF47w.example.com")]
    public void NormalizeGateway_RejectsBadValues(string input)
    {
        Assert.Null(McpHostRules.NormalizeGateway(input, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void FavoriteOnly_SkipsApprovalOnlyForFavoriteChanges()
    {
        Assert.True(McpHostRules.IsFavoriteOnly(new[] { "즐겨찾기: 아니오 → 예" }));
        Assert.False(McpHostRules.IsFavoriteOnly(Array.Empty<string>()));
        Assert.False(McpHostRules.IsFavoriteOnly(new[] { "즐겨찾기: 아니오 → 예", "이름: a → b" }));
        Assert.False(McpHostRules.IsFavoriteOnly(new[] { "즐겨찾기: 아니오 → 예", McpHostRules.JumpLabel + ": 없음 → x" }));
        Assert.False(McpHostRules.IsFavoriteOnly(new[] { McpHostRules.AnswersLabel + ": 2개 → 지움" }));
    }

    [Fact]
    public void Risky_IncludesJumpAndGateway()
    {
        Assert.True(McpHostRules.IsRisky(McpHostRules.JumpLabel + ": 없음 → bastion (10.0.0.1)"));
        Assert.True(McpHostRules.IsRisky(McpHostRules.GatewayLabel + ": 없음 → gw.example.com"));
        Assert.True(McpHostRules.IsRisky("주소: a → b"));
        Assert.True(McpHostRules.IsRisky("Vault 계정: 없음 → ops"));
        Assert.False(McpHostRules.IsRisky("즐겨찾기: 아니오 → 예"));
        Assert.False(McpHostRules.IsRisky("이름: a → b"));
        Assert.False(McpHostRules.IsRisky(McpHostRules.AnswersLabel + ": 2개 → 지움"));
    }
}
