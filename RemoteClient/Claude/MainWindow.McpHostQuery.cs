using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Nubis;

// MCP 호스트 도구 (UI 동작과 같은 경로): get_host 상세 조회 · open_session 의 빠른 연결(target_address).
// 보안 원칙:
// - get_host 는 비밀번호·키 파일 경로·기억한 답 내용을 넣지 않는다 (있음/없음 · 개수만)
// - 빠른 연결은 설정의 기본 Vault 계정이 채워질 수 있어 항상 승인 + 감사 로그 (등록된 호스트 open_session 과 달리)
public partial class MainWindow
{
    /// <summary>호스트 조회 도구: list_groups · list_hosts · get_host (BuildTools 맨 앞에서 등록)</summary>
    private IEnumerable<McpTool> HostQueryTools()
    {
        yield return new McpTool("list_groups",
            "등록된 호스트 그룹 목록(직접 등록 그룹 + 클라우드 프로필 그룹)과 서버 수, 호스트 목록 화면에서 접혀 있는지(저장된 상태)를 반환한다. " +
            "접기·펴기는 set_group_collapsed.",
            Schema(new { }),
            _ => OnUi(() =>
            {
                var canon = CanonicalGroupNames(_hosts);
                var sb = new StringBuilder("그룹 | 서버 수 | 실행 중 | SSH | RDP | 접힘\n");
                foreach (var g in _hosts.GroupBy(GroupLabel).OrderBy(g => g.Key))
                    sb.AppendLine($"{g.Key} | {g.Count()} | {g.Count(h => !h.IsStopped)} | {g.Count(h => h.Protocol == Protocol.Ssh)} | {g.Count(h => h.Protocol == Protocol.Rdp)} | " +
                                  (GroupCollapsedState(g.First(), canon) ? "예" : "아니오"));
                sb.AppendLine($"(즐겨찾기 그룹 접힘: {(_collapsedLocal.Contains(FavoritesKey) ? "예" : "아니오")} · 최근 접속 그룹 접힘: {(_collapsedLocal.Contains(RecentKey) ? "예" : "아니오")})");
                if (SearchBox.Text.Trim().Length > 0) sb.AppendLine("지금 검색 중이라 화면에서는 검색을 지울 때까지 모든 그룹이 펼쳐져 보입니다.");
                return sb.ToString();
            }));

        yield return new McpTool("list_hosts",
            "호스트 검색. 결과 형식: id | 이름 | 그룹 | 프로토콜 사설주소:포트 (공인 IP) | 상태 | 용도 | OS, 그 뒤에 해당할 때만 " +
            "| ★즐겨찾기 | 점프 <점프 서버 이름 (주소)> | 게이트웨이 <RD 게이트웨이> | 태그 키=값, ... | 계정 <Vault 이름>(직접/프로필 기본/설정 기본). " +
            "용도 칸은 클라우드 태그에서 온 값이고, 사용자가 붙인 태그는 '태그' 칸. '계정' 칸은 접속할 때 실제로 쓰이는 Vault 계정과 그 출처. 비밀번호는 포함하지 않는다. " +
            "selected_only=true 면 사용자가 화면에서 고른 카드만 (첫 줄에 선택 개수). 한 호스트의 자세한 설정은 get_host.",
            Schema(new
            {
                query = new { type = "string", description = "이름·IP(사설/공인)·인스턴스 ID·리전·용도·OS·태그(키=값) 부분 검색어" },
                group = new { type = "string", description = "그룹 이름 (예: 'Production', '[AWS] prod')" },
                protocol = new { type = "string", @enum = new[] { "ssh", "rdp" } },
                running_only = new { type = "boolean", description = "실행 중인 서버만" },
                favorites_only = new { type = "boolean", description = "즐겨찾기한 호스트만" },
                vault = new { type = "string", description = "이 Vault 계정(이름·id)으로 로그인하는 호스트만 — 직접 연결한 호스트와 프로필·설정의 기본 계정으로 쓰는 호스트 모두 (화면 검색의 'vault:이름' 과 같은 기준)" },
                selected_only = new { type = "boolean", description = "사용자가 화면에서 고른 카드만: Ctrl/Shift+클릭으로 여러 개 고른 카드, 없으면 키보드·클릭으로 고른 카드 1개. 화면 순서로 반환" },
                limit = new { type = "integer", description = "최대 개수 (기본 300)" },
            }),
            a => OnUi(() =>
            {
                var q = Arg(a, "query");
                var group = Arg(a, "group");
                var proto = Arg(a, "protocol");
                IEnumerable<HostEntry> list = _hosts;
                var header = "";
                var selectedOnly = BoolArg(a, "selected_only") == true;
                if (selectedOnly)
                {
                    var (picked, kind) = UserPickedHosts();
                    header = McpHostParity.PickedHeader(picked.Count, kind) + "\n";
                    list = picked;
                }
                if (Arg(a, "vault").Trim() is { Length: > 0 } vaultSpec)
                {
                    var users = VaultUsers(ResolveVault(vaultSpec)!.Id).ToHashSet();
                    list = list.Where(users.Contains);
                }
                if (q.Length > 0)
                    list = list.Where(h => new[] { h.DisplayName, h.Address, h.PrivateIp, h.PublicIp, h.CloudInstanceId, h.CloudRegion, h.Purpose, h.Os, h.Username, h.TagsText }
                        .Any(s => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true));
                if (group.Length > 0)
                    list = list.Where(h => GroupLabel(h).Contains(group, StringComparison.OrdinalIgnoreCase));
                if (proto.Length > 0)
                    list = list.Where(h => h.ProtocolText.Equals(proto, StringComparison.OrdinalIgnoreCase));
                if (BoolArg(a, "running_only") == true) list = list.Where(h => !h.IsStopped);
                if (BoolArg(a, "favorites_only") == true) list = list.Where(h => h.Favorite);
                // 고른 카드는 화면 순서 그대로 (사용자가 보는 순서)
                var all = selectedOnly ? list.ToList() : list.OrderBy(GroupLabel).ThenBy(h => h.DisplayName).ToList();
                var limit = IntArg(a, "limit") ?? 300;
                var sb = new StringBuilder(header + $"{all.Count}개" + (all.Count > limit ? $" (앞 {limit}개만 표시)" : "") + "\n");
                foreach (var h in all.Take(limit)) sb.AppendLine(HostLine(h));
                return sb.ToString();
            }));

        yield return new McpTool("get_host",
            "등록된 호스트 1개의 상세 정보: id · 이름 · 그룹 · 접속 주소 · 클라우드 정보 · 사용자명 · 실제로 쓰이는 Vault 계정과 출처(직접/프로필 기본/설정 기본) · " +
            "접속 계정 출처 · 저장된 비밀번호와 키 파일 경로 있음/없음 · 기억한 인증 답 개수 · NLA · 점프 서버 · RD 게이트웨이 · 태그 · 즐겨찾기 · 마지막 접속 · 열린 탭. " +
            "비밀번호·키 파일 경로·인증 답 내용은 포함하지 않는다.",
            Schema(new { host = new { type = "string", description = "호스트 이름·IP·id (정확히 1개)" } }, "host"),
            a => OnUi(() =>
            {
                var h = ResolveOneHost(Arg(a, "host"));
                var p = _profiles.FirstOrDefault(x => x.Id == h.CloudProfileId);
                var sessions = _tabs.Where(t => ReferenceEquals(t.Host, h)).Select(t => t.Id).ToList();
                return McpHostParity.Detail(h, p, GroupLabel(h), h.JumpHostId != null ? JumpName(h.JumpHostId) : null,
                    JumpUsers(_hosts, h), sessions);
            }));
    }

