using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>3차 리뷰: 업로드 존재 확인 캐시의 대소문자 처리 · Windows 경로 입력 · 로컬 폴더 훑기(건너뛴 항목) · 탭 닫기 시간 제한</summary>
public class Review3SftpTests
{
    [Fact]
    public void 대소문자만_다른_이름은_없음이_아니라_모름으로_돌려_서버에_묻게_한다()
    {
        // 대소문자를 가리지 않는 서버(Windows OpenSSH)에서 "없음"으로 판단하면 덮어쓰기 확인 없이 기존 파일에 쓰고, 실패 때 지워 버린다
        var cache = new RemoteNameCache(p => p == "/up" ? new[] { "foo.txt", "Data" } : null);

        Assert.True(cache.Exists("/up/foo.txt"));
        Assert.Null(cache.Exists("/up/Foo.txt"));
        Assert.Null(cache.Exists("/up/FOO.TXT"));
        Assert.Null(cache.Exists("/up/data"));
        Assert.True(cache.Exists("/up/Data"));
        Assert.False(cache.Exists("/up/bar.txt"));
    }

    [Fact]
    public void 새로_만든_폴더는_정확한_이름으로_기억하고_대소문자만_다르면_모름()
    {
        var calls = 0;
        var cache = new RemoteNameCache(p => { calls++; return p == "/up" ? Array.Empty<string>() : null; });

        Assert.False(cache.Exists("/up/New"));
        cache.Created("/up/New");
        Assert.True(cache.Exists("/up/New"));
        Assert.Null(cache.Exists("/up/new"));
        Assert.False(cache.Exists("/up/New/a.txt"));   // 방금 만든 폴더는 비어 있다 — 다시 읽지 않음
        Assert.Equal(1, calls);
    }

    [Fact]
    public void 없는_폴더_아래에_만든_폴더도_기억한다()
    {
        var cache = new RemoteNameCache(_ => null);   // 어떤 폴더도 없음
        Assert.False(cache.Exists("/x/y"));
        cache.Created("/x/y");
        Assert.True(cache.Exists("/x/y"));
        Assert.False(cache.Exists("/x/y/z.txt"));
    }

    [Theory]
    [InlineData(@"C:\Users\me", "/C:/Users/me")]
    [InlineData("C:/inetpub/logs", "/C:/inetpub/logs")]
    [InlineData("d:", "/d:")]
    [InlineData(@"C:\Users\..\Windows", "/C:/Windows")]
    public void Windows_드라이브_경로는_현재_폴더에_붙이지_않고_SFTP_경로로_바꾼다(string input, string expected) =>
        Assert.Equal(expected, SftpPanel.ResolvePathInput(input, "/C:/srv", "/C:/Users/me"));

    [Theory]
    [InlineData("C:x", "/srv/C:x")]          // 드라이브 뒤에 구분자가 없으면 그냥 이름
    [InlineData("ab:cd", "/srv/ab:cd")]
    public void 콜론이_든_일반_이름은_상대_경로로_본다(string input, string expected) =>
        Assert.Equal(expected, SftpPanel.ResolvePathInput(input, "/srv", "/home/me"));

    [Fact]
    public void 로컬_폴더_훑기는_숨김_파일을_포함하고_폴더는_부모가_먼저다()
    {
        var root = Path.Combine(Path.GetTempPath(), "nubis-r3-scan-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "b", "deep"));
            Directory.CreateDirectory(Path.Combine(root, "a"));
            File.WriteAllText(Path.Combine(root, "top.txt"), "12345");
            File.WriteAllText(Path.Combine(root, "b", "deep", "x.bin"), "xx");
            var hidden = Path.Combine(root, "a", "secret.cfg");
            File.WriteAllText(hidden, "h");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var scan = SftpPanel.ScanLocalFolder(root);

            Assert.Equal(new[] { "a", "b", "b/deep" }, scan.Dirs);
            Assert.Equal(0, scan.Skipped);
            var rel = scan.Files.Select(f => f.Rel).OrderBy(r => r, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "a/secret.cfg", "b/deep/x.bin", "top.txt" }, rel);
            Assert.Equal(5, scan.Files.Single(f => f.Rel == "top.txt").Size);
            Assert.All(scan.Files, f => Assert.True(File.Exists(f.Local)));
            // 부모 폴더가 자식보다 먼저 (원격에 차례로 만든다)
            Assert.True(scan.Dirs.IndexOf("b") < scan.Dirs.IndexOf("b/deep"));
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch { }
        }
    }

    [Fact]
    public void 고른_폴더_자체를_못_열면_조용히_비우지_않고_예외()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nubis-r3-missing-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.ThrowsAny<IOException>(() => SftpPanel.ScanLocalFolder(missing));
    }

    [Fact]
    public void 탭_닫기_정리는_2분_안에_끝난다() => Assert.Equal(TimeSpan.FromMinutes(2), SftpPanel.CloseBudget);
}
