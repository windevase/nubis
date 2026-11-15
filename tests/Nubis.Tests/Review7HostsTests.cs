using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>호스트 목록 리뷰 7차 회귀 테스트: 그룹 고르기 목록의 표기</summary>
public class Review7HostsTests
{
    [Fact]
    public void KnownGroupNames_UsesSameSpellingAsGroupHeader()
    {
        // 목록에 "web" 이 먼저 나와도 머리글(CanonicalGroupNames)과 같은 "Web" 하나만
        var hosts = new[] { "web", "Web", " db ", "", "  " }.Select(g => new HostEntry { Group = g }).ToList();
        var names = MainWindow.KnownGroupNames(hosts);
        Assert.Equal(new[] { "db", "Web" }, names);
        Assert.Equal(MainWindow.CanonicalGroupNames(hosts)["web"], names[1]);
    }

    [Fact]
    public void KnownGroupNames_SkipsCloudHosts()
    {
        var hosts = new[]
        {
            new HostEntry { Group = "prod" },
            new HostEntry { Group = "cloud-only", CloudProfileId = System.Guid.NewGuid() },
        };
        Assert.Equal(new[] { "prod" }, MainWindow.KnownGroupNames(hosts));
    }
}
