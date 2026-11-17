using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>세션 탭 · 터미널 · SFTP 영역 MCP 도구 보강 (move_session · set_terminal_zoom · list_closed_tabs · SFTP 여러 항목)의 판단 로직</summary>
public class McpParitySessionsTests
{
    // ---------- move_session ----------

    [Theory]
    [InlineData(4, 0, 4, 3)]   // 맨 왼쪽 → 맨 오른쪽
    [InlineData(4, 3, 1, 0)]   // 맨 오른쪽 → 맨 왼쪽
    [InlineData(4, 1, 2, 1)]   // 제자리
    public void position_은_1부터_왼쪽_기준(int count, int from, int position, int expected) =>
        Assert.Equal(expected, McpSessionLogic.MoveTargetIndex(count, from, position, null));

    [Theory]
    [InlineData(4, 0, 3, 2)]   // 오른쪽으로: 빠진 자리만큼 앞당김 → 3번 탭 바로 왼쪽
    [InlineData(4, 3, 1, 1)]   // 왼쪽으로
    [InlineData(4, 0, 1, 0)]   // 이미 바로 왼쪽
    public void before_는_그_탭_바로_왼쪽(int count, int from, int before, int expected)
    {
        var to = McpSessionLogic.MoveTargetIndex(count, from, null, before);
        Assert.Equal(expected, to);
        // ObservableCollection.Move 와 같은 결과인지 실제로 옮겨 본다
        var list = Enumerable.Range(0, count).ToList();
        var moved = list[from];
        var beforeItem = list[before];
        list.RemoveAt(from);
        list.Insert(to, moved);
        Assert.Equal(list.IndexOf(beforeItem) - 1, list.IndexOf(moved));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, 2)]
    [InlineData(0, null)]
    [InlineData(5, null)]
    public void 잘못된_자리는_거부(int? position, int? before) =>
        Assert.Throws<ArgumentException>(() => McpSessionLogic.MoveTargetIndex(4, 1, position, before));

    [Fact]
    public void 자기_자신_앞으로는_거부() =>
        Assert.Throws<ArgumentException>(() => McpSessionLogic.MoveTargetIndex(4, 2, null, 2));

    [Fact]
    public void 끌기와_MCP_가_같은_MoveTab_을_쓴다()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "RemoteClient", "MainWindow.TabStrip.cs"));
        Assert.Contains("MoveTab(dragged, to)", src);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"_tabs\.Move\("));
    }

    // ---------- set_terminal_zoom ----------

    [Theory]
    [InlineData(0, 3, null, 3)]
    [InlineData(2, null, 2, 4)]
    [InlineData(2, null, -5, -3)]
    [InlineData(0, 100, null, 24)]
    [InlineData(0, -100, null, -6)]
    [InlineData(20, null, 10, 24)]
    [InlineData(5, null, null, 5)]
    [InlineData(0, null, int.MinValue, -6)]   // 넘침 없이 끝 값
    public void 조절값은_범위로_자른다(int current, int? zoom, int? delta, int expected) =>
        Assert.Equal(expected, McpSessionLogic.NextZoom(current, zoom, delta, XtermView.MinZoom, XtermView.MaxZoom));

    [Fact]
    public void zoom_과_delta_를_함께_주면_거부() =>
        Assert.Throws<ArgumentException>(() => McpSessionLogic.NextZoom(0, 1, 1, -6, 24));

    [Theory]
    [InlineData(14, 0, 14)]
    [InlineData(14, 3, 17)]
    [InlineData(8, -6, 6)]
    [InlineData(6, -6, 6)]
    public void 글꼴_크기는_6pt_아래로_내려가지_않는다(int baseSize, int zoom, int expected) =>
        Assert.Equal(expected, McpSessionLogic.ZoomedFontSize(baseSize, zoom));

    [Fact]
    public void 터미널_페이지가_setzoom_메시지를_UI_와_같은_함수로_처리한다()
    {
        var html = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "RemoteClient", "wwwroot", "terminal.html"));
        Assert.Contains("case 'setzoom': setZoom(", html);
        Assert.Contains("Math.max(-6, Math.min(24, z))", html);   // XtermView.MinZoom/MaxZoom 과 같은 범위
    }

    // ---------- list_closed_tabs ----------

    [Fact]
    public void 닫은_쿠버네티스_클러스터_탭은_클러스터로_표시()
    {
        var cid = Guid.NewGuid();
        var line = McpSessionLogic.ClosedTabLine(new ClosedTabHistory.Entry(null, null, cid), _ => null, id => id == cid ? "prod-eks" : null);
        Assert.Equal("쿠버네티스 클러스터 | prod-eks", line);
    }

    [Fact]
    public void 지워진_클러스터는_빠진다() =>
        Assert.Null(McpSessionLogic.ClosedTabLine(new ClosedTabHistory.Entry(null, null, Guid.NewGuid()), _ => null, _ => null));

    [Fact]
    public void 로컬_탭과_호스트_탭()
    {
        Assert.Equal("로컬 PowerShell", McpSessionLogic.ClosedTabLine(new ClosedTabHistory.Entry(null, null), _ => null, _ => null));
        var h = new HostEntry { Label = "web01", Address = "10.0.0.1", Protocol = Protocol.Ssh };
        var line = McpSessionLogic.ClosedTabLine(new ClosedTabHistory.Entry(h.Id, true), id => id == h.Id ? h : null, _ => null);
        Assert.Equal($"{h.Id.ToString()[..8]} | web01 | ssh | 공인 주소", line);
        Assert.Null(McpSessionLogic.ClosedTabLine(new ClosedTabHistory.Entry(Guid.NewGuid(), null), _ => null, _ => null));
    }

    [Fact]
    public void 클러스터_이름의_보이지_않는_문자는_드러낸다()
    {
        var cid = Guid.NewGuid();
        var line = McpSessionLogic.ClosedTabLine(new ClosedTabHistory.Entry(null, null, cid), _ => null, _ => "a\u0007b");
        Assert.DoesNotContain("\u0007", line, StringComparison.Ordinal);
        Assert.Contains("\\x07", line);
    }

    // ---------- SFTP 여러 항목 ----------

    [Fact]
    public void 단일과_목록은_둘_중_하나만()
    {
        Assert.NotNull(McpSftpRules.CollectPaths("path", "/a", "paths", new[] { "/b" }, false, out _));
        Assert.NotNull(McpSftpRules.CollectPaths("path", "", "paths", null, false, out _));
        Assert.NotNull(McpSftpRules.CollectPaths("path", null, "paths", Array.Empty<string>(), false, out _));
        Assert.Null(McpSftpRules.CollectPaths("path", " /a ", "paths", null, false, out var one));
        Assert.Equal(new[] { "/a" }, one);
    }

    [Fact]
    public void 목록은_정리하고_같은_항목은_합친다()
    {
        Assert.Null(McpSftpRules.CollectPaths("path", null, "paths", new[] { "/a", " /b", "/a" }, false, out var list));
        Assert.Equal(new[] { "/a", "/b" }, list);
        Assert.Null(McpSftpRules.CollectPaths("local_path", null, "local_paths", new[] { @"C:\X", @"c:\x" }, true, out var local));
        Assert.Single(local);
    }

    [Fact]
    public void 목록의_빈_항목_제어_문자_너무_많음은_거부()
    {
        Assert.NotNull(McpSftpRules.CollectPaths("path", null, "paths", new[] { "/a", " " }, false, out _));
        Assert.NotNull(McpSftpRules.CollectPaths("path", null, "paths", new[] { "/a\nb" }, false, out _));
        Assert.NotNull(McpSftpRules.CollectPaths("path", null, "paths", new[] { "/a\u202Eb" }, false, out _));
        var many = Enumerable.Range(0, McpSftpRules.MaxMultiPaths + 1).Select(i => $"/f{i}").ToArray();
        Assert.NotNull(McpSftpRules.CollectPaths("path", null, "paths", many, false, out _));
        Assert.Null(McpSftpRules.CollectPaths("path", null, "paths", many[..McpSftpRules.MaxMultiPaths], false, out _));
    }

    [Theory]
    [InlineData("/a", "/a/b")]
    [InlineData("/a/b", "/a")]
    [InlineData("/srv/x", "/srv/x/y/z")]
    public void 서로_품는_삭제_경로는_거부(string p1, string p2) =>
        Assert.NotNull(McpSftpRules.NestedRemotePaths(new[] { p1, p2 }));

    [Theory]
    [InlineData("/a", "/ab")]
    [InlineData("/a/b", "/a/c")]
    public void 이름만_비슷한_경로는_괜찮다(string p1, string p2) =>
        Assert.Null(McpSftpRules.NestedRemotePaths(new[] { p1, p2 }));

    [Fact]
    public void 올릴_로컬_목록_겹침()
    {
        Assert.NotNull(McpSftpRules.LocalPathListProblem(new[] { @"C:\data", @"C:\Data\sub" }));          // 안에 있음 (대소문자 무시)
        Assert.NotNull(McpSftpRules.LocalPathListProblem(new[] { @"C:\a\log.txt", @"D:\b\log.txt" }));   // 원격에서 같은 이름
        Assert.NotNull(McpSftpRules.LocalPathListProblem(new[] { @"C:\" }));                              // 드라이브 루트
        Assert.Null(McpSftpRules.LocalPathListProblem(new[] { @"C:\data", @"C:\data2", @"C:\x\Log.txt", @"C:\y\log.txt" }));
    }

    [Theory]
    [InlineData(null, null, "fail")]
    [InlineData(null, false, "fail")]
    [InlineData(null, true, "overwrite")]
    [InlineData("skip", null, "skip")]
    [InlineData(" Overwrite ", true, "overwrite")]
    [InlineData("fail", false, "fail")]
    public void 같은_이름_처리_방식(string? onConflict, bool? overwrite, string expected)
    {
        Assert.Null(McpSftpRules.ResolveConflictMode(onConflict, overwrite, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("skip", true)]
    [InlineData("fail", true)]
    [InlineData("overwrite", false)]
    [InlineData("merge", null)]
    public void 어긋나거나_모르는_처리_방식은_거부(string onConflict, bool? overwrite) =>
        Assert.NotNull(McpSftpRules.ResolveConflictMode(onConflict, overwrite, out _));

    // UploadDecision(mode, wantDir, exists, isLink, isDir, isRegular)
    [Theory]
    [InlineData("fail", false, false, false, false, false, McpSftpRules.UploadAction.Create)]
    [InlineData("fail", true, false, false, false, false, McpSftpRules.UploadAction.Create)]
    [InlineData("fail", true, true, false, true, false, McpSftpRules.UploadAction.Merge)]       // 폴더는 어느 방식이든 병합
    [InlineData("skip", true, true, false, true, false, McpSftpRules.UploadAction.Merge)]
    [InlineData("overwrite", true, true, false, true, false, McpSftpRules.UploadAction.Merge)]
    [InlineData("overwrite", false, true, false, false, true, McpSftpRules.UploadAction.Overwrite)]
    [InlineData("skip", false, true, false, false, true, McpSftpRules.UploadAction.Skip)]
    [InlineData("fail", false, true, false, false, true, McpSftpRules.UploadAction.Conflict)]
    // 링크에는 쓰지 않는다: 덮어쓰기는 거부, 건너뛰기는 건너뜀
    [InlineData("overwrite", false, true, true, false, false, McpSftpRules.UploadAction.Refuse)]
    [InlineData("skip", false, true, true, false, false, McpSftpRules.UploadAction.Skip)]
    [InlineData("fail", false, true, true, false, false, McpSftpRules.UploadAction.Conflict)]
    [InlineData("overwrite", true, true, true, false, false, McpSftpRules.UploadAction.Refuse)]  // 폴더 자리의 링크(링크 너머에 쓰지 않음)
    [InlineData("fail", true, true, true, false, false, McpSftpRules.UploadAction.Refuse)]
    [InlineData("skip", true, true, true, false, false, McpSftpRules.UploadAction.Skip)]
    // 종류가 다른 같은 이름
    [InlineData("overwrite", false, true, false, true, false, McpSftpRules.UploadAction.Refuse)]
    [InlineData("overwrite", true, true, false, false, true, McpSftpRules.UploadAction.Refuse)]
    [InlineData("skip", true, true, false, false, true, McpSftpRules.UploadAction.Skip)]
    [InlineData("overwrite", false, true, false, false, false, McpSftpRules.UploadAction.Refuse)] // 장치·FIFO
    public void 올릴_항목_판단(string mode, bool wantDir, bool exists, bool isLink, bool isDir, bool isRegular, McpSftpRules.UploadAction expected) =>
        Assert.Equal(expected, McpSftpRules.UploadDecision(mode, wantDir, exists, isLink, isDir, isRegular).Action);

    [Theory]
    [InlineData(true, false, false, false)]   // 읽기 전용 자동 승인 켜짐 + 보통 경로 → 승인 없음
    [InlineData(false, false, false, true)]   // 자동 승인 꺼짐 → 승인
    [InlineData(true, true, false, true)]     // sudo 패널 → 항상 승인
    [InlineData(true, false, true, true)]     // 비밀이 든 경로 → 승인
    public void 패널_폴더_이동_승인은_sftp_list_와_같은_기준(bool auto, bool elevated, bool sensitive, bool expected) =>
        Assert.Equal(expected, McpSftpRules.PanelNavigateNeedsApproval(auto, elevated, sensitive));

    [Fact]
    public void 목록_문구는_앞부분만()
    {
        var items = Enumerable.Range(1, 25).Select(i => $"/f{i}").ToList();
        var text = McpSftpRules.ListText(items, 20);
        Assert.Equal(21, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("외 5개", text);
        Assert.Equal("", McpSftpRules.ListText(Array.Empty<string>()));
    }

    private static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "build.ps1"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