    /// <summary>
    /// open_session 의 빠른 연결: 등록하지 않은 주소로 탭을 연다 (화면 검색창의 빠른 접속과 같은 MakeQuickHost 경로).
    /// 항상 승인 — 설정의 기본 Vault 계정이 채워져 그 계정의 비밀번호·키가 이 주소로 갈 수 있다
    /// </summary>
    private async Task<SessionTab> OpenQuickSessionAsync(JsonElement a)
    {
        if (Arg(a, "host").Trim().Length > 0) throw new McpToolException("host 와 target_address 는 함께 쓸 수 없습니다 (등록된 호스트는 host, 등록하지 않은 주소는 target_address).");
        if (Arg(a, "address") is { Length: > 0 } mode && mode != "default")
            throw new McpToolException("address(default/private/public)는 등록된 호스트(host)에만 씁니다. target_address 로 접속할 주소를 직접 지정하세요.");
        var addr = Arg(a, "target_address").Trim();
        var proto = Arg(a, "protocol").Trim().ToLowerInvariant() switch
        {
            "" or "ssh" => Protocol.Ssh,
            "rdp" => Protocol.Rdp,
            _ => throw new McpToolException("protocol 은 ssh 또는 rdp 입니다."),
        };
        var user = Arg(a, "username").Trim();
        int? port = Has(a, "port") ? IntArg(a, "port") ?? 0 : null;
        if (QuickTargetError(addr, user, port) is { } err) throw new McpToolException(err);

        var host = MakeQuickHost(proto, addr, user, port);
        // 접속에 쓰일 계정 출처 (빠른 연결은 프로필이 없으므로 설정의 기본 Vault 계정만 해당)
        var credUi = host.CredentialSourceText(null);
        var credAudit = McpHelpers.CredentialSourceForAudit(host, null);
        var credBefore = host.ResolveVault(null);   // 승인 뒤 재확인용 (이름이 아니라 계정 id·출처로 비교)
        var target = McpHostParity.QuickTargetLabel(host);
        var command = "(빠른 연결 탭 열기 — 등록하지 않음)" + (credAudit != null ? $" · {credAudit}" : "");
        var registered = _hosts.Where(h => h.Protocol == proto &&
                                           (h.Address.Equals(addr, StringComparison.OrdinalIgnoreCase) ||
                                            addr.Equals(h.PrivateIp, StringComparison.OrdinalIgnoreCase) ||
                                            addr.Equals(h.PublicIp, StringComparison.OrdinalIgnoreCase))).ToList();
        var desc = L.T("등록하지 않은 주소에 세션 탭을 엽니다 (화면의 빠른 접속과 같음). 주소가 맞는지 확인하세요.") + "\n" +
                   (credUi != null ? L.F("접속에 쓰일 계정: {0}", credUi) : L.T("저장된 계정이 없어 접속할 때 사용자명·비밀번호를 묻습니다.")) +
                   (registered.Count > 0
                       ? "\n" + L.F("같은 주소로 등록된 호스트가 있습니다: {0}", string.Join(", ", registered.Take(5).Select(h => McpHelpers.VisibleControlChars(h.DisplayName))))
                       : "");
        if (!await AskApprovalAsync("open_session", L.T("Claude 가 등록하지 않은 주소에 접속을 요청합니다"), desc, new[] { target }, L.T("(빠른 연결 탭 열기)")))
        {
            AuditLog.Write("open_session", new[] { target }, command, "거부");
            throw new McpToolException("사용자가 빠른 연결을 거부했습니다.");
        }
        // 승인 창이 큐에서 기다리는 사이 설정의 기본 Vault 계정이 바뀌었으면 사용자가 본 것과 다른 계정이 이 주소로 간다 — 실행하지 않는다
        if (host.ResolveVault(null) != credBefore)
        {
            AuditLog.Write("open_session", new[] { target }, command, "승인 후 취소(대기 중 접속 계정이 바뀜)");
            throw new McpToolException("승인을 기다리는 동안 접속에 쓰일 기본 계정이 바뀌어 접속하지 않았습니다. 다시 요청하세요.");
        }
        var tab = Connect(host);
        AuditLog.Write("open_session", new[] { target }, command + $" (session {tab.Id})", "승인·실행");
        return tab;
    }

