using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>설정·Vault·화면 이동 MCP 도구(set_language · vault_update · open_app_page 확장 · set_window_state)의 화면 무관 판단</summary>
public class McpParitySettingsTests
{
    // ----- set_language -----

    [Theory]
    [InlineData("ko", "ko")]
    [InlineData(" KO ", "ko")]
    [InlineData("korean", "ko")]
    [InlineData("한국어", "ko")]
    [InlineData("en", "en")]
    [InlineData("English", "en")]
    [InlineData("en-US", "en")]
    public void 언어_인자는_ko_en_으로_맞춘다(string arg, string expected) =>
        Assert.Equal(expected, MainWindow.NormalizeLanguageArg(arg));

    [Theory]
    [InlineData("")]
    [InlineData("ja")]
    [InlineData("ko;en")]
    [InlineData("k o")]
    public void 모르는_언어는_null(string arg) => Assert.Null(MainWindow.NormalizeLanguageArg(arg));

    [Fact]
    public void 언어_표시_이름은_그_언어_자신의_이름()
    {
        Assert.Equal("English", MainWindow.LanguageLabel(L.English));
        Assert.Equal("한국어", MainWindow.LanguageLabel(L.Korean));
    }

    // ----- set_window_state -----

    [Theory]
    [InlineData("normal", System.Windows.WindowState.Normal)]
    [InlineData("Maximized", System.Windows.WindowState.Maximized)]
    [InlineData(" minimized ", System.Windows.WindowState.Minimized)]
    [InlineData("restore", System.Windows.WindowState.Normal)]
    public void 창_상태_인자(string arg, System.Windows.WindowState expected) =>
        Assert.Equal(expected, MainWindow.ParseWindowStateArg(arg));

    [Theory]
    [InlineData("close")]
    [InlineData("closed")]
    [InlineData("hidden")]
    [InlineData("")]
    public void 창_닫기_숨기기는_받지_않는다(string arg) => Assert.Null(MainWindow.ParseWindowStateArg(arg));

    // ----- open_app_page -----

    [Theory]
    [InlineData("k8s", "kubernetes")]
    [InlineData(" Kubernetes ", "kubernetes")]
    [InlineData("settings_lang", "settings_language")]
    [InlineData("settings-ports", "settings_ports")]
    [InlineData("SETTINGS_CLOUD", "settings_cloud_sync")]
    [InlineData("hosts", "hosts")]
    public void 화면_이름_별칭(string arg, string expected) => Assert.Equal(expected, MainWindow.NormalizeAppPage(arg));

