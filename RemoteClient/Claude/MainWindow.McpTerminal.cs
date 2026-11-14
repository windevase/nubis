using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>
/// Claude 패널 MCP 도구 — 터미널: read_terminal, get_terminal_info, get_selection, send_to_terminal, wait_for_output 등록.
/// (등록 순서는 BuildTools / ExtraTools 에서 정한다. wait_for_output 구현은 MainWindow.McpDiagnostics.cs)
/// </summary>
public partial class MainWindow
{
    /// <summary>터미널 읽기 도구 (read_terminal, get_terminal_info, get_selection) — BuildTools 에서 open_session 다음에 yield</summary>
    private IEnumerable<McpTool> TerminalReadTools()
    {
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
    }

    /// <summary>wait_for_output 등록 (구현: WaitForOutputAsync) — ExtraTools 에서 test_connection 다음에 yield</summary>
    private McpTool WaitForOutputTool() =>
        new("wait_for_output",
            "SSH/로컬 터미널에서 출력이 나올 때까지 기다린다. pattern 을 주면 그 문자열(또는 regex=true 면 정규식)이 새로 나타날 때까지, " +
            "생략하면 명령이 끝날 때까지 기다린 뒤 화면 마지막 lines 줄을 돌려준다 — 실행 중인 명령이 앱이 알아보는 셸 프롬프트에서 입력됐으면 프롬프트가 돌아올 때까지" +
            "(출력이 오래 멈춰도 timeout_sec 까지 기다림 — 비밀번호·[Y/n] 질문은 알아보지만 그 밖의 입력 대기가 예상되면 timeout_sec 를 짧게), " +
            "아니면(또는 idle_sec 를 직접 주면) 출력이 idle_sec 초 동안 멈출 때까지. " +
            "셸 프롬프트로 돌아와 있고 마지막 명령 출력에 pattern 이 이미 있으면 바로 돌려준다. 명령이 아직 실행 중이면 화면에 이미 있던 일치는 세지 않고 새로 나타날 때까지 기다린다. " +
            "기다리는 중 연결이 끊기면 그 사실과 화면을 돌려준다. " +
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

    /// <summary>send_to_terminal 등록 — BuildTools 마지막에 yield</summary>
    private McpTool SendToTerminalTool() =>
        new("send_to_terminal",
            "열려 있는 SSH/로컬 세션의 대화형 터미널에 텍스트를 입력한다 (사용자가 보는 화면에 그대로 타이핑). " +
            "실행 전에 앱이 사용자 승인을 받는다 — SSH 세션에서 사용자가 '읽기 전용 명령은 승인 없이 실행' 을 켰고, 화면이 셸 프롬프트이고, 읽기 전용 명령이면 승인 없이 입력한다 " +
            "(제어 문자가 들어 있으면 항상 승인, OS 가 Windows 로 보이는 SSH 호스트는 셸이 cmd/PowerShell 이라 항상 승인). " +
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
                // 감사 로그에는 제어 문자를 \xNN 으로 드러낸 형태로 남긴다 (승인 창 표시와 같은 형태)
                var logged = McpHelpers.VisibleControlChars(text);
                var atPromptBefore = await tab.Session.IsAtPromptAsync();
                // 읽기 전용 자동 승인: 설정을 켰고 + 원격(SSH) 탭이고 + 셸 프롬프트가 보이고(비밀번호 질문·vim 등에 들어가지 않게) + 한 줄 읽기 전용 명령 + Enter
                // + 제어 문자 없음(Ctrl-U 등으로 줄을 지우고 바꾸지 못하게).
                // 로컬 탭은 gc ~\.ssh\id_rsa 같은 '읽기 전용' 명령으로 이 PC 의 비밀 파일을 화면에 띄워 read_terminal 로 읽을 수 있어 제외
                // OS 가 Windows 로 보이는 SSH 대상(Windows OpenSSH — 기본 셸 cmd/PowerShell)은 sh 기준 판정이 맞지 않아 항상 승인
                var auto = McpHelpers.CanAutoApproveTerminalInput(SettingsStore.Current.AutoApproveReadOnly, text, enter,
                    atPromptBefore, t => ReadOnlyCommands.IsReadOnly(t, shell), localTerminal: tab.Protocol == Protocol.Local,
                    windowsHost: McpHelpers.IsWindowsOs(tab.Host?.Os));
                // 승인 창은 제어 문자를 \xNN 으로 보여 준다 (ApprovalDialog)
                if (!auto && !await AskApprovalAsync("send_to_terminal", L.T("Claude 가 터미널 입력을 요청합니다"),
                        L.T("아래 세션의 터미널에 텍스트를 입력합니다."), new[] { target }, text))
                {
                    AuditLog.Write("send_to_terminal", new[] { target }, logged, "거부");
                    throw new McpToolException("사용자가 입력을 거부했습니다.");
                }
                if (!_tabs.Contains(tab)) throw new McpToolException("승인을 기다리는 동안 탭이 닫혀 입력하지 않았습니다.");
                var before = await tab.Session.ReadTerminalAsync(200) ?? "";
                var ok = tab.Session.SendText(text + (enter ? "\r" : ""));
                AuditLog.Write("send_to_terminal", new[] { target }, logged,
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
