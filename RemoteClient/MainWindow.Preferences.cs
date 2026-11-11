using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Nubis;

public partial class MainWindow
{
    // =================== 설정: 기본 Vault 계정 · 편의 옵션 ===================

    /// <summary>Settings 페이지를 열 때 편의 옵션·기본 Vault 표시를 현재 값으로</summary>
    private void RefreshPreferences()
    {
        var s = SettingsStore.Current;
        ShowRecentBox.IsChecked = s.ShowRecent;
        ConfirmCloseBox.IsChecked = s.ConfirmCloseTab;
        SmartCopyBox.IsChecked = s.SmartCopyPaste;
        PasteConfirmBox.IsChecked = s.ConfirmMultilinePaste;
        SshAutoReconnectBox.IsChecked = s.SshAutoReconnect;
        SftpOpenFolderBox.IsChecked = s.SftpOpenFolderAfterDownload;
        CloudSyncStartBox.IsChecked = s.CloudSyncOnStart;
        CloudSyncIntervalBox.Text = s.CloudSyncIntervalMinutes.ToString();
        DefSshVaultText.Text = DefaultVaultLabel(s.DefaultSshVaultId);
        DefRdpVaultText.Text = DefaultVaultLabel(s.DefaultRdpVaultId);
    }

    private static string DefaultVaultLabel(Guid? id) =>
        VaultStore.Get(id) is { } v ? $"{v.Name}  ·  {v.Detail}" : id != null ? L.T("삭제된 계정 — 다시 고르세요") : L.T("지정 안 함");

    private void OnPrefClick(object sender, RoutedEventArgs e)
    {
        var recentBefore = SettingsStore.Current.ShowRecent;
        SettingsStore.Update(s =>
        {
            s.ShowRecent = ShowRecentBox.IsChecked == true;
            s.ConfirmCloseTab = ConfirmCloseBox.IsChecked == true;
            s.SmartCopyPaste = SmartCopyBox.IsChecked == true;
            s.ConfirmMultilinePaste = PasteConfirmBox.IsChecked == true;
            s.SshAutoReconnect = SshAutoReconnectBox.IsChecked == true;
            s.SftpOpenFolderAfterDownload = SftpOpenFolderBox.IsChecked == true;
            s.CloudSyncOnStart = CloudSyncStartBox.IsChecked == true;
        });
        if (recentBefore != SettingsStore.Current.ShowRecent) RefreshHosts();
    }

    private void OnCloudSyncIntervalChanged(object sender, RoutedEventArgs e)
    {
        var v = int.TryParse(CloudSyncIntervalBox.Text.Trim(), out var n) && n > 0 ? Math.Clamp(n, 5, 24 * 60) : 0;
        CloudSyncIntervalBox.Text = v.ToString();
        if (v == SettingsStore.Current.CloudSyncIntervalMinutes) return;
        SettingsStore.Update(s => s.CloudSyncIntervalMinutes = v);
        StartCloudAutoSync();
    }

    private void OnDefaultVaultPick(object sender, RoutedEventArgs e)
    {
        var rdp = (sender as FrameworkElement)?.Tag as string == "rdp";
        var s = SettingsStore.Current;
        var sub = rdp
            ? L.T("계정을 따로 정하지 않은 모든 RDP(Windows) 호스트가 이 계정으로 로그인합니다.")
            : L.T("계정을 따로 정하지 않은 모든 SSH(Linux) 호스트가 이 계정으로 로그인합니다.");
        if (VaultPickerDialog.Pick(this, rdp ? s.DefaultRdpVaultId : s.DefaultSshVaultId, rdp ? Protocol.Rdp : Protocol.Ssh, sub) is not { } id) return;
        SettingsStore.Update(x => { if (rdp) x.DefaultRdpVaultId = id; else x.DefaultSshVaultId = id; });
        RefreshPreferences();
        RefreshHosts();
    }

    private void OnDefaultVaultClear(object sender, RoutedEventArgs e)
    {
        var rdp = (sender as FrameworkElement)?.Tag as string == "rdp";
        SettingsStore.Update(x => { if (rdp) x.DefaultRdpVaultId = null; else x.DefaultSshVaultId = null; });
        RefreshPreferences();
        RefreshHosts();
    }

    /// <summary>목록에 없는 글꼴 이름을 직접 입력 (설치된 글꼴만)</summary>
    private void OnOtherFont(object sender, RoutedEventArgs e)
    {
        if (PromptDialog.Ask(this, L.T("터미널 글꼴"), L.T("설치된 글꼴 이름 (예: Sarasa Mono K)"), SettingsStore.Current.FontFamily) is not { Length: > 0 } name) return;
        var installed = Fonts.SystemFontFamilies.FirstOrDefault(f => f.Source.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (installed == null)
        {
            Dialog.Show(this, L.F("'{0}' 글꼴이 이 PC 에 설치되어 있지 않습니다.", name), L.T("터미널 글꼴"));
            return;
        }
        SettingsStore.Update(x => x.FontFamily = installed.Source);
        foreach (var rb in FontPanel.Children.OfType<System.Windows.Controls.Border>().Select(b => (System.Windows.Controls.RadioButton)b.Child))
            rb.IsChecked = (string)rb.Tag == installed.Source;
    }

    // =================== Vault 페이지: 기본 계정 지정 · 쓰는 호스트 보기 ===================

    private void OnVaultSetDefault(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry v) return;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string header, Action act)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += (_, _) => { act(); RefreshVault(); RefreshHosts(); };
            menu.Items.Add(item);
        }
        var s = SettingsStore.Current;
        Add(s.DefaultSshVaultId == v.Id ? L.T("SSH 기본 계정 해제") : L.T("SSH(Linux) 기본 계정으로"),
            () => SettingsStore.Update(x => x.DefaultSshVaultId = x.DefaultSshVaultId == v.Id ? null : v.Id));
        Add(s.DefaultRdpVaultId == v.Id ? L.T("RDP 기본 계정 해제") : L.T("RDP(Windows) 기본 계정으로"),
            () => SettingsStore.Update(x => x.DefaultRdpVaultId = x.DefaultRdpVaultId == v.Id ? null : v.Id));
        menu.IsOpen = true;
    }

    /// <summary>이 계정을 쓰는 호스트만 호스트 목록에 보이기 (기본 계정으로 쓰는 호스트 포함)</summary>
    private void OnVaultShowHosts(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry v) return;
        NavHosts.IsChecked = true;
        SearchBox.Text = "vault:" + v.Name.Split(' ')[0];
        _searchTimer?.Stop();
        RefreshHosts();
    }
}
