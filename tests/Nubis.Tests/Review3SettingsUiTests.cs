using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 리뷰 3차 (설정 화면 · 공통 UI): 섹션 바로가기 강조 판단, 고정 머리글 구조, 기본 Vault 계정 줄 정렬, 입력란 비활성 모양
/// </summary>
public class Review3SettingsUiTests
{
    private static string SourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "RemoteClient", "Nubis.csproj"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("저장소 루트를 찾지 못함"), "RemoteClient");
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(SourceRoot(), name), Encoding.UTF8);

    // ----- 지금 보고 있는 섹션 -----

    private static readonly double[] Tops = { 0, 300, 700, 1200, 1500 };

    [Fact]
    public void 맨_위에서는_첫_섹션()
    {
        Assert.Equal(0, MainWindow.CurrentSettingsSection(Tops, 0, 1000));
    }

    [Fact]
    public void 제목이_화면_위쪽을_지난_마지막_섹션()
    {
        Assert.Equal(1, MainWindow.CurrentSettingsSection(Tops, 292, 1000));   // 바로가기는 제목 8px 위로 스크롤
        Assert.Equal(1, MainWindow.CurrentSettingsSection(Tops, 650, 1000));
        Assert.Equal(2, MainWindow.CurrentSettingsSection(Tops, 690, 1000));
    }

    [Fact]
    public void 맨_아래까지_내리면_마지막_섹션()
    {
        Assert.Equal(4, MainWindow.CurrentSettingsSection(Tops, 1000, 1000));
    }

    [Fact]
    public void 바로가기로_고른_섹션은_화면에_보이는_동안_강조()
    {
        // 끝 근처 섹션(3)은 맨 위까지 오지 못한다 — 맨 아래(offset 1000)에서도 고른 섹션이 화면 안이면 그것
        Assert.Equal(3, MainWindow.CurrentSettingsSection(Tops, 1000, 1000, viewport: 600, picked: 3));
        // 고른 섹션 제목이 화면 위로 지나가면 평소 기준
        Assert.Equal(3, MainWindow.CurrentSettingsSection(Tops, 1250, 0, viewport: 600, picked: 1));
    }

    [Fact]
    public void 위치를_못_구한_섹션은_건너뛴다()
    {
        var tops = new[] { double.NaN, 300, double.NaN };
        Assert.Equal(1, MainWindow.CurrentSettingsSection(tops, 0, 1000));
        Assert.Equal(1, MainWindow.CurrentSettingsSection(tops, 1000, 1000));
        Assert.Equal(-1, MainWindow.CurrentSettingsSection(Array.Empty<double>(), 0, 0));
    }

    // ----- XAML 구조 -----

    [Fact]
    public void Settings_머리글은_스크롤_영역_밖에_있고_바로가기가_모두_들어_있다()
    {
        var xaml = Read("MainWindow.xaml");
        var view = xaml.IndexOf(@"x:Name=""SettingsView""", StringComparison.Ordinal);
        var bar = xaml.IndexOf(@"x:Name=""SettingsJumpBar""", StringComparison.Ordinal);
        var page = xaml.IndexOf(@"x:Name=""SettingsPage""", StringComparison.Ordinal);
        Assert.True(view >= 0 && bar > view && page > bar, "SettingsView > SettingsJumpBar > SettingsPage(ScrollViewer) 순서여야 한다");
        Assert.Contains(@"ScrollChanged=""OnSettingsScrollChanged""", xaml);
        // 바로가기 버튼은 SettingsJumpBar 안에만 (강조 처리가 그 자식만 본다)
        var barEnd = xaml.IndexOf("</WrapPanel>", bar, StringComparison.Ordinal);
        var inBar = Regex.Matches(xaml[bar..barEnd], @"Click=""OnSettingsJump""").Count;
        Assert.Equal(Regex.Matches(xaml, @"Click=""OnSettingsJump""").Count, inBar);
        // 머리글 표시를 코드가 SettingsPage 와 함께 바꾼다
        Assert.Contains("SettingsView.Visibility = SettingsPage.Visibility", Read("MainWindow.xaml.cs"));
    }

    [Fact]
    public void 기본_Vault_계정_줄은_가운데로_밀리지_않는다()
    {
        // Stretch 인 Grid 에 MaxWidth 를 주면 넓은 창에서 가운데 정렬돼 'SSH (Linux)' 가 섹션 글보다 오른쪽에서 시작한다
        var xaml = Read("MainWindow.xaml");
        var label = xaml.IndexOf("SSH (Linux)", StringComparison.Ordinal);
        Assert.True(label > 0);
        // 이 줄들을 담은 Grid 여는 태그 (<Grid.ColumnDefinitions> 등은 제외)
        var grid = Regex.Matches(xaml[..label], @"<Grid[\s>]").Last().Index;
        var tag = xaml[grid..xaml.IndexOf('>', grid)];
        Assert.DoesNotContain("MaxWidth", tag);
    }

    [Fact]
    public void 입력란은_비활성일_때_흐리게_보인다()
    {
        var app = Read("App.xaml");
        foreach (var type in new[] { "TextBox", "PasswordBox" })
        {
            var start = app.IndexOf($@"<ControlTemplate TargetType=""{type}"">", StringComparison.Ordinal);
            Assert.True(start >= 0, type);
            var end = app.IndexOf("</ControlTemplate>", start, StringComparison.Ordinal);
            Assert.Contains(@"<Trigger Property=""IsEnabled"" Value=""False"">", app[start..end]);
        }
    }

    [Fact]
    public void 로그인_입력란은_패널_투명도를_겹쳐_낮추지_않는다()
    {
        // 입력란 자체가 비활성 모양을 가지므로 LoginFields.Opacity 까지 낮추면 0.3 수준으로 읽기 어려워진다
        Assert.DoesNotContain("LoginFields.Opacity", Read("MainWindow.Settings.cs"));
    }
}
