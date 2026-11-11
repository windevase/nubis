using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nubis;

/// <summary>여러 호스트를 한 번에 편집. 체크한 항목만 바꾼다.</summary>
public partial class BulkEditDialog : Window
{
    private readonly IReadOnlyList<HostEntry> _hosts;
    private readonly IReadOnlyList<string> _groups;
    private Guid? _vaultId;
    private bool _loading = true;

    private BulkEditDialog(IReadOnlyList<HostEntry> hosts, IReadOnlyList<string> groups)
    {
        InitializeComponent();
        _hosts = hosts;
        _groups = groups;

        var ssh = hosts.Count(h => h.Protocol == Protocol.Ssh);
        var rdp = hosts.Count(h => h.Protocol == Protocol.Rdp);
        var cloud = hosts.Count(h => h.IsCloud);
        var withPublic = hosts.Count(h => h.HasPublicIp);
        TitleText.Text = L.F("호스트 {0}개 일괄 편집", hosts.Count);
        var kinds = new List<string>();
        if (ssh > 0) kinds.Add($"SSH {ssh}");
        if (rdp > 0) kinds.Add($"RDP {rdp}");
        if (cloud > 0) kinds.Add(L.F("클라우드 {0}", cloud));
        SubText.Text = L.F("{0}. 체크한 항목만 바뀌고 나머지는 호스트마다 그대로입니다.", string.Join(" · ", kinds));

        // 모두 같은 값이면 그 값을 미리 채운다
        var ids = hosts.Select(h => h.VaultId).Distinct().ToList();
        _vaultId = ids.Count == 1 ? ids[0] : null;
        UpdateVaultButton();
        if (hosts.Select(h => h.Username).Distinct().Count() == 1) UserBox.Text = hosts[0].Username;
        if (hosts.Select(h => h.Port).Distinct().Count() == 1) PortBox.Text = hosts[0].Port.ToString();
        var locals = hosts.Where(h => !h.IsCloud).Select(h => h.Group?.Trim() ?? "").Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (locals.Count == 1) GroupBox.Text = locals[0];

        // 해당 없는 항목은 끈다
        GroupCheck.IsEnabled = GroupBox.IsEnabled = cloud < hosts.Count;
        IpCheck.IsEnabled = withPublic > 0;
        if (withPublic > 0 && hosts.Where(h => h.HasPublicIp).All(h => h.UsePublicIp)) IpPublic.IsChecked = true;
        if (hosts.All(h => h.Favorite)) FavOff.IsChecked = true;

        var notes = new List<string>();
        if (cloud > 0 && cloud < hosts.Count) notes.Add(L.F("그룹은 직접 등록한 호스트 {0}개에만 적용됩니다 (클라우드 호스트는 프로필 그룹).", hosts.Count - cloud));
        if (withPublic > 0 && withPublic < hosts.Count) notes.Add(L.F("접속 IP 는 공인 IP 가 있는 {0}개에만 적용됩니다.", withPublic));
        if (ssh > 0 && rdp > 0) notes.Add(L.T("SSH 와 RDP 가 섞여 있습니다. 포트를 바꾸면 둘 다 같은 포트가 됩니다."));
        NoteText.Text = string.Join("\n", notes);
        NoteText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => _loading = false;
    }

    /// <summary>바꿀 값, 취소하면 null</summary>
    public static BulkEdit? Ask(Window owner, IReadOnlyList<HostEntry> hosts, IReadOnlyList<string> groups)
    {
        var dlg = new BulkEditDialog(hosts, groups) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private BulkEdit? _result;

    private void UpdateVaultButton() =>
        VaultPickButton.Content = VaultStore.Get(_vaultId) is { } v ? $"{v.Name}  ({v.Username})" : L.T("계정 선택...");

    private void OnPickVault(object sender, RoutedEventArgs e)
    {
        var proto = _hosts.All(h => h.Protocol == Protocol.Rdp) ? Protocol.Rdp : Protocol.Ssh;
        if (VaultPickerDialog.Pick(this, _vaultId, proto, L.F("고른 호스트 {0}개가 이 계정으로 로그인합니다.", _hosts.Count)) is not { } id) return;
        _vaultId = id;
        VaultCheck.IsChecked = true;
        VaultSet.IsChecked = true;
        UpdateVaultButton();
    }

    private void OnPickGroup(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var g in _groups.Prepend(""))
        {
            var item = new MenuItem { Header = g.Length == 0 ? L.T("(그룹 없음)") : g };
            item.Click += (_, _) => { GroupBox.Text = g; GroupCheck.IsChecked = true; };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>값을 입력하면 그 항목을 자동으로 체크</summary>
    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (sender == UserBox) UserCheck.IsChecked = true;
        else if (sender == PortBox) PortCheck.IsChecked = true;
        else if (sender == GroupBox) GroupCheck.IsChecked = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var r = new BulkEdit();
        if (VaultCheck.IsChecked == true)
        {
            if (VaultSet.IsChecked == true && VaultStore.Get(_vaultId) == null) { ShowError(L.T("지정할 Vault 계정을 고르세요.")); return; }
            r.SetVault = true;
            r.VaultId = VaultSet.IsChecked == true ? _vaultId : null;
        }
        r.ClearOwnCredentials = ClearOwnCheck.IsChecked == true;
        if (UserCheck.IsChecked == true) r.Username = UserBox.Text.Trim();
        if (PortCheck.IsChecked == true)
        {
            if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) { ShowError(L.T("포트는 1~65535 사이 숫자로 입력하세요.")); PortBox.Focus(); return; }
            r.Port = port;
        }
        if (GroupCheck.IsChecked == true && GroupCheck.IsEnabled) r.Group = GroupBox.Text.Trim();
        if (IpCheck.IsChecked == true && IpCheck.IsEnabled) r.UsePublicIp = IpPublic.IsChecked == true;
        if (FavCheck.IsChecked == true) r.Favorite = FavOn.IsChecked == true;
        if (r.IsEmpty) { ShowError(L.T("바꿀 항목을 하나 이상 체크하세요.")); return; }
        _result = r;
        DialogResult = true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }
}
