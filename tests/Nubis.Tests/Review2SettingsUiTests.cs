using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 리뷰 2차 (설정 화면 · 공통 UI): XAML 의 감싸지 않은 한국어 문구, Settings 바로가기 대상, 키보드 포커스 표시
/// </summary>
public class Review2SettingsUiTests
{
    private static string SourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "RemoteClient", "Nubis.csproj"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("저장소 루트를 찾지 못함"), "RemoteClient");
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(SourceRoot(), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string WithoutComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>
    /// 번역하지 않는 한국어가 허용되는 곳: 언어 선택 창(아직 언어를 모르므로 두 언어로 씀)과 언어 이름 "한국어"
    /// </summary>
    private static bool Allowed(string file, string value) =>
        Path.GetFileName(file) == "LanguageWindow.xaml" || value == "한국어";

    [Fact]
    public void XAML_화면_문구에_감싸지_않은_한국어가_없다()
    {
        var bad = new List<string>();
        var attr = new Regex(@"\b(Text|Content|Header|ToolTip|Tag|Title|AutomationProperties\.Name|AutomationProperties\.HelpText)=""([^""]*)""");
        var inner = new Regex(@">([^<>]*[가-힣][^<>]*)<");
        foreach (var file in XamlFiles())
        {
            var text = WithoutComments(File.ReadAllText(file, Encoding.UTF8));
            foreach (Match m in attr.Matches(text))
            {
                var v = m.Groups[2].Value;
                if (v.StartsWith("{", StringComparison.Ordinal) || !Regex.IsMatch(v, "[가-힣]") || Allowed(file, v)) continue;
                bad.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}=\"{v}\"");
            }
            foreach (Match m in inner.Matches(text))
            {
                var v = m.Groups[1].Value.Trim();
                if (!Allowed(file, v)) bad.Add($"{Path.GetFileName(file)}: >{v}<");
            }
        }
        Assert.True(bad.Count == 0, "{l:Tr '…'} 로 감싸지 않은 한국어 문구:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Settings_바로가기_Tag_는_있는_이름을_가리킨다()
    {
        var xaml = File.ReadAllText(Path.Combine(SourceRoot(), "MainWindow.xaml"), Encoding.UTF8);
        var names = Regex.Matches(xaml, @"x:Name=""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        var tags = Regex.Matches(xaml, @"Tag=""([^""]+)""\s+Click=""OnSettingsJump""").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(tags);
        var missing = tags.Where(t => !names.Contains(t)).ToList();
        Assert.True(missing.Count == 0, "없는 이름을 가리키는 바로가기: " + string.Join(", ", missing));
    }

    [Fact]
    public void 공통_스타일은_키보드_포커스_표시를_끄지_않는다()
    {
        // 버튼·체크박스·사이드바·세그먼트가 FocusVisualStyle 을 x:Null 로 두면 Tab 으로 어디 있는지 보이지 않는다
        var app = File.ReadAllText(Path.Combine(SourceRoot(), "App.xaml"), Encoding.UTF8);
        Assert.DoesNotContain(@"Property=""FocusVisualStyle"" Value=""{x:Null}""", app);
        Assert.Contains(@"x:Key=""FocusRing""", app);
    }

    [Fact]
    public void 체크박스_템플릿은_긴_설명이_줄바꿈되게_폭을_제한한다()
    {
        // 가로 StackPanel 은 자식에게 무한 폭을 줘서 TextWrapping 이 동작하지 않는다
        var app = File.ReadAllText(Path.Combine(SourceRoot(), "App.xaml"), Encoding.UTF8);
        var start = app.IndexOf(@"<ControlTemplate TargetType=""CheckBox"">", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = app.IndexOf("</ControlTemplate>", start, StringComparison.Ordinal);
        var template = app[start..end];
        Assert.DoesNotContain(@"<StackPanel Orientation=""Horizontal""", template);
        Assert.Contains("<Grid", template);
    }
}
