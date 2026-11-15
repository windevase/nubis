using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>명령 팔레트 검색 · 순위 (PaletteIndex) — 화면 없이</summary>
public class CommandPaletteTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0);

    // 기본 주소는 테스트 검색어("10" · "12" · 글자)에 걸리지 않는 값
    private static PaletteItem Host(string name, string addr = "192.168.0.1",string group = "", string? user = null,
        DateTime? last = null, bool fav = false, string? pub = null) => new()
    {
        Kind = PaletteKind.Host,
        Title = name,
        Addresses = new[] { addr, pub },
        Group = group,
        Extra = user,
        LastUsed = last,
        Favorite = fav,
        HasPublicIp = pub != null,
    };

    private static PaletteItem Tab(string title) => new() { Kind = PaletteKind.Tab, Title = title };
    private static PaletteItem Act(string title, string keywords = "") => new() { Kind = PaletteKind.Action, Title = title, Extra = keywords };

    private static List<string> Titles(PaletteIndex idx, string q, int limit = PaletteIndex.DefaultLimit)
    {
        var output = new List<PaletteItem>();
        idx.Search(q, output, limit, Now);
        return output.Select(i => i.Title).ToList();
    }

    [Fact]
    public void 이름_정확_앞쪽_단어앞_포함_순서()
    {
        var idx = new PaletteIndex(new[]
        {
            Host("myweb"),         // 포함
            Host("api-web"),       // 단어 앞
            Host("web-01"),        // 앞쪽
            Host("web"),           // 정확
        });
        Assert.Equal(new[] { "web", "web-01", "api-web", "myweb" }, Titles(idx, "web"));
    }

    [Fact]
    public void 대소문자를_무시한다()
    {
        var idx = new PaletteIndex(new[] { Host("Prod-DB") });
        Assert.Equal(new[] { "Prod-DB" }, Titles(idx, "PROD"));
        Assert.Equal(new[] { "Prod-DB" }, Titles(idx, "db"));
    }

    [Fact]
    public void 주소_그룹_사용자로도_찾는다_이름이_먼저()
    {
        var idx = new PaletteIndex(new[]
        {
            Host("a", addr: "10.1.2.3"),
            Host("b", group: "billing"),
            Host("c", user: "deploy"),
            Host("10-box"),
        });
        Assert.Equal(new[] { "10-box", "a" }, Titles(idx, "10"));
        Assert.Equal(new[] { "b" }, Titles(idx, "bill"));
        Assert.Equal(new[] { "c" }, Titles(idx, "deploy"));
    }

    [Fact]
    public void 공인_IP_도_주소로_찾는다()
    {
        var idx = new PaletteIndex(new[] { Host("x", addr: "10.0.0.5", pub: "52.1.2.3") });
        Assert.Equal(new[] { "x" }, Titles(idx, "52.1"));
    }

    [Fact]
    public void 주소_앞쪽이_주소_중간보다_먼저()
    {
        var idx = new PaletteIndex(new[]
        {
            Host("mid", addr: "10.0.0.12"),
            Host("start", addr: "12.0.0.1"),
        });
        Assert.Equal(new[] { "start", "mid" }, Titles(idx, "12"));
    }

    [Fact]
    public void 여러_단어는_모두_맞아야_한다()
    {
        var idx = new PaletteIndex(new[]
        {
            Host("web-01", group: "prod"),
            Host("web-02", group: "dev"),
            Host("db-01", group: "prod"),
        });
        Assert.Equal(new[] { "web-01" }, Titles(idx, "web prod"));
        Assert.Equal(new[] { "web-01" }, Titles(idx, "  prod   web  "));
        Assert.Empty(Titles(idx, "web stage"));
    }

    [Fact]
    public void 퍼지는_글자_순서만_맞아도_찾지만_가장_뒤()
    {
        var idx = new PaletteIndex(new[]
        {
            Host("production-server"),
            Host("pserver"),
        });
        // "psv": production-server 에는 p..s..v 가 순서대로 있다 (부분 문자열은 아님)
        var r = Titles(idx, "psv");
        Assert.Equal(2, r.Count);
        Assert.Equal("pserver", r[0]);   // 더 촘촘히 맞음
        Assert.True(PaletteIndex.FuzzyScore("psv", "production-server") > 0);
        Assert.Equal(0, PaletteIndex.FuzzyScore("vsp", "production-server"));
        // 한 글자는 퍼지로 찾지 않는다
        Assert.Equal(0, PaletteIndex.FuzzyScore("z", "z"));
    }

    [Fact]
    public void 퍼지_점수는_어떤_부분_문자열_점수보다_낮다()
    {
        Assert.True(PaletteIndex.FuzzyMax < PaletteIndex.ExtraContains);
        Assert.True(PaletteIndex.ScoreToken("ab", "a-b", "", "", "") <= PaletteIndex.FuzzyMax);
    }

    [Fact]
    public void 동점이면_최근_접속이_먼저()
    {
        var idx = new PaletteIndex(new[]
        {
            Host("web-a", last: Now.AddDays(-100)),
            Host("web-b", last: Now.AddDays(-200)),
            Host("web-c", last: Now.AddDays(-50)),
        });
        Assert.Equal(new[] { "web-c", "web-a", "web-b" }, Titles(idx, "web"));
    }

    [Fact]
    public void 가산점은_일치_종류를_뒤집지_않는다()
    {
        // 즐겨찾기 + 최근 + 탭 가산점을 다 받아도 이름 포함이 이름 앞쪽을 넘지 않는다
        var maxBonus = PaletteIndex.Bonus(new PaletteItem { Kind = PaletteKind.Tab, Title = "x", Favorite = true, LastUsed = Now }, Now);
        Assert.True(PaletteIndex.NameContains + maxBonus < PaletteIndex.NameWord);
        Assert.True(PaletteIndex.AddrContains + maxBonus < PaletteIndex.AddrPrefix);
    }

    [Fact]
    public void 열린_탭이_같은_점수의_호스트보다_먼저()
    {
        var idx = new PaletteIndex(new[] { Host("web-01"), Tab("web-01") });
        var output = new List<PaletteItem>();
        idx.Search("web-01", output, now: Now);
        Assert.Equal(PaletteKind.Tab, output[0].Kind);
    }

    [Fact]
    public void 동작은_영어_낱말로도_찾는다()
    {
        var idx = new PaletteIndex(new[] { Act("설정 페이지", "설정 settings preferences"), Act("Vault 페이지", "vault") });
        Assert.Equal(new[] { "설정 페이지" }, Titles(idx, "sett"));
        Assert.Equal(new[] { "설정 페이지" }, Titles(idx, "설정"));
    }

    [Fact]
    public void 검색어가_없으면_최근_호스트_탭_동작_즐겨찾기_순()
    {
        var idx = new PaletteIndex(new[]
        {
            Act("act"),
            Host("old", last: Now.AddDays(-30)),
            Host("never"),
            Host("fav", fav: true),
            Tab("tab1"),
            Host("new", last: Now.AddHours(-1), fav: true),
        });
        // 즐겨찾기인 "new" 는 최근에 이미 있으므로 두 번 나오지 않는다. 접속한 적 없는 일반 호스트는 빠진다
        Assert.Equal(new[] { "new", "old", "tab1", "act", "fav" }, Titles(idx, ""));
        Assert.Equal(Titles(idx, ""), Titles(idx, "   "));
    }

    [Fact]
    public void 최근_호스트는_최대_10개()
    {
        var hosts = Enumerable.Range(0, 15).Select(i => Host("h" + i, last: Now.AddMinutes(-i))).ToArray();
        var r = Titles(new PaletteIndex(hosts), "");
        Assert.Equal(PaletteIndex.RecentCount, r.Count);
        Assert.Equal("h0", r[0]);
        Assert.Equal("h9", r[^1]);
    }

    [Fact]
    public void 결과_수_제한()
    {
        var hosts = Enumerable.Range(0, 200).Select(i => Host("srv" + i)).ToArray();
        var idx = new PaletteIndex(hosts);
        Assert.Equal(PaletteIndex.DefaultLimit, Titles(idx, "srv").Count);
        Assert.Equal(5, Titles(idx, "srv", limit: 5).Count);
        Assert.Empty(Titles(idx, "srv", limit: 0));
    }

    [Fact]
    public void 빈_목록과_null_필드도_안전()
    {
        Assert.Empty(Titles(new PaletteIndex(Array.Empty<PaletteItem>()), "x"));
        var idx = new PaletteIndex(new[] { new PaletteItem { Kind = PaletteKind.Host, Title = "" } });
        Assert.Empty(Titles(idx, "x"));
        Assert.Empty(Titles(idx, null!));
    }

    [Fact]
    public void 출력_목록을_비우고_다시_채운다()
    {
        var idx = new PaletteIndex(new[] { Host("a1"), Host("b1") });
        var output = new List<PaletteItem> { Host("stale") };
        idx.Search("a", output, now: Now);
        Assert.Equal(new[] { "a1" }, output.Select(i => i.Title));
        idx.Search("zzz", output, now: Now);
        Assert.Empty(output);
    }

    [Fact]
    public void 호스트_1000개_검색이_충분히_빠르다()
    {
        var rnd = new Random(1);
        var hosts = Enumerable.Range(0, 1000).Select(i => Host($"svc-{rnd.Next(100000)}-{i}", $"10.{i / 250}.{i % 250}.{rnd.Next(255)}",
            "group" + (i % 20), "user" + (i % 7))).ToArray();
        var idx = new PaletteIndex(hosts);
        var output = new List<PaletteItem>();
        idx.Search("svc", output, now: Now);   // 예열 (JIT)
        var sw = Stopwatch.StartNew();
        const int runs = 50;
        foreach (var q in Enumerable.Repeat(new[] { "s", "sv", "svc", "svc-1", "10.2", "group1 user3", "xyz" }, runs).SelectMany(x => x))
            idx.Search(q, output, now: Now);
        var perQuery = sw.Elapsed.TotalMilliseconds / (runs * 7);
        // 목표는 5ms 미만 — 느린 CI 머신을 감안해 넉넉히
        Assert.True(perQuery < 20, $"검색 1회 {perQuery:F2}ms");
    }
}
