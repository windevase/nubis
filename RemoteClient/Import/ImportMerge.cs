using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Nubis;

/// <summary>미리보기 한 줄의 상태</summary>
public enum ImportStatus
{
    /// <summary>새로 등록할 호스트</summary>
    New,
    /// <summary>목록에 이미 있음 (주소·포트·종류가 같음)</summary>
    Existing,
    /// <summary>가져오기 결과 안에서 앞 줄과 겹침 — 첫 줄만 쓴다</summary>
    Duplicate,
    /// <summary>주소·포트가 잘못됨</summary>
    Invalid,
}

/// <summary>가져온 서버를 HostEntry 로 바꿀 때 쓸 값 (미리보기 화면의 옵션)</summary>
public sealed record ImportDefaults
{
    /// <summary>포트가 없는 SSH 서버에 넣을 포트 (설정의 기본 SSH 포트)</summary>
    public int SshPort { get; init; } = 22;
    /// <summary>포트가 없는 RDP 서버에 넣을 포트 (설정의 기본 RDP 포트)</summary>
    public int RdpPort { get; init; } = 3389;
    /// <summary>가져온 그룹 앞에 붙일 이름 ("상위 / 가져온 그룹"). null·빈 값이면 붙이지 않음</summary>
    public string? GroupPrefix { get; init; }
    /// <summary>null 이 아니면 모든 호스트를 이 그룹으로 ("" = 그룹 없음). GroupPrefix 보다 우선</summary>
    public string? GroupOverride { get; init; }
    /// <summary>SSH 호스트에 지정할 Vault 계정 (지정하면 가져온 사용자명은 넣지 않음)</summary>
    public Guid? SshVaultId { get; init; }
    /// <summary>RDP 호스트에 지정할 Vault 계정</summary>
    public Guid? RdpVaultId { get; init; }

    /// <summary>설정의 기본 포트로 채운 기본값</summary>
    public static ImportDefaults FromSettings() => new()
    {
        SshPort = HostEntry.PreferredPort(Protocol.Ssh),
        RdpPort = HostEntry.PreferredPort(Protocol.Rdp),
    };
}

/// <summary>미리보기 한 줄. 체크(IsSelected)만 화면에서 바뀐다</summary>
public sealed class ImportRow : INotifyPropertyChanged
{
    public ImportRow(ImportedHost source, ImportStatus status, HostEntry? host, HostEntry? existing, string message)
    {
        Source = source;
        Status = status;
        Host = host;
        Existing = existing;
        Message = message;
        _isSelected = IsSelectable;
    }

    /// <summary>파서가 읽은 원본</summary>
    public ImportedHost Source { get; }
    public ImportStatus Status { get; }
    /// <summary>등록할 호스트 (Invalid 이면 null). Existing 이면 갱신에 쓸 값</summary>
    public HostEntry? Host { get; }
    /// <summary>목록에 이미 있는 같은 호스트 (Existing 일 때)</summary>
    public HostEntry? Existing { get; }
    /// <summary>경고·안내 (점프 서버를 못 찾음, 클라우드 호스트라 건너뜀 등)</summary>
    public string Message { get; }

    /// <summary>체크할 수 있는 줄 — 새 호스트, 또는 클라우드가 아닌 기존 호스트 (갱신 옵션용)</summary>
    public bool IsSelectable => Status == ImportStatus.New || (Status == ImportStatus.Existing && Existing is { IsCloud: false });

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var v = value && IsSelectable;
            if (_isSelected == v) return;
            _isSelected = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    // ----- 미리보기 표시용 -----
    public string NameText => Host?.Label is { Length: > 0 } l ? l : (Source.Name.Trim().Length > 0 ? Source.Name.Trim() : Source.Address.Trim());
    public string AddressText => Host?.Address ?? Source.Address.Trim();
    public string PortText => Host?.Port.ToString() ?? Source.Port?.ToString() ?? "";
    public string ProtocolText => Host != null ? Host.ProtocolText : Source.Protocol switch
    {
        Protocol.Ssh => "SSH",
        Protocol.Rdp => "RDP",
        _ => "",
    };
    public string GroupText => Host?.Group ?? Source.Group ?? "";
    /// <summary>로그인 사용자 — Vault 를 지정했으면 그 계정 이름</summary>
    public string UserText => Host?.VaultId is { } vid
        ? (VaultStore.Get(vid) is { } v ? "Vault · " + v.Name : "Vault")
        : (Host?.Username is { Length: > 0 } u ? u : Source.Username ?? "");
    public string StatusText => Status switch
    {
        ImportStatus.New => L.T("새 호스트"),
        ImportStatus.Existing => L.T("이미 있음"),
        ImportStatus.Duplicate => L.T("중복"),
        _ => L.T("오류"),
    };
    /// <summary>메모 칸: 경고가 있으면 앞에, 그다음 원본 메모·OS</summary>
    public string DetailText => string.Join(" · ", new[] { Message, Source.Note, Source.Os }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));

    /// <summary>줄 툴팁 (메모가 없으면 null — 빈 툴팁을 띄우지 않게)</summary>
    public string? DetailTip => DetailText is { Length: > 0 } t ? t : null;

    private string? _searchText;
    /// <summary>
    /// 미리보기 검색 대상 (이름 · 주소 · 그룹 · 사용자 · 메모). 줄 값은 바뀌지 않으므로 처음 한 번만 만든다 —
    /// 수천 줄에서 글자를 칠 때마다 줄마다 메모 문자열을 새로 합치지 않게
    /// </summary>
    public string SearchText => _searchText ??= string.Join("\n", NameText, AddressText, GroupText, UserText, DetailText);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>미리보기 결과 (줄 목록 + 만들 때 쓴 옵션)</summary>
