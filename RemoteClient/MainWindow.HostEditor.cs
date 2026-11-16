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
        // 이미 열린 편집 패널에 저장하지 않은 변경이 있으면 조용히 버리지 않는다 (탭 메뉴 "호스트 편집" 등)
        if (_editorOpen && EditorSnapshot() != _editorSnapshot)
        {
            // 같은 호스트를 편집 중이면 지금 패널을 그대로 둔다
            if (h != null && ReferenceEquals(h, _editing)) { NavHosts.IsChecked = true; return; }
            if (Dialog.Show(this, L.T("저장하지 않은 변경 내용이 있습니다. 버리고 닫을까요?"), L.T("호스트 편집"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                NavHosts.IsChecked = true;
                return;
            }
        }

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
        EdNoNla.IsChecked = h?.RdpDisableNla == true;
        EdTags.Text = HostTags.ToText(h?.Tags);
        _edVaultId = h?.VaultId;
        _edClearAnswers = false;
        UpdateRememberedUi(h);
        _edJumpId = h?.JumpHostId;
        UpdateJumpUi();
        UpdateAuthUi();
        _editorValues = EditorValues();
        _editorSnapshot = EditorSnapshot();

        NavHosts.IsChecked = true;
        _editorOpen = true;
        EditorLayer.IsHitTestVisible = true;
        EditorLayer.Visibility = Visibility.Visible;
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(EditorSlideOut, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Dispatcher.BeginInvoke(() => EdAddress.Focus(), DispatcherPriority.Input);
    }

    private HostEntry? _template;
    // 편집 패널이 화면 밖으로 빠지는 거리: 패널 폭 390 + 왼쪽으로 번지는 그림자(Shadow16) 자리
    private const double EditorSlideOut = 430;
    // 편집 패널이 열려 있는지 — 닫기 애니메이션(140ms) 동안은 EditorLayer 가 아직 Visible 이지만 false.
    // 그 사이 저장 두 번 클릭·Ctrl+S 반복이 다시 저장하면 비운 비밀번호 칸이 "바뀜" 으로 잡혀 저장된 비밀번호를 지우거나
    // 새 호스트를 두 번 추가했다
    private bool _editorOpen;
    private string _editorSnapshot = "";
    // 편집 패널을 열 때의 칸별 값 — 저장할 때 사용자가 바꾼 칸만 호스트에 반영한다
    // (열어 둔 사이 클라우드 동기화·Claude update_host 가 바꾼 값을 그대로 덮어쓰지 않게)
    private string[] _editorValues = Array.Empty<string>();

    // EditorValues 의 칸 순서
    private const int FProto = 0, FAddress = 1, FPort = 2, FPublicIp = 3, FUsePublic = 4, FLabel = 5, FGroup = 6, FUser = 7,
                      FPassword = 8, FKey = 9, FGateway = 10, FVault = 11, FClearAnswers = 12, FJump = 13, FTags = 14, FNoNla = 15;

    /// <summary>편집 칸 값 (칸별)</summary>
    private string[] EditorValues() => new[]
    {
        (ProtoRdp.IsChecked == true).ToString(), EdAddress.Text, EdPort.Text, EdPublicIp.Text, (EdUsePublic.IsChecked == true).ToString(),
        EdLabel.Text, EdGroup.Text, EdUser.Text, EdPassword.Password, EdKey.Text, EdGateway.Text,
        _edVaultId?.ToString() ?? "", _edClearAnswers.ToString(), _edJumpId?.ToString() ?? "", EdTags.Text, (EdNoNla.IsChecked == true).ToString(),
    };

    /// <summary>편집 칸 값 전체 (닫을 때 바뀐 게 있는지 비교)</summary>
    private string EditorSnapshot() => string.Join("\u0001", EditorValues());

    /// <summary>열 때와 비교해 바뀐 칸 (스냅숏이 없으면 전부 바뀐 것으로)</summary>
    internal static bool[] ChangedFields(string[] before, string[] now) =>
        now.Select((v, i) => before.Length != now.Length || before[i] != v).ToArray();

    /// <summary>저장하지 않은 변경이 있으면 물어보고 닫는다 (Esc · 바깥 클릭 · X)</summary>
    private void TryCloseEditor()
    {
        if (!_editorOpen) return;
        if (EditorSnapshot() != _editorSnapshot &&
            Dialog.Show(this, L.T("저장하지 않은 변경 내용이 있습니다. 버리고 닫을까요?"), L.T("호스트 편집"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CloseEditor();
    }

    private void CloseEditor()
    {
        // 닫는 즉시 "닫힘" 으로 — 애니메이션 동안 저장·닫기·삭제가 다시 실행되지 않게, 클릭도 받지 않게
        _editorOpen = false;
        EditorLayer.IsHitTestVisible = false;
        var anim = new DoubleAnimation(0, EditorSlideOut, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        // 애니메이션 도중 다시 열었으면 숨기지 않는다
        anim.Completed += (_, _) => { if (!_editorOpen) EditorLayer.Visibility = Visibility.Collapsed; };
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        EdPassword.Clear();
        // 평문 비밀번호가 담긴 비교용 값도 비운다 (다시 열 때 OpenEditor 가 새로 만든다)
        _editorValues = Array.Empty<string>();
        _editorSnapshot = "";
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

    /// <summary>
    /// NLA 끄기 안내: 켰으면 무엇이 달라지는지, 꺼져 있는데 사용자명이 AzureAD\ 형식이면 꺼야 할 수 있다고,
    /// Default.rdp 에서 이미 껐으면 그 사실을
    /// </summary>
    private void UpdateNlaHint(RdpDefaults? d = null)
    {
        if (NlaHint == null || EdNoNla == null || EdUser == null) return;   // InitializeComponent 중
        d ??= RdpDefaults.Load();
        var entra = RdpDefaults.IsEntraUser(EdUser.Text) || RdpDefaults.IsEntraUser(VaultStore.Get(_edVaultId)?.Username);
        var warn = entra && EdNoNla.IsChecked != true && d.CredSsp;
        NlaHint.Text = EdNoNla.IsChecked == true
            ? L.T("접속 전 인증 없이 원격 PC 의 로그인 화면까지 연결되고, 계정은 그 화면에서 확인됩니다. 이 설정이 필요한 PC 에만 켜세요.")
            : !d.CredSsp
                ? L.T("원격 데스크톱 연결(mstsc) 기본 설정(Default.rdp)에서 NLA 를 꺼 두어 모든 RDP 호스트에 NLA 를 쓰지 않습니다.")
                : warn
                    ? L.T("사용자명이 AzureAD\\ 형식입니다. 이 PC 가 같은 Entra 테넌트에 가입돼 있지 않으면 NLA 를 꺼야 접속됩니다.")
                    : L.T("Microsoft Entra ID(Azure AD)에 가입된 PC 에 AzureAD\\사용자@도메인 형식으로 접속할 때만 켭니다.");
        NlaHint.Foreground = (System.Windows.Media.Brush)FindResource(warn ? "Warning" : "SubText");
    }

    private void OnEdNlaChanged(object sender, RoutedEventArgs e) => UpdateNlaHint();

    private void OnEdUserChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ProtoRdp?.IsChecked == true) UpdateNlaHint();
    }

    private void UpdateProtoUi()
    {
        KeyPanel.Visibility = ProtoSsh.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        JumpPanel.Visibility = ProtoSsh.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GatewayPanel.Visibility = ProtoRdp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (ProtoRdp.IsChecked == true)
        {
            var d = RdpDefaults.Load();
            GatewayHint.Text = !string.IsNullOrWhiteSpace(d.GatewayHost) && d.GatewayUsage != 0
                ? L.F("비워 두면 원격 데스크톱 연결(mstsc) 기본 설정의 게이트웨이 '{0}' 을 씁니다. 게이트웨이에도 같은 계정으로 로그인합니다.", d.GatewayHost)
                : L.T("회사 밖에서 RD 게이트웨이를 거쳐야 하는 서버만 적으세요. 게이트웨이에도 같은 계정으로 로그인합니다.");
            UpdateNlaHint(d);
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
        if (!ssh) UpdateNlaHint();   // Vault 계정의 사용자명이 AzureAD\ 형식인지도 본다
    }

    /// <summary>Vault 계정을 고르지 않았을 때 실제로 쓰일 기본 계정 안내 (프로필 기본 계정 · 설정의 기본 Vault)</summary>
    private string DefaultVaultHint(bool ssh)
    {
        var probe = new HostEntry
        {
            Protocol = ssh ? Protocol.Ssh : Protocol.Rdp,
            // ResolveVault 는 비밀번호가 있는지만 본다 — Password 에 넣으면 입력 글자마다 DPAPI 로 암호화했다
            ProtectedPassword = EdPassword?.Password.Length > 0 ? "set" : null,
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

    /// <summary>비밀번호·키를 적거나 지우면 어떤 기본 계정이 쓰일지(또는 안 쓰일지) 안내를 다시 계산</summary>
    private void OnEdCredentialChanged(object sender, RoutedEventArgs e)
    {
        if (_edVaultId == null) UpdateAuthUi();
    }

    // 편집 중인 점프 서버 (ProxyJump)
    private Guid? _edJumpId;

    private void UpdateJumpUi()
    {
        var j = _edJumpId is { } id ? _hosts.FirstOrDefault(x => x.Id == id) : null;
        EdJumpButton.Content = _edJumpId == null ? L.T("없음 (바로 연결)")
            : j == null ? L.T("삭제된 호스트 — 다른 호스트를 고르거나 없음으로")
            : $"{j.DisplayName}  ({j.Address})";
        // 이 호스트를 점프 서버로 쓰는 호스트가 있으면 — 점프는 한 단계만이라 그 호스트들은 여기서 고른 점프 서버를 거치지 않는다
        var users = _edJumpId == null ? 0 : JumpUsers(_hosts, _editing);
        JumpWarning.Text = users > 0
            ? L.F("이 호스트를 점프 서버로 쓰는 호스트가 {0}개 있습니다. 점프 서버는 한 단계만 지원해, 그 호스트들은 여기서 고른 점프 서버를 거치지 않고 이 호스트에 바로 연결합니다.", users)
            : "";
        JumpWarning.Visibility = users > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>editing 을 점프 서버로 쓰는 (자기 자신이 아닌) 호스트 수. 새 호스트·복제는 0</summary>
    internal static int JumpUsers(IEnumerable<HostEntry> all, HostEntry? editing) =>
        editing == null ? 0 : all.Count(x => x.JumpHostId == editing.Id && !ReferenceEquals(x, editing));

    /// <summary>점프 서버로 쓸 SSH 호스트 고르기 (자기 자신 제외)</summary>
    private void OnPickJump(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = EdJumpButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var none = new MenuItem { Header = L.T("없음 (바로 연결)") };
        none.Click += (_, _) => { _edJumpId = null; UpdateJumpUi(); };
        menu.Items.Add(none);
        menu.Items.Add(new Separator());
        foreach (var h in _hosts.Where(x => x.Protocol == Protocol.Ssh && !ReferenceEquals(x, _editing))
                                .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            // 점프 서버는 한 단계만 지원 (AttachJump) — 자기도 점프 서버를 거치는 호스트는 점프 서버로 쓰면 그 점프 없이 직접 붙어
            // 연결되지 않으므로 고르지 못하게 한다 (이미 지정돼 있으면 그대로 보여 준다)
            var nested = h.JumpHostId != null && h.Id != _edJumpId;
            var item = new MenuItem
            {
                Header = MenuText($"{h.DisplayName}  ({h.Address})" + (h.Group.Length > 0 ? $"  · {h.Group}" : "")),
                IsChecked = h.Id == _edJumpId,
                IsEnabled = !nested,
            };
            if (nested)
            {
                item.ToolTip = L.T("이 호스트도 점프 서버를 거쳐 접속합니다. 점프 서버는 한 단계만 지원합니다.");
                ToolTipService.SetShowOnDisabled(item, true);
            }
            var id = h.Id;
            item.Click += (_, _) => { _edJumpId = id; UpdateJumpUi(); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 2) menu.Items.Add(new MenuItem { Header = L.T("점프 서버로 쓸 SSH 호스트가 없습니다"), IsEnabled = false });
        menu.IsOpen = true;
    }

    /// <summary>메뉴 항목 글자 그대로 (MenuItem 은 '_' 를 단축키 표시로 먹어 web_01 이 web01 로 보였다)</summary>
    internal static string MenuText(string s) => s.Replace("_", "__");

    // 저장할 때 기억한 인증 답을 지울지 (지우기 버튼)
    private bool _edClearAnswers;

    /// <summary>기억한 인증 답 안내 ("Choose [1] Push, or [2] TOTP: → 1" 등). 없거나 지우기를 눌렀으면 숨긴다</summary>
    private void UpdateRememberedUi(HostEntry? h)
    {
        var answers = _edClearAnswers ? null : h?.PromptAnswers;
        RememberedPanel.Visibility = answers is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (answers is not { Count: > 0 }) return;
        var sample = answers.First();
        RememberedText.Text = L.F("서버 인증 질문에 기억한 답 {0}개 — 예: {1} → {2}", answers.Count, sample.Key.TrimEnd(':', ' '),
            sample.Value.Length == 0 ? L.T("(빈 값)") : sample.Value);
    }

    private void OnClearRemembered(object sender, RoutedEventArgs e)
    {
        _edClearAnswers = true;
        UpdateRememberedUi(_editing);
    }

    private void OnPickVault(object sender, RoutedEventArgs e)
    {
        var proto = ProtoRdp.IsChecked == true ? Protocol.Rdp : Protocol.Ssh;
        if (VaultPickerDialog.Pick(this, _edVaultId, proto, allowClear: true) is not { } id) return;
        // "지정 해제" = 해제 버튼과 같다 (Vault 계정 없이 직접 입력한 값 · 기본 계정)
        _edVaultId = id == VaultPickerDialog.Cleared ? null : id;
        UpdateAuthUi();
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
        if (!_editorOpen) return null;   // 닫는 중 (저장 두 번 클릭 · Ctrl+S 반복)
        var address = EdAddress.Text.Trim();
        if (address.Length == 0) { EditorError.Text = L.T("주소를 입력하세요."); EdAddress.Focus(); return null; }
        if (address.Contains(' ') || address.Contains('@'))
        {
            EditorError.Text = L.T("주소에는 IP 또는 호스트명만 넣으세요. 사용자명은 아래 '사용자명' 칸에 넣습니다.");
            EdAddress.Focus();
            return null;
        }
        var publicIp = EdPublicIp.Text.Trim();
        if (publicIp.Contains(' ') || publicIp.Contains('@'))
        {
            // 주소 칸과 같은 검사 — 걸러 내지 않으면 "공인 IP로 접속" 이 "user@1.2.3.4" 같은 주소로 연결을 시도했다
            EditorError.Text = L.T("공인 IP 에는 IP 또는 호스트명만 넣으세요.");
            EdPublicIp.Focus();
            return null;
        }
        if (!int.TryParse(EdPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            EditorError.Text = L.T("포트는 1 ~ 65535 사이 숫자여야 합니다.");
            EdPort.Focus();
            return null;
        }
        // 태그: 한 줄에 키=값 — 잘못된 줄이 있으면 저장하지 않고 알린다 ("키=" 처럼 값이 빈 줄은 태그를 만들지 않음)
        Dictionary<string, string>? tags;
        try { tags = HostTags.Apply(null, HostTags.ParseText(EdTags.Text), replace: true, new List<HostTags.Change>()); }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            EditorError.Text = ex.Message;
            EdTags.Focus();
            return null;
        }
        EditorError.Text = "";
        // 지워진 Vault 계정을 가리키고 있으면 저장 전에 고르게 한다 — 원본 호스트를 바꾸기 전에 (취소하면 아무것도 바뀌지 않게)
        if (_edVaultId != null && VaultStore.Get(_edVaultId) == null) { OnPickVault(this, new RoutedEventArgs()); return null; }

        // 편집하던 호스트가 그사이 목록에서 지워졌으면(클라우드 동기화·Claude delete_host 등) 조용히 버리지 않고 묻는다
        var editing = _editing;
        var readd = false;
        if (editing != null && !_hosts.Contains(editing))
        {
            if (Dialog.Show(this, L.F("편집하던 호스트 '{0}' 이(가) 그사이 목록에서 삭제되었습니다 (클라우드 동기화 등).\n편집한 내용으로 새 호스트를 추가할까요?", editing.DisplayName),
                    L.T("호스트 편집"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return null;
            readd = true;
        }

        var h = editing ?? _template?.Clone() ?? new HostEntry();
        // 기존 호스트는 사용자가 바꾼 칸만 반영 (새 호스트·복제·다시 추가는 전부)
        var changed = ChangedFields(editing == null || readd ? Array.Empty<string>() : _editorValues, EditorValues());
        var ssh = ProtoSsh.IsChecked == true;

        if (changed[FProto]) h.Protocol = ssh ? Protocol.Ssh : Protocol.Rdp;
        if (changed[FAddress])
        {
            h.Address = address;
            if (h.PrivateIp != null) h.PrivateIp = address;
            h.SshBannerOs = null;   // 다른 서버일 수 있어 다음 접속 때 다시 판별
        }
        if (changed[FPublicIp] || changed[FUsePublic])
        {
            h.PublicIp = publicIp.Length > 0 ? publicIp : null;
            h.UsePublicIp = EdUsePublic.IsChecked == true && h.PublicIp != null;
        }
        if (changed[FPort]) h.Port = port;
        if (changed[FLabel]) h.Label = EdLabel.Text.Trim();
        if (changed[FGroup]) h.Group = EdGroup.Text.Trim();
        if (changed[FTags]) h.Tags = tags;

        if (changed[FVault]) h.VaultId = _edVaultId;
        if (changed[FUser]) h.Username = EdUser.Text.Trim();
        // 평문이 같으면 다시 암호화하지 않는다 (암호문이 바뀌면 열린 탭이 "계정이 바뀜" 으로 보고 세션에서 입력한 계정을 버렸다)
        if (changed[FPassword] && h.Password != EdPassword.Password) h.Password = EdPassword.Password;
        if (changed[FKey] || changed[FProto]) h.KeyPath = ssh && EdKey.Text.Trim().Length > 0 ? EdKey.Text.Trim() : null;
        if (changed[FNoNla] || changed[FProto]) h.RdpDisableNla = !ssh && EdNoNla.IsChecked == true;
        if (changed[FGateway] || changed[FProto]) h.RdpGateway = !ssh && EdGateway.Text.Trim().Length > 0 ? EdGateway.Text.Trim() : null;
        if (_edClearAnswers) h.PromptAnswers = null;
        if (changed[FJump] || changed[FProto]) h.JumpHostId = ssh ? _edJumpId : null;

        if (readd)
        {
            // 클라우드 호스트였으면 직접 관리하는 호스트로 (복제와 같게 — 같은 인스턴스 ID 를 가진 유령 사본 방지)
            if (h.IsCloud) DetachFromCloud(h);
            UpdateProfileCounts();
        }
        if (editing == null || readd) _hosts.Add(h);
        _template = null;
        SaveHosts();
        RefreshHosts();
        CloseEditor();
        return h;
    }

    private void OnEditorSave(object sender, RoutedEventArgs e) => SaveEditor();

    private void OnEditorSaveConnect(object sender, RoutedEventArgs e)
    {
        if (!_editorOpen) return;
        if (SaveEditor() is { } h) Connect(h);
    }

    private void OnEditorDelete(object sender, RoutedEventArgs e)
    {
        if (!_editorOpen || _editing == null) return;
        // 그사이 목록에서 이미 지워졌으면(클라우드 동기화·Claude delete_host 등) 지울 것이 없다 — 예전에는 삭제를 확인해도
        // 아무것도 바뀌지 않아 패널이 닫히지 않았다
        if (!_hosts.Contains(_editing)) { CloseEditor(); FlashHostStatus(L.T("이미 목록에서 삭제된 호스트입니다"), ToastKind.Info); return; }
        DeleteHost(_editing);
        if (!_hosts.Contains(_editing)) CloseEditor();
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
            var item = new MenuItem { Header = MenuText(n) };
            item.Click += (_, _) => EdGroup.Text = n;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
