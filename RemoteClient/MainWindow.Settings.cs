using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Nubis;

public partial class MainWindow
{
    // =================== 설정 ===================

    private static readonly string[] FontCandidates =
        { "Cascadia Mono", "Cascadia Code", "D2Coding", "JetBrains Mono", "Fira Code", "NanumGothicCoding", "Consolas", "Lucida Console" };

    private List<ThemeItem>? _themeItems;

    private void RefreshSettingsPage()
    {
        var s = SettingsStore.Current;

        _themeItems ??= TerminalThemes.All.Select(t => new ThemeItem { Theme = t }).ToList();
        foreach (var it in _themeItems) it.IsSelected = it.Theme.Id == TerminalThemes.Get(s.TerminalTheme).Id;
        ThemeList.ItemsSource ??= _themeItems;

        AutoContrastBox.IsChecked = s.AutoContrast;
        HlIpBox.IsChecked = s.HighlightIp;
        HlNumBox.IsChecked = s.HighlightNumbers;
        HlKwBox.IsChecked = s.HighlightKeywords;
        GpuBox.IsChecked = s.TerminalGpu;
        RdpCertBox.IsChecked = s.RdpCertWarning;
        SshKeyPromptBox.IsChecked = s.SshHostKeyPrompt;
        AutoReadOnlyBox.IsChecked = s.AutoApproveReadOnly;
        SftpAutoOpenBox.IsChecked = s.SftpAutoOpen;
        SftpFollowBox.IsChecked = s.SftpFollowTerminal;
        SftpEditorBox.Text = s.SftpEditor;
        DefSshPortBox.Text = HostEntry.PreferredPort(Protocol.Ssh).ToString();
        DefRdpPortBox.Text = HostEntry.PreferredPort(Protocol.Rdp).ToString();
        FontSizeText.Text = $"{s.FontSize}px";

        if (FontPanel.Children.Count == 0)
        {
            var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in FontCandidates.Where(installed.Contains))
            {
                var rb = new RadioButton
                {
                    Content = name,
                    Tag = name,
                    GroupName = "termfont",
                    Style = (Style)FindResource("Segment"),
                    FontFamily = new FontFamily(name),
                    FontWeight = FontWeights.Normal,
                    MinWidth = 150,
                    Padding = new Thickness(14, 0, 14, 0),
                };
                rb.Checked += (_, _) => SettingsStore.Update(x => x.FontFamily = name);
                FontPanel.Children.Add(new Border
                {
                    Background = (Brush)FindResource("InputBg"),
                    BorderBrush = (Brush)FindResource("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(3),
                    Margin = new Thickness(0, 0, 8, 8),
                    Child = rb,
                });
            }
        }
        foreach (var rb in FontPanel.Children.OfType<Border>().Select(b => (RadioButton)b.Child))
            if ((string)rb.Tag == s.FontFamily && rb.IsChecked != true) rb.IsChecked = true;
    }