public sealed record ImportPlan(List<ImportRow> Rows, ImportDefaults Defaults);

/// <summary>등록 결과</summary>
public sealed record ImportApplyResult(int Added, int Updated, int Skipped, List<string> Warnings)
{
    /// <summary>새로 추가한 호스트 Id (추가한 순서). "가져온 호스트 보기" 처럼 등록 뒤 목록에서 골라 보여 줄 때 쓴다</summary>
    public IReadOnlyList<Guid> AddedIds { get; init; } = Array.Empty<Guid>();
    /// <summary>값이 바뀐 기존 호스트 Id (갱신한 순서)</summary>
    public IReadOnlyList<Guid> UpdatedIds { get; init; } = Array.Empty<Guid>();

    /// <summary>"N개 추가 · M개 갱신 · K개 건너뜀"</summary>
    public string Summary => L.F("{0}개 추가 · {1}개 갱신 · {2}개 건너뜀", Added, Updated, Skipped);
}

/// <summary>가져오기 창이 돌려주는 값 — 호출하는 쪽(MainWindow)이 ApplyTo 로 호스트 목록에 반영하고 저장한다</summary>
public sealed record ImportPlanResult(ImportPlan Plan, IReadOnlyList<ImportRow> Selected, bool UpdateExisting)
{
    public ImportApplyResult ApplyTo(List<HostEntry> hosts) => ImportMerge.Apply(Plan, Selected, hosts, UpdateExisting);
}

/// <summary>
/// 가져온 서버(ImportedHost) → HostEntry 변환 · 기존 목록과의 중복 판정 · 등록. UI 와 무관 (ImportMergeTests).
/// 비밀번호·키는 어떤 경우에도 넣지 않는다 — 계정은 Vault 로 지정한다.
/// </summary>
public static partial class ImportMerge
{
    /// <summary>ImportedHost 를 새 HostEntry 로 (주소·포트 검사는 하지 않음 — Plan 이 한다)</summary>
    public static HostEntry ToHostEntry(ImportedHost h, ImportDefaults d)
    {
        var proto = InferProtocol(h);
        var (address, addrPort) = SplitHostPort(h.Address);
        var name = h.Name.Trim();
        var vault = proto == Protocol.Rdp ? d.RdpVaultId : d.SshVaultId;
        return new HostEntry
        {
            Label = name.Length > 0 ? name : address,
            Address = address,
            Protocol = proto,
            Port = h.Port ?? addrPort ?? (proto == Protocol.Rdp ? d.RdpPort : d.SshPort),
            Group = ResolveGroup(h.Group, d),
            // Vault 를 지정하면 계정은 Vault 가 정한다 (호스트에 따로 적지 않음)
            Username = vault == null ? (h.Username?.Trim() ?? "") : "",
            VaultId = vault,
            Os = string.IsNullOrWhiteSpace(h.Os) ? null : h.Os.Trim(),
            // RDP 전용 값은 RDP 호스트에만 (호스트 편집기 · MCP 와 같은 규칙)
            RdpGateway = proto == Protocol.Rdp && !string.IsNullOrWhiteSpace(h.RdpGateway) ? h.RdpGateway.Trim() : null,
            RdpDisableNla = proto == Protocol.Rdp && h.RdpDisableNla,
        };
    }

