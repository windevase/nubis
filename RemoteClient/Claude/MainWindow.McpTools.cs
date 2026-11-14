using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Nubis;

/// <summary>
/// Claude 패널 MCP 도구 (2차): Vault 목록, 호스트 추가·수정·삭제, 접속 진단, 출력 기다리기, SFTP, 클라우드 동기화.
/// 보안 원칙:
/// - 호스트 추가·수정·삭제는 항상 승인 (주소를 바꾸고 Vault 계정을 붙여 open_session 하면 계정이 다른 서버로 갈 수 있음)
/// - Claude 는 비밀번호를 넣거나 읽지 못한다 (Vault 계정 연결만)
/// - 원격 파일 읽기는 run_command 와 같은 규칙(승인, 설정 시 읽기 전용 자동 승인), 파일 전송은 항상 승인
/// </summary>
public partial class MainWindow
{
    // =================== MCP 도구 ===================

    /// <summary>UI 스레드에서 실행하고 결과 반환 (MCP 요청은 백그라운드 스레드에서 들어옴)</summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> f) => Dispatcher.InvokeAsync(f).Task.Unwrap();
    private Task<T> OnUi<T>(Func<T> f) => Dispatcher.InvokeAsync(f).Task;

    private static string Arg(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // 숫자 인자는 숫자 문자열("10")도 받고, 잘못된 값이면 예외 없이 null (McpHelpers.NumberArg)
    private static int? IntArg(JsonElement a, string name) =>
        McpHelpers.NumberArg(a, name) is { } d ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : null;

    private static double? NumArg(JsonElement a, string name) => McpHelpers.NumberArg(a, name);

    private static bool? BoolArg(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static List<string> ListArg(JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return new();
        if (v.ValueKind == JsonValueKind.String) return new() { v.GetString() ?? "" };
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new();
    }

    private string GroupLabel(HostEntry h)
    {
        if (h.CloudProfileId is { } id && _profiles.FirstOrDefault(p => p.Id == id) is { } p) return $"[{p.ProviderText}] {p.GroupName}";
        return string.IsNullOrWhiteSpace(h.Group) ? "Hosts" : h.Group;
    }

    private string HostLine(HostEntry h) =>
        $"{h.Id.ToString()[..8]} | {h.DisplayName} | {GroupLabel(h)} | {h.ProtocolText.ToLowerInvariant()} {h.PrimaryAddress}:{h.Port}" +
        (h.HasPublicIp ? $" (공인 {h.PublicIp}{(h.UsePublicIp ? ", 기본 접속" : "")})" : "") +
        $" | {(h.IsCloud ? h.CloudState ?? "" : "-")} | {h.Purpose ?? ""} | {h.Os ?? ""}";

    /// <summary>
    /// 호스트 지정 해석: "group:이름"(그룹·클라우드 프로필 전체), ID(앞 8자리 가능), 정확한 이름, IP, 그다음 부분 일치.
    /// </summary>
    private List<HostEntry> ResolveHosts(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) return new();

        if (spec.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
        {
            var g = spec[6..].Trim();
            // "[AWS] IM" 처럼 정확히 쓴 그룹이 있으면 그것만, 없을 때만 "IM" 으로 끝나는 그룹(여러 클라우드 가능)
            var exactGroup = _hosts.Where(h => GroupLabel(h).Equals(g, StringComparison.OrdinalIgnoreCase)).ToList();
            return exactGroup.Count > 0 ? exactGroup
                : _hosts.Where(h => GroupLabel(h).EndsWith("] " + g, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var exact = _hosts.Where(h =>
            h.Id.ToString().StartsWith(spec, StringComparison.OrdinalIgnoreCase) && spec.Length >= 8 ||
            h.DisplayName.Equals(spec, StringComparison.OrdinalIgnoreCase) ||
            h.Address.Equals(spec, StringComparison.OrdinalIgnoreCase) ||
            spec.Equals(h.PrivateIp, StringComparison.OrdinalIgnoreCase) ||
            spec.Equals(h.PublicIp, StringComparison.OrdinalIgnoreCase) ||
            spec.Equals(h.CloudInstanceId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;

        return _hosts.Where(h => h.DisplayName.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private SessionTab? FindSession(JsonElement a)
    {
        var id = IntArg(a, "session_id");
        return id == null ? _tabs.FirstOrDefault(t => t.IsSelected) : _tabs.FirstOrDefault(t => t.Id == id);
    }

    private static object Schema(object properties, params string[] required) =>
        new { type = "object", properties, required };

    private IEnumerable<McpTool> BuildTools()
    {
        yield return new McpTool("list_groups",
            "등록된 호스트 그룹 목록(직접 등록 그룹 + 클라우드 프로필 그룹)과 서버 수를 반환한다.",
            Schema(new { }),
            _ => OnUi(() =>
            {
                var sb = new StringBuilder("그룹 | 서버 수 | 실행 중 | SSH | RDP\n");
                foreach (var g in _hosts.GroupBy(GroupLabel).OrderBy(g => g.Key))
                    sb.AppendLine($"{g.Key} | {g.Count()} | {g.Count(h => !h.IsStopped)} | {g.Count(h => h.Protocol == Protocol.Ssh)} | {g.Count(h => h.Protocol == Protocol.Rdp)}");
                return sb.ToString();
            }));

        yield return new McpTool("list_hosts",
            "호스트 검색. 결과 형식: id | 이름 | 그룹 | 프로토콜 사설주소:포트 (공인 IP) | 상태 | 용도 | OS. 비밀번호는 포함하지 않는다.",
            Schema(new
            {
                query = new { type = "string", description = "이름·IP(사설/공인)·인스턴스 ID·리전·용도·OS 부분 검색어" },
                group = new { type = "string", description = "그룹 이름 (예: 'Production', '[AWS] prod')" },
                protocol = new { type = "string", @enum = new[] { "ssh", "rdp" } },
                running_only = new { type = "boolean", description = "실행 중인 서버만" },
                limit = new { type = "integer", description = "최대 개수 (기본 300)" },
            }),
            a => OnUi(() =>
            {
                var q = Arg(a, "query");
                var group = Arg(a, "group");
                var proto = Arg(a, "protocol");
                IEnumerable<HostEntry> list = _hosts;
                if (q.Length > 0)
                    list = list.Where(h => new[] { h.DisplayName, h.Address, h.PrivateIp, h.PublicIp, h.CloudInstanceId, h.CloudRegion, h.Purpose, h.Os, h.Username }
                        .Any(s => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true));
                if (group.Length > 0)
                    list = list.Where(h => GroupLabel(h).Contains(group, StringComparison.OrdinalIgnoreCase));
                if (proto.Length > 0)
                    list = list.Where(h => h.ProtocolText.Equals(proto, StringComparison.OrdinalIgnoreCase));
                if (BoolArg(a, "running_only") == true) list = list.Where(h => !h.IsStopped);
                var all = list.OrderBy(GroupLabel).ThenBy(h => h.DisplayName).ToList();
                var limit = IntArg(a, "limit") ?? 300;
                var sb = new StringBuilder($"{all.Count}개" + (all.Count > limit ? $" (앞 {limit}개만 표시)" : "") + "\n");
                foreach (var h in all.Take(limit)) sb.AppendLine(HostLine(h));
                return sb.ToString();
            }));

        yield return new McpTool("list_sessions",
            "앱에 열려 있는 세션 탭 목록. active=현재 사용자가 보고 있는 탭.",
            Schema(new { }),
            _ => OnUi(() =>
            {
                if (_tabs.Count == 0) return "열린 세션이 없습니다.";
                var sb = new StringBuilder("session_id | 제목 | 종류 | 호스트 | 상태 | active\n");
                foreach (var t in _tabs)
                    sb.AppendLine($"{t.Id} | {t.Title} | {t.Protocol.ToString().ToLowerInvariant()} | " +
                                  $"{(t.Host != null ? $"{t.Host.Address}:{t.Host.Port}" : "local")} | {t.Status} | {(t.IsSelected ? "active" : "")}");
                return sb.ToString();
            }));

        yield return new McpTool("open_session",
            "호스트에 SSH 또는 RDP 세션 탭을 열고 앱 화면에 표시한다. host 는 이름, IP, id 중 하나. 이름이 여러 개 일치하면 후보를 돌려준다. " +
            "바로 send_to_terminal 을 쓸 거면 wait_connected_sec 로 연결이 끝날 때까지 기다린다 (연결 중인 탭에는 입력할 수 없음).",
            Schema(new
            {
                host = new { type = "string", description = "호스트 이름, IP 또는 id" },
                address = new { type = "string", @enum = new[] { "default", "private", "public" }, description = "접속 주소: 호스트 설정(기본) / 사설 IP / 공인 IP(EIP)" },
                wait_connected_sec = new { type = "number", description = "연결 완료(또는 실패)까지 기다릴 최대 초 (기본 0 = 기다리지 않음, 최대 60)" },
            }, "host"),
            a => OnUiAsync(async () =>
            {
                var ct = McpRequest.Token;
                var waitConnected = Math.Clamp(NumArg(a, "wait_connected_sec") ?? 0, 0, 60);
                var spec = Arg(a, "host");
                var found = ResolveHosts(spec);
                if (found.Count == 0) throw new McpToolException($"'{spec}' 와 일치하는 호스트가 없습니다. list_hosts 로 검색하세요.");
                if (found.Count > 1)
                    throw new McpToolException($"'{spec}' 와 일치하는 호스트가 {found.Count}개입니다. 하나를 id 로 지정하세요.\n" +
                                               string.Join("\n", found.Take(15).Select(HostLine)));
                var mode = Arg(a, "address");
                if (mode == "public" && !found[0].HasPublicIp) throw new McpToolException($"'{found[0].DisplayName}' 에는 공인 IP 가 없습니다.");
                var tab = Connect(found[0], mode switch { "public" => true, "private" => false, _ => null });
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                var opened = $"세션을 열었습니다: session_id={tab.Id}, {found[0].DisplayName} ({found[0].ProtocolText} {found[0].Address}:{found[0].Port}).";
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
            }));

        yield return new McpTool("read_terminal",
            "세션 터미널 화면의 마지막 N 줄을 읽는다. session_id 를 생략하면 사용자가 보고 있는 탭. RDP 세션은 읽을 수 없다.",
            Schema(new
            {
                session_id = new { type = "integer" },
                lines = new { type = "integer", description = "읽을 줄 수 (기본 80, 최대 5000)" },
            }),
            a => OnUiAsync(async () =>
            {
                var tab = FindSession(a) ?? throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
                var text = await tab.Session.ReadTerminalAsync(IntArg(a, "lines") ?? 80)
                           ?? throw new McpToolException($"'{tab.Title}' 은 RDP 세션이라 글자로 읽을 수 없습니다. capture_screen 으로 화면을 캡처해 보세요.");
                AuditLog.Write("read_terminal", new[] { $"session {tab.Id}: {tab.Title}" }, $"(화면 {text.Split('\n').Length}줄 읽음)", "조회");
                return $"[session {tab.Id}: {tab.Title}]\n{text}";
            }));

        yield return new McpTool("get_terminal_info",
            "SSH/로컬 터미널의 현재 상태를 명령 없이 바로 알려준다: 현재 폴더(셸의 OSC 7 또는 프롬프트에서 앱이 추적 중인 값), 로그인 계정, " +
            "현재 셸 사용자(sudo su - 등으로 바뀌었는지), 셸 프롬프트가 보이는지(명령 실행 중·vim·비밀번호 질문 화면 여부), SFTP 패널 권한. " +
            "'지금 폴더 어디야', '누구로 로그인돼 있어' 같은 질문에는 pwd/whoami 를 send_to_terminal 로 보내지 말고 이 도구를 먼저 쓴다. " +
            "session_id 생략 시 사용자가 보고 있는 탭.",
            Schema(new { session_id = new { type = "integer" } }),
            a => OnUiAsync(async () =>
            {
                var tab = FindSession(a) ?? throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
                var info = await tab.Session.GetTerminalInfoAsync()
                           ?? throw new McpToolException($"'{tab.Title}' 은 RDP 세션이라 터미널 정보가 없습니다. capture_screen 으로 화면을 보세요.");
                AuditLog.Write("get_terminal_info", new[] { $"session {tab.Id}: {tab.Title}" }, "(터미널 상태 조회)", "조회");
                return $"[session {tab.Id}: {tab.Title}]\n{info}";
            }));

        yield return new McpTool("get_selection",
            "사용자가 터미널에서 드래그로 선택한 텍스트를 가져온다. session_id 생략 시 현재 탭.",
            Schema(new { session_id = new { type = "integer" } }),
            a => OnUiAsync(async () =>
            {
                var tab = FindSession(a) ?? throw new McpToolException("세션을 찾을 수 없습니다.");
                var sel = await tab.Session.GetSelectionAsync();
                AuditLog.Write("get_selection", new[] { $"session {tab.Id}: {tab.Title}" }, $"(선택 {sel?.Length ?? 0}자 읽음)", "조회");
                return string.IsNullOrEmpty(sel) ? "선택된 텍스트가 없습니다." : sel;
            }));

        yield return new McpTool("close_session",
            "세션 탭을 닫는다 (SSH/RDP 연결 종료, 로컬 터미널 종료). Claude 가 연 탭을 정리할 때 사용. " +
            "연결 중인 탭은 닫으면 실행 중인 프로그램이 끝날 수 있어 앱이 사용자 승인을 받는다 (끊긴 탭은 바로 닫음).",
            Schema(new { session_id = new { type = "integer" } }, "session_id"),
            a => OnUi(() =>
            {
                var tab = FindSession(a) ?? throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
                var title = tab.Title;
                var target = $"session {tab.Id}: {title}" + (tab.Host != null ? $" ({tab.Host.Address})" : "");
                // 연결된 탭을 닫으면 SSH 는 원격 포그라운드 프로세스에 SIGHUP, 로컬은 프로세스 종료 — 항상 승인 (사용자의 탭 닫기 확인 설정과 무관)
                var live = tab.Status != SessionStatus.Disconnected;
                if (live && !AskApproval("close_session", L.T("Claude 가 세션 탭 닫기를 요청합니다"),
                        L.T("연결을 끊고 탭을 닫습니다. 터미널에서 실행 중인 프로그램은 종료될 수 있습니다."), new[] { target }, L.T("(탭 닫기)")))
                {
                    AuditLog.Write("close_session", new[] { target }, "(탭 닫기)", "거부");
                    throw new McpToolException("사용자가 탭 닫기를 거부했습니다.");
                }
                if (!_tabs.Contains(tab)) return $"session {tab.Id} ({title}) 은 이미 닫혀 있습니다.";
                CloseTab(tab);
                AuditLog.Write("close_session", new[] { target }, "(탭 닫기)", live ? "승인·실행" : "실행(끊긴 탭)");
                return $"session {tab.Id} ({title}) 을 닫았습니다.";
            }));

        yield return new McpTool("diagnostics",
            "Nubis 자체 상태 진단: 메모리(전체 GC 후 관리 힙), 프로세스 핸들·스레드 수, 열린 세션 수. 성능·누수 점검용.",
            Schema(new { }),
            _ => OnUi(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                p.Refresh();
                return $"managed_heap_mb={GC.GetTotalMemory(false) / 1048576.0:0.0} private_mb={p.PrivateMemorySize64 / 1048576.0:0.0} " +
                       $"working_set_mb={p.WorkingSet64 / 1048576.0:0.0} handles={p.HandleCount} threads={p.Threads.Count} " +
                       $"gdi={GetGuiResources(p.Handle, 0)} user={GetGuiResources(p.Handle, 1)} " +
                       $"sessions={_tabs.Count} ssh={_tabs.Count(t => t.Protocol == Protocol.Ssh)} rdp={_tabs.Count(t => t.Protocol == Protocol.Rdp)} " +
                       $"local={_tabs.Count(t => t.Protocol == Protocol.Local)} hosts={_hosts.Count}";
            }));

        foreach (var t in ExtraTools()) yield return t;
        foreach (var t in SetupTools()) yield return t;

        yield return new McpTool("get_settings",
            "Nubis 앱 설정(터미널 테마·글꼴·구문 강조·기본 포트·보안 옵션)의 현재 값과 바꿀 수 있는 값 범위를 보여준다.",
            Schema(new { }),
            _ => OnUi(() => SettingsCatalog.Describe(SettingsStore.Current)));

        yield return new McpTool("update_settings",
            "Nubis 앱 설정을 바꾼다. changes 에 {\"키\": 값} 형식 (키는 get_settings 참고, 여러 개 한 번에 가능). " +
            "화면 설정은 바로 적용되고, 보안 설정(RdpCertWarning, SshHostKeyPrompt, AutoApproveReadOnly)은 사용자 승인 창을 거친다. " +
            "AutoApproveReadOnly 는 끄기만 가능하다. 값이 하나라도 잘못되면 아무것도 바꾸지 않는다.",
            Schema(new
            {
                changes = new { type = "object", description = "예: {\"TerminalTheme\": \"dracula\", \"FontSize\": 15}", additionalProperties = true },
            }, "changes"),
            a => OnUi(() => UpdateSettingsFromClaude(a)));

        yield return new McpTool("capture_screen",
            "세션 탭 화면을 캡처해 이미지로 반환한다. RDP(Windows 원격 데스크톱) 화면을 볼 때 사용 — 에러 창, 설치 화면, 서비스 관리자 등. " +
            "SSH 터미널 내용은 read_terminal 이 더 정확하다. session_id 생략 시 현재 탭. 대상 탭이 가려져 있으면 그 탭으로 전환한 뒤 캡처한다.",
            Schema(new
            {
                session_id = new { type = "integer" },
                max_width = new { type = "integer", description = "이미지 최대 가로 픽셀 (기본 1600, 작을수록 토큰 절약)" },
            }),
            a => OnUiAsync(async () => (object)await CaptureSessionAsync(FindSession(a), IntArg(a, "max_width") ?? 1600)));

        yield return new McpTool("run_command",
            "여러 서버에 같은 명령을 병렬 실행하고 서버별 출력·종료 코드를 반환한다. " +
            "리눅스(SSH 호스트)는 SSH 로 셸 명령을, Windows(RDP 호스트)는 WinRM(5986 HTTPS 우선, 실패 시 5985)으로 PowerShell(기본) 또는 cmd 명령을 실행한다. " +
            "리눅스와 Windows 는 명령 문법이 다르므로 섞지 말고 OS 별로 나눠 호출할 것 (list_hosts 의 프로토콜 ssh/rdp 로 구분). " +
            "targets 항목은 호스트 이름·IP·id 또는 'group:그룹이름'(그룹 전체). 중지된 서버는 제외된다. " +
            "실행 전에 앱이 사용자 승인 창을 띄우며, 거부되면 실행하지 않는다. 비대화형이므로 sudo 비밀번호 입력·확인 프롬프트 등 대화형 명령은 쓰지 말 것.",
            Schema(new
            {
                targets = new { type = "array", items = new { type = "string" }, description = "대상 호스트 목록" },
                command = new { type = "string", description = "실행할 명령 (리눅스: sh, Windows: PowerShell 또는 cmd)" },
                windows_shell = new { type = "string", @enum = new[] { "powershell", "cmd" }, description = "Windows 대상 명령 해석기 (기본 powershell)" },
                timeout_sec = new { type = "integer", description = "서버당 제한 시간 (기본 60초)" },
            }, "targets", "command"),
            RunCommandAsync);

        yield return new McpTool("send_to_terminal",
            "열려 있는 SSH/로컬 세션의 대화형 터미널에 텍스트를 입력한다 (사용자가 보는 화면에 그대로 타이핑). " +
            "실행 전에 앱이 사용자 승인을 받는다 — SSH 세션에서 사용자가 '읽기 전용 명령은 승인 없이 실행' 을 켰고, 화면이 셸 프롬프트이고, 읽기 전용 명령이면 승인 없이 입력한다. " +
            "로컬 PowerShell 탭(이 PC)은 이 PC 의 파일을 읽을 수 있으므로 읽기 전용 명령도 항상 승인을 받는다. " +
            "연결 중(Connecting)이거나 끊긴 탭에는 입력할 수 없다 — open_session 의 wait_connected_sec 로 연결을 기다릴 것. " +
            "현재 폴더·사용자는 이 도구 대신 get_terminal_info 로 확인할 것. " +
            "Enter 로 실행하면 셸 프롬프트가 다시 나올 때까지(최대 wait_sec, 기본 10초) 기다려 출력을 함께 돌려준다 — 따로 wait_for_output 을 부를 필요 없음. " +
            "입력 전 화면이 앱이 알아보는 셸 프롬프트였으면 프롬프트가 돌아와야 끝난 것으로 보고, 아니면 출력이 1.5초 멈출 때 돌려준다. " +
            "그 안에 끝나지 않으면 '아직 실행 중' 과 현재 화면을 돌려주니 그때 wait_for_output 으로 이어서 기다린다 (같은 명령을 다시 보내지 말 것).",
            Schema(new
            {
                session_id = new { type = "integer", description = "생략 시 현재 탭" },
                text = new { type = "string" },
                press_enter = new { type = "boolean", description = "끝에 Enter 입력 (기본 true)" },
                wait_sec = new { type = "number", description = "프롬프트가 돌아올 때까지 기다릴 최대 초 (기본 10, 0 이면 기다리지 않음, 최대 120)" },
                lines = new { type = "integer", description = "돌려줄 출력 줄 수 (기본 60)" },
            }, "text"),
            a => OnUiAsync(async () =>
            {
                // 인자는 입력 전에 모두 읽는다 — 입력 뒤에 인자 오류로 실패하면 Claude 가 다시 보내 명령이 두 번 실행될 수 있다
                var ct = McpRequest.Token;
                var text = Arg(a, "text");
                var enter = BoolArg(a, "press_enter") != false;
                var waitSec = Math.Clamp(NumArg(a, "wait_sec") ?? 10, 0, 120);
                var lines = Math.Clamp(IntArg(a, "lines") ?? 60, 1, 2000);
                var tab = FindSession(a) ?? throw new McpToolException("세션을 찾을 수 없습니다.");
                if (tab.Protocol == Protocol.Rdp) throw new McpToolException("RDP 세션에는 입력할 수 없습니다.");
                // 연결 중인 탭에 승인 창부터 띄우면 승인 뒤 입력이 실패한다 — 먼저 거른다
                if (tab.Status == SessionStatus.Connecting)
                    throw new McpToolException($"session {tab.Id} 은 아직 연결 중입니다. open_session 의 wait_connected_sec 로 기다리거나 list_sessions 로 Connected 를 확인한 뒤 보내세요.");
                if (tab.Status != SessionStatus.Connected)
                    throw new McpToolException($"session {tab.Id} 은 연결이 끊겨 있어 입력할 수 없습니다.");
                var target = $"session {tab.Id}: {tab.Title}" + (tab.Host != null ? $" ({tab.Host.Address})" : "");
                var shell = tab.Protocol == Protocol.Local ? CommandShell.PowerShell : CommandShell.Sh;
                var atPromptBefore = await tab.Session.IsAtPromptAsync();
                // 읽기 전용 자동 승인: 설정을 켰고 + 원격(SSH) 탭이고 + 셸 프롬프트가 보이고(비밀번호 질문·vim 등에 들어가지 않게) + 한 줄 읽기 전용 명령 + Enter.
                // 로컬 탭은 gc ~\.ssh\id_rsa 같은 '읽기 전용' 명령으로 이 PC 의 비밀 파일을 화면에 띄워 read_terminal 로 읽을 수 있어 제외
                var auto = McpHelpers.CanAutoApproveTerminalInput(SettingsStore.Current.AutoApproveReadOnly, text, enter,
                    atPromptBefore, t => ReadOnlyCommands.IsReadOnly(t, shell), localTerminal: tab.Protocol == Protocol.Local);
                if (!auto && !AskApproval("send_to_terminal", L.T("Claude 가 터미널 입력을 요청합니다"),
                        L.T("아래 세션의 터미널에 텍스트를 입력합니다."), new[] { target }, text))
                {
                    AuditLog.Write("send_to_terminal", new[] { target }, text, "거부");
                    throw new McpToolException("사용자가 입력을 거부했습니다.");
                }
                if (!_tabs.Contains(tab)) throw new McpToolException("승인을 기다리는 동안 탭이 닫혀 입력하지 않았습니다.");
                var before = await tab.Session.ReadTerminalAsync(200) ?? "";
                var ok = tab.Session.SendText(text + (enter ? "\r" : ""));
                AuditLog.Write("send_to_terminal", new[] { target }, text,
                    ok ? (auto ? "자동 승인(읽기 전용)·입력" : "승인·입력") : "실패(세션 끊김)");
                if (!ok) throw new McpToolException("세션이 연결되어 있지 않아 입력하지 못했습니다.");
                SelectTab(tab);
                var head = auto ? "읽기 전용 명령이라 승인 없이 입력했습니다." : "입력했습니다.";

                // 결과까지 기다려 함께 돌려준다 (Claude 가 wait_for_output 을 따로 부르는 왕복을 줄임):
                // 화면이 바뀌고, 0.3초 동안 그대로이고, 셸 프롬프트로 돌아오면 끝.
                // 여기부터 예외·취소가 나도 입력은 이미 됐다 — 메시지에 그 사실을 밝혀 다시 보내지 않게 한다
                if (!enter || waitSec <= 0) return head + " 결과는 wait_for_output 으로 확인하세요.";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string last = before;
                var lastChange = sw.Elapsed;
                string Screen(string s) => $"\n[session {tab.Id}: {tab.Title}]\n" + string.Join("\n", s.Split('\n').TakeLast(lines));
                while (sw.Elapsed.TotalSeconds < waitSec)
                {
                    try { await Task.Delay(150, ct); }
                    catch (OperationCanceledException) { throw new McpToolException($"{head} 결과 기다리기는 요청 취소로 중단했습니다 (명령은 이미 입력됨 — 다시 보내지 말 것)."); }
                    if (!_tabs.Contains(tab)) throw new McpToolException($"{head} 기다리는 동안 탭이 닫혔습니다.");
                    var now = await tab.Session.ReadTerminalAsync(200) ?? "";
                    if (tab.Status == SessionStatus.Disconnected) return $"{head} 기다리는 동안 연결이 끊겼습니다 ({sw.Elapsed.TotalSeconds:0.0}초)." + Screen(now);
                    if (now != last) { last = now; lastChange = sw.Elapsed; continue; }
                    if (now != before && sw.Elapsed - lastChange >= TimeSpan.FromMilliseconds(300) && await tab.Session.IsAtPromptAsync())
                        return $"{head} 끝났습니다 ({sw.Elapsed.TotalSeconds:0.0}초)." + Screen(now);
                    // 입력 전에 프롬프트를 알아보지 못했으면(zsh 테마 등) 출력이 1.5초 멈출 때 돌려준다.
                    // 알아본 프롬프트였으면 프롬프트가 돌아오는 것만 끝으로 본다 (apt·docker pull·curl 처럼 중간에 조용한 명령을 끝난 것으로 오인하지 않게)
                    if (!atPromptBefore && now != before && sw.Elapsed - lastChange >= TimeSpan.FromSeconds(1.5))
                        return $"{head} 출력이 1.5초 동안 멈췄습니다 ({sw.Elapsed.TotalSeconds:0.0}초, 끝났는지는 화면으로 판단)." + Screen(now);
                }
                return $"{head} {waitSec:0.#}초 안에 셸 프롬프트로 돌아오지 않았습니다 (아직 실행 중이거나 입력을 기다리는 중). " +
                       "wait_for_output 으로 이어서 기다리세요 (같은 명령을 다시 보내지 말 것)." + Screen(last);
            }));
    }

    // =================== 설정 변경 (update_settings) ===================

    private string UpdateSettingsFromClaude(JsonElement a)
    {
        var changesArg = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("changes", out var c) ? c : default;
        List<SettingsCatalog.Change> plan;
        try { plan = SettingsCatalog.Plan(SettingsStore.Current, changesArg); }
        catch (ArgumentException ex) { throw new McpToolException(ex.Message); }
        if (plan.Count == 0) return "이미 요청한 값과 같아 바뀐 설정이 없습니다.";

        var lines = plan.Select(p => p.Text).ToList();
        var command = string.Join("\n", lines);
        if (plan.Any(p => p.Def.Security) &&
            !AskApproval("update_settings", L.T("Claude 가 보안 설정 변경을 요청합니다"),
                L.T("아래 Nubis 설정을 바꿉니다. 보안에 영향을 주는 항목이 포함되어 있습니다."), new[] { "Nubis 설정" }, command))
        {
            AuditLog.Write("update_settings", new[] { "Nubis 설정" }, command, "거부");
            throw new McpToolException("사용자가 설정 변경을 거부했습니다.");
        }

        // 기본 포트는 클라우드 호스트 포트도 함께 바꾸는 경로로, 나머지는 한 번에 저장
        var ports = plan.Where(p => SettingsCatalog.PortKeys.Contains(p.Def.Key)).ToList();
        var others = plan.Except(ports).ToList();
        if (others.Count > 0) SettingsStore.Update(s => { foreach (var p in others) p.Def.Set(s, p.New); });
        var note = "";
        if (ports.Count > 0)
        {
            int Port(string key, Protocol proto) => ports.FirstOrDefault(p => p.Def.Key == key)?.New as int? ?? HostEntry.PreferredPort(proto);
            var moved = ApplyDefaultPorts(Port(nameof(AppSettings.DefaultSshPort), Protocol.Ssh), Port(nameof(AppSettings.DefaultRdpPort), Protocol.Rdp));
            if (moved > 0) note = $"\n클라우드 호스트 {moved}개의 포트도 새 기본 포트로 바꿨습니다.";
        }
        if (SettingsPage.Visibility == Visibility.Visible) RefreshSettingsPage();
        AuditLog.Write("update_settings", new[] { "Nubis 설정" }, command, plan.Any(p => p.Def.Security) ? "승인·변경" : "변경");
        if (plan.Any(p => p.Def.Key == nameof(AppSettings.TerminalGpu))) note += "\nGPU 가속은 앱을 다시 시작해야 적용됩니다.";
        return "설정을 바꿨습니다:\n" + command + note;
    }

    // =================== MCP 도구 (2차) ===================
    private IEnumerable<McpTool> ExtraTools()
    {
        // ---------- Vault ----------
        yield return new McpTool("list_vault",
            "Vault 계정 목록 (이름 · 사용자명 · 로그인 방식 · 사용 중인 호스트 수). 비밀번호·키 내용은 절대 포함하지 않는다. " +
            "add_host / update_host 의 vault 인자에 이름을 넘겨 호스트에 연결한다.",
            Schema(new { }),
            _ => OnUi(() =>
            {
                var all = VaultStore.All();
                if (all.Count == 0) return "Vault 에 저장된 계정이 없습니다. 사용자에게 Nubis 의 Vault 메뉴에서 계정을 추가해 달라고 안내하세요.";
                var sb = new StringBuilder("id | 이름 | 사용자명 | 로그인 방식 | 사용 호스트\n");
                foreach (var v in all)
                    sb.AppendLine($"{v.Id.ToString()[..8]} | {v.Name} | {(v.Username.Length > 0 ? v.Username : "-")} | {v.MethodsText}{(v.KeyType.Length > 0 ? $" ({v.KeyType})" : "")} | {_hosts.Count(h => h.VaultId == v.Id)}");
                return sb.ToString();
            }));

        // ---------- 호스트 관리 ----------
        yield return new McpTool("add_host",
            "호스트(SSH/RDP 서버)를 새로 등록한다. 비밀번호는 넣을 수 없다 — 계정은 vault 로 Vault 계정을 연결하거나, 비워 두면 접속할 때 앱이 묻는다. " +
            "등록 전에 앱이 사용자 승인을 받는다.",
            Schema(new
            {
                address = new { type = "string", description = "IP 또는 호스트명" },
                protocol = new { type = "string", @enum = new[] { "ssh", "rdp" }, description = "기본 ssh" },
                label = new { type = "string" },
                group = new { type = "string" },
                port = new { type = "integer", description = "생략하면 설정의 기본 포트" },
                username = new { type = "string" },
                vault = new { type = "string", description = "Vault 계정 이름 또는 id" },
                public_ip = new { type = "string" },
                use_public_ip = new { type = "boolean" },
            }, "address"),
            a => OnUi(() => AddHostFromClaude(a)));

        yield return new McpTool("update_host",
            "등록된 호스트 1개의 설정을 바꾼다 (넘긴 항목만). host 는 이름·IP·id. 클라우드 동기화 호스트는 주소를 바꿀 수 없다. " +
            "비밀번호는 바꿀 수 없다. 변경 전에 앱이 사용자 승인을 받는다.",
            Schema(new
            {
                host = new { type = "string", description = "대상 호스트 (이름·IP·id, 정확히 1개여야 함)" },
                address = new { type = "string", description = "새 주소 (직접 등록한 호스트만)" },
                protocol = new { type = "string", @enum = new[] { "ssh", "rdp" } },
                label = new { type = "string" },
                group = new { type = "string", description = "그룹 (직접 등록한 호스트만)" },
                port = new { type = "integer" },
                username = new { type = "string", description = "빈 문자열 = 지우기 (Vault 계정의 사용자명을 따름)" },
                vault = new { type = "string", description = "Vault 계정 이름 또는 id (list_vault). \"none\" = 연결 해제" },
                public_ip = new { type = "string", description = "빈 문자열 = 지우기" },
                use_public_ip = new { type = "boolean" },
            }, "host"),
            a => OnUi(() => UpdateHostFromClaude(a)));

        yield return new McpTool("delete_host",
            "호스트를 목록에서 지운다 (여러 개 가능, 'group:이름' 은 허용하지 않음). 클라우드 호스트는 클라우드 서버는 그대로 두고 다음 동기화에서도 숨긴다. " +
            "삭제 전에 앱이 사용자 승인을 받는다.",
            Schema(new { hosts = new { type = "array", items = new { type = "string" }, description = "이름·IP·id 목록" } }, "hosts"),
            a => OnUi(() => DeleteHostsFromClaude(a)));

        // ---------- 접속 진단 ----------
        yield return new McpTool("test_connection",
            "등록된 호스트에 접속이 안 될 때 원인을 찾는다: 주소별(사설 → 공인) DNS 조회, TCP 포트 연결(SSH 는 서버 배너까지), RDP 호스트는 WinRM(5986/5985)도 확인. " +
            "로그인은 하지 않으며 서버를 바꾸지 않는다. ports 로 확인할 포트를 직접 지정할 수 있다(최대 10개).",
            Schema(new
            {
                host = new { type = "string", description = "호스트 이름·IP·id" },
                ports = new { type = "array", items = new { type = "integer" }, description = "확인할 포트 (생략 시 프로토콜 기본)" },
                timeout_sec = new { type = "integer", description = "포트당 제한 시간 (기본 4초)" },
            }, "host"),
            TestConnectionAsync);

        // ---------- 출력 기다리기 ----------
        yield return new McpTool("wait_for_output",
            "SSH/로컬 터미널에서 출력이 나올 때까지 기다린다. pattern 을 주면 그 문자열(또는 regex=true 면 정규식)이 새로 나타날 때까지, " +
            "생략하면 명령이 끝날 때까지 기다린 뒤 화면 마지막 lines 줄을 돌려준다 — 실행 중인 명령이 앱이 알아보는 셸 프롬프트에서 입력됐으면 프롬프트가 돌아올 때까지, " +
            "아니면(또는 idle_sec 를 직접 주면) 출력이 idle_sec 초 동안 멈출 때까지. " +
            "셸 프롬프트로 돌아와 있고 마지막 명령 출력에 pattern 이 이미 있으면 바로 돌려준다. 기다리는 중 연결이 끊기면 그 사실과 화면을 돌려준다. " +
            "send_to_terminal 이 '아직 실행 중' 으로 돌아왔을 때 이어서 기다리는 용도.",
            Schema(new
            {
                session_id = new { type = "integer", description = "생략 시 현재 탭" },
                pattern = new { type = "string", description = "기다릴 문자열 (예: '$ ', 'Complete!', 'error')" },
                regex = new { type = "boolean" },
                timeout_sec = new { type = "integer", description = "최대 대기 (기본 60, 최대 600)" },
                idle_sec = new { type = "number", description = "pattern 없을 때 출력이 멈춘 것으로 볼 시간 (기본 2초, 주면 프롬프트를 알아봐도 이 규칙을 씀)" },
                lines = new { type = "integer", description = "돌려줄 줄 수 (기본 60)" },
            }),
            WaitForOutputAsync);

        // ---------- SFTP ----------
        yield return new McpTool("sftp_list",
            "SSH 호스트의 원격 폴더 목록 (이름·종류·크기·수정 시각·권한). 승인 규칙은 run_command 와 같다(설정에 따라 읽기 전용 자동 승인).",
            Schema(new
            {
                host = new { type = "string" },
                path = new { type = "string", description = "원격 경로 (생략 시 홈)" },
            }, "host"),
            a => SftpListAsync(a));

        yield return new McpTool("sftp_read",
            "SSH 호스트의 원격 텍스트 파일을 읽는다 (로그·설정 확인용, 기본 64KB, 최대 1MB). tail=true 면 끝부분. 바이너리 파일은 거부. " +
            "승인 규칙은 run_command 와 같다.",
            Schema(new
            {
                host = new { type = "string" },
                path = new { type = "string" },
                max_bytes = new { type = "integer", description = "읽을 최대 바이트 (기본 65536, 최대 1048576)" },
                tail = new { type = "boolean", description = "파일 끝부분을 읽기 (로그)" },
            }, "host", "path"),
            a => SftpReadAsync(a));

        yield return new McpTool("sftp_download",
            "SSH 호스트의 원격 파일·폴더를 이 PC 의 다운로드\\Nubis\\<호스트> 폴더로 받는다 (경로는 앱이 정함, 기존 파일은 덮어쓰지 않음). 항상 사용자 승인.",
            Schema(new
            {
                host = new { type = "string" },
                path = new { type = "string", description = "원격 파일 또는 폴더" },
            }, "host", "path"),
            a => SftpDownloadAsync(a));

        yield return new McpTool("sftp_upload",
            "이 PC 의 파일·폴더를 SSH 호스트의 원격 폴더로 올린다. 같은 이름이 있으면 overwrite=true 일 때만 덮어쓴다. 항상 사용자 승인. " +
            "Nubis 데이터 폴더와 ~/.ssh, 개인키 파일은 올릴 수 없다.",
            Schema(new
            {
                host = new { type = "string" },
                local_path = new { type = "string", description = "이 PC 의 파일 또는 폴더 (절대 경로)" },
                remote_dir = new { type = "string", description = "올릴 원격 폴더" },
                overwrite = new { type = "boolean" },
            }, "host", "local_path", "remote_dir"),
            a => SftpUploadAsync(a));

        // ---------- 클라우드 ----------
        yield return new McpTool("cloud_sync",
            "클라우드 프로필의 서버 목록을 다시 조회해 호스트 목록에 반영한다 (조회 API 만 사용, 클라우드 리소스는 바꾸지 않음). " +
            "profile 은 그룹 이름·프로필 이름 또는 'all'. 생략하면 프로필 목록만 보여준다.",
            Schema(new { profile = new { type = "string" } }),
            CloudSyncAsync);
    }
}
