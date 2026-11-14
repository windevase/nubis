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

    /// <param name="template">새 호스트를 이 값으로 채워 연다 (복제). 저장할 때만 목록에 추가된다</param>
    private void OpenEditor(HostEntry? h, HostEntry? template = null)
    {
        _editing = h;
        _template = h == null ? template : null;
        EditorTitle.Text = h == null ? (template != null ? L.T("호스트 복제") : L.T("새 호스트")) : L.T("호스트 편집");
        DeleteButton.Visibility = h == null ? Visibility.Collapsed : Visibility.Visible;
        EditorError.Text = "";
        h ??= template;

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
        EdGateway.Text = h?.RdpGateway ?? "";
        _edVaultId = h?.VaultId;
        UpdateAuthUi();
        _editorSnapshot = EditorSnapshot();

        NavHosts.IsChecked = true;
        EditorLayer.Visibility = Visibility.Visible;
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(390, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Dispatcher.BeginInvoke(() => EdAddress.Focus(), DispatcherPriority.Input);
    }

    private HostEntry? _template;
    private string _editorSnapshot = "";

    /// <summary>편집 칸 값 전체 (닫을 때 바뀐 게 있는지 비교)</summary>
    private string EditorSnapshot() => string.Join("\u0001", ProtoRdp.IsChecked == true, EdAddress.Text, EdPort.Text, EdPublicIp.Text,
        EdUsePublic.IsChecked == true, EdLabel.Text, EdGroup.Text, EdUser.Text, EdPassword.Password, EdKey.Text, EdGateway.Text, _edVaultId);

    /// <summary>저장하지 않은 변경이 있으면 물어보고 닫는다 (Esc · 바깥 클릭 · X)</summary>
    private void TryCloseEditor()
    {
        if (EditorLayer.Visibility != Visibility.Visible) return;
        if (EditorSnapshot() != _editorSnapshot &&
            Dialog.Show(this, L.T("저장하지 않은 변경 내용이 있습니다. 버리고 닫을까요?"), L.T("호스트 편집"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CloseEditor();
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
        GatewayPanel.Visibility = ProtoRdp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (ProtoRdp.IsChecked == true)
        {
            var d = RdpDefaults.Load();
            GatewayHint.Text = !string.IsNullOrWhiteSpace(d.GatewayHost) && d.GatewayUsage != 0
                ? L.F("비워 두면 원격 데스크톱 연결(mstsc) 기본 설정의 게이트웨이 '{0}' 을 씁니다. 게이트웨이에도 같은 계정으로 로그인합니다.", d.GatewayHost)
                : L.T("회사 밖에서 RD 게이트웨이를 거쳐야 하는 서버만 적으세요. 게이트웨이에도 같은 계정으로 로그인합니다.");
        }
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

        // Vault 계정을 고르면 그 계정이 우선 — 이 호스트에 따로 저장된 계정 정보는 숨기고 쓰지 않는다(지우지 않음).
        // 해제하면 숨겼던 값이 다시 보이고 그 값으로 로그인한다 (HostEntry.WithDefaults · VaultStore.Apply 와 같은 규칙)
        var vaultChosen = _edVaultId != null && v != null;
        var vaultUser = vaultChosen && !string.IsNullOrWhiteSpace(v!.Username) ? v.Username : null;
        VaultHint.Text = !vaultChosen
            ? (missing ? "" : DefaultVaultHint(ssh))
            : v!.HasPassword || (v.HasKey && ssh)
                ? L.T("이 Vault 계정으로 로그인합니다.")
                : L.T("이 Vault 계정에는 비밀번호가 없어 접속할 때 비밀번호를 묻습니다.");
        HostUserPanel.Visibility = vaultUser == null ? Visibility.Visible : Visibility.Collapsed;
        HostSecretPanel.Visibility = vaultChosen ? Visibility.Collapsed : Visibility.Visible;
        var hasHidden = vaultChosen && (EdPassword.Password.Length > 0 || EdKey.Text.Trim().Length > 0 ||
                                        (vaultUser != null && EdUser.Text.Trim().Length > 0));
        HiddenCredNote.Visibility = hasHidden ? Visibility.Visible : Visibility.Collapsed;

        EdUserLabel.Text = vaultChosen ? L.T("사용자명 (이 Vault 계정에 사용자명이 없음)") : L.T("사용자명");
        EdUser.Tag = L.T("예: ubuntu  /  DOMAIN\\user");
        EdPasswordLabel.Text = L.T("비밀번호");
        PwHint.Text = L.T("비워두면 접속할 때 묻습니다. 저장 시 Windows DPAPI 로 암호화됩니다.");
        EdKeyLabel.Text = L.T("개인키 파일 (선택)");
        KeyHint.Text = L.T("키에 암호가 걸려 있으면 비밀번호 칸의 값을 암호로 사용합니다.");
    }

    /// <summary>Vault 계정을 고르지 않았을 때 실제로 쓰일 기본 계정 안내 (프로필 기본 계정 · 설정의 기본 Vault)</summary>
    private string DefaultVaultHint(bool ssh)
    {
        var probe = new HostEntry
        {
            Protocol = ssh ? Protocol.Ssh : Protocol.Rdp,
            Password = EdPassword?.Password ?? "",
            KeyPath = ssh && EdKey?.Text.Trim().Length > 0 ? EdKey.Text.Trim() : null,
            CloudProfileId = _editing?.CloudProfileId,
        };
        var profile = _profiles.FirstOrDefault(p => p.Id == probe.CloudProfileId);
        var (id, source) = probe.ResolveVault(profile);
        if (VaultStore.Get(id) is { } d)
            return source == "profile"
                ? L.F("지정하지 않으면 이 클라우드 프로필의 기본 계정 '{0}' 으로 로그인합니다.", d.Name)
                : L.F("지정하지 않으면 기본 Vault 계정 '{0}' 으로 로그인합니다 (Settings → 기본 Vault 계정).", d.Name);
        return L.T("여러 서버가 같은 계정을 쓰면 Vault 에 한 번 저장해 두고 골라 쓰세요. 아래에 직접 입력해도 됩니다.");
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
        if (address.Length == 0) { EditorError.Text = L.T("주소를 입력하세요."); EdAddress.Focus(); return null; }
        if (address.Contains(' ') || address.Contains('@'))
        {
            EditorError.Text = L.T("주소에는 IP 또는 호스트명만 넣으세요. 사용자명은 아래 '사용자명' 칸에 넣습니다.");
            EdAddress.Focus();
            return null;
        }
        if (!int.TryParse(EdPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            EditorError.Text = L.T("포트는 1 ~ 65535 사이 숫자여야 합니다.");
            EdPort.Focus();
            return null;
        }
        EditorError.Text = "";

        var h = _editing ?? _template?.Clone() ?? new HostEntry();
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
        h.RdpGateway = !ssh && EdGateway.Text.Trim().Length > 0 ? EdGateway.Text.Trim() : null;

        if (_editing == null) _hosts.Add(h);
        _template = null;
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

    private void OnEditorClose(object sender, RoutedEventArgs e) => TryCloseEditor();
    private void OnEditorDimClick(object sender, MouseButtonEventArgs e) => TryCloseEditor();

    /// <summary>그룹 칸 옆 ▾: 이미 쓰는 그룹 이름 고르기 (오타로 그룹이 갈라지지 않게)</summary>
    private void OnPickGroup(object sender, RoutedEventArgs e)
    {
        var names = KnownGroupNames();
        var menu = new ContextMenu { PlacementTarget = EdGroup, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (names.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("아직 만든 그룹이 없습니다"), IsEnabled = false });
        foreach (var n in names)
        {
            var item = new MenuItem { Header = n };
            item.Click += (_, _) => EdGroup.Text = n;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
