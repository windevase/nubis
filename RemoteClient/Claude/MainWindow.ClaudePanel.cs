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
/// Claude Code 패널 + 앱 내장 MCP 서버.
/// 패널은 공식 claude CLI 를 로컬 터미널(ConPTY)로 띄우고, --mcp-config 로 이 앱의 MCP 서버에 연결한다.
/// </summary>
public partial class MainWindow
{
    private McpServer? _mcp;
    private LocalTerminalView? _claudeView;
    private double _claudeWidth = 520;

    private static string ClaudeWorkDir
    {
        get
        {
            var dir = Path.Combine(AppPaths.DataDir, "claude");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // =================== 패널 ===================

    private void OnToggleClaude(object sender, RoutedEventArgs e) => ToggleClaude();
    private void OnCloseClaude(object sender, RoutedEventArgs e) => ToggleClaude(false);

    private void ToggleClaude(bool? open = null)
    {
        var isOpen = ClaudeColumn.Width.Value > 0;
        var want = open ?? !isOpen;
        if (want == isOpen)
        {
            if (want) _claudeView?.FocusSession();
            return;
        }

        if (want)
        {
            ClaudeColumn.Width = new GridLength(_claudeWidth);
            ClaudeColumn.MinWidth = 320;
            ClaudeSplitter.Visibility = Visibility.Visible;
            ClaudePanel.Visibility = Visibility.Visible;
            EnsureClaude();
            Dispatcher.BeginInvoke(() => _claudeView?.FocusSession(), System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            _claudeWidth = ClaudeColumn.ActualWidth > 0 ? ClaudeColumn.ActualWidth : _claudeWidth;
            ClaudeColumn.MinWidth = 0;
            ClaudeColumn.Width = new GridLength(0);
            ClaudeSplitter.Visibility = Visibility.Collapsed;
            ClaudePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenAuditLog(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AuditLog.FilePath)) File.WriteAllText(AuditLog.FilePath, "");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AuditLog.FilePath) { UseShellExecute = true });
    }

    private void OnRestartClaude(object sender, RoutedEventArgs e)
    {
        if (_claudeView != null)
        {
            _claudeView.CloseSession();
            ClaudeHost.Content = null;
            _claudeView = null;
        }
        EnsureClaude();
    }

    private void EnsureClaude()
    {
        if (_claudeView != null) return;

        _mcp ??= new McpServer(BuildTools(), McpInstructions);
        var configPath = _mcp.WriteClientConfig(Path.Combine(ClaudeWorkDir, "mcp-config.json"));
        WriteClaudeMd();
        // 저장된 계정 파일 접근 차단 규칙 (실행할 때마다 다시 써서 수정돼도 원래대로)
        var guardPath = Path.Combine(ClaudeWorkDir, ClaudeGuard.FileName);
        _lastActiveContext = null;
        UpdateActiveContext();
        File.WriteAllText(guardPath, ClaudeGuard.BuildSettingsJson(AppPaths.DataDir, ActiveContextPath, AppPaths.RootDir), new UTF8Encoding(false));

        _claudeView = new LocalTerminalView(() =>
        {
            var exe = FindClaude();
            if (exe == null)
                // /c 로 바로 끝내면 패널이 "Enter 키를 누르면 다시 시작" 을 보여 주고, Enter 때 다시 찾는다 (설치 후 앱 재시작 불필요)
                return "cmd.exe /d /c \"echo " + L.T("Claude Code CLI(claude) 를 찾을 수 없습니다.") + " & echo. & echo   npm install -g @anthropic-ai/claude-code & echo. & echo " +
                       L.T("설치한 뒤 이 패널에서 Enter 를 누르면 다시 찾습니다.") + "\"";
            var args = $"--mcp-config \"{configPath}\" --settings \"{guardPath}\" --allowedTools mcp__{McpServer.ServerName}";
            // 시작하기의 "Claude 에게 묻기": 첫 질문으로 대화를 시작한다 (한 번만)
            if (_pendingClaudePrompt is { Length: > 0 } first) { args += $" \"{SafePromptArg(first)}\""; _pendingClaudePrompt = null; }
            // npm 설치본은 claude.cmd 라 cmd 를 거쳐 실행
            return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? $"\"{exe}\" {args}"
                : $"cmd.exe /d /s /c \"\"{exe}\" {args}\"";
        }, ClaudeWorkDir, L.T("Claude Code 가 종료되었습니다."), killTreeOnClose: true);
        ClaudeHost.Content = _claudeView;
    }

    // =================== 현재 화면 알려주기 (UserPromptSubmit hook) ===================
    // 사용자가 탭을 바꿔도 Claude 는 알 수 없으므로, 앱이 현재 탭 정보를 파일에 써 두고
    // Claude 패널의 UserPromptSubmit hook 이 메시지를 보낼 때마다 그 내용을 질문에 붙인다.

    private static string ActiveContextPath => Path.Combine(ClaudeWorkDir, ClaudeGuard.ActiveContextFile);
    private string? _lastActiveContext;

    /// <summary>현재 탭 정보를 파일에 쓴다 (탭 선택·닫기·연결 상태 변경 때 호출)</summary>
    private void UpdateActiveContext()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateActiveContext); return; }
        var text = ActiveContextText(_tabs.FirstOrDefault(t => t.IsSelected), _tabs.Count);
        if (text == _lastActiveContext) return;
        try
        {
            File.WriteAllText(ActiveContextPath, text, new UTF8Encoding(false));
            _lastActiveContext = text;
        }
        catch { }
    }

    public static string StatusText(SessionStatus s) => s switch
    {
        SessionStatus.Connected => "연결됨",
        SessionStatus.Connecting => "연결 중",
        _ => "끊김",
    };

    /// <summary>hook 이 질문에 붙일 문구. 비밀 값(계정·비밀번호)은 넣지 않는다.</summary>
    public static string ActiveContextText(SessionTab? tab, int openTabs) =>
        ActiveContextCore(tab, openTabs) + (L.IsEnglish ? " (The user's app language is English — reply in English.)" : "");

    private static string ActiveContextCore(SessionTab? tab, int openTabs) =>
        tab == null
            ? ActiveContextText(null, "", Protocol.Local, null, 0, SessionStatus.Disconnected, openTabs)
            : ActiveContextText(tab.Id, tab.Title, tab.Protocol, tab.Host?.Address, tab.Host?.Port ?? 0, tab.Status, openTabs);

    public static string ActiveContextText(int? id, string title, Protocol protocol, string? address, int port, SessionStatus status, int openTabs)
    {
        if (id == null)
            return $"[Nubis 현재 화면] 사용자는 세션 탭이 아닌 호스트 목록(홈) 화면을 보고 있습니다. 열린 세션 탭 {openTabs}개 (list_sessions 로 확인).";
        var kind = protocol switch
        {
            Protocol.Ssh => "SSH 터미널",
            Protocol.Rdp => "RDP 원격 데스크톱",
            _ => "로컬 PowerShell",
        };
        var host = address != null ? $" · {address}:{port}" : "";
        var how = protocol == Protocol.Rdp ? "capture_screen" : "get_terminal_info(현재 폴더·사용자) / read_terminal / get_selection / capture_screen";
        // 탭 제목은 호스트 이름에서 오므로 줄바꿈·따옴표를 정리해 한 줄을 유지
        var safeTitle = title.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
        return $"[Nubis 현재 화면] 사용자가 지금 보고 있는 탭: session_id={id} · \"{safeTitle}\" · {kind}{host} · {StatusText(status)}. " +
               $"'이 화면 / 지금 터미널 / 이 에러' 는 이 탭을 뜻합니다. 화면 내용은 {how} 를 session_id={id} 로 호출해 확인하세요 " +
               $"(이전 대화의 session_id 를 그대로 쓰지 마세요). 열린 세션 탭 {openTabs}개.";
    }

    private void ShutdownClaude()
    {
        try { _claudeView?.CloseSession(); } catch { }
        try { _mcp?.Dispose(); } catch { }
    }

    /// <summary>PATH 와 일반 설치 위치에서 claude 실행 파일 찾기</summary>
    private static string? FindClaude()
    {
        var names = new[] { "claude.exe", "claude.cmd" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var d in dirs)
        foreach (var n in names)
        {
            try
            {
                var p = Path.Combine(d.Trim().Trim('"'), n);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    private static string McpInstructions => McpInstructionsBase + (L.IsEnglish ? "- Answer in English." : "- 답변은 한국어로 하세요.");

    private const string McpInstructionsBase =
        "이 MCP 서버는 Windows 원격 접속 클라이언트 'Nubis' 입니다. 사용자는 이 앱의 Claude 패널에서 대화하고 있습니다.\n" +
        "- 서버 찾기: list_groups 로 그룹(클라우드 프로필 포함)을 보고, list_hosts 로 이름·IP·상태를 검색하세요.\n" +
        "- '접속해줘/열어줘' 요청은 open_session 으로 앱에 SSH/RDP 탭을 엽니다.\n" +
        "- 여러 서버 점검은 run_command 로 병렬 실행합니다(리눅스는 SSH, Windows 는 WinRM PowerShell). OS 가 다르면 나눠서 호출하세요. 실행 전에 앱이 사용자에게 승인을 받습니다. " +
        "가능하면 읽기 전용 명령을 쓰고, 변경 작업은 사용자가 명시적으로 요청했을 때만 하세요.\n" +
        "- 사용자가 '이 화면', '지금 터미널', '이 에러' 라고 하면 read_terminal 이나 get_selection 으로 현재 탭 내용을 먼저 확인하세요. RDP(Windows 원격 데스크톱) 화면은 capture_screen 으로 이미지를 받아 보세요.\n" +
        "- 사용자는 대화 중에 탭을 자주 바꿉니다. 메시지마다 붙는 '[Nubis 현재 화면]' 줄이 그 메시지를 보낸 순간 사용자가 보고 있던 탭입니다. 이전에 쓰던 session_id 대신 그 줄의 session_id 를 쓰세요.\n" +
        "- 호스트 등록·수정·삭제는 add_host / update_host / delete_host 로 합니다(앱이 승인을 받음). 계정은 비밀번호 대신 list_vault 의 Vault 계정을 vault 로 연결하세요.\n" +
        "- 접속이 안 된다고 하면 먼저 test_connection 으로 DNS·포트·SSH 배너를 확인하세요.\n" +
        "- 터미널의 현재 폴더·로그인 사용자·sudo 전환 여부는 get_terminal_info 로 바로 확인하세요(명령을 보내지 않아 빠르고 승인도 없음). pwd·whoami 를 send_to_terminal 로 보내지 마세요.\n" +
        "- send_to_terminal 은 명령이 끝나 셸 프롬프트가 돌아올 때까지(기본 10초) 기다려 출력을 함께 돌려줍니다. 그 결과로 바로 답하고, '아직 실행 중' 일 때만 wait_for_output 으로 이어서 기다리세요.\n" +
        "- 로그·설정 파일은 sftp_read(tail=true 로 로그 끝부분), 폴더는 sftp_list 로 보세요. 파일 전송은 sftp_download / sftp_upload.\n" +
        "- 클라우드 서버 목록이 오래됐으면 cloud_sync 로 다시 가져오세요.\n" +
        "- 설정 안내를 부탁받으면 get_setup_status 로 상태를 먼저 보고, open_app_page 로 해당 화면을 열어 사용자와 같은 화면을 보며 한 단계씩 안내하세요. 시작 화면 로그인(SSO) 값은 Claude 가 바꿀 수 없으니 사용자가 직접 입력·저장하도록 안내합니다.\n" +
        "- 앱 설정(테마·글꼴·구문 강조·기본 포트 등)을 바꿔 달라고 하면 get_settings 로 확인한 뒤 update_settings 로 바꾸세요. settings.json 을 직접 고치지 마세요(앱이 덮어씁니다).\n" +
        "- 비밀번호는 앱이 관리하므로 묻거나 다루지 마세요. 계정이 없다는 오류가 나면 앱의 호스트/클라우드 프로필 설정에서 계정을 저장하도록 안내하세요.\n" +
        "- Nubis 데이터 파일(hosts.json, cloud_profiles.json, vault.json)을 직접 읽거나 저장된 암호를 복호화하지 마세요. 호스트 정보는 list_hosts 로 확인합니다.\n" +
        "";

    private static void WriteClaudeMd()
    {
        var path = Path.Combine(ClaudeWorkDir, "CLAUDE.md");
        if (File.Exists(path)) return;   // 사용자가 수정했을 수 있으므로 덮어쓰지 않음
        File.WriteAllText(path,
            "# Nubis 의 Claude 패널\n\n" +
            "이 대화는 원격 접속 클라이언트 Nubis 안에서 실행 중이다. `nubis` MCP 도구로 앱을 다룬다.\n\n" +
            McpInstructionsBase.Replace("\n", "\n\n") + "\n\n" +
            "이 파일은 직접 수정해도 된다 (앱이 다시 덮어쓰지 않음).\n", new UTF8Encoding(false));
    }
}
