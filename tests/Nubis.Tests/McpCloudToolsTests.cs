using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class McpCloudToolsTests
{
    private static readonly List<CloudCandidate> Candidates = new()
    {
        new CloudCandidate("prod", "PROD"),
        new CloudCandidate("dev", "DEV"),
        new CloudCandidate("11111111-2222-3333-4444-555555555555", "Main Subscription", "tenant-a"),
        new CloudCandidate("aaaa", "Shared"),
        new CloudCandidate("bbbb", "Shared"),
    };

    [Fact]
    public void 프로바이더_이름을_해석한다()
    {
        Assert.Equal(CloudProvider.Aws, McpCloudHelpers.ParseProvider("aws"));
        Assert.Equal(CloudProvider.Tencent, McpCloudHelpers.ParseProvider(" Tencent "));
        Assert.Equal(CloudProvider.Gcp, McpCloudHelpers.ParseProvider("GCP"));
        Assert.Equal(CloudProvider.Azure, McpCloudHelpers.ParseProvider("azure"));
        Assert.Equal(CloudProvider.Oci, McpCloudHelpers.ParseProvider("oci"));
        Assert.Null(McpCloudHelpers.ParseProvider("alibaba"));
        Assert.Null(McpCloudHelpers.ParseProvider(""));
        Assert.Null(McpCloudHelpers.ParseProvider(null));
        Assert.Null(McpCloudHelpers.ParseProvider("0"));   // enum 숫자 문자열은 받지 않음
        // 스키마 enum 과 해석이 일치
        foreach (var n in McpCloudHelpers.ProviderNames)
            Assert.Equal(n, McpCloudHelpers.ProviderKey(McpCloudHelpers.ParseProvider(n)!.Value));
    }

    [Fact]
    public void all_이면_추가_안_된_후보만_고른다()
    {
        var sel = McpCloudHelpers.SelectCandidates(Candidates, new[] { "PROD" }, new[] { "ALL" });
        Assert.Equal(new[] { "dev", "11111111-2222-3333-4444-555555555555", "aaaa", "bbbb" }, sel.Pick.Select(c => c.Name));
        Assert.Empty(sel.Unknown);
    }

    [Fact]
    public void 이름_표시이름으로_고르고_이미_추가된_것과_없는_것을_나눈다()
    {
        var sel = McpCloudHelpers.SelectCandidates(Candidates, new[] { "prod" },
            new[] { "Prod", "DEV", "Main Subscription", "nope", "dev", "Shared" });
        Assert.Equal(new[] { "dev", "11111111-2222-3333-4444-555555555555" }, sel.Pick.Select(c => c.Name));   // dev 중복 제거
        Assert.Equal(new[] { "Prod" }, sel.AlreadyAdded);
        Assert.Equal(new[] { "nope", "Shared" }, sel.Unknown);   // 표시 이름이 여러 개와 일치하면 모호
        Assert.Equal("tenant-a", sel.Pick[1].TenantId);
    }

    [Fact]
    public void 후보_하나를_찾는다()
    {
        Assert.Equal("aaaa", McpCloudHelpers.FindCandidate(Candidates, "AAAA")?.Name);
        Assert.Null(McpCloudHelpers.FindCandidate(Candidates, "Shared"));
        Assert.Null(McpCloudHelpers.FindCandidate(Candidates, " "));
        Assert.Null(McpCloudHelpers.FindCandidate(Candidates, "pro"));   // 부분 일치는 하지 않음
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("my-profile_1.prod", true)]
    [InlineData("user@corp", true)]
    [InlineData("11111111-2222-3333-4444-555555555555", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]   // 옵션으로 해석될 수 있는 값
    [InlineData("a b", false)]
    [InlineData("a&calc", false)]
    [InlineData("a|b", false)]
    [InlineData("a\"b", false)]
    [InlineData("%PATH%", false)]
    [InlineData("a^b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\n", false)]   // 끝 줄바꿈 ($ 가 아닌 \z 로 끝을 본다)
    public void 명령줄_인자_허용_문자(string s, bool ok) => Assert.Equal(ok, McpCloudHelpers.IsSafeCliArg(s));

    [Theory]
    [InlineData("aws sso login --profile prod", true)]
    [InlineData("gcloud auth application-default login", true)]
    [InlineData("az login --tenant 11111111-2222-3333-4444-555555555555", true)]
    [InlineData("oci session authenticate --profile-name DEFAULT --region ap-seoul-1", true)]
    [InlineData("tccli configure --profile default", true)]
    [InlineData("aws sso login --profile x & calc", false)]
    [InlineData("aws sso login --profile x > out", false)]
    [InlineData("aws sso login --profile \"x\"", false)]
    [InlineData("aws sso login --profile (x)", false)]
    [InlineData("aws sso login --profile !x!", false)]
    [InlineData("", false)]
    public void 로그인_명령_허용_문자(string s, bool ok) => Assert.Equal(ok, McpCloudHelpers.IsSafeCommandLine(s));

    [Fact]
    public void 실제_로그인_명령이_허용_문자_검사를_통과한다()
    {
        var p = new CloudProfile { Name = "prod", TenantId = "11111111-2222-3333-4444-555555555555" };
        foreach (var pr in CloudProviders.All)
        {
            Assert.True(McpCloudHelpers.IsSafeCommandLine(pr.LoginCommand(p)), pr.LoginCommand(p));
            Assert.True(McpCloudHelpers.IsSafeCommandLine(pr.LoginCommand(null)), pr.LoginCommand(null));
        }
    }
}
