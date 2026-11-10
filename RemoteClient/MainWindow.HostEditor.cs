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
    // =================== 편집 패널 ===================

    private void OpenEditor(HostEntry? h)
    {
        _editing = h;
        EditorTitle.Text = h == null ? "New Host" : "Edit Host";
        DeleteButton.Visibility = h == null ? Visibility.Collapsed : Visibility.Visible;

        var proto = h?.Protocol ?? Protocol.Ssh;
        EdPort.Text = (h?.Port ?? HostEntry.PreferredPort(proto)).ToString();
        (proto == Protocol.Ssh ? ProtoSsh : ProtoRdp).IsChecked = true;
        UpdateProtoUi();

        EdAddress.Text = h?.PrimaryAddress ?? "";
        EdAddressLabel.Text = h?.IsCloud == true ? L.T("주소 (사설 IP · 동기화로 갱신)") : L.T("주소");
        EdPublicIp.Text = h?.PublicIp ?? "";
        EdUsePublic.IsChecked = h?.UsePublicIp == true;
        EdLabel.Text = h?.Label ?? "";
        EdGroup.Text = h?.Group ?? "";
        EdUser.Text = h?.Username ?? "";
        EdPassword.Password = h?.Password ?? "";
        EdKey.Text = h?.KeyPath ?? "";
        _edVaultId = h?.VaultId;
        UpdateAuthUi();

        NavHosts.IsChecked = true;
        EditorLayer.Visibility = Visibility.Visible;
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(390, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Dispatcher.BeginInvoke(() => EdAddress.Focus(), DispatcherPriority.Input);
    }

    private void CloseEditor()
    {
        var anim = new DoubleAnimation(0, 390, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) => EditorLayer.Visibility = Visibility.Collapsed;
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        EdPassword.Clear();
    }

    private void OnProtoChanged(object sender, RoutedEventArgs e)
    {
        if (EdPort == null) return;
        var proto = ProtoRdp.IsChecked == true ? Protocol.Rdp : Protocol.Ssh;
        var other = proto == Protocol.Ssh ? Protocol.Rdp : Protocol.Ssh;
        if (string.IsNullOrWhiteSpace(EdPort.Text) || EdPort.Text == HostEntry.PreferredPort(other).ToString())
            EdPort.Text = HostEntry.PreferredPort(proto).ToString();
        UpdateProtoUi();
    }

    private void UpdateProtoUi()
    {
        KeyPanel.Visibility = ProtoSsh.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateAuthUi();
    }

    // 편집 중인 Vault 계정 (저장 전까지는 호스트에 반영하지 않음)
    private Guid? _edVaultId;

    /// <summary>
    /// 로그인 영역: Vault 계정을 골랐으면 카드로 보여주고(무엇으로 로그인하는지),
    /// 아래 사용자명·비밀번호·키 파일 칸은 "비워 두면 Vault 값" 으로 안내한다.
    /// </summary>
    private void UpdateAuthUi()
    {
        if (VaultCard == null || KeyFileRow == null) return;   // InitializeComponent 중
        var ssh = ProtoSsh.IsChecked == true;
        var v = VaultStore.Get(_edVaultId);
        var missing = _edVaultId != null && v == null;

        VaultCard.Visibility = _edVaultId != null ? Visibility.Visible : Visibility.Collapsed;
        VaultPickButton.Visibility = _edVaultId == null ? Visibility.Visible : Visibility.Collapsed;
        if (v != null)
        {
            EdVaultGlyph.Text = v.Glyph;
            EdVaultName.Text = v.Name;
            var methods = new List<string> { string.IsNullOrWhiteSpace(v.Username) ? L.T("사용자명 없음") : L.F("사용자 {0}", v.Username) };
            if (v.HasPassword) methods.Add(L.T("비밀번호"));
            if (v.HasKey) methods.Add(ssh ? L.F("{0} 키", v.KeyType.Length > 0 ? v.KeyType : "SSH") : L.T("SSH 키 (RDP 에서는 안 씀)"));
            EdVaultDetail.Text = string.Join(" · ", methods);
        }
        else if (missing)
        {
            EdVaultGlyph.Text = "";
            EdVaultName.Text = L.T("삭제된 Vault 계정");
            EdVaultDetail.Text = L.T("다른 계정을 고르거나 해제하세요.");
        }

        VaultHint.Text = v == null
            ? L.T("여러 서버가 같은 계정을 쓰면 Vault 에 한 번 저장해 두고 골라 쓰세요. 아래에 직접 입력해도 됩니다.")
            : L.T("이 계정으로 로그인합니다. 아래 칸에 값을 넣으면 그 값이 Vault 보다 우선합니다.");

        // 직접 입력 칸: Vault 값을 쓰는지 바로 보이게
        var vaultUser = v != null && !string.IsNullOrWhiteSpace(v.Username) ? v.Username : null;
        EdUserLabel.Text = vaultUser != null ? L.T("사용자명 (비워 두면 Vault)") : L.T("사용자명");
        EdUser.Tag = vaultUser != null ? L.F("Vault 사용자: {0}", vaultUser) : L.T("예: ubuntu  /  DOMAIN\\user");
        EdPasswordLabel.Text = v?.HasPassword == true ? L.T("비밀번호 (비워 두면 Vault)") : L.T("비밀번호");
        PwHint.Text = v?.HasPassword == true
            ? L.T("비워 두면 Vault 계정의 비밀번호를 씁니다.")
            : v?.HasKey == true && ssh
                ? L.T("Vault 키로 인증합니다. 키 인증이 안 되는 서버를 위해 비밀번호를 넣어 둘 수도 있습니다.")
                : v != null && !ssh
                    ? L.T("이 Vault 계정에는 비밀번호가 없어 RDP 접속 때 비밀번호를 묻습니다.")
                    : L.T("비워두면 접속할 때 묻습니다. 저장 시 Windows DPAPI 로 암호화됩니다.");

        EdKeyLabel.Text = v?.HasKey == true ? L.T("개인키 파일 (비워 두면 Vault 키)") : L.T("개인키 파일 (선택)");
        KeyHint.Text = v?.HasKey == true
            ? L.T("Vault 계정의 키로 인증합니다 (키 암호도 Vault 값). 다른 키를 쓰려면 파일을 지정하세요.")
            : L.T("키에 암호가 걸려 있으면 비밀번호 칸의 값을 암호로 사용합니다.");
    }

    private void OnPickVault(object sender, RoutedEventArgs e)
    {
        var proto = ProtoRdp.IsChecked == true ? Protocol.Rdp : Protocol.Ssh;
        if (VaultPickerDialog.Pick(this, _edVaultId, proto) is { } id) { _edVaultId = id; UpdateAuthUi(); }
    }

    private void OnClearVault(object sender, RoutedEventArgs e)
    {
        _edVaultId = null;
        UpdateAuthUi();
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("SSH 개인키 선택"),
            Filter = L.T("모든 파일|*.*|PuTTY 키 (*.ppk)|*.ppk|PEM (*.pem)|*.pem"),
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dlg.ShowDialog(this) == true) EdKey.Text = dlg.FileName;
    }

    private HostEntry? SaveEditor()
    {
        var address = EdAddress.Text.Trim();
        if (address.Length == 0) { EdAddress.Focus(); return null; }
        if (!int.TryParse(EdPort.Text.Trim(), out var port) || port is < 1 or > 65535) { EdPort.Focus(); return null; }

        var h = _editing ?? new HostEntry();
        h.Protocol = ProtoRdp.IsChecked == true ? Protocol.Rdp : Protocol.Ssh;
        h.Address = address;
        if (h.PrivateIp != null) h.PrivateIp = address;
        var pub = EdPublicIp.Text.Trim();
        h.PublicIp = pub.Length > 0 ? pub : null;
        h.UsePublicIp = EdUsePublic.IsChecked == true && h.PublicIp != null;
        h.Port = port;
        h.Label = EdLabel.Text.Trim();
        h.Group = EdGroup.Text.Trim();
        // 지워진 Vault 계정을 가리키고 있으면 저장 전에 고르게 한다
        if (_edVaultId != null && VaultStore.Get(_edVaultId) == null) { OnPickVault(this, new RoutedEventArgs()); return null; }
        var ssh = ProtoSsh.IsChecked == true;

        h.VaultId = _edVaultId;
        h.Username = EdUser.Text.Trim();
        h.Password = EdPassword.Password;
        h.KeyPath = ssh && EdKey.Text.Trim().Length > 0 ? EdKey.Text.Trim() : null;

        if (_editing == null) _hosts.Add(h);
        SaveHosts();
        RefreshHosts();
        CloseEditor();
        return h;
    }

    private void OnEditorSave(object sender, RoutedEventArgs e) => SaveEditor();

    private void OnEditorSaveConnect(object sender, RoutedEventArgs e)
    {
        if (SaveEditor() is { } h) Connect(h);
    }

    private void OnEditorDelete(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        var before = _hosts.Count;
        DeleteHost(_editing);
        if (_hosts.Count < before) CloseEditor();
    }

    private void OnEditorClose(object sender, RoutedEventArgs e) => CloseEditor();
    private void OnEditorDimClick(object sender, MouseButtonEventArgs e) => CloseEditor();
}
