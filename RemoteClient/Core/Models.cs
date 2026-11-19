using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Nubis;

public enum Protocol { Ssh, Rdp, Local }

public sealed class HostEntry : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public string Group { get; set; } = "";
    public Protocol Protocol { get; set; } = Protocol.Ssh;
    public string Address { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    /// <summary>DPAPI(CurrentUser)로 암호화한 비밀번호(Base64). 평문은 저장하지 않는다.</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>SSH 개인키 파일 경로</summary>
    public string? KeyPath { get; set; }
    /// <summary>Vault 계정 (사용자명·비밀번호·SSH 키). 고르면 이 계정이 호스트에 직접 적은 값보다 우선 (적은 값은 숨김, 해제하면 다시 사용)</summary>
    public Guid? VaultId { get; set; }
    /// <summary>RDP: RD 게이트웨이 ("host" 또는 "host:port"). 비우면 mstsc 의 Default.rdp 게이트웨이 설정을 따른다</summary>
    public string? RdpGateway { get; set; }
    /// <summary>
    /// RDP: NLA(CredSSP)를 쓰지 않는다 (.rdp 의 enablecredsspsupport:i:0). Microsoft Entra ID(Azure AD)에 가입된 PC 에
    /// 같은 테넌트에 가입하지 않은 PC 에서 접속할 때 필요하다 (사용자명 AzureAD\사용자@도메인). 접속 전 인증 없이
    /// 원격 PC 의 로그인 화면까지 연결되므로 필요한 호스트에만 켠다
    /// </summary>
    public bool RdpDisableNla { get; set; }
    /// <summary>
    /// SSH 인증 질문에 기억해 둔 답 (질문 → 답). "Choose [1] Push, or [2] TOTP" 같은 고정 선택만 —
    /// 가려진 질문은 빈 답만 저장한다 (KbdAuth.CanUseRemembered). hosts.json 은 파일 전체가 DPAPI 암호화
    /// </summary>
    public Dictionary<string, string>? PromptAnswers { get; set; }
    /// <summary>SSH: 이 호스트를 거쳐 접속 (ProxyJump). 목록의 다른 SSH 호스트 Id</summary>
    public Guid? JumpHostId { get; set; }
    /// <summary>접속용 사본에만: 점프 서버의 접속용 사본 (계정·주소가 채워진 것). 저장하지 않는다</summary>
    [JsonIgnore] public HostEntry? Jump { get; set; }
    /// <summary>점프 서버로 지정한 호스트가 목록에 없음 (지워짐) — 이 표시가 있는 Jump 로는 연결하지 않는다 (점프 없이 직접 연결 방지)</summary>
    [JsonIgnore] public bool IsMissingJump { get; set; }
    // 예전 형식(암호/키 따로 선택) 읽기 전용 — HostStore.Load 에서 VaultId 로 옮긴다
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? VaultPasswordId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? VaultKeyId { get; set; }
    public DateTime? LastConnected { get; set; }
    /// <summary>즐겨찾기 (호스트 목록 맨 위 "즐겨찾기" 그룹에도 표시)</summary>
    public bool Favorite { get; set; }

    // ----- 접속용 사본에만 채워지는 값 (VaultStore.Apply). 저장하지 않는다 -----
    /// <summary>Vault 에서 꺼낸 개인키 내용</summary>
    [JsonIgnore] public string? KeyMaterial { get; set; }
    /// <summary>Vault 에서 꺼낸 키 암호 (없으면 비밀번호를 키 암호로 시도)</summary>
    [JsonIgnore] public string? KeyPassphrase { get; set; }
    /// <summary>키 인증 수단이 있는지 (파일 또는 Vault 키)</summary>
    [JsonIgnore] public bool HasKey => !string.IsNullOrWhiteSpace(KeyPath) || !string.IsNullOrEmpty(KeyMaterial);
    [JsonIgnore] public bool UsesVault => VaultId != null;

    /// <summary>예전 형식(VaultPasswordId/VaultKeyId)을 VaultId 로. 바뀌었으면 true</summary>
    public bool MigrateLegacyVault()
    {
        if (VaultPasswordId == null && VaultKeyId == null) return false;
        VaultId ??= VaultPasswordId ?? VaultKeyId;
        VaultPasswordId = null;
        VaultKeyId = null;
        return true;
    }

    // ----- 클라우드 동기화로 등록된 호스트 -----
    /// <summary>소속 클라우드 프로필 (null 이면 직접 등록한 호스트)</summary>
    public Guid? CloudProfileId { get; set; }
    public string? CloudInstanceId { get; set; }
    public string? CloudState { get; set; }
    public string? CloudRegion { get; set; }
    /// <summary>동기화로 받은 클라우드 이름. 사용자가 Label 을 이 값과 다르게 바꿨으면 동기화가 덮어쓰지 않는다</summary>
    public string? CloudName { get; set; }
    public string? Purpose { get; set; }
    public string? Os { get; set; }
    /// <summary>
    /// 사용자 태그 (키=값 자유 형식, 예: 용도=웹, env=prod). 클라우드 동기화는 건드리지 않는다.
    /// 키는 대소문자를 구분하지 않는다 (HostTags.Apply 가 같은 키를 하나로 다룸)
    /// </summary>
    public Dictionary<string, string>? Tags { get; set; }
    /// <summary>태그를 "키=값, ..." 한 줄로 (목록·검색용). 없으면 빈 문자열</summary>
    [JsonIgnore] public string TagsText => Tags is { Count: > 0 } t ? string.Join(", ", t.Select(kv => $"{kv.Key}={kv.Value}")) : "";
    /// <summary>SSH 접속 때 서버 배너로 판별한 OS ("windows" / "unix", SshServerOs). 주소를 바꾸면 지운다 (다른 서버일 수 있음)</summary>
    public string? SshBannerOs { get; set; }

    // ----- 주소 (사설 / 공인) -----
    /// <summary>사설 IP (클라우드 동기화). Address 는 기본 접속 주소로 보통 사설 IP 와 같다</summary>
    public string? PrivateIp { get; set; }
    /// <summary>공인 IP (EIP 등). 클라우드 동기화로 채워지고, 직접 등록한 호스트는 사용자가 입력</summary>
    public string? PublicIp { get; set; }
    /// <summary>접속할 때 공인 IP 사용 (공인 IP 가 있을 때만)</summary>
    public bool UsePublicIp { get; set; }

    [JsonIgnore] public bool HasPublicIp => !string.IsNullOrWhiteSpace(PublicIp);
    /// <summary>사설(기본) 주소</summary>
    [JsonIgnore] public string PrimaryAddress => !string.IsNullOrWhiteSpace(PrivateIp) ? PrivateIp : Address;
    /// <summary>실제로 접속할 주소 (공인 IP 사용 설정이면 공인 IP)</summary>
    [JsonIgnore] public string EffectiveAddress => UsePublicIp && HasPublicIp ? PublicIp! : PrimaryAddress;

    /// <summary>명령 실행 시도 순서: 사설 → 공인 (중복 제거)</summary>
    public List<string> AddressCandidates()
    {
        var list = new List<string>();
        foreach (var a in new[] { PrimaryAddress, PublicIp })
            if (!string.IsNullOrWhiteSpace(a) && !list.Contains(a, StringComparer.OrdinalIgnoreCase)) list.Add(a);
        return list;
    }

    [JsonIgnore] public bool IsCloud => CloudProfileId != null;
    [JsonIgnore]
    public bool IsStopped => IsCloud && !string.IsNullOrEmpty(CloudState) &&
                             !CloudState.Equals("running", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public double CardOpacity => IsStopped ? 0.45 : 1.0;

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Address : Label;
    [JsonIgnore] public string ProtocolText => Protocol switch { Protocol.Ssh => "SSH", Protocol.Rdp => "RDP", _ => "Local" };
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { Protocol switch { Protocol.Ssh => "ssh", Protocol.Rdp => "rdp", _ => "local" } };
            if (IsCloud)
            {
                parts.Add(UsePublicIp && HasPublicIp ? L.F("{0} (공인)", PublicIp) : PrimaryAddress + (HasPublicIp ? " · EIP" : ""));
                if (!string.IsNullOrWhiteSpace(Purpose)) parts.Add(Purpose);
                if (IsStopped) parts.Add(CloudState!.ToLowerInvariant());
                AddTagValues(parts);
                return string.Join(" · ", parts);
            }
            // 실제로 로그인하는 사용자명 — 직접 고른 Vault 계정에 사용자명이 있으면 그 값 (호스트에 숨겨진 값이 아니라)
            var user = VaultStore.Get(VaultId) is { } v && !string.IsNullOrWhiteSpace(v.Username) ? v.Username : Username;
            if (!string.IsNullOrWhiteSpace(user)) parts.Add(user);
            var addr = UsePublicIp && HasPublicIp ? L.F("{0} (공인)", EffectiveAddress) : EffectiveAddress;
            if (!string.IsNullOrWhiteSpace(Label)) parts.Add(Port == DefaultPort(Protocol) ? addr : $"{addr}:{Port}");
            var text = string.Join(", ", parts);
            var tags = new List<string>();
            AddTagValues(tags);
            return tags.Count > 0 ? text + " · " + string.Join(" · ", tags) : text;
        }
    }

    /// <summary>카드 부제목에는 태그 값만 (키까지 넣으면 길어짐 — 키=값은 툴팁·list_hosts 에)</summary>
    private void AddTagValues(List<string> parts)
    {
        if (Tags == null) return;
        foreach (var v in Tags.Values)
            if (!string.IsNullOrWhiteSpace(v)) parts.Add(v);
    }

    /// <summary>클라우드 프로필 찾기 (툴팁의 계정 출처 표시용, MainWindow 가 연결)</summary>
    public static Func<Guid?, CloudProfile?>? ProfileResolver { get; set; }

    [JsonIgnore]
    public string ToolTipText =>
        $"{DisplayName}\n" + L.F("사설 {0}", PrimaryAddress) + (HasPublicIp ? "   " + L.F("공인 {0}", PublicIp) : "") + $"   :{Port} ({ProtocolText})\n" +
        L.F("접속 주소: {0}", UsePublicIp && HasPublicIp ? L.F("{0} (공인)", EffectiveAddress) : EffectiveAddress) + "\n" +
        (Protocol != Protocol.Local && CredentialSourceText(ProfileResolver?.Invoke(CloudProfileId)) is { } src ? L.F("로그인 계정: {0}", src) + "\n" : "") +
        (IsCloud ? $"{CloudRegion}  ·  {CloudState}\n{Os}\n{CloudInstanceId}\n" : "") +
        (TagsText.Length > 0 ? L.F("태그: {0}", TagsText) + "\n" : "") +
        "\n" + (HasPublicIp ? L.T("더블클릭하여 연결 · 우클릭으로 사설/공인 선택") : L.T("더블클릭하여 연결"));

    /// <summary>
    /// 접속용 사본: 비어 있는 계정은 프로필 기본 계정으로 채우고, Address 를 실제 접속 주소로 바꾼다.
    /// usePublic 을 주면 호스트 설정 대신 그 값으로 (우클릭 "공인/사설 IP 로 연결").
    /// </summary>
    public HostEntry ForConnection(CloudProfile? p, bool? usePublic = null)
    {
        var c = WithDefaults(p);
        var addr = (usePublic ?? UsePublicIp) && HasPublicIp ? PublicIp! : PrimaryAddress;
        if (addr == c.Address) return c;
        if (ReferenceEquals(c, this)) c = Clone();
        c.Address = addr;
        return c;
    }

    /// <summary>
    /// 이 호스트가 실제로 쓰는 Vault 계정. 우선순위:
    /// 1) 호스트에 직접 고른 Vault 계정
    /// 2) 호스트에 비밀번호·키 파일을 직접 적었으면 Vault 를 쓰지 않음
    /// 3) 클라우드 프로필의 기본 Vault 계정 (SSH / RDP 따로)
    /// 4) 프로필에 기본 사용자명·비밀번호를 적었으면 그 값 (Vault 안 씀)
    /// 5) 설정의 기본 Vault 계정 (SSH / RDP 따로)
    /// 지워진 Vault 계정은 건너뛴다.
    /// </summary>
    public Guid? EffectiveVaultId(CloudProfile? p) => ResolveVault(p).id;

    /// <summary>EffectiveVaultId 와 그 출처 ("host" · "profile" · "default" · null)</summary>
    public (Guid? id, string? source) ResolveVault(CloudProfile? p)
    {
        if (VaultId != null && VaultStore.Get(VaultId) != null) return (VaultId, "host");
        if (Protocol == Protocol.Local) return (null, null);
        if (!string.IsNullOrEmpty(ProtectedPassword) || !string.IsNullOrWhiteSpace(KeyPath)) return (null, null);
        var fromProfile = Protocol == Protocol.Rdp ? p?.RdpVaultId : p?.SshVaultId;
        if (fromProfile != null && VaultStore.Get(fromProfile) != null) return (fromProfile, "profile");
        if (p != null && (!string.IsNullOrEmpty(p.DefaultUsername) || !string.IsNullOrEmpty(p.ProtectedDefaultPassword))) return (null, null);
        var s = SettingsStore.Current;
        var global = Protocol == Protocol.Rdp ? s.DefaultRdpVaultId : s.DefaultSshVaultId;
        return global != null && VaultStore.Get(global) != null ? (global, "default") : (null, null);
    }

    /// <summary>
    /// 접속용 계정을 채운 사본. 호스트에 직접 고른 Vault 계정이면 그 계정이 우선(호스트에 따로 적힌 값은 숨겨진 채 쓰지 않음),
    /// 기본 계정(프로필·설정)이면 호스트에 비어 있는 값만 채운다. 클라우드 호스트는 그래도 비어 있는 계정을 프로필 기본 계정으로 채운다.
    /// </summary>
    public HostEntry WithDefaults(CloudProfile? p)
    {
        var (vid, source) = ResolveVault(p);
        var preferVault = vid != null && source == "host";
        var c = vid != null ? VaultStore.Apply(Clone(), vid, preferVault) : this;
        if (p == null || (!string.IsNullOrEmpty(c.Username) && !string.IsNullOrEmpty(c.ProtectedPassword))) return c;
        if (ReferenceEquals(c, this)) c = Clone();
        if (string.IsNullOrEmpty(c.Username)) c.Username = p.DefaultUsername;
        // 직접 고른 Vault 계정에 비밀번호가 없으면(키 전용 등) 프로필 기본 비밀번호를 몰래 채우지 않는다 — 편집 화면 안내("접속할 때 묻습니다")와 같게
        if (!preferVault && string.IsNullOrEmpty(c.ProtectedPassword)) c.ProtectedPassword = p.ProtectedDefaultPassword;
        return c;
    }
    [JsonIgnore] public string IconGlyph => Protocol == Protocol.Ssh ? "" : "";
    private static readonly Brush SshBrush = Frozen(Color.FromRgb(0x20, 0xC5, 0xA0));
    private static readonly Brush RdpBrushStatic = Frozen(Color.FromRgb(0x4F, 0x8C, 0xFF));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    [JsonIgnore] public Brush IconBrush => Protocol == Protocol.Ssh ? SshBrush : RdpBrushStatic;

    [JsonIgnore]
    public string Password
    {
        get => Secret.Unprotect(ProtectedPassword);
        set => ProtectedPassword = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    public static int DefaultPort(Protocol p) => p == Protocol.Ssh ? 22 : 3389;

    /// <summary>새로 만드는 호스트·프로필에 넣을 포트 (설정의 기본 포트, 잘못된 값이면 표준 포트)</summary>
    public static int PreferredPort(Protocol p)
    {
        var s = SettingsStore.Current;
        var port = p == Protocol.Ssh ? s.DefaultSshPort : s.DefaultRdpPort;
        return port is >= 1 and <= 65535 ? port : DefaultPort(p);
    }

    public HostEntry Clone()
    {
        var c = (HostEntry)MemberwiseClone();
        c.PropertyChanged = null;
        c._highlighted = false;
        c._selected = false;
        // 얕은 복사면 복제본에서 기억한 답이 원본에도 들어간다
        c.PromptAnswers = PromptAnswers == null ? null : new Dictionary<string, string>(PromptAnswers);
        c.Tags = Tags == null ? null : new Dictionary<string, string>(Tags, StringComparer.OrdinalIgnoreCase);
        return c;
    }

    /// <summary>
    /// 열려 있는 탭의 접속용 사본에 저장된 호스트·Vault 의 최신 계정을 반영한다 (탭은 처음 열 때의 사본을 들고 있어,
    /// 그 뒤에 Vault 비밀번호를 고쳐도 다시 연결할 때 예전 값을 쓰던 문제). 계정이 바뀌었으면 true.
    /// </summary>
    public bool RefreshCredentialsFrom(HostEntry fresh)
    {
        // 점프 서버(바뀐 점프 서버 계정·주소 포함)는 계정 변경 여부와 상관없이 늘 최신으로
        Jump = fresh.Jump;
        JumpHostId = fresh.JumpHostId;
        // 비밀번호는 풀어서 비교한다 — DPAPI 암호문은 같은 비밀번호도 암호화할 때마다 달라 "바뀜" 으로 잘못 판단했다
        var changed = Username != fresh.Username || !SamePassword(ProtectedPassword, fresh.ProtectedPassword) || KeyPath != fresh.KeyPath ||
                      KeyMaterial != fresh.KeyMaterial || KeyPassphrase != fresh.KeyPassphrase || VaultId != fresh.VaultId;
        if (!changed) return false;
        (Username, ProtectedPassword, KeyPath, KeyMaterial, KeyPassphrase, VaultId) =
            (fresh.Username, fresh.ProtectedPassword, fresh.KeyPath, fresh.KeyMaterial, fresh.KeyPassphrase, fresh.VaultId);
        return true;
    }

    /// <summary>
    /// 두 DPAPI 암호문이 같은 비밀번호인지 (암호문이 같으면 풀지 않는다).
    /// 어느 한쪽이라도 풀리지 않으면(다른 PC 데이터 등) 둘 다 "" 가 되어 같다고 잘못 보지 않게 암호문 문자열로 비교한다.
    /// </summary>
    internal static bool SamePassword(string? protectedA, string? protectedB)
    {
        if ((protectedA ?? "") == (protectedB ?? "")) return true;
        if (!Secret.TryUnprotect(protectedA, out var a) || !Secret.TryUnprotect(protectedB, out var b)) return false;   // 암호문이 다르고 풀 수 없음 → 다름
        return a == b;
    }

    /// <summary>접속할 때 쓰는 계정이 어디서 왔는지 (터미널 첫 줄·카드 툴팁 표시용). 사용자명이 없으면 null</summary>
    public string? CredentialSourceText(CloudProfile? p)
    {
        var (id, source) = ResolveVault(p);
        if (id != null && VaultStore.Get(id) is { } v)
            return source switch
            {
                "host" => L.F("Vault 계정 '{0}'", v.Name),
                "profile" => L.F("클라우드 프로필 기본 Vault 계정 '{0}'", v.Name),
                _ => L.F("기본 Vault 계정 '{0}'", v.Name),
            };
        if (!string.IsNullOrEmpty(ProtectedPassword) || !string.IsNullOrWhiteSpace(KeyPath)) return L.T("이 호스트에 저장된 계정");
        if (p != null && (!string.IsNullOrEmpty(p.DefaultUsername) || !string.IsNullOrEmpty(p.ProtectedDefaultPassword))) return L.T("클라우드 프로필 기본 계정");
        return null;
    }

    // ----- 화면 표시용: 키보드로 고른 카드 -----
    private bool _highlighted;
    [JsonIgnore]
    public bool IsHighlighted
    {
        get => _highlighted;
        set { if (_highlighted == value) return; _highlighted = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted))); }
    }
    // ----- 화면 표시용: Ctrl/Shift+클릭으로 여러 개 고른 카드 -----
    private bool _selected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }
    [JsonIgnore] public Visibility FavoriteVisibility => Favorite ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class HostGroup : INotifyPropertyChanged
{
    private bool _isCollapsed;

    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public List<HostEntry> Hosts { get; init; } = new();
    /// <summary>클라우드 프로필 그룹이면 해당 프로필</summary>
    public CloudProfile? Profile { get; init; }

    /// <summary>직접 등록한 그룹의 실제 Group 값 ("" = 그룹 없음)</summary>
    public string GroupName { get; init; } = "";
    public bool IsCloud => Profile != null;
    /// <summary>즐겨찾기 · 최근 접속처럼 다른 그룹의 호스트를 모아 보여주는 그룹</summary>
    public bool IsVirtual => Key is "F:" or "R:";
    public bool IsLocal => !IsCloud && !IsVirtual;
    public string Glyph => Key switch { "F:" => "", "R:" => "", _ => "" };
    public string BadgeText => Profile?.ProviderText ?? "";
    public Brush? BadgeBrush => Profile?.ProviderBrush;
    public string CountText
    {
        get
        {
            var running = Hosts.Count(h => !h.IsStopped);
            return IsCloud && running != Hosts.Count ? L.F("{0}  (실행 {1})", Hosts.Count, running) : Hosts.Count.ToString();
        }
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set { _isCollapsed = value; OnChanged(); OnChanged(nameof(Chevron)); }
    }
    public string Chevron => IsCollapsed ? "" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>가상화 목록의 "카드 한 줄" 행</summary>
/// <remarks>
/// 값이 바뀌지 않는 행이지만 INotifyPropertyChanged 를 구현한다 — 구현하지 않은 개체의 속성에 바인딩하면 WPF 가
/// PropertyDescriptor.AddValueChanged 로 변경을 듣는데, 이 등록은 전역 표에 원본(행 → 호스트 목록)을 붙잡아 두어
/// 목록을 다시 만들 때마다 버린 행·지운 호스트가 정리 시점까지 남는다. 구현하면 약한 참조(PropertyChangedEventManager)를 쓴다.
/// 알림을 보낼 일이 없으므로 구독은 저장하지 않는다.
/// </remarks>
public sealed class CardRow : INotifyPropertyChanged
{
    public required List<HostEntry> Hosts { get; init; }

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged { add { } remove { } }
}

/// <summary>Cloud 페이지의 클라우드별 섹션</summary>
public sealed class CloudSection : INotifyPropertyChanged
{
    private string _message = "";
    private bool _busy;

    public required CloudProvider Provider { get; init; }
    public string Title => CloudProviders.Label(Provider);
    public Brush Brush => CloudProviders.Brush(Provider);
    public string AuthHint => CloudProviders.Get(Provider).AuthHint;
    public System.Collections.ObjectModel.ObservableCollection<CloudProfile> Profiles { get; init; } = new();

    public string Message { get => _message; set { _message = value; OnChanged(); } }

    // 인증 파일 경로 (기본 위치 또는 사용자가 고른 위치)
    public string PathText => CloudPaths.Effective(Provider) + (IsCustomPath ? "" : L.T("  (기본)"));
    public bool IsCustomPath => CloudPaths.Custom(Provider) != null;
    public string PathKindText => CloudPaths.IsFolder(Provider) ? L.T("폴더") : L.T("파일");
    public void NotifyPath() { OnChanged(nameof(PathText)); OnChanged(nameof(IsCustomPath)); }
    public bool IsBusy { get => _busy; set { _busy = value; OnChanged(); OnChanged(nameof(AddButtonText)); } }
    public string AddButtonText => IsBusy ? L.T("찾는 중...") : Provider switch
    {
        CloudProvider.Gcp => L.T("프로젝트 추가"),
        CloudProvider.Azure => L.T("구독 추가"),
        _ => L.T("프로필 추가"),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
