using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>2차 리뷰: SFTP 패널 정렬·경로 입력·이름 검사·업로드 존재 확인 캐시·진행 문구·위험 확장자</summary>
public class Review2SftpTests
{
    private static SftpItem Item(string name, bool dir = false, long size = 0, int day = 1) => new()
    {
        Name = name, FullName = "/x/" + name, IsDir = dir, Size = size, Modified = new DateTime(2026, 1, day),
    };

    [Fact]
    public void 정렬은_폴더가_항상_먼저이고_고른_열과_방향을_따른다()
    {
        var items = new[] { Item("b.txt", size: 10, day: 3), Item("A.txt", size: 30, day: 1), Item("zdir", dir: true), Item("adir", dir: true), Item("c.txt", size: 20, day: 2) };

        Assert.Equal(new[] { "adir", "zdir", "A.txt", "b.txt", "c.txt" }, SftpPanel.SortItems(items, SftpSortKey.Name, false).Select(i => i.Name));
        Assert.Equal(new[] { "zdir", "adir", "c.txt", "b.txt", "A.txt" }, SftpPanel.SortItems(items, SftpSortKey.Name, true).Select(i => i.Name));
        Assert.Equal(new[] { "adir", "zdir", "A.txt", "c.txt", "b.txt" }, SftpPanel.SortItems(items, SftpSortKey.Size, true).Select(i => i.Name));
        Assert.Equal(new[] { "adir", "zdir", "b.txt", "c.txt", "A.txt" }, SftpPanel.SortItems(items, SftpSortKey.Modified, true).Select(i => i.Name));
        Assert.Equal(new[] { "adir", "zdir", "A.txt", "c.txt", "b.txt" }, SftpPanel.SortItems(items, SftpSortKey.Modified, false).Select(i => i.Name));
    }

    [Theory]
    [InlineData(".bashrc", true)]
    [InlineData(".ssh", true)]
    [InlineData("a.txt", false)]
    [InlineData("file.", false)]
    public void 점으로_시작하는_이름은_숨김_파일이다(string name, bool hidden) => Assert.Equal(hidden, SftpPanel.IsHiddenName(name));

    [Theory]
    [InlineData("~", "/var/log", "/home/me", "/home/me")]
    [InlineData("~/app/conf", "/var/log", "/home/me", "/home/me/app/conf")]
    [InlineData("nginx", "/var/log", "/home/me", "/var/log/nginx")]
    [InlineData("../lib", "/var/log", "/home/me", "/var/lib")]
    [InlineData("./a/./b/", "/srv", "/home/me", "/srv/a/b")]
    [InlineData("/etc//ssh/../ssl", "/srv", "/home/me", "/etc/ssl")]
    [InlineData("/../..", "/srv", "/home/me", "/")]
    [InlineData("  /tmp  ", "/srv", "/home/me", "/tmp")]
    [InlineData("x", "", "/home/me", "/home/me/x")]   // 아직 목록을 못 열었으면 홈 기준
    public void 경로_칸의_물결표와_상대_경로를_절대_경로로_바꾼다(string input, string cwd, string home, string expected) =>
        Assert.Equal(expected, SftpPanel.ResolvePathInput(input, cwd, home));

    [Theory]
    [InlineData("new-folder", true)]
    [InlineData("이름 있는 폴더", true)]
    [InlineData(".hidden", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("../etc", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("tab\tname", false)]
    public void 새_폴더와_이름_변경에_쓸_수_없는_이름을_거른다(string name, bool ok) =>
        Assert.Equal(ok, SftpPanel.ValidateRemoteName(name) == null);

    [Fact]
    public void 업로드_존재_확인은_폴더_목록을_폴더마다_한_번만_읽는다()
    {
        var calls = new List<string>();
        var server = new Dictionary<string, string[]>
        {
            ["/up"] = new[] { "a.txt", "sub" },
            ["/up/sub"] = new[] { "b.txt" },
        };
        var cache = new RemoteNameCache(p => { calls.Add(p); return server.TryGetValue(p, out var n) ? n : null; });

        Assert.True(cache.Exists("/up/a.txt"));
        Assert.False(cache.Exists("/up/c.txt"));
        Assert.True(cache.Exists("/up/sub"));
        Assert.True(cache.Exists("/up/sub/b.txt"));
        Assert.False(cache.Exists("/up/sub/c.txt"));
        Assert.False(cache.Exists("/up/new/x.txt"));   // 없는 폴더 → 없는 파일
        Assert.Equal(new[] { "/up", "/up/sub", "/up/new" }, calls);

        // 새로 만든 폴더는 빈 폴더로 기억 — 다시 읽지 않는다
        cache.Created("/up/new");
        Assert.True(cache.Exists("/up/new"));
        Assert.False(cache.Exists("/up/new/x.txt"));
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public void 폴더_목록을_못_읽으면_모름을_돌려_하나씩_묻게_한다()
    {
        var cache = new RemoteNameCache(p => p == "/denied" ? throw new UnauthorizedAccessException() : Array.Empty<string>());
        Assert.Null(cache.Exists("/denied/a.txt"));
        Assert.Null(cache.Exists("/denied/b.txt"));
        Assert.False(cache.Exists("/ok/a.txt"));
    }

    [Fact]
    public void 진행_문구는_1초_전에는_양만_그_뒤엔_속도와_남은_시간()
    {
        Assert.Equal("0 B / 10 MB", TransferMeter.Format(0, 10L * 1024 * 1024, TimeSpan.FromSeconds(5)));
        Assert.Equal("1 MB / 10 MB", TransferMeter.Format(1024 * 1024, 10L * 1024 * 1024, TimeSpan.FromMilliseconds(500)));
        var text = TransferMeter.Format(2L * 1024 * 1024, 10L * 1024 * 1024, TimeSpan.FromSeconds(2));
        Assert.StartsWith("2 MB / 10 MB", text);
        Assert.Contains("1 MB/s", text);
        Assert.Contains("0:08", text);   // 남은 8 MB 를 1 MB/s 로
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600 + 62, "1:01:02")]
    [InlineData(30 * 3600, "30:00:00")]
    public void 남은_시간_형식(int seconds, string expected) => Assert.Equal(expected, TransferMeter.FormatEta(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void 매우_느린_전송도_남은_시간이_넘치지_않는다()
    {
        var text = TransferMeter.Format(1, long.MaxValue / 2, TimeSpan.FromSeconds(100));
        Assert.Contains("99:59:59", text);
    }

    [Theory]
    [InlineData("conn.rdp")]
    [InlineData("sandbox.wsb")]
    [InlineData("x.sct")]
    [InlineData("x.appinstaller")]
    [InlineData("db.mdb")]
    [InlineData("app.jnlp")]
    [InlineData("script.msh1")]
    [InlineData("CONN.RDP. ")]
    public void 빠져_있던_실행_형식도_위험으로_본다(string path) => Assert.True(SftpPanel.IsRiskyToOpen(path));

    [Theory]
    [InlineData("nginx.conf")]
    [InlineData("app.log")]
    [InlineData("data.json")]
    public void 일반_설정_로그_파일은_위험하지_않다(string path) => Assert.False(SftpPanel.IsRiskyToOpen(path));

    [Fact]
    public void 장치와_FIFO_소켓은_특수_파일이다()
    {
        Assert.True(SftpPanel.IsSpecialFile(false, false, true, false));   // /dev/zero
        Assert.True(SftpPanel.IsSpecialFile(false, true, false, false));   // FIFO
        Assert.False(SftpPanel.IsSpecialFile(false, false, false, false));
    }
}
