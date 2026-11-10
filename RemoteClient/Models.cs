using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Nubis;

public enum Protocol { Ssh, Rdp, Local }

public sealed class HostEntry
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
    /// <summary>Vault 계정 (사용자명·비밀번호·SSH 키). 호스트에 직접 적은 값이 있으면 그 값이 우선</summary>
    public Guid? VaultId { get; set; }
    // 예전 형식(암호/키 따로 선택) 읽기 전용 — HostStore.Load 에서 VaultId 로 옮긴다
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? VaultPasswordId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? VaultKeyId { get; set; }
    public DateTime? LastConnected { get; set; }

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
    public string? Purpose { get; set; }
    public string? Os { get; set; }

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
    [JsonIgnore] public string ProtocolText => Protocol == Protocol.Ssh ? "SSH" : "RDP";
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { Protocol == Protocol.Ssh ? "ssh" : "rdp" };
            if (IsCloud)
            {
                parts.Add(UsePublicIp && HasPublicIp ? $"{PublicIp} (공인)" : PrimaryAddress + (HasPublicIp ? " · EIP" : ""));
                if (!string.IsNullOrWhiteSpace(Purpose)) parts.Add(Purpose);
                if (IsStopped) parts.Add(CloudState!.ToLowerInvariant());
                return string.Join(" · ", parts);
            }
            if (!string.IsNullOrWhiteSpace(Username)) parts.Add(Username);
            var addr = EffectiveAddress + (UsePublicIp && HasPublicIp ? " (공인)" : "");
            if (!string.IsNullOrWhiteSpace(Label)) parts.Add(Port == DefaultPort(Protocol) ? addr : $"{addr}:{Port}");
            return string.Join(", ", parts);
        }
    }

    [JsonIgnore]
    public string ToolTipText =>
        $"{DisplayName}\n사설 {PrimaryAddress}" + (HasPublicIp ? $"   공인 {PublicIp}" : "") + $"   :{Port} ({ProtocolText})\n" +
        $"접속 주소: {EffectiveAddress}{(UsePublicIp && HasPublicIp ? " (공인)" : "")}\n" +
        (IsCloud ? $"{CloudRegion}  ·  {CloudState}\n{Os}\n{CloudInstanceId}\n" : "") +
        "\n더블클릭하여 연결" + (HasPublicIp ? " · 우클릭으로 사설/공인 선택" : "");

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
    /// 접속용 계정을 채운 사본: Vault 항목을 쓰면 그 값을 먼저 채우고(호스트 설정이 우선),
    /// 클라우드 호스트는 그래도 비어 있는 계정을 프로필 기본 계정으로 채운다.
    /// </summary>
    public HostEntry WithDefaults(CloudProfile? p)
    {
        var c = UsesVault ? VaultStore.Apply(Clone()) : this;
        if (p == null || (!string.IsNullOrEmpty(c.Username) && !string.IsNullOrEmpty(c.ProtectedPassword))) return c;
        if (ReferenceEquals(c, this)) c = Clone();
        if (string.IsNullOrEmpty(c.Username)) c.Username = p.DefaultUsername;
        if (string.IsNullOrEmpty(c.ProtectedPassword)) c.ProtectedPassword = p.ProtectedDefaultPassword;
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

    public HostEntry Clone() => (HostEntry)MemberwiseClone();
}

public sealed class HostGroup : INotifyPropertyChanged
{
    private bool _isCollapsed;

    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public List<HostEntry> Hosts { get; init; } = new();
    /// <summary>클라우드 프로필 그룹이면 해당 프로필</summary>
    public CloudProfile? Profile { get; init; }

    public bool IsCloud => Profile != null;
    public string BadgeText => Profile?.ProviderText ?? "";
    public Brush? BadgeBrush => Profile?.ProviderBrush;
    public string CountText
    {
        get
        {
            var running = Hosts.Count(h => !h.IsStopped);
            return IsCloud && running != Hosts.Count ? $"{Hosts.Count}  (실행 {running})" : Hosts.Count.ToString();
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
public sealed class CardRow
{
    public required List<HostEntry> Hosts { get; init; }
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
    public string PathText => CloudPaths.Effective(Provider) + (IsCustomPath ? "" : "  (기본)");
    public bool IsCustomPath => CloudPaths.Custom(Provider) != null;
    public string PathKindText => CloudPaths.IsFolder(Provider) ? "폴더" : "파일";
    public void NotifyPath() { OnChanged(nameof(PathText)); OnChanged(nameof(IsCustomPath)); }
    public bool IsBusy { get => _busy; set { _busy = value; OnChanged(); OnChanged(nameof(AddButtonText)); } }
    public string AddButtonText => IsBusy ? "찾는 중..." : Provider switch
    {
        CloudProvider.Gcp => "프로젝트 추가",
        CloudProvider.Azure => "구독 추가",
        _ => "프로필 추가",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public enum SessionStatus { Connecting, Connected, Disconnected }

public interface ISessionView
{
    event Action<SessionStatus>? StatusChanged;
    void FocusSession();
    void CloseSession();
    /// <summary>터미널 화면의 마지막 N 줄. 터미널이 아니면(RDP) null. UI 스레드에서 호출</summary>
    System.Threading.Tasks.Task<string?> ReadTerminalAsync(int lines);
    /// <summary>터미널에서 선택한 텍스트. 터미널이 아니면 null. UI 스레드에서 호출</summary>
    System.Threading.Tasks.Task<string?> GetSelectionAsync();
    /// <summary>대화형 세션에 텍스트 입력. 불가능하면 false</summary>
    bool SendText(string text);
}

public sealed class SessionTab : INotifyPropertyChanged
{
    private bool _isSelected;
    private SessionStatus _status = SessionStatus.Connecting;

    /// <summary>MCP 에서 세션을 가리킬 때 쓰는 번호</summary>
    public int Id { get; init; }
    public required string Title { get; init; }
    public required Protocol Protocol { get; init; }
    public required System.Windows.FrameworkElement View { get; init; }
    /// <summary>원본 호스트 (로컬 터미널이면 null)</summary>
    public HostEntry? Host { get; init; }
    public ISessionView Session => (ISessionView)View;

    public string Glyph => Protocol switch
    {
        Protocol.Ssh => "",
        Protocol.Rdp => "",
        _ => "",
    };

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnChanged(); }
    }

    public SessionStatus Status
    {
        get => _status;
        set { _status = value; OnChanged(); OnChanged(nameof(StatusBrush)); }
    }

    public Brush StatusBrush => Status switch
    {
        SessionStatus.Connected => new SolidColorBrush(Color.FromRgb(0x20, 0xC5, 0xA0)),
        SessionStatus.Connecting => new SolidColorBrush(Color.FromRgb(0xF5, 0xB7, 0x40)),
        _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x88)),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