    /// <summary>
    /// 주소 칸에 포트가 붙어 온 경우("host:2222" · "[v6]:22" · "[v6]")를 나눈다. 콜론이 여럿인 IPv6 는 그대로.
    /// 이렇게 하지 않으면 "host:2222" 가 주소로 등록돼 접속할 수 없다 (':' 는 IPv6 때문에 주소 글자로 허용됨)
    /// </summary>
    public static (string Host, int? Port) SplitHostPort(string address)
    {
        var s = address.Trim();
        if (s.StartsWith('[') && s.IndexOf(']') is var close && close > 0)
        {
            var rest = s[(close + 1)..];
            var host6 = s[1..close].Trim();
            if (rest.Length == 0) return (host6, null);
            if (rest[0] == ':' && int.TryParse(rest[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p6) && p6 is >= 1 and <= 65535)
                return (host6, p6);
            return (s, null);
        }
        var colon = s.IndexOf(':');
        if (colon > 0 && colon == s.LastIndexOf(':') &&
            int.TryParse(s[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535)
            return (s[..colon], p);
        return (s, null);
    }

    /// <summary>종류: 출처에 있으면 그대로, 없으면 OS 에 Windows 가 있거나 포트가 3389 면 RDP, 아니면 SSH</summary>
    public static Protocol InferProtocol(ImportedHost h)
    {
        if (h.Protocol is { } p) return p;
        if (h.Os?.Contains("windows", StringComparison.OrdinalIgnoreCase) == true || (h.Port ?? SplitHostPort(h.Address).Port) == 3389) return Protocol.Rdp;
        return Protocol.Ssh;
    }

    /// <summary>그룹: "모두 이 그룹으로" 가 우선, 아니면 "앞에 붙일 이름 / 가져온 그룹"</summary>
    public static string ResolveGroup(string? group, ImportDefaults d)
    {
        if (d.GroupOverride != null) return d.GroupOverride.Trim();
        var p = d.GroupPrefix?.Trim() ?? "";
        var g = group?.Trim() ?? "";
        if (p.Length > 0 && g.Length > 0) return $"{p} / {g}";
        return p.Length > 0 ? p : g;
    }

    /// <summary>미리보기: 줄마다 상태(새 호스트 / 이미 있음 / 중복 / 오류)와 등록할 HostEntry 를 만든다</summary>
    public static ImportPlan Plan(IEnumerable<ImportedHost> hosts, IReadOnlyList<HostEntry> existing, ImportDefaults d)
    {
        var list = hosts.ToList();
        var rows = new List<ImportRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 점프 서버를 찾을 수 있는지 미리 보기 위한 이름·주소 (가져온 것 + 기존 SSH 호스트)
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addrs = new JumpAddressSet();
        foreach (var h in list)
        {
            // 점프 서버는 SSH 호스트만 될 수 있다 (등록할 때도 SSH 만 찾음) — RDP 항목 이름으로 "찾음" 이라 미리 보이지 않게
            if (InferProtocol(h) != Protocol.Ssh) continue;
            if (!string.IsNullOrWhiteSpace(h.Name)) names.Add(h.Name.Trim());
            if (!string.IsNullOrWhiteSpace(h.Address))
            {
                var (ha, hp) = SplitHostPort(h.Address);
                addrs.Add(ha, h.Port ?? hp ?? d.SshPort);
            }
        }
        foreach (var e in existing.Where(x => x.Protocol == Protocol.Ssh))
        {
            if (!string.IsNullOrWhiteSpace(e.Label)) names.Add(e.Label.Trim());
            foreach (var a in Addresses(e)) addrs.Add(a, e.Port);
        }
        // 기존 목록 대조용 색인 (줄마다 목록 전체를 훑지 않게 — 수백~수천 대 가져오기)
        var index = new ExistingIndex(existing);

        foreach (var h in list)
        {
            var proto = InferProtocol(h);
            var address = SplitHostPort(h.Address).Host;
            var error = proto == Protocol.Local ? L.T("SSH 또는 RDP 서버만 가져올 수 있습니다.")
                : address.Length == 0 ? L.T("주소가 없습니다.")
                : !IsValidAddress(address) ? L.T("주소 형식이 올바르지 않습니다.")
                // 콜론이 하나뿐이면 IPv6 가 아니라 포트를 읽지 못한 "host:99999" · "host:ssh" 같은 값 (그대로 두면 접속할 수 없는 호스트가 생김)
                : address.Count(c => c == ':') == 1 ? L.T("주소 뒤의 포트 형식이 올바르지 않습니다.")
                // 괄호가 남아 있으면 SplitHostPort 가 읽지 못한 "[v6]:0" · "[v6]:99999" · "[v6]x" (괄호째 등록되면 접속할 수 없음)
                : address.IndexOfAny(new[] { '[', ']' }) >= 0 ? L.T("주소 뒤의 포트 형식이 올바르지 않습니다.")
                : (h.Port is < 1 or > 65535) ? L.T("포트는 1~65535 사이여야 합니다.")
                : null;
            if (error != null)
            {
                rows.Add(new ImportRow(h, ImportStatus.Invalid, null, null, error));
                continue;
            }

            var host = ToHostEntry(h, d);
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(h.JumpHost))
            {
                if (host.Protocol != Protocol.Ssh) notes.Add(L.T("RDP 는 점프 서버를 쓰지 않아 무시합니다."));
                else if (!CanResolveJump(h.JumpHost, names, addrs)) notes.Add(L.F("점프 서버 '{0}' 를 찾지 못해 비워 둡니다.", h.JumpHost.Trim()));
            }

            if (!seen.Add(Key(host.Protocol, host.Address, host.Port)))
            {
                rows.Add(new ImportRow(h, ImportStatus.Duplicate, host, null, L.T("앞 줄과 주소·포트·종류가 같아 건너뜁니다.")));
                continue;
            }

            var match = index.Find(host.Protocol, host.Address, host.Port);
            if (match != null)
            {
                notes.Insert(0, match.IsCloud
                    ? L.F("클라우드 동기화 호스트 '{0}' 와 같아 바꾸지 않습니다.", match.DisplayName)
                    : L.F("목록의 '{0}' 와 같습니다.", match.DisplayName));
                rows.Add(new ImportRow(h, ImportStatus.Existing, host, match, string.Join(" ", notes)));
                continue;
            }
            rows.Add(new ImportRow(h, ImportStatus.New, host, null, string.Join(" ", notes)));
        }
        return new ImportPlan(rows, d);
    }

    /// <summary>
    /// 고른 줄을 호스트 목록에 반영한다 (목록을 바꾼다 — 저장은 호출한 쪽에서).
    /// 새 호스트는 추가, 이미 있는 호스트는 updateExisting 이면 이름·그룹·사용자명만 갱신 (Vault·클라우드 값 등은 그대로, 클라우드 호스트는 항상 건너뜀).
    /// 등록 직전에 목록과 다시 대조하므로 미리보기 뒤에 목록이 바뀌어도 중복 등록하지 않는다.
    /// Skipped = 미리보기 줄 중 추가·갱신되지 않은 줄 (체크 안 함 · 중복 · 오류 · 바뀐 값 없음 포함).
    /// </summary>
    public static ImportApplyResult Apply(ImportPlan plan, IEnumerable<ImportRow> selected, List<HostEntry> existing, bool updateExisting)
    {
        var picked = new HashSet<ImportRow>(selected, ReferenceEqualityComparer.Instance);
        var warnings = new List<string>();
        var added = new List<(ImportRow Row, HostEntry Host)>();
        var byName = new Dictionary<string, HostEntry>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.Ordinal);
        int addCount = 0, updateCount = 0;
        var updatedIds = new List<Guid>();
        var index = new ExistingIndex(existing);

        foreach (var row in plan.Rows)
        {
            if (row.Host is not { } src || row.Status is ImportStatus.Invalid or ImportStatus.Duplicate) continue;
            var name = row.Source.Name.Trim();
            var match = index.Find(src.Protocol, src.Address, src.Port);
            // 고르지 않은 줄이라도 이미 목록에 있으면 점프 서버 이름으로 찾을 수 있게
            if (match != null && name.Length > 0) byName.TryAdd(name, match);
            if (!picked.Contains(row) || !done.Add(Key(src.Protocol, src.Address, src.Port))) continue;

            if (match != null)
            {
                if (updateExisting && !match.IsCloud && UpdateExisting(match, row, plan.Defaults))
                {
                    updateCount++;
                    updatedIds.Add(match.Id);
                }
                continue;
            }
            var host = src.Clone();
            host.Id = Guid.NewGuid();
            host.ProtectedPassword = null;
            host.KeyPath = null;
            host.JumpHostId = null;
            existing.Add(host);
            index.Add(host);
            added.Add((row, host));
            addCount++;
            if (name.Length > 0) byName.TryAdd(name, host);
        }

        // 점프 서버는 모두 추가한 뒤에 연결 (가져온 순서와 무관하게 찾도록)
        // SSH 호스트 색인은 한 번만 (점프 서버를 쓰는 호스트 수천 대 × 목록 전체를 훑지 않게)
        SshLookup? ssh = null;
        foreach (var (row, host) in added)
        {
            var spec = row.Source.JumpHost?.Trim();
            if (string.IsNullOrEmpty(spec) || host.Protocol != Protocol.Ssh) continue;
            ssh ??= new SshLookup(existing);
            var jump = ResolveJump(spec, byName, ssh);
            if (jump != null && jump.Id != host.Id && jump.Protocol == Protocol.Ssh) host.JumpHostId = jump.Id;
            else warnings.Add(L.F("{0}: 점프 서버 '{1}' 를 찾지 못해 비워 두었습니다.", host.DisplayName, spec));
        }

        return new ImportApplyResult(addCount, updateCount, plan.Rows.Count - addCount - updateCount, warnings)
        {
            AddedIds = added.Select(x => x.Host.Id).ToList(),
            UpdatedIds = updatedIds.Distinct().ToList(),
        };
    }

    /// <summary>기존 호스트 갱신: 이름·그룹·사용자명만 (가져온 값이 있을 때만). 바뀌었으면 true</summary>
    private static bool UpdateExisting(HostEntry target, ImportRow row, ImportDefaults d)
    {
        var before = (target.Label, target.Group, target.Username);
        var name = row.Source.Name.Trim();
        // 이름 칸이 없어 파서가 주소를 이름으로 채운 줄("10.0.0.1")이면 사용자가 붙여 둔 이름을 주소로 덮지 않는다
        if (name.Length > 0 && !IsAddressName(name, row.Source.Address)) target.Label = name;
        // 가져온 그룹 정보가 없으면 기존 그룹을 지우거나 "앞에 붙일 이름" 만으로 바꾸지 않는다
        // ("모두 이 그룹으로" 를 고른 경우만 그 값으로)
        if (d.GroupOverride != null) target.Group = d.GroupOverride.Trim();
        else if (!string.IsNullOrWhiteSpace(row.Source.Group)) target.Group = ResolveGroup(row.Source.Group, d);
        if (row.Source.Username?.Trim() is { Length: > 0 } user) target.Username = user;
        return before != (target.Label, target.Group, target.Username);
    }

    /// <summary>이름이 주소 그 자체인지 ("host:2222" · "[v6]" 처럼 포트 · 괄호가 붙은 형태 포함 — 이름 쪽에 괄호 · 포트가 있어도)</summary>
    private static bool IsAddressName(string name, string address)
    {
        var a = address.Trim();
        if (a.Length == 0) return false;
        return name.Equals(a, StringComparison.OrdinalIgnoreCase)
            || SplitHostPort(name).Host.Equals(SplitHostPort(a).Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>목록에서 같은 호스트 (주소 대소문자 무시 + 포트 + 종류). 클라우드 호스트는 사설·공인 IP 도 비교</summary>
    public static HostEntry? FindExisting(IEnumerable<HostEntry> existing, Protocol protocol, string address, int port) =>
        existing.FirstOrDefault(e => e.Protocol == protocol && e.Port == port &&
                                     Addresses(e).Any(a => a.Equals(address, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> Addresses(HostEntry e)
    {
        foreach (var a in new[] { e.Address, e.PrivateIp, e.PublicIp })
            if (!string.IsNullOrWhiteSpace(a)) yield return a.Trim();
    }

    private static string Key(Protocol p, string address, int port) => $"{p}|{address.Trim().ToLowerInvariant()}|{port}";

    /// <summary>IP · 호스트명에 쓸 수 있는 글자만 (공백·@·/ 등이 있으면 잘못 읽은 값)</summary>
    public static bool IsValidAddress(string address) =>
        address.Length is > 0 and <= 253 && address.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '%' or '[' or ']');

    /// <summary>
    /// 점프 서버 표기 → (주소, 포트). "user@host:port" · "[v6]:port" · "host". 여러 단계("a,b")면 대상 바로 앞(마지막) 단계.
    /// </summary>
    public static (string Host, int? Port) ParseJumpSpec(string spec)
    {
        var s = spec.Trim();
        var comma = s.LastIndexOf(',');
        if (comma >= 0) s = s[(comma + 1)..].Trim();
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close > 0)
            {
                var host6 = s[1..close];
                return s.Length > close + 2 && s[close + 1] == ':' && int.TryParse(s[(close + 2)..], out var p6) ? (host6, p6) : (host6, null);
            }
        }
        var colon = s.IndexOf(':');
        if (colon > 0 && colon == s.LastIndexOf(':') && int.TryParse(s[(colon + 1)..], out var port))
            return (s[..colon], port);
        return (s, null);
    }

    private static bool CanResolveJump(string spec, HashSet<string> names, JumpAddressSet addrs)
    {
        if (names.Contains(spec.Trim())) return true;
        var (host, port) = ParseJumpSpec(spec);
        return host.Length > 0 && (names.Contains(host) || addrs.Contains(host, port));
    }
}
