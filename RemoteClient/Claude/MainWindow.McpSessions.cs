using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Nubis;

// MCP 도구: 세션 탭 조작 (다시 연결 · 고르기 · 이름 · 로컬 터미널 · 여러 탭 닫기 · SFTP 패널 · 닫은 탭) — UI 에서 손으로 하는 동작을 Claude 도 할 수 있게 (BuildTools 에서 등록)
// 보안 원칙:
// - 다시 연결 · 닫은 탭 다시 열기는 open_session 과 같은 수준(저장된 계정으로 접속 시작) — 승인 없이 감사 로그만. 비밀번호·OTP·호스트키 확인은 사용자 몫
// - 연결된 탭을 닫는 것은 원격 프로그램을 끝낼 수 있어 승인 (close_session 과 같음)
// - 탭 제목은 승인 창에 나오므로 보이지 않는 문자·제어 문자를 넣지 못하게 한다
public partial class MainWindow
{
    private IEnumerable<McpTool> SessionTools()
    {
        yield return new McpTool("reconnect_session",
            "끊긴 세션 탭을 다시 연결한다 (탭 우클릭 '다시 연결' / '끊긴 탭 모두 다시 연결'과 같음). 연결을 시작만 하고 바로 돌아온다 — " +
            "비밀번호·OTP·호스트키 확인 창은 사용자가 직접 답한다. 결과는 list_sessions 로 확인. session_id 또는 all_disconnected=true 중 하나.",
            Schema(new
            {
                session_id = new { type = "integer", description = "다시 연결할 탭" },
                all_disconnected = new { type = "boolean", description = "true 면 끊긴 탭 모두" },
            }),
            a => OnUiAsync(async () =>
            {
                if (BoolArg(a, "all_disconnected") == true)
                {
                    var list = _tabs.Where(t => t.Status == SessionStatus.Disconnected).ToList();
                    if (list.Count == 0) return "끊긴 탭이 없습니다.";
                    AuditLog.Write("reconnect_session", list.Select(SessionAuditTarget), "(끊긴 탭 모두 다시 연결)", "실행");
                    // RDP 계정 입력 창(중첩 메시지 루프)이 도구 응답을 붙잡지 않게 다음 차례에 시작한다. ReconnectAll 이 탭마다 다시 확인한다
                    _ = Dispatcher.BeginInvoke(() => ReconnectAll(list));
                    return $"끊긴 탭 {list.Count}개의 다시 연결을 시작했습니다: {string.Join(", ", list.Select(t => t.Id))}. 결과는 list_sessions 로 확인하세요.";
                }
                var tab = RequireSession(a, allowCurrent: false);
                if (tab.Status != SessionStatus.Disconnected)
                    return $"session {tab.Id} 은 이미 {(tab.Status == SessionStatus.Connected ? "연결돼" : "연결 중")} 있습니다.";
                // UI 와 같이 탭을 보여 준 뒤 다시 연결 — 확인 창이 그 탭 위에서 뜨게. 탭 표시는 진행 중인 RDP 입력이 끝난 뒤 (입력 중 탭을 바꿔 끊지 않게)
                return await UnderRdpInputSectionAsync(() =>
                {
                    // 기다리는 사이 닫혔거나 다시 연결됐을 수 있다
                    if (!_tabs.Contains(tab)) throw new McpToolException($"session {tab.Id} 은 기다리는 사이 닫혔습니다.");
                    if (tab.Status != SessionStatus.Disconnected) return $"session {tab.Id} 은 기다리는 사이 이미 다시 연결됐습니다.";
                    AuditLog.Write("reconnect_session", new[] { SessionAuditTarget(tab) }, "(다시 연결)", "실행");
                    SelectTab(tab);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_tabs.Contains(tab) && tab.Status == SessionStatus.Disconnected) tab.Session.Reconnect();
                    });
                    return $"session {tab.Id} 의 다시 연결을 시작했습니다. 결과는 list_sessions 또는 read_terminal 로 확인하세요.";
                });
            }));

        yield return new McpTool("select_session",
            "세션 탭을 앱 화면에 표시한다 (탭 클릭과 같음). session_id 에 \"home\" 을 주면 호스트 목록(홈) 화면으로 간다.",
            Schema(new
            {
                session_id = new { type = new[] { "integer", "string" }, description = "탭 번호 또는 \"home\"" },
            }, "session_id"),
            a => OnUiAsync(async () =>
            {
                // 화면 전환은 진행 중인 RDP 입력이 끝난 뒤 (입력 중 탭을 바꿔 끊지 않게)
                if (Arg(a, "session_id").Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
                    return await UnderRdpInputSectionAsync(() =>
                    {
                        SelectHome();
                        AuditLog.Write("select_session", new[] { "home" }, "(홈 화면 표시)", "실행");
                        return "호스트 목록(홈) 화면으로 전환했습니다.";
                    });
                var tab = RequireSession(a, allowCurrent: false);
                return await UnderRdpInputSectionAsync(() =>
                {
                    if (!_tabs.Contains(tab)) throw new McpToolException($"session {tab.Id} 은 기다리는 사이 닫혔습니다.");
                    SelectTab(tab);
                    AuditLog.Write("select_session", new[] { SessionAuditTarget(tab) }, "(탭 표시)", "실행");
                    return $"session {tab.Id} ({tab.Title}) 을 표시했습니다.";
                });
            }));

        yield return new McpTool("rename_session",
            $"세션 탭 이름을 바꾼다 (탭 우클릭 '탭 이름 바꾸기'와 같음). 앞뒤 공백은 지우고, 빈 이름·보이지 않는 문자·제어 문자·괄호·{McpSessionLogic.MaxTitleLength}자 초과는 거부한다. 로컬 PowerShell 탭은 바꿀 수 없다.",
            Schema(new
            {
                session_id = new { type = "integer" },
                title = new { type = "string", description = "새 탭 이름" },
            }, "session_id", "title"),
            a => OnUi(() =>
            {
                string title;
                try { title = McpSessionLogic.NormalizeTitle(Arg(a, "title")); }
                catch (ArgumentException ex) { throw new McpToolException(ex.Message); }
                var tab = RequireSession(a, allowCurrent: false);
                // 로컬 탭은 승인 창(send_to_terminal 등)에 주소가 붙지 않는다 — 'web01' 처럼 원격 서버로 보이게 바꾸면
                // 사용자가 원격 명령으로 알고 이 PC 에서 실행될 입력을 승인할 수 있다
                if (tab.Protocol == Protocol.Local)
                    throw new McpToolException($"session {tab.Id} 은 로컬 PowerShell 탭이라 Claude 가 이름을 바꿀 수 없습니다 (승인 창에서 원격 서버처럼 보이지 않게).");
                var old = tab.Title;
                if (old == title) return $"session {tab.Id} 의 이름이 이미 '{title}' 입니다.";
                tab.Title = title;
                if (tab.IsSelected) UpdateActiveContext();   // Claude 에게 알려 주는 현재 탭 정보도 새 이름으로 (UI 와 같음)
                AuditLog.Write("rename_session", new[] { $"session {tab.Id}" },
                    $"(탭 이름 바꾸기: {McpHelpers.VisibleControlChars(old)} → {title})", "실행");
                return $"session {tab.Id} 의 이름을 '{title}' 로 바꿨습니다.";
            }));

        yield return new McpTool("open_local_terminal",
            "이 PC 의 PowerShell 탭을 새로 연다 (Ctrl+Shift+T 와 같음). 입력은 send_to_terminal(사용자 승인)로 보낸다.",
            Schema(new { }),
            _ => OnUiAsync(() => UnderRdpInputSectionAsync(() =>   // 새 탭이 선택되며 진행 중인 RDP 입력을 끊지 않게
            {
                var before = _tabs.Count;
                OpenLocalTerminal();
                // AddTab 은 끝에 붙인다
                var tab = _tabs.Count > before ? _tabs[^1] : null;
                AuditLog.Write("open_local_terminal", new[] { tab != null ? $"session {tab.Id}: PowerShell" : "PowerShell" }, "(로컬 터미널 열기)", "실행");
                return tab != null ? $"로컬 PowerShell 탭을 열었습니다: session_id={tab.Id}." : "로컬 PowerShell 탭을 열지 못했습니다.";
            })));

        yield return new McpTool("close_sessions",
            "여러 세션 탭을 한 번에 닫는다 (탭 우클릭 메뉴와 같음). filter: others = session_id 말고 모두, right = session_id 오른쪽 모두, disconnected = 끊긴 탭 모두. " +
            "session_id 생략 시 현재 탭 기준 (disconnected 는 기준 탭이 필요 없음). 연결된(또는 연결 중인) 탭이 끼어 있으면 그 목록으로 사용자 승인을 한 번 받는다.",
            Schema(new
            {
                filter = new { type = "string", @enum = McpSessionLogic.CloseFilters },
                session_id = new { type = "integer", description = "기준 탭 (others/right)" },
            }, "filter"),
            a => OnUiAsync(() => CloseSessionsAsync(a)));

        yield return new McpTool("toggle_sftp_panel",
            "SSH 탭 왼쪽의 SFTP 파일 패널을 열거나 닫는다 (탭의 SFTP 버튼과 같음). open 생략 시 토글. session_id 생략 시 현재 탭. " +
            "패널을 열면 터미널의 SSH 연결로 SFTP 를 열어 홈 폴더 목록을 읽는다.",
            Schema(new
            {
                session_id = new { type = "integer" },
                open = new { type = "boolean", description = "true = 열기, false = 닫기" },
            }),
            a => OnUiAsync(async () =>
            {
                var tab = RequireSession(a, allowCurrent: true);
                if (tab.View is not SshSessionView ssh) throw new McpToolException($"session {tab.Id} 은 SSH 탭이 아닙니다 (SFTP 패널은 SSH 탭에만 있음).");
                var was = IsSftpPanelOpen(ssh);
                var want = BoolArg(a, "open") ?? !was;
                if (want == was) return $"session {tab.Id} 의 SFTP 패널은 이미 {(was ? "열려" : "닫혀")} 있습니다.";
                // 패널 닫기는 그 탭 터미널로 포커스를 옮긴다 — 진행 중인 RDP 입력의 포커스를 빼앗지 않게 입력이 끝난 뒤, 포커스 되돌리기까지 잠금 안에서
                try { await _rdpInputSection.WaitAsync(McpRequest.Token); }
                catch (OperationCanceledException) { throw new McpToolException("Claude 쪽에서 요청이 취소되어 실행하지 않았습니다."); }
                try
                {
                    if (!_tabs.Contains(tab)) throw new McpToolException($"session {tab.Id} 은 기다리는 사이 닫혔습니다.");
                    ssh.ToggleSftp(want);
                    // 패널을 닫으면 ToggleSftp 가 그 탭 터미널에 포커스를 준다 — 보이지 않는 탭이면 사용자가 Claude 패널 등에 치던 글자가
                    // 숨은 원격 터미널로 가므로 포커스를 되돌린다 (보이는 탭이면 UI 버튼과 같음)
                    if (!want && !tab.IsSelected)
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (ClaudeColumn.Width.Value > 0) _claudeView?.FocusSession();
                            else if (_tabs.FirstOrDefault(t => t.IsSelected) is { } cur) cur.Session.FocusSession();
                            else ReleaseHiddenSessionFocus();
                        }, System.Windows.Threading.DispatcherPriority.Input);
                }
                finally { _rdpInputSection.Release(); }
                AuditLog.Write("toggle_sftp_panel", new[] { SessionAuditTarget(tab) }, want ? "(SFTP 패널 열기)" : "(SFTP 패널 닫기)", "실행");
                return $"session {tab.Id} 의 SFTP 패널을 {(want ? "열었습니다 (Claude 가 파일을 읽으려면 sftp_list·sftp_read 를 쓰세요)" : "닫았습니다")}.";
            }));

        yield return new McpTool("move_session",
            "세션 탭의 순서를 바꾼다 (탭을 끌어서 옮기는 것과 같음). position = 왼쪽부터 몇 번째 자리로 (1 = 맨 왼쪽, 탭 수 = 맨 오른쪽), " +
            "before_session_id = 그 탭 바로 왼쪽으로 — 둘 중 하나. 결과로 바뀐 탭 순서(session_id)를 돌려준다.",
            Schema(new
            {
                session_id = new { type = "integer", description = "옮길 탭" },
                position = new { type = "integer", description = "옮길 자리 (1부터, 왼쪽 기준)" },
                before_session_id = new { type = "integer", description = "이 탭 바로 왼쪽으로" },
            }, "session_id"),
            a => OnUi(() =>
            {
                var tab = RequireSession(a, allowCurrent: false);
                var position = StrictIntArg(a, "position");
                var beforeId = StrictIntArg(a, "before_session_id");
                int? beforeIndex = null;
                if (beforeId is { } bid)
                {
                    var before = _tabs.FirstOrDefault(t => t.Id == bid) ?? throw new McpToolException($"before_session_id {bid} 탭이 없습니다. list_sessions 로 확인하세요.");
                    beforeIndex = _tabs.IndexOf(before);
                }
                int to;
                try { to = McpSessionLogic.MoveTargetIndex(_tabs.Count, _tabs.IndexOf(tab), position, beforeIndex); }
                catch (ArgumentException ex) { throw new McpToolException(ex.Message); }
                if (!MoveTab(tab, to)) return $"session {tab.Id} 은 이미 그 자리({to + 1}번째)에 있습니다. 순서: {string.Join(", ", _tabs.Select(t => t.Id))}";
                AuditLog.Write("move_session", new[] { SessionAuditTarget(tab) }, $"(탭 순서 바꾸기: {to + 1}번째로)", "실행");
                return $"session {tab.Id} 을 {to + 1}번째로 옮겼습니다. 탭 순서(왼쪽부터): {string.Join(", ", _tabs.Select(t => t.Id))}";
            }));

        yield return new McpTool("set_terminal_zoom",
            "터미널 탭(SSH · 로컬 PowerShell · 쿠버네티스 파드)의 글자 크기를 그 탭만 키우거나 줄인다 (터미널에서 Ctrl+= / Ctrl+- / Ctrl+0 · Ctrl+휠과 같음). " +
            $"zoom = 설정 글꼴 크기에 더할 값으로 정하기 ({XtermView.MinZoom}~{XtermView.MaxZoom}, 0 = 기본 크기), delta = 지금 값에서 더하거나 빼기 — 둘 중 하나, 둘 다 없으면 지금 값만 알려 준다. " +
            "session_id 생략 시 현재 탭. 범위를 넘으면 끝 값으로 맞춘다. 결과로 조절값과 실제 글꼴 크기를 돌려준다.",
            Schema(new
            {
                session_id = new { type = "integer" },
                zoom = new { type = "integer", description = $"조절값 ({XtermView.MinZoom}~{XtermView.MaxZoom}, 0 = 기본)" },
                delta = new { type = "integer", description = "지금 조절값에 더할 값 (예: 2, -1)" },
            }),
            a => OnUi(() =>
            {
                var tab = RequireSession(a, allowCurrent: true);
                var term = tab.View switch { SshSessionView s => s.Terminal, LocalTerminalView l => l.Terminal, _ => null }
                           ?? throw new McpToolException($"session {tab.Id} 은 터미널 탭이 아닙니다 (RDP · 쿠버네티스 클러스터 화면은 글자 크기 조절이 없음).");
                var zoom = StrictIntArg(a, "zoom");
                var delta = StrictIntArg(a, "delta");
                int next;
                try { next = McpSessionLogic.NextZoom(term.Zoom, zoom, delta, XtermView.MinZoom, XtermView.MaxZoom); }
                catch (ArgumentException ex) { throw new McpToolException(ex.Message); }
                var font = SettingsStore.Current.FontSize;
                if (zoom == null && delta == null)
                    return $"session {tab.Id} 의 글자 크기 조절값 {term.Zoom} (글꼴 {McpSessionLogic.ZoomedFontSize(font, term.Zoom)}pt, 설정 {font}pt).";
                var before = term.Zoom;
                var applied = term.SetZoom(next);
                AuditLog.Write("set_terminal_zoom", new[] { SessionAuditTarget(tab) }, $"(글자 크기 조절: {before} → {applied})", "실행");
                return $"session {tab.Id} 의 글자 크기 조절값 {before} → {applied} (글꼴 {McpSessionLogic.ZoomedFontSize(font, applied)}pt, 설정 {font}pt)." +
                       (zoom is { } z && z != applied || delta is { } && before + delta != applied ? $" 범위({XtermView.MinZoom}~{XtermView.MaxZoom})에 맞췄습니다." : "");
            }));

        yield return new McpTool("list_closed_tabs",
            "최근에 닫은 탭 기록 (reopen_closed_tab 이 다시 열 순서, 첫 줄이 다음 차례). 호스트 · 로컬 PowerShell · 쿠버네티스 클러스터 탭. 그 사이 지운 호스트·클러스터는 빠진다.",
            Schema(new { }),
            _ => OnUi(() =>
            {
                var lines = _closedTabs.Items.Reverse()
                    .Select(e => McpSessionLogic.ClosedTabLine(e, id => _hosts.FirstOrDefault(h => h.Id == id), cid => K8sStore.Get(cid)?.Name))
                    .Where(l => l != null).ToList();
                if (lines.Count == 0) return "다시 열 수 있는 닫은 탭이 없습니다.";
                return "다시 열 순서 (위가 먼저):\n" + string.Join("\n", lines);
            }));

        yield return new McpTool("reopen_closed_tab",
            "마지막으로 닫은 탭을 같은 호스트·같은 주소(공인/사설)로 다시 연다 (Ctrl+Shift+R 과 같음). 접속은 open_session 처럼 저장된 계정으로 시작되고, " +
            "비밀번호·OTP·호스트키 확인은 사용자가 답한다.",
            Schema(new { }),
            _ => OnUiAsync(() => UnderRdpInputSectionAsync(() =>   // 새 탭이 선택되며 진행 중인 RDP 입력을 끊지 않게
            {
                if (!CanReopenClosedTab) return "다시 열 수 있는 닫은 탭이 없습니다.";
                var before = _tabs.Count;
                ReopenClosedTab();
                var tab = _tabs.Count > before ? _tabs[^1] : null;
                if (tab == null) return "닫은 탭을 다시 열지 못했습니다.";
                AuditLog.Write("reopen_closed_tab", new[] { SessionAuditTarget(tab) }, $"(닫은 탭 다시 열기: session {tab.Id})", "실행");
                return $"닫은 탭을 다시 열었습니다: session_id={tab.Id}, {tab.Title}. 연결 결과는 list_sessions 로 확인하세요.";
            })));
    }

    /// <summary>close_sessions: 고른 탭에 연결된 탭이 있으면 그 목록으로 승인 한 번, 승인 사이 바뀐 탭은 다시 확인</summary>
    private async System.Threading.Tasks.Task<string> CloseSessionsAsync(JsonElement a)
    {
        var filter = Arg(a, "filter").Trim().ToLowerInvariant();
        if (!McpSessionLogic.CloseFilters.Contains(filter))
            throw new McpToolException($"filter 는 {string.Join(" / ", McpSessionLogic.CloseFilters)} 중 하나여야 합니다.");
        var anchor = filter == "disconnected" ? null : RequireSession(a, allowCurrent: true);
        var closing = McpSessionLogic.PickTabsToClose(_tabs, anchor, filter, t => t.Status == SessionStatus.Disconnected);
        if (closing.Count == 0) return "닫을 탭이 없습니다.";

        var live = closing.Where(t => t.Status != SessionStatus.Disconnected).ToList();
        var editing = closing.Sum(SftpEditingCount);
        var targets = closing.Select(SessionAuditTarget).ToList();
        var command = $"(탭 {closing.Count}개 닫기: {filter})";
        if (live.Count > 0 || editing > 0)
        {
            // 연결된 탭을 닫으면 SSH 는 원격 포그라운드 프로세스에 SIGHUP, 로컬은 프로세스 종료 — 끊어질 탭만 승인 창에 나열
            var desc = live.Count > 0
                ? L.F("탭 {0}개를 닫습니다. 아래 연결된 탭 {1}개는 연결을 끊으며, 터미널에서 실행 중인 프로그램은 종료될 수 있습니다.", closing.Count, live.Count)
                : L.F("끊긴 탭 {0}개를 닫습니다.", closing.Count);
            if (editing > 0) desc += "\n" + L.F("SFTP 로 편집 중인 파일 {0}개는 닫은 뒤 저장해도 서버에 올라가지 않습니다.", editing);
            // 연결된 탭 + SFTP 편집 중인 파일이 있는 탭 (끊긴 탭이라도 편집 중이면 어느 탭인지 보이게)
            var shown = (live.Count > 0 ? closing.Where(t => t.Status != SessionStatus.Disconnected || SftpEditingCount(t) > 0) : closing)
                .Select(SessionAuditTarget).ToList();
            if (!await AskApprovalAsync("close_sessions", L.T("Claude 가 세션 탭 여러 개 닫기를 요청합니다"), desc, shown, L.T("(탭 닫기)")))
            {
                AuditLog.Write("close_sessions", targets, command, "거부");
                throw new McpToolException("사용자가 탭 닫기를 거부했습니다.");
            }
        }
        // 승인 창이 떠 있는 동안 닫혔거나, 끊겨 있다가 다시 연결된(승인 목록에 없던) 탭은 건드리지 않는다
        var approvedLive = new HashSet<SessionTab>(live);
        var now = closing.Where(t => _tabs.Contains(t) && (approvedLive.Contains(t) || t.Status == SessionStatus.Disconnected)).ToList();
        var skipped = closing.Count - now.Count;
        if (now.Count > 0) CloseTabsNow(now);
        AuditLog.Write("close_sessions", now.Select(SessionAuditTarget), command, live.Count > 0 || editing > 0 ? "승인·실행" : "실행(끊긴 탭)");
        var sb = new StringBuilder($"탭 {now.Count}개를 닫았습니다: {string.Join(", ", now.Select(t => t.Id))}.");
        if (skipped > 0) sb.Append($" {skipped}개는 그 사이 닫혔거나 다시 연결돼 건너뛰었습니다.");
        return sb.ToString();
    }

    /// <summary>session_id 로 탭을 찾는다. 값이 잘못됐으면 현재 탭으로 넘어가지 않고 오류 (닫기 등에서 엉뚱한 탭을 고르지 않게)</summary>
    private SessionTab RequireSession(JsonElement a, bool allowCurrent)
    {
        var has = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("session_id", out var v) && v.ValueKind != JsonValueKind.Null;
        var id = IntArg(a, "session_id");
        if (has && id == null) throw new McpToolException("session_id 는 숫자여야 합니다. list_sessions 로 확인하세요.");
        if (id == null && !allowCurrent) throw new McpToolException("session_id 가 필요합니다. list_sessions 로 확인하세요.");
        var tab = id == null ? _tabs.FirstOrDefault(t => t.IsSelected) : _tabs.FirstOrDefault(t => t.Id == id);
        return tab ?? throw new McpToolException(id == null
            ? "지금 보이는 세션 탭이 없습니다(홈 화면). session_id 를 지정하세요."
            : "세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
    }

    /// <summary>정수 인자 — 없거나 null 이면 null, 있는데 정수가 아니면 오류 (잘못된 값을 '없음'으로 보고 다른 동작을 하지 않게)</summary>
    private static int? StrictIntArg(JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (McpHelpers.NumberArg(a, name) is not { } d || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
            throw new McpToolException($"{name} 는 정수여야 합니다.");
        return (int)d;
    }

    /// <summary>감사 로그·승인 창용 탭 표시 (보이지 않는 문자는 드러낸다, 비밀번호 없음)</summary>
    private static string SessionAuditTarget(SessionTab t) =>
        $"session {t.Id}: {McpHelpers.VisibleControlChars(t.Title)}" + (t.Host != null ? $" ({t.Host.Address})" : "");

    /// <summary>SFTP 패널이 열려 있는가 — 패널은 처음 열 때 만들어지고, 닫으면 Collapsed 로 남는다 (SshSessionView.ToggleSftp)</summary>
    private static bool IsSftpPanelOpen(SshSessionView ssh) =>
        FindSftpPanels(ssh).Any(p => p.Visibility == Visibility.Visible);
}

/// <summary>세션 탭 MCP 도구의 UI 와 무관한 판단 (테스트용)</summary>
internal static class McpSessionLogic
{
    public const int MaxTitleLength = 100;
    public static readonly string[] CloseFilters = { "others", "right", "disconnected" };
    private static readonly char[] TitleForbidden = { '(', ')', '\uFF08', '\uFF09' };

    /// <summary>탭 제목: 앞뒤 공백 제거, 빈 이름·보이지 않는 문자·제어 문자·너무 긴 이름은 ArgumentException (승인 창에 그대로 나오므로)</summary>
    public static string NormalizeTitle(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) throw new ArgumentException("탭 이름이 비어 있습니다.");
        if (McpHelpers.HasInvisibleChars(t)) throw new ArgumentException("탭 이름에 보이지 않는 문자·제어 문자는 쓸 수 없습니다.");
        // 승인 창 대상은 "session N: 제목 (주소)" — 제목에 괄호를 넣어 다른 주소를 꾸미지 못하게 (전각 괄호 포함)
        if (t.IndexOfAny(TitleForbidden) >= 0) throw new ArgumentException("탭 이름에 괄호는 쓸 수 없습니다 (승인 창의 주소 표시와 헷갈리지 않게).");
        if (t.Length > MaxTitleLength) throw new ArgumentException($"탭 이름은 {MaxTitleLength}자 이하여야 합니다.");
        return t;
    }

    /// <summary>탭 메뉴와 같은 기준으로 닫을 탭: others = anchor 말고 모두, right = anchor 오른쪽, disconnected = 끊긴 탭 모두</summary>
    public static List<T> PickTabsToClose<T>(IReadOnlyList<T> all, T? anchor, string filter, Func<T, bool> isDisconnected) where T : class
    {
        switch (filter)
        {
            case "disconnected":
                return all.Where(isDisconnected).ToList();
            case "others":
                if (anchor == null) throw new ArgumentNullException(nameof(anchor));
                return all.Where(t => !ReferenceEquals(t, anchor)).ToList();
            case "right":
                if (anchor == null) throw new ArgumentNullException(nameof(anchor));
                var at = -1;
                for (var i = 0; i < all.Count; i++) if (ReferenceEquals(all[i], anchor)) { at = i; break; }
                return at < 0 ? new List<T>() : all.Skip(at + 1).ToList();
            default:
                throw new ArgumentException("unknown filter: " + filter);
        }
    }

    /// <summary>
    /// move_session: 옮긴 뒤의 인덱스(ObservableCollection.Move 의 newIndex). position 은 1부터(왼쪽 기준),
    /// beforeIndex 는 그 탭의 지금 인덱스 — 그 탭 바로 왼쪽으로. 둘 중 정확히 하나. 잘못되면 ArgumentException
    /// </summary>
    public static int MoveTargetIndex(int count, int from, int? position, int? beforeIndex)
    {
        if (from < 0 || from >= count) throw new ArgumentException("옮길 탭을 찾을 수 없습니다.");
        if ((position == null) == (beforeIndex == null)) throw new ArgumentException("position 과 before_session_id 중 하나만 주세요.");
        if (position is { } p)
        {
            if (p < 1 || p > count) throw new ArgumentException($"position 은 1~{count} 사이여야 합니다 (탭 {count}개).");
            return p - 1;
        }
        var b = beforeIndex!.Value;
        if (b < 0 || b >= count) throw new ArgumentException("before_session_id 탭을 찾을 수 없습니다.");
        if (b == from) throw new ArgumentException("before_session_id 가 옮길 탭과 같습니다.");
        // 왼쪽에서 오른쪽으로 옮기면 빠진 자리만큼 앞당겨진다
        return from < b ? b - 1 : b;
    }

    /// <summary>set_terminal_zoom: 다음 조절값 (zoom = 절대값, delta = 더할 값, 둘 다 없으면 지금 값). 범위로 자른다. 둘 다 주면 ArgumentException</summary>
    public static int NextZoom(int current, int? zoom, int? delta, int min, int max)
    {
        if (zoom != null && delta != null) throw new ArgumentException("zoom 과 delta 중 하나만 주세요.");
        var next = zoom ?? (delta is { } d ? (long)current + d : current);
        return (int)Math.Clamp(next, min, max);
    }

    /// <summary>터미널에 실제로 쓰이는 글꼴 크기 (terminal.html: Math.max(6, 설정 + 조절값))</summary>
    public static int ZoomedFontSize(int baseSize, int zoom) => Math.Max(6, baseSize + zoom);

    /// <summary>
    /// list_closed_tabs 의 한 줄 (TabHistory 의 기록 하나). 쿠버네티스 클러스터 탭은 HostId 가 비어 있으므로 먼저 본다 —
    /// 그 사이 지운 호스트·클러스터는 null (다시 열 때도 건너뛰므로 목록에서 뺀다)
    /// </summary>
    public static string? ClosedTabLine(ClosedTabHistory.Entry e, Func<Guid, HostEntry?> findHost, Func<Guid, string?> clusterName)
    {
        if (e.K8sClusterId is { } cid)
            return clusterName(cid) is { } name ? $"쿠버네티스 클러스터 | {McpHelpers.VisibleControlChars(name)}" : null;
        if (e.HostId is not { } id) return "로컬 PowerShell";
        if (findHost(id) is not { } h) return null;
        return $"{h.Id.ToString()[..8]} | {McpHelpers.VisibleControlChars(h.DisplayName)} | {h.ProtocolText.ToLowerInvariant()}" +
               (e.UsePublic is { } pub ? (pub ? " | 공인 주소" : " | 사설 주소") : "");
    }
}