    /// <summary>open_session 공통: 창을 앞으로 가져오고, 요청하면 연결이 끝날 때까지 기다린다</summary>
    private async Task<string> AfterOpenSessionAsync(SessionTab tab, string opened, double waitConnected, CancellationToken ct)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (waitConnected <= 0) return opened + " 연결 결과는 잠시 후 list_sessions 또는 read_terminal 로 확인하세요.";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (tab.Status == SessionStatus.Connecting && _tabs.Contains(tab) && sw.Elapsed.TotalSeconds < waitConnected)
            await Task.Delay(200, ct);
        if (!_tabs.Contains(tab)) return opened + " 기다리는 동안 탭이 닫혔습니다.";
        return opened + tab.Status switch
        {
            SessionStatus.Connected => $" 연결됐습니다 ({sw.Elapsed.TotalSeconds:0.0}초).",
            SessionStatus.Disconnected => " 연결에 실패했거나 끊겼습니다. read_terminal 로 오류 메시지를 확인하세요.",
            _ => $" {waitConnected:0.#}초 안에 연결이 끝나지 않았습니다 (호스트키·비밀번호 확인 창을 기다리는 중일 수 있음). list_sessions 로 다시 확인하세요.",
        };
    }

    /// <summary>list_hosts selected_only: 사용자가 화면에서 고른 카드 (다중 선택이 있으면 그것, 없으면 키보드로 고른 카드)</summary>
    private (List<HostEntry> Hosts, string Kind) UserPickedHosts() =>
        McpHostParity.Picked(SelectedHosts(), _highlighted, _hosts);

    /// <summary>list_groups 의 접힘 여부 (저장된 상태 — 검색 중에는 화면에서 모두 펼쳐져 보임)</summary>
    private bool GroupCollapsedState(HostEntry first, IReadOnlyDictionary<string, string> canon) =>
        McpHostParity.IsCollapsedState(McpHostParity.GroupStateKey(first, _profiles.Any(p => p.Id == first.CloudProfileId), canon),
            _collapsedLocal, _expandedCloud);
}
