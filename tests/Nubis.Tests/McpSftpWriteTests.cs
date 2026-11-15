using System;
using Nubis;
using Renci.SshNet;
using Xunit;

namespace Nubis.Tests;

/// <summary>MCP SFTP 쓰기 도구(sftp_mkdir · sftp_rename · sftp_delete · sftp_write · sftp_upload 덮어쓰기)의 판단 로직</summary>
public class McpSftpWriteTests
{
    // ---------- 경로 · 이름 검사 ----------

    [Theory]
    [InlineData("/a/b", "/a/b")]
    [InlineData("//a//b/", "/a/b")]
    [InlineData("  /home/u/x.txt ", "/home/u/x.txt")]
    [InlineData("/", "/")]
    public void 절대_경로는_정리해서_받는다(string input, string expected)
    {
        Assert.Null(McpSftpRules.ValidateRemotePath(input, out var p));
        Assert.Equal(expected, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    [InlineData("~/x")]
    [InlineData("/a/../etc/passwd")]
    [InlineData("/a/./b")]
    [InlineData("/a/b\nc")]
    [InlineData("/a/\u0085b")]
    [InlineData("/a/\u2028b")]
    [InlineData("/a/\u202Etxt.sh")]
    [InlineData("/a/\u200Bb")]
    public void 상대_경로_점_조각_제어_보이지_않는_문자는_거부(string input) =>
        Assert.NotNull(McpSftpRules.ValidateRemotePath(input, out _));

    [Theory]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("   ")]
    [InlineData("x\ty")]
    [InlineData("x\u007fy")]
    [InlineData("x\u0090y")]
    [InlineData("evil\u202Egnp.sh")]
    public void 이름_검사는_패널_규칙에_보이지_않는_문자까지(string name) =>
        Assert.NotNull(McpSftpRules.ValidateName(name));

    [Theory]
    [InlineData("config.yml")]
    [InlineData(".bashrc")]
    [InlineData("새 폴더")]
    public void 정상_이름은_통과(string name) => Assert.Null(McpSftpRules.ValidateName(name));

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/usr")]
    [InlineData("/home")]
    [InlineData("/var")]
    public void 루트와_최상위_시스템_폴더는_지우거나_바꾸지_않는다(string path) =>
        Assert.NotNull(McpSftpRules.ProtectedPathReason(path));

    [Theory]
    [InlineData("/etc/nginx")]
    [InlineData("/data")]
    [InlineData("/home/u/app")]
    public void 그_아래는_보호하지_않는다(string path) => Assert.Null(McpSftpRules.ProtectedPathReason(path));

    // ---------- 링크 덮어쓰기 판단 ----------

    [Fact]
    public void 없는_대상은_링크_확인_없이_통과() =>
        Assert.Null(McpSftpRules.LinkOverwriteRefusal("/a/new", exists: false, isLink: null));

    [Fact]
    public void 링크는_덮어쓰지_않는다() =>
        Assert.NotNull(McpSftpRules.LinkOverwriteRefusal("/a/link", exists: true, isLink: true));

    [Fact]
    public void 링크인지_모르면_덮어쓰지_않는다() =>
        Assert.NotNull(McpSftpRules.LinkOverwriteRefusal("/a/x", exists: true, isLink: null));

    [Fact]
    public void 일반_파일은_덮어쓸_수_있다() =>
        Assert.Null(McpSftpRules.LinkOverwriteRefusal("/a/x", exists: true, isLink: false));

    // ---------- sftp_write 충돌 확인 ----------

    private static readonly DateTime T0 = new(2026, 11, 15, 1, 2, 3, DateTimeKind.Utc);

    [Fact]
    public void 읽은_값과_같으면_쓴다() =>
        Assert.Null(McpSftpRules.WriteConflict(true, T0, 100, T0, 100, createOnly: false));

    [Fact]
    public void 초_아래_차이는_같은_것으로_본다() =>
        Assert.Null(McpSftpRules.WriteConflict(true, T0.AddMilliseconds(400), 100, T0, 100, createOnly: false));

    [Fact]
    public void 수정_시각이_다르면_거부() =>
        Assert.NotNull(McpSftpRules.WriteConflict(true, T0.AddSeconds(1), 100, T0, 100, createOnly: false));

    [Fact]
    public void 크기가_다르면_거부() =>
        Assert.NotNull(McpSftpRules.WriteConflict(true, T0, 101, T0, 100, createOnly: false));

    [Fact]
    public void 있는_파일을_기준값_없이_덮어쓰지_않는다()
    {
        Assert.NotNull(McpSftpRules.WriteConflict(true, T0, 100, null, null, createOnly: false));
        Assert.NotNull(McpSftpRules.WriteConflict(true, T0, 100, T0, null, createOnly: false));
        Assert.NotNull(McpSftpRules.WriteConflict(true, T0, 100, null, 100, createOnly: false));
    }

    [Fact]
    public void create_는_없는_파일만()
    {
        Assert.Null(McpSftpRules.WriteConflict(false, null, null, null, null, createOnly: true));
        Assert.NotNull(McpSftpRules.WriteConflict(true, T0, 100, null, null, createOnly: true));
    }

    [Fact]
    public void 없는_파일은_create_없이_만들지_않고_읽은_뒤_없어졌으면_거부()
    {
        Assert.NotNull(McpSftpRules.WriteConflict(false, null, null, null, null, createOnly: false));
        Assert.NotNull(McpSftpRules.WriteConflict(false, null, null, T0, 100, createOnly: false));
    }

    [Fact]
    public void 수정_시각_형식은_그대로_다시_읽힌다()
    {
        var text = McpSftpRules.FormatMtime(T0);
        Assert.Equal("2026-11-15T01:02:03Z", text);
        Assert.Equal(T0, McpSftpRules.ParseMtime(text));
        Assert.Null(McpSftpRules.ParseMtime("not a date"));
        Assert.Null(McpSftpRules.ParseMtime(""));
    }

    // ---------- 승인 창 바뀜 요약 ----------

    [Fact]
    public void 바뀜_요약은_크기와_처음_바뀐_줄을_보인다()
    {
        var s = McpSftpRules.DiffSummary("a\nb\nc\n", 6, "a\nB\nc\n", 6);
        Assert.Contains("처음 바뀐 줄: 2", s);
        Assert.Contains("- b", s);
        Assert.Contains("+ B", s);
    }

    [Fact]
    public void 새_파일은_앞_줄을_보이고_보이지_않는_문자는_드러낸다()
    {
        var s = McpSftpRules.DiffSummary(null, -1, "line1\nx\u202Ey\n", 11);
        Assert.Contains("새 파일", s);
        Assert.Contains("+ line1", s);
        Assert.False(s.Contains('\u202E'));   // 문화권 비교는 이 문자를 무시해 DoesNotContain 이 늘 찾았다고 한다 (서수 비교로 확인)
        Assert.Contains("\\u{202E}", s);
    }

    [Fact]
    public void 같은_내용이면_알린다() =>
        Assert.Contains("내용이 같음", McpSftpRules.DiffSummary("x\n", 2, "x\n", 2));

    // ---------- SftpRaw.LStat (링크 자체의 속성) ----------

    [Fact]
    public void LStat_리플렉션_대상이_있다() => Assert.True(SftpRaw.LStatSupported);

    [Fact]
    public void LStat_상대_경로는_거부하고_연결_전에는_예외()
    {
        using var c = new SftpClient("127.0.0.1", "u", "p");
        Assert.Throws<ArgumentException>(() => SftpRaw.LStat(c, "link"));
        Assert.Throws<Renci.SshNet.Common.SshConnectionException>(() => SftpRaw.LStat(c, "/a/link"));
    }

    // ---------- SftpRaw.RemoveDirectory (REALPATH 없이 RMDIR) ----------

    [Fact]
    public void RmDir_리플렉션_대상이_있다() => Assert.True(SftpRaw.RmDirSupported);

    [Fact]
    public void RmDir_상대_경로는_거부하고_연결_전에는_예외()
    {
        using var c = new SftpClient("127.0.0.1", "u", "p");
        Assert.Throws<ArgumentException>(() => SftpRaw.RemoveDirectory(c, "dir"));
        Assert.Throws<Renci.SshNet.Common.SshConnectionException>(() => SftpRaw.RemoveDirectory(c, "/a/dir"));
    }

    // ---------- 홈 폴더 보호 ----------

    [Theory]
    [InlineData("/home/u", "/home/u")]
    [InlineData("/home/u/", "/home/u")]
    [InlineData("/data", "/data/users/u")]
    [InlineData("/data/users", "/data/users/u/")]
    public void 홈과_홈을_품은_폴더는_막는다(string path, string home) => Assert.True(McpSftpRules.ContainsHome(path, home));

    [Theory]
    [InlineData("/home/u/app", "/home/u")]
    [InlineData("/home/user2", "/home/u")]
    [InlineData("/home/us", "/home/user")]
    [InlineData("/data", "")]
    [InlineData("/data", null)]
    public void 홈_아래나_다른_폴더는_막지_않는다(string path, string? home) => Assert.False(McpSftpRules.ContainsHome(path, home));

    [Fact]
    public void sudo_권한이면_충돌_사유에_지금_값을_넣지_않는다()
    {
        var shown = McpSftpRules.WriteConflict(true, T0.AddSeconds(5), 4242, T0, 100, createOnly: false)!;
        var hidden = McpSftpRules.WriteConflict(true, T0.AddSeconds(5), 4242, T0, 100, createOnly: false, showActual: false)!;
        Assert.Contains("4242", shown);
        Assert.DoesNotContain("4242", hidden);
        Assert.DoesNotContain("mtime_utc=", hidden);
    }
}
