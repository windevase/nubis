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

    private void OnToggleClaude(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b) _claudeToggle ??= b;
        ToggleClaude();
    }
    private void OnCloseClaude(object sender, RoutedEventArgs e) => ToggleClaude(false);

    // =================== 제목 표시줄 Claude 버튼의 "실행 중" 표시 ===================
    // 패널을 닫아도 claude 프로세스는 계속 돌며 MCP 요청(승인 창 포함)을 보낼 수 있다 — 버튼에 점·툴팁으로 알린다.
    // XAML 연결: 버튼에 x:Name 이 없어 Claude 로고 이미지로 찾는다. MainWindow.xaml 에서 버튼에 x:Name 을 주거나
    // ClaudeRunningInBackground 에 바인딩하려면 ClaudeBackgroundChanged 이벤트를 쓰면 된다 (이 경우 FindClaudeToggle 은 그 이름을 쓰게 바꾼다).

    private System.Windows.Controls.Button? _claudeToggle;
    private System.Windows.Shapes.Ellipse? _claudeRunningDot;
    private object? _claudeToggleTip;
    private bool _claudeBackgroundShown;

    /// <summary>claude 프로세스가 실행 중인가 (패널 열림 여부와 무관)</summary>
    public bool IsClaudeRunning => _claudeView?.Status == SessionStatus.Connected;

    /// <summary>패널은 닫혔지만 claude 가 실행 중 — 제목 표시줄 버튼에 표시하는 상태</summary>
    public bool ClaudeRunningInBackground => ClaudeBackgroundState(IsClaudeRunning, ClaudeColumn.Width.Value > 0);

    /// <summary>표시 판단 (UI 와 무관, 테스트용): 실행 중이고 패널이 닫혀 있을 때만</summary>
    internal static bool ClaudeBackgroundState(bool running, bool panelOpen) => running && !panelOpen;

    /// <summary>ClaudeRunningInBackground 가 바뀔 때 (UI 스레드)</summary>
    public event Action<bool>? ClaudeBackgroundChanged;

    private System.Windows.Controls.Button? FindClaudeToggle()
    {
        if (_claudeToggle != null) return _claudeToggle;
        static System.Windows.Controls.Button? Walk(object node)
        {
            if (node is System.Windows.Controls.Button { Content: System.Windows.Controls.Panel sp } btn &&
                sp.Children.OfType<System.Windows.Controls.Image>().Any(i => ReferenceEquals(i.Source, BrandIcons.ClaudeMark)))
                return btn;
            if (node is not DependencyObject d) return null;
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                if (Walk(child) is { } found) return found;
            return null;
        }
        return _claudeToggle = Walk(this);
    }

    /// <summary>제목 표시줄 Claude 버튼의 실행 중 표시를 지금 상태로 맞춘다 (패널 열기·닫기, claude 시작·종료 때 호출)</summary>
    private void UpdateClaudeIndicator()
    {
        var on = ClaudeRunningInBackground;
        try
        {
            if (FindClaudeToggle() is { Content: System.Windows.Controls.Panel sp } btn)
            {
                if (_claudeRunningDot == null)
                {
                    _claudeRunningDot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 7, Height = 7, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                        Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A)), Visibility = Visibility.Collapsed,
                    };
                    sp.Children.Add(_claudeRunningDot);
                    _claudeToggleTip = btn.ToolTip;
                }
                _claudeRunningDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                btn.ToolTip = on ? L.T("Claude Code 패널 (Ctrl+Shift+A) — 패널은 닫혀 있지만 Claude 가 실행 중입니다") : _claudeToggleTip;
            }
        }
        catch { }
        if (on != _claudeBackgroundShown)
        {
            _claudeBackgroundShown = on;
            ClaudeBackgroundChanged?.Invoke(on);
        }
    }

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
        UpdateClaudeIndicator();
    }

    private void OnOpenAuditLog(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AuditLog.FilePath)) File.WriteAllText(AuditLog.FilePath, "");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AuditLog.FilePath) { UseShellExecute = true });
    }

    private void OnRestartClaude(object sender, RoutedEventArgs e)
    {
        // 이전 claude 가 보낸 요청(열려 있는 승인 창 포함)은 응답을 받을 쪽이 없으므로 모두 취소 — 승인 창은 거부로 닫힌다
        _mcp?.CancelAll();
        if (_claudeView != null)
        {
            _claudeView.CloseSession();
            ClaudeHost.Content = null;
            _claudeView = null;
        }
        EnsureClaude();
        UpdateClaudeIndicator();
    }

    private void EnsureClaude()
    {
        if (_claudeView != null) return;

        try { _mcp ??= new McpServer(BuildTools(), McpInstructions); }
        catch (Exception ex)
        {
            // 로컬 포트를 열지 못함 (보안 프로그램·URL ACL 등) — 패널만 쓸 수 없고 앱은 계속 동작
            Dialog.Show(this, L.F("Claude 패널용 MCP 서버를 시작하지 못했습니다: {0}", ex.Message), "Nubis", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string configPath, guardPath;
        try
        {
            configPath = _mcp.WriteClientConfig(McpConfigPath);
            WriteClaudeMd();
            // 저장된 계정 파일 접근 차단 규칙 (실행할 때마다 다시 써서 수정돼도 원래대로)
            guardPath = Path.Combine(ClaudeWorkDir, ClaudeGuard.FileName);
            _lastActiveContext = null;
            UpdateActiveContext();
            File.WriteAllText(guardPath, ClaudeGuard.BuildSettingsJson(AppPaths.DataDir, ActiveContextPath, AppPaths.RootDir, ClaudeGuard.LegacyDataDir), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 차단 규칙 파일을 쓰지 못한 채로 claude 를 띄우면 계정 파일 보호 없이 실행된다 — 시작하지 않는다
            Dialog.Show(this, L.F("Claude 패널 설정 파일을 쓰지 못해 시작하지 않았습니다: {0}", ex.Message), "Nubis", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
        // claude 프로세스가 끝나면(종료·충돌) 그 claude 의 처리 중 요청을 모두 취소한다 — 열린 승인 창이 나중에 승인되어
        // 아무도 결과를 받지 않는 원격 명령이 실행되는 것을 막는다. Enter 로 다시 시작하는 새 claude 의 요청은 그 뒤에 온다
        var view = _claudeView;
        view.StatusChanged += s =>
        {
            if (s == SessionStatus.Disconnected && _claudeView == view) _mcp?.CancelAll();
            if (_claudeView == view) UpdateClaudeIndicator();
        };
        ClaudeHost.Content = _claudeView;
    }

    // =================== 현재 화면 알려주기 (UserPromptSubmit hook) ===================
    // 사용자가 탭을 바꿔도 Claude 는 알 수 없으므로, 앱이 현재 탭 정보를 파일에 써 두고
    // Claude 패널의 UserPromptSubmit hook 이 메시지를 보낼 때마다 그 내용을 질문에 붙인다.

    private static string ActiveContextPath => Path.Combine(ClaudeWorkDir, ClaudeGuard.ActiveContextFile);
    /// <summary>claude --mcp-config 파일 (MCP 토큰이 들어 있어 앱 종료 때 지운다)</summary>
    private static string McpConfigPath => Path.Combine(ClaudeWorkDir, "mcp-config.json");
    private string? _lastActiveContext;

    /// <summary>현재 탭 정보를 파일에 쓴다 (탭 선택·닫기·연결 상태 변경 때 호출)</summary>
    private void UpdateActiveContext()
    {
        // Claude 패널을 한 번도 열지 않았으면 읽을 쪽이 없다 — 탭을 바꿀 때마다 디스크에 쓰지 않는다
        // (패널을 처음 열 때 EnsureClaude 가 _mcp 를 만든 뒤 바로 다시 불러 그때의 탭을 쓴다)
        if (_mcp == null) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateActiveContext); return; }
        var selected = _tabs.FirstOrDefault(t => t.IsSelected);
        // 나란히 보기 중이면 함께 보이는 다른 쪽 탭도 알려 준다 (MainWindow.SplitView.cs)
        var beside = selected != null && _split?.Partner(selected) is { } p && _tabs.Contains(p) ? p : null;
        var text = ActiveContextText(selected, _tabs.Count, beside, beside != null && ReferenceEquals(_split!.Right, beside));
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
    public static string ActiveContextText(SessionTab? tab, int openTabs, SessionTab? beside = null, bool besideOnRight = true) =>
        ActiveContextCore(tab, openTabs)
        + (tab != null && beside != null ? BesideContextText(beside.Id, beside.Title, besideOnRight) : "")
        + (L.IsEnglish ? " (The user's app language is English — reply in English.)" : "");

    /// <summary>나란히 보기 중 함께 보이는 다른 쪽 탭 (현재 화면 문구 뒤에 붙인다). 포커스는 위의 탭에 있다</summary>
    internal static string BesideContextText(int id, string title, bool onRight) =>
        $" 화면을 나눠 {(onRight ? "오른쪽" : "왼쪽")}에 함께 보이는 탭: session_id={id} · \"{SafeContextTitle(title)}\" " +
        "(사용자가 '오른쪽/왼쪽 화면' 이라고 하면 위치로 구분하세요. 따로 말하지 않으면 위의 포커스가 있는 탭이 기준입니다).";

    private static string ActiveContextCore(SessionTab? tab, int openTabs) =>
        tab == null
            ? ActiveContextText(null, "", Protocol.Local, null, 0, SessionStatus.Disconnected, openTabs)
            : ActiveContextText(tab.Id, tab.Title, tab.Protocol, tab.Host?.Address, tab.Host?.Port ?? 0, tab.Status, openTabs, K8sTabKind(tab));

    /// <summary>쿠버네티스 탭 종류 설명 (현재 화면 문구 · list_sessions). 쿠버네티스 탭이 아니면 null</summary>
    internal static string? K8sTabKind(SessionTab tab) =>
        tab.K8sClusterId is not { } cid ? null
        : (tab.K8sPod ? "쿠버네티스 파드 셸/로그 (kubectl exec·logs, 컨테이너 안 셸 — PowerShell 아님)" : "쿠버네티스 kubectl 터미널 (이 PC 의 PowerShell, KUBECONFIG = 클러스터)")
          + (K8sStore.Get(cid) is { } c ? $" · 클러스터 '{c.Name}'" : "");

    public static string ActiveContextText(int? id, string title, Protocol protocol, string? address, int port, SessionStatus status, int openTabs, string? kindOverride = null)
    {
        if (id == null)
            return $"[Nubis 현재 화면] 사용자는 세션 탭이 아닌 호스트 목록(홈) 화면을 보고 있습니다. 열린 세션 탭 {openTabs}개 (list_sessions 로 확인).";
        var kind = kindOverride ?? protocol switch
        {
            Protocol.Ssh => "SSH 터미널",
            Protocol.Rdp => "RDP 원격 데스크톱",
            _ => "로컬 PowerShell",
        };
        var host = address != null ? $" · {address}:{port}" : "";
        var how = protocol == Protocol.Rdp ? "capture_screen(조작은 rdp_click / rdp_key / rdp_type)" :"get_terminal_info(현재 폴더·사용자) / read_terminal / get_selection / capture_screen";
        var safeTitle = SafeContextTitle(title);
        return $"[Nubis 현재 화면] 사용자가 지금 보고 있는 탭: session_id={id} · \"{safeTitle}\" · {kind}{host} · {StatusText(status)}. " +
               $"'이 화면 / 지금 터미널 / 이 에러' 는 이 탭을 뜻합니다. 화면 내용은 {how} 를 session_id={id} 로 호출해 확인하세요 " +
               $"(이전 대화의 session_id 를 그대로 쓰지 마세요). 열린 세션 탭 {openTabs}개.";
    }

    /// <summary>탭 제목에 허용하는 최대 글자 수 (현재 화면 문구)</summary>
    internal const int ContextTitleMax = 80;

    /// <summary>
    /// 현재 화면 문구에 넣을 탭 제목 정리. 탭 제목은 호스트 이름·클라우드 태그·사용자 입력(탭 이름 바꾸기)에서 오므로
    /// 질문에 붙는 지시문처럼 보이지 않게 한 줄로 만들고(제어 문자·줄바꿈 제거), 큰따옴표를 바꾸고,
    /// '[Nubis 현재 화면]' 같은 머리글을 흉내 내지 못하게 대괄호를 괄호로 바꾸고, 길이를 자른다.
    /// </summary>
    internal static string SafeContextTitle(string title)
    {
        var sb = new StringBuilder(Math.Min(title.Length, ContextTitleMax + 1));
        foreach (var ch in title)
        {
            if (sb.Length > ContextTitleMax) break;
            sb.Append(ch switch
            {
                '"' => '\'',
                '[' => '(',
                ']' => ')',
                _ when char.IsControl(ch) || ch is '\u2028' or '\u2029' => ' ',
                _ => ch,
            });
        }
        var s = sb.ToString();
        if (s.Length <= ContextTitleMax) return s;
        // 서로게이트 쌍(이모지 등) 가운데서 자르면 짝 없는 반쪽이 UTF-8 로 쓸 때 깨진 문자가 되므로 한 글자 앞에서 자른다
        var cut = char.IsHighSurrogate(s[ContextTitleMax - 1]) ? ContextTitleMax - 1 : ContextTitleMax;
        return s[..cut] + "…";
    }

    private void ShutdownClaude()
    {
        try { _mcp?.CancelAll(); } catch { }
        try { _claudeView?.CloseSession(); } catch { }
        try { _mcp?.Dispose(); } catch { }
        // 서버가 닫히면 토큰은 쓸모없지만, 디스크에 남겨 두지 않는다
        if (_mcp != null) McpServer.DeleteClientConfig(McpConfigPath);
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
        "- RDP 화면 조작은 capture_screen 이미지 좌표로 rdp_click / rdp_drag / rdp_scroll, 키 조합은 rdp_key, 글자는 rdp_type(매번 승인). PowerShell 은 rdp_key ['win+r'] → rdp_type 'powershell' 로 엽니다. 끝나면 rdp_release_control 로 허용을 반납하세요.\n" +
        "- 사용자가 '이 호스트들 / 선택한 것' 이라고 하면 list_hosts(selected_only=true) 로 화면에서 고른 카드를 확인하고, 호스트 하나의 자세한 설정은 get_host 로 보세요.\n" +
        "- 사용자는 대화 중에 탭을 자주 바꿉니다. 메시지마다 붙는 '[Nubis 현재 화면]' 줄이 그 메시지를 보낸 순간 사용자가 보고 있던 탭입니다. 이전에 쓰던 session_id 대신 그 줄의 session_id 를 쓰세요.\n" +
        "- 호스트 등록·수정·삭제는 add_host / update_host / delete_host 로 합니다(앱이 승인을 받음). 계정은 비밀번호 대신 list_vault 의 Vault 계정을 vault 로 연결하세요.\n" +
        "- 접속이 안 된다고 하면 먼저 test_connection 으로 DNS·포트·SSH 배너를 확인하세요.\n" +
        "- 쿠버네티스는 list_k8s_clusters 로 등록된 클러스터를 보고, run_kubectl 로 클러스터를 지정해 실행합니다(인증은 앱이 고정, 승인 필요). 파드 셸·로그 탭은 open_k8s_session, 포트 포워딩은 k8s_port_forward(승인 필요). " +
        "클러스터 관리(이름·네임스페이스·그룹)는 update_k8s_cluster, 삭제 delete_k8s_cluster, 연결 확인 test_k8s_cluster, 클라우드에서 찾기 find_k8s_clusters, kubeconfig 가져오기 import_k8s_clusters.\n" +
        "- 포트 포워딩: SSH 탭은 list_port_forwards 로 보고 add_port_forward(승인 필요, 이 탭에서만 임시)로 열고 close_port_forward 로 닫습니다. 클러스터 탭(쿠버네티스)은 k8s_port_forward(list · start · stop)입니다.\n" +
        "- 터미널의 현재 폴더·로그인 사용자·sudo 전환 여부는 get_terminal_info 로 바로 확인하세요(명령을 보내지 않아 빠르고 승인도 없음). pwd·whoami 를 send_to_terminal 로 보내지 마세요.\n" +
        "- send_to_terminal 은 명령이 끝나 셸 프롬프트가 돌아올 때까지(기본 10초) 기다려 출력을 함께 돌려줍니다. 그 결과로 바로 답하고, '아직 실행 중' 일 때만 wait_for_output 으로 이어서 기다리세요.\n" +
        "- 로그·설정 파일은 sftp_read(tail=true 로 로그 끝부분), 폴더는 sftp_list 로 보세요. 파일 전송은 sftp_download / sftp_upload.\n" +
        "- OTP·Push 인증을 요구하는 서버는 먼저 open_session(wait_connected_sec) 으로 탭을 열어 사용자가 인증을 마치게 한 뒤 run_command 를 쓰세요(열린 탭의 로그인된 연결로 실행됩니다).\n" +
        "- 클라우드 서버 목록이 오래됐으면 cloud_sync 로 다시 가져오세요.\n" +
        "- '서버 가져와 줘' 같은 요청은 discover_sources 로 이 PC 에 있는 출처(클라우드 CLI 프로필·~/.ssh/config·mRemoteNG·AD)를 먼저 확인하세요. " +
        "클라우드는 cloud_add_profile(로그인이 필요하면 cloud_login 후 사용자가 마치면 다시 시도), 온프레미스·엑셀 표는 import_hosts 를 apply=false 로 미리보기 → 사용자 확인 → apply=true 순서로 등록합니다.\n" +
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