    private void OnThemeClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThemeItem item || _themeItems == null) return;
        foreach (var it in _themeItems) it.IsSelected = it == item;
        SettingsStore.Update(s => s.TerminalTheme = item.Theme.Id);
    }

    private void OnAutoContrastClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.AutoContrast = AutoContrastBox.IsChecked == true);

    private void OnHighlightClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s =>
        {
            s.HighlightIp = HlIpBox.IsChecked == true;
            s.HighlightNumbers = HlNumBox.IsChecked == true;
            s.HighlightKeywords = HlKwBox.IsChecked == true;
        });

    private void OnSshKeyPromptClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.SshHostKeyPrompt = SshKeyPromptBox.IsChecked == true);

    /// <summary>기본 포트 입력란: 1~65535 숫자만 저장하고, 비우거나 잘못 넣으면 표준 포트로 되돌린다</summary>
    private void OnDefaultPortChanged(object sender, RoutedEventArgs e)
    {
        var ssh = int.TryParse(DefSshPortBox.Text.Trim(), out var a) && a is >= 1 and <= 65535 ? a : HostEntry.DefaultPort(Protocol.Ssh);
        var rdp = int.TryParse(DefRdpPortBox.Text.Trim(), out var b) && b is >= 1 and <= 65535 ? b : HostEntry.DefaultPort(Protocol.Rdp);
        DefSshPortBox.Text = ssh.ToString();
        DefRdpPortBox.Text = rdp.ToString();
        ApplyDefaultPorts(ssh, rdp);
    }

    /// <summary>
    /// 기본 포트 변경. 포트를 따로 지정하지 않은 클라우드 프로필의 호스트 중 이전 기본 포트를 쓰던 것도 함께 바꾼다.
    /// 바뀐 호스트 수를 돌려준다.
    /// </summary>
    private int ApplyDefaultPorts(int ssh, int rdp)
    {
        var (oldSsh, oldRdp) = (HostEntry.PreferredPort(Protocol.Ssh), HostEntry.PreferredPort(Protocol.Rdp));
        if (oldSsh == ssh && oldRdp == rdp) return 0;
        SettingsStore.Update(x => { x.DefaultSshPort = ssh; x.DefaultRdpPort = rdp; });

        // 포트를 따로 지정하지 않은 클라우드 프로필의 호스트 중, 이전 기본 포트를 쓰던 것도 함께 변경
        // (사용자가 호스트별로 바꾼 포트는 유지)
        var changed = 0;
        foreach (var p in _profiles)
            foreach (var h in _hosts.Where(h => h.CloudProfileId == p.Id))
            {
                if (p.SshPort == null && h.Protocol == Protocol.Ssh && h.Port == oldSsh) { h.Port = ssh; changed++; }
                if (p.RdpPort == null && h.Protocol == Protocol.Rdp && h.Port == oldRdp) { h.Port = rdp; changed++; }
            }
        if (changed > 0) { SaveHosts(); RefreshHosts(); }
        return changed;
    }

    private void OnSftpOptionClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s =>
        {
            s.SftpAutoOpen = SftpAutoOpenBox.IsChecked == true;
            s.SftpFollowTerminal = SftpFollowBox.IsChecked == true;
        });

    private void OnSftpEditorChanged(object sender, RoutedEventArgs e)
    {
        var v = SftpEditorBox.Text.Trim().Trim('"');
        if (v != SettingsStore.Current.SftpEditor) SettingsStore.Update(s => s.SftpEditor = v);
    }

    private void OnSftpEditorVsCode(object sender, RoutedEventArgs e)
    {
        var code = SftpPanel.FindVsCode();
        if (code == null) { Dialog.Show(this, "VS Code(Code.exe)를 찾지 못했습니다. '찾기' 로 직접 지정하세요.", "파일 편집기"); return; }
        SftpEditorBox.Text = code;
        OnSftpEditorChanged(sender, e);
    }

    private void OnSftpEditorBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "편집기 프로그램 선택", Filter = "프로그램 (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|모든 파일|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        SftpEditorBox.Text = dlg.FileName;
        OnSftpEditorChanged(sender, e);
    }

    private void OnAutoReadOnlyClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.AutoApproveReadOnly = AutoReadOnlyBox.IsChecked == true);

    private void OnRdpCertClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.RdpCertWarning = RdpCertBox.IsChecked == true);

    private void OnGpuClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.TerminalGpu = GpuBox.IsChecked == true);

    private void OnFontSmaller(object sender, RoutedEventArgs e) => ChangeFontSize(-1);
    private void OnFontBigger(object sender, RoutedEventArgs e) => ChangeFontSize(+1);

    private void ChangeFontSize(int delta)
    {
        SettingsStore.Update(s => s.FontSize = Math.Clamp(s.FontSize + delta, 9, 28));
        FontSizeText.Text = $"{SettingsStore.Current.FontSize}px";
    }

    private void RefreshKnownHosts() =>
        KnownList.ItemsSource = KnownHostStore.Load().OrderBy(kv => kv.Key).ToList();

    private void OnKnownDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KeyValuePair<string, string> kv)
        {
            KnownHostStore.Remove(kv.Key);
            RefreshKnownHosts();
        }
    }
}
