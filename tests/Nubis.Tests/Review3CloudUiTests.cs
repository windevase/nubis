using System;
using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리뷰 3차: 클라우드 화면 · 클라우드 MCP 도구 (동기화로 지워진 점프 호스트 정리 · 로그인 인자 '/' 허용)</summary>
public class Review3CloudUiTests
{
    private static readonly Guid Profile = Guid.NewGuid();

    private static HostEntry Cloud(string label, string instance, Protocol proto = Protocol.Ssh) =>
        new() { Label = label, CloudProfileId = Profile, CloudInstanceId = instance, Protocol = proto, Address = "10.0.0.1" };

    [Fact]
    public void 중복_정리로_지워진_점프_호스트는_남은_같은_인스턴스로_옮긴다()
    {
        var keep = Cloud("bastion", "i-1");
        var dup = Cloud("bastion", "i-1");
        var target = new HostEntry { Label = "app", Address = "10.0.0.9", JumpHostId = dup.Id };
        var hosts = new List<HostEntry> { keep, target };   // dup 은 이미 목록에서 제거됨

        var dangling = CloudJumpFix.Apply(hosts, new[] { dup });

        Assert.Empty(dangling);
        Assert.Equal(keep.Id, target.JumpHostId);
    }

    [Fact]
    public void 서버가_사라진_점프_지정은_그대로_두고_알린다()
    {
        var gone = Cloud("bastion", "i-1");
        var target = new HostEntry { Label = "app", Address = "10.0.0.9", JumpHostId = gone.Id };
        var other = new HostEntry { Label = "db", Address = "10.0.0.8" };
        var hosts = new List<HostEntry> { target, other };

        var dangling = CloudJumpFix.Apply(hosts, new[] { gone });

        Assert.Equal(new[] { target }, dangling);
        // 지우지 않는다 — 지우면 다음 접속이 점프 없이 직접 연결을 시도한다 (접속 때 "삭제된 점프 서버" 오류가 나게 둠)
        Assert.Equal(gone.Id, target.JumpHostId);
        Assert.Null(other.JumpHostId);
    }

    [Fact]
    public void 남은_같은_인스턴스가_RDP_면_점프로_옮기지_않는다()
    {
        var gone = Cloud("win", "i-2");
        var rdp = Cloud("win", "i-2", Protocol.Rdp);
        var target = new HostEntry { Label = "app", Address = "10.0.0.9", JumpHostId = gone.Id };

        var dangling = CloudJumpFix.Apply(new List<HostEntry> { rdp, target }, new[] { gone });

        Assert.Single(dangling);
        Assert.Equal(gone.Id, target.JumpHostId);
    }

    [Fact]
    public void 지운_호스트가_없으면_아무것도_바꾸지_않는다()
    {
        var j = Cloud("bastion", "i-1");
        var target = new HostEntry { Label = "app", Address = "10.0.0.9", JumpHostId = j.Id };
        Assert.Empty(CloudJumpFix.Apply(new List<HostEntry> { j, target }, Array.Empty<HostEntry>()));
        Assert.Equal(j.Id, target.JumpHostId);
    }

    [Theory]
    [InlineData("org/admin", true)]        // AWS 프로필 이름에 쓰이는 '/'
    [InlineData("a/b/c-1", true)]
    [InlineData("/k", false)]              // 첫 글자가 '/' 면 cmd 스위치로 읽힐 수 있음
    [InlineData("a/b&calc", false)]
    [InlineData("a\\b", false)]
    public void 프로필_이름의_슬래시(string name, bool ok) =>
        Assert.Equal(ok, McpCloudHelpers.IsSafeCliArg(name));

    [Fact]
    public void 슬래시가_든_AWS_프로필도_UI_로그인_검증을_통과한다()
    {
        var p = new CloudProfile { Provider = CloudProvider.Aws, Name = "org/admin" };
        var cmd = CloudProviders.Get(CloudProvider.Aws).LoginCommand(p);
        Assert.True(McpCloudHelpers.IsSafeLogin(p.Name, p.TenantId, cmd), cmd);
    }
}