    [Fact]
    public void Settings_바로가기_칩_11개가_모두_섹션으로_열린다()
    {
        // MainWindow.xaml 의 바로가기 칩 Tag 와 open_app_page settings_* 가 어긋나지 않게
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "RemoteClient", "MainWindow.xaml"));
        var bar = xaml[xaml.IndexOf("x:Name=\"SettingsJumpBar\"", StringComparison.Ordinal)..];
        bar = bar[..bar.IndexOf("</WrapPanel>", StringComparison.Ordinal)];
        var tags = Regex.Matches(bar, "Tag=\"(Sec\\w+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(11, tags.Count);
        var mapped = MainWindow.AppPageNames.Select(MainWindow.SettingsSectionFor).Where(x => x != null).ToHashSet();
        foreach (var tag in tags) Assert.Contains(tag, mapped);
        // 스크롤 대상 이름이 XAML 에 실제로 있다
        foreach (var name in mapped) Assert.Contains($"x:Name=\"{name}\"", xaml);
    }

    [Fact]
    public void 화면_이름_목록은_모두_열_수_있는_이름()
    {
        var known = new[] { "hosts", "new_host", "cloud", "kubernetes", "vault", "known_hosts", "settings", "onboarding" };
        foreach (var p in MainWindow.AppPageNames)
            Assert.True(known.Contains(p) || MainWindow.SettingsSectionFor(p) != null, p);
        Assert.Contains("kubernetes", MainWindow.AppPageNames);
        Assert.Null(MainWindow.SettingsSectionFor("settings_unknown"));
        Assert.Null(MainWindow.SettingsSectionFor("settings"));
    }

    // ----- vault_update -----

    private static VaultEntry Entry() => new() { Name = "운영 root", Username = "root", Note = "memo" };

    [Fact]
    public void 바뀐_값만_계획에_들어간다()
    {
        var plan = MainWindow.PlanVaultEdit(Entry(), new("운영 root", " ubuntu ", "memo"), (_, _) => false);
        Assert.Null(plan.Error);
        Assert.Null(plan.Name);
        Assert.Equal("ubuntu", plan.Username);
        Assert.Null(plan.Note);
        Assert.True(MainWindow.PlanVaultEdit(Entry(), new(null, "root", null), (_, _) => false).Empty);
    }

    [Fact]
    public void 이름은_비울_수_없고_사용자명_메모는_지울_수_있다()
    {
        Assert.NotNull(MainWindow.PlanVaultEdit(Entry(), new("  ", null, null), (_, _) => false).Error);
        var plan = MainWindow.PlanVaultEdit(Entry(), new(null, "", ""), (_, _) => false);
        Assert.Null(plan.Error);
        Assert.Equal("", plan.Username);
        Assert.Equal("", plan.Note);
    }

    [Fact]
    public void 다른_계정과_같은_이름이면_거부_대소문자만_바꾸는_것은_허용()
    {
        var e = Entry();
        Assert.NotNull(MainWindow.PlanVaultEdit(e, new("배포", null, null), (n, id) => n == "배포" && id == e.Id).Error);
        // 자기 이름의 대소문자만 바꿀 때는 중복 검사를 하지 않는다 (VaultEntryDialog 와 같음)
        var x = new VaultEntry { Name = "Deploy" };
        var plan = MainWindow.PlanVaultEdit(x, new("deploy", null, null), (_, _) => true);
        Assert.Null(plan.Error);
        Assert.Equal("deploy", plan.Name);
    }

    [Theory]
    [InlineData("a‮b")]
    [InlineData("a\nb")]
    [InlineData("a​b")]
    public void 보이지_않는_문자는_거부(string bad)
    {
        Assert.NotNull(MainWindow.PlanVaultEdit(Entry(), new(bad, null, null), (_, _) => false).Error);
        Assert.NotNull(MainWindow.PlanVaultEdit(Entry(), new(null, bad, null), (_, _) => false).Error);
        Assert.NotNull(MainWindow.PlanVaultEdit(Entry(), new(null, null, bad), (_, _) => false).Error);
    }

    [Fact]
    public void 너무_긴_값은_거부()
    {
        Assert.NotNull(MainWindow.PlanVaultEdit(Entry(), new(new string('a', MainWindow.VaultNameMax + 1), null, null), (_, _) => false).Error);
        Assert.NotNull(MainWindow.PlanVaultEdit(Entry(), new(null, null, new string('a', MainWindow.VaultNoteMax + 1)), (_, _) => false).Error);
    }

    [Theory]
    // 호스트에 직접 고른 계정: 계정 사용자명이 우선, 비었으면 호스트 값
    [InlineData(true, "hostuser", "vault", "", "vault")]
    [InlineData(true, "hostuser", "", "", "hostuser")]
    // 기본 계정: 호스트 값이 우선, 비었으면 계정 값, 그래도 비면 프로필 기본 사용자명
    [InlineData(false, "hostuser", "vault", "", "hostuser")]
    [InlineData(false, "", "vault", "prof", "vault")]
    [InlineData(false, "", "", "prof", "prof")]
    public void 접속_사용자명_우선순위(bool prefer, string host, string vault, string profile, string expected) =>
        Assert.Equal(expected, MainWindow.EffectiveUsername(prefer, host, vault, profile));

    [Fact]
    public void 기본_계정을_쓰는데_호스트에_사용자명이_있으면_사용자명_변경_영향_없음()
    {
        Assert.Equal(MainWindow.EffectiveUsername(false, "admin", "root", null), MainWindow.EffectiveUsername(false, "admin", "ubuntu", null));
        Assert.NotEqual(MainWindow.EffectiveUsername(true, "admin", "root", null), MainWindow.EffectiveUsername(true, "admin", "ubuntu", null));
    }

    // ----- 등록 · 승인 · 감사 경로 -----

    [Fact]
    public void 새_도구가_등록되고_승인_감사_경로를_쓴다()
    {
        var dir = Path.Combine(RepoRoot(), "RemoteClient", "Claude");
        var setup = File.ReadAllText(Path.Combine(dir, "MainWindow.McpSetup.cs"));
        var vault = File.ReadAllText(Path.Combine(dir, "MainWindow.McpVault.cs"));
        foreach (var (tool, src) in new[] { ("set_language", setup), ("set_window_state", setup), ("vault_update", vault) })
        {
            Assert.Contains($"new McpTool(\"{tool}\"", src);
            Assert.Contains($"AuditLog.Write(\"{tool}\"", src);
        }
        Assert.Contains("AskApprovalAsync(\"set_language\"", setup);
        Assert.Contains("AskApprovalAsync(\"vault_update\"", vault);
        // vault_update 는 비밀 값을 다루지 않는다
        var update = vault[vault.IndexOf("VaultUpdateFromClaude(System.Text.Json.JsonElement", StringComparison.Ordinal)..];
        update = update[..update.IndexOf("/// <summary>", StringComparison.Ordinal)];
        foreach (var secret in new[] { "Password", "Passphrase", "ProtectedKey", ".Key ", "Secret." })
            Assert.DoesNotContain(secret, update);
        // 언어는 공통 설정 — 프로필 설정(SettingsStore.Update)이 아니라 UpdateGlobal 로 저장
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "RemoteClient", "MainWindow.Settings.cs"));
        var save = settings[settings.IndexOf("private bool SaveLanguage", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("SettingsStore.UpdateGlobal(", save);
        // 로그인 설정은 Claude 가 바꾸지 않는다
        var lang = setup[setup.IndexOf("SetLanguageFromClaude(string", StringComparison.Ordinal)..];
        lang = lang[..lang.IndexOf("ParseWindowStateArg", StringComparison.Ordinal)];
        Assert.DoesNotContain(".Login", lang);
        Assert.DoesNotContain("SettingsStore.Update(", lang);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "build.ps1"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
