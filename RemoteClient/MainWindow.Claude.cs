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
        File.WriteAllText(guardPath, ClaudeGuard.BuildSettingsJson(AppPaths.DataDir, ActiveContextPath), new UTF8Encoding(false));

        _claudeView = new LocalTerminalView(() =>
        {
            var exe = FindClaude();
            if (exe == null)
                return "cmd.exe /d /k echo Claude Code CLI(claude) 를 찾을 수 없습니다. 설치: npm install -g @anthropic-ai/claude-code";
            var args = $"--mcp-config \"{configPath}\" --settings \"{guardPath}\" --allowedTools mcp__{McpServer.ServerName}";
            // npm 설치본은 claude.cmd 라 cmd 를 거쳐 실행
            return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? $"\"{exe}\" {args}"
                : $"cmd.exe /d /s /c \"\"{exe}\" {args}\"";
        }, ClaudeWorkDir, "Claude Code 가 종료되었습니다.");
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
        var how = protocol == Protocol.Rdp ? "capture_screen" : "read_terminal / get_selection / capture_screen";
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

    private const string McpInstructions =
        "이 MCP 서버는 Windows 원격 접속 클라이언트 'Nubis' 입니다. 사용자는 이 앱의 Claude 패널에서 대화하고 있습니다.\n" +
        "- 서버 찾기: list_groups 로 그룹(클라우드 프로필 포함)을 보고, list_hosts 로 이름·IP·상태를 검색하세요.\n" +
        "- '접속해줘/열어줘' 요청은 open_session 으로 앱에 SSH/RDP 탭을 엽니다.\n" +
        "- 여러 서버 점검은 run_command 로 병렬 실행합니다(리눅스는 SSH, Windows 는 WinRM PowerShell). OS 가 다르면 나눠서 호출하세요. 실행 전에 앱이 사용자에게 승인을 받습니다. " +
        "가능하면 읽기 전용 명령을 쓰고, 변경 작업은 사용자가 명시적으로 요청했을 때만 하세요.\n" +
        "- 사용자가 '이 화면', '지금 터미널', '이 에러' 라고 하면 read_terminal 이나 get_selection 으로 현재 탭 내용을 먼저 확인하세요. RDP(Windows 원격 데스크톱) 화면은 capture_screen 으로 이미지를 받아 보세요.\n" +
        "- 사용자는 대화 중에 탭을 자주 바꿉니다. 메시지마다 붙는 '[Nubis 현재 화면]' 줄이 그 메시지를 보낸 순간 사용자가 보고 있던 탭입니다. 이전에 쓰던 session_id 대신 그 줄의 session_id 를 쓰세요.\n" +
        "- 호스트 등록·수정·삭제는 add_host / update_host / delete_host 로 합니다(앱이 승인을 받음). 계정은 비밀번호 대신 list_vault 의 Vault 계정을 vault 로 연결하세요.\n" +
        "- 접속이 안 된다고 하면 먼저 test_connection 으로 DNS·포트·SSH 배너를 확인하세요.\n" +
        "- send_to_terminal 로 명령을 보낸 뒤에는 wait_for_output 으로 끝날 때까지(또는 원하는 문자열이 나올 때까지) 기다렸다가 결과를 보세요.\n" +
        "- 로그·설정 파일은 sftp_read(tail=true 로 로그 끝부분), 폴더는 sftp_list 로 보세요. 파일 전송은 sftp_download / sftp_upload.\n" +
        "- 클라우드 서버 목록이 오래됐으면 cloud_sync 로 다시 가져오세요.\n" +
        "- 앱 설정(테마·글꼴·구문 강조·기본 포트 등)을 바꿔 달라고 하면 get_settings 로 확인한 뒤 update_settings 로 바꾸세요. settings.json 을 직접 고치지 마세요(앱이 덮어씁니다).\n" +
        "- 비밀번호는 앱이 관리하므로 묻거나 다루지 마세요. 계정이 없다는 오류가 나면 앱의 호스트/클라우드 프로필 설정에서 계정을 저장하도록 안내하세요.\n" +
        "- Nubis 데이터 파일(hosts.json, cloud_profiles.json, vault.json)을 직접 읽거나 저장된 암호를 복호화하지 마세요. 호스트 정보는 list_hosts 로 확인합니다.\n" +
        "- 답변은 한국어로 하세요.";

    private static void WriteClaudeMd()
    {
        var path = Path.Combine(ClaudeWorkDir, "CLAUDE.md");
        if (File.Exists(path)) return;   // 사용자가 수정했을 수 있으므로 덮어쓰지 않음
        File.WriteAllText(path,
            "# Nubis 의 Claude 패널\n\n" +
            "이 대화는 원격 접속 클라이언트 Nubis 안에서 실행 중이다. `nubis` MCP 도구로 앱을 다룬다.\n\n" +
            McpInstructions.Replace("\n", "\n\n") + "\n\n" +
            "이 파일은 직접 수정해도 된다 (앱이 다시 덮어쓰지 않음).\n", new UTF8Encoding(false));
    }

    // =================== MCP 도구 ===================

    /// <summary>UI 스레드에서 실행하고 결과 반환 (MCP 요청은 백그라운드 스레드에서 들어옴)</summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> f) => Dispatcher.InvokeAsync(f).Task.Unwrap();
    private Task<T> OnUi<T>(Func<T> f) => Dispatcher.InvokeAsync(f).Task;

    private static string Arg(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? IntArg(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : null;

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
            h.Address.Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
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
                query = new { type = "string", description = "이름·IP·용도·OS 부분 검색어" },
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
                    list = list.Where(h => new[] { h.DisplayName, h.Address, h.Purpose, h.Os, h.Username }
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
            "호스트에 SSH 또는 RDP 세션 탭을 열고 앱 화면에 표시한다. host 는 이름, IP, id 중 하나. 이름이 여러 개 일치하면 후보를 돌려준다.",
            Schema(new
            {
                host = new { type = "string", description = "호스트 이름, IP 또는 id" },
                address = new { type = "string", @enum = new[] { "default", "private", "public" }, description = "접속 주소: 호스트 설정(기본) / 사설 IP / 공인 IP(EIP)" },
            }, "host"),
            a => OnUi(() =>
            {
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
                return $"세션을 열었습니다: session_id={tab.Id}, {found[0].DisplayName} ({found[0].ProtocolText} {found[0].Address}:{found[0].Port}). " +
                       "연결 결과는 잠시 후 list_sessions 또는 read_terminal 로 확인하세요.";
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
            "세션 탭을 닫는다 (SSH/RDP 연결 종료, 로컬 터미널 종료). Claude 가 연 탭을 정리할 때 사용.",
            Schema(new { session_id = new { type = "integer" } }, "session_id"),
            a => OnUi(() =>
            {
                var tab = FindSession(a) ?? throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
                var title = tab.Title;
                CloseTab(tab);
                AuditLog.Write("close_session", new[] { $"session {tab.Id}: {title}" }, "(탭 닫기)", "실행");
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
            "실행 전에 앱이 사용자 승인을 받는다. 결과는 잠시 후 read_terminal 로 확인.",
            Schema(new
            {
                session_id = new { type = "integer", description = "생략 시 현재 탭" },
                text = new { type = "string" },
                press_enter = new { type = "boolean", description = "끝에 Enter 입력 (기본 true)" },
            }, "text"),
            a => OnUi(() =>
            {
                var tab = FindSession(a) ?? throw new McpToolException("세션을 찾을 수 없습니다.");
                if (tab.Protocol == Protocol.Rdp) throw new McpToolException("RDP 세션에는 입력할 수 없습니다.");
                var text = Arg(a, "text");
                var target = $"session {tab.Id}: {tab.Title}" + (tab.Host != null ? $" ({tab.Host.Address})" : "");
                if (!ApprovalDialog.Ask(this, "Claude 가 터미널 입력을 요청합니다",
                        "아래 세션의 터미널에 텍스트를 입력합니다.", new[] { target }, text))
                {
                    AuditLog.Write("send_to_terminal", new[] { target }, text, "거부");
                    throw new McpToolException("사용자가 입력을 거부했습니다.");
                }
                var ok = tab.Session.SendText(text + (BoolArg(a, "press_enter") == false ? "" : "\r"));
                AuditLog.Write("send_to_terminal", new[] { target }, text, ok ? "승인·입력" : "승인·실패(세션 끊김)");
                if (!ok) throw new McpToolException("세션이 연결되어 있지 않아 입력하지 못했습니다.");
                SelectTab(tab);
                return "입력했습니다. read_terminal 로 결과를 확인하세요.";
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
            !ApprovalDialog.Ask(this, "Claude 가 보안 설정 변경을 요청합니다",
                "아래 Nubis 설정을 바꿉니다. 보안에 영향을 주는 항목이 포함되어 있습니다.", new[] { "Nubis 설정" }, command))
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

    // =================== 화면 캡처 (capture_screen) ===================

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);   // 0 = GDI, 1 = USER 객체 수

    /// <summary>
    /// 탭 영역을 PrintWindow(전체 렌더링 포함)로 캡처. RDP ActiveX·WebView2 처럼 자체 창에 그리는 화면도 담긴다.
    /// UI 스레드에서 호출.
    /// </summary>
    private async Task<McpImage> CaptureSessionAsync(SessionTab? tab, int maxWidth)
    {
        if (tab == null) throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
        if (WindowState == WindowState.Minimized)
            throw new McpToolException("Nubis 창이 최소화되어 있어 화면을 캡처할 수 없습니다. 창을 열어 달라고 사용자에게 요청하세요.");

        var switched = !tab.IsSelected;
        if (switched)
        {
            // 가려진 RDP 화면은 갱신되지 않으므로 보이게 한 뒤 그려질 시간을 준다
            SelectTab(tab);
            await Task.Delay(700);
        }

        var view = tab.View;
        if (view.ActualWidth < 10 || view.ActualHeight < 10) throw new McpToolException("탭 화면 크기를 알 수 없습니다.");
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = view.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0));
        var crop = new System.Drawing.Rectangle(
            (int)(origin.X * dpi.DpiScaleX), (int)(origin.Y * dpi.DpiScaleY),
            (int)(view.ActualWidth * dpi.DpiScaleX), (int)(view.ActualHeight * dpi.DpiScaleY));

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var clientW = (int)(ActualWidth * dpi.DpiScaleX) + 32;
        var clientH = (int)(ActualHeight * dpi.DpiScaleY) + 32;

        byte[] data;
        string mime;
        int outW, outH;
        using (var full = new System.Drawing.Bitmap(clientW, clientH))
        {
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, 0x1 | 0x2); }   // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                finally { g.ReleaseHdc(hdc); }
            }
            crop.Intersect(new System.Drawing.Rectangle(0, 0, full.Width, full.Height));

            var scale = Math.Min(1.0, Math.Clamp(maxWidth, 320, 3840) / (double)crop.Width);
            outW = Math.Max(1, (int)(crop.Width * scale));
            outH = Math.Max(1, (int)(crop.Height * scale));
            using var shot = new System.Drawing.Bitmap(outW, outH);
            using (var g = System.Drawing.Graphics.FromImage(shot))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, new System.Drawing.Rectangle(0, 0, outW, outH), crop, System.Drawing.GraphicsUnit.Pixel);
            }

            // 대부분 PNG, 사진·배경화면처럼 큰 화면은 JPEG 로 (토큰·전송량 절약)
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            if (ms.Length <= 1_500_000) { data = ms.ToArray(); mime = "image/png"; }
            else
            {
                using var jms = new MemoryStream();
                var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
                using var p = new System.Drawing.Imaging.EncoderParameters(1);
                p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                shot.Save(jms, jpeg, p);
                data = jms.ToArray();
                mime = "image/jpeg";
            }
        }

        var target = $"session {tab.Id}: {tab.Title}" + (tab.Host != null ? $" ({tab.Host.Address})" : "");
        AuditLog.Write("capture_screen", new[] { target }, $"({outW}x{outH} {mime})", "조회");
        var kind = tab.Protocol switch { Protocol.Rdp => "RDP", Protocol.Ssh => "SSH", _ => "로컬" };
        return new McpImage(data, mime,
            $"[{target}] {kind} 화면 {crop.Width}x{crop.Height}" + (outW != crop.Width ? $" (축소 {outW}x{outH})" : "") +
            (switched ? " — 캡처를 위해 이 탭으로 전환했습니다." : ""));
    }

    private async Task<string> RunCommandAsync(JsonElement a)
    {
        var specs = ListArg(a, "targets");
        var command = Arg(a, "command");
        var timeout = Math.Clamp(IntArg(a, "timeout_sec") ?? 60, 5, 1800);
        if (specs.Count == 0 || command.Trim().Length == 0) throw new McpToolException("targets 와 command 가 필요합니다.");

        // 대상 해석 (UI 스레드의 호스트 목록 사용)
        var (targets, skipped) = await OnUi(() =>
        {
            var found = new List<HostEntry>();
            var skip = new List<string>();
            foreach (var spec in specs)
            {
                var r = ResolveHosts(spec);
                if (r.Count == 0) skip.Add($"{spec}: 일치하는 호스트 없음");
                if (r.Count > 1 && !spec.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                    skip.Add($"{spec}: {r.Count}개 일치 → 모두 포함");
                found.AddRange(r);
            }
            var unique = found.DistinctBy(h => h.Id).ToList();
            foreach (var h in unique.Where(h => h.IsStopped)) skip.Add($"{h.DisplayName}: 중지 상태({h.CloudState})");
            var run = unique.Where(h => h.Protocol is Protocol.Ssh or Protocol.Rdp && !h.IsStopped).Select(WithProfileDefaults).ToList();
            return (run, skip);
        });

        if (targets.Count == 0)
            throw new McpToolException("실행할 수 있는 대상이 없습니다.\n" + string.Join("\n", skipped));

        var useCmd = Arg(a, "windows_shell").Equals("cmd", StringComparison.OrdinalIgnoreCase);
        var linux = targets.Where(h => h.Protocol == Protocol.Ssh).ToList();
        var windows = targets.Where(h => h.Protocol == Protocol.Rdp).ToList();
        var winLabel = useCmd ? "WinRM cmd" : "WinRM PowerShell";
        // 시도할 주소 순서(사설 → 공인)를 승인 창에 그대로 보여준다
        string Addrs(HostEntry h, string ports) => string.Join(" → ", h.AddressCandidates().Select(a => $"{a}:{ports}"));
        var labels = linux.Select(h => $"[SSH]  {h.DisplayName}  ({Addrs(h, h.Port.ToString())})")
            .Concat(windows.Select(h => $"[{winLabel}]  {h.DisplayName}  ({Addrs(h, $"{WinRm.HttpsPort}/{WinRm.HttpPort}")})"))
            .ToList();
        var mixed = linux.Count > 0 && windows.Count > 0
            ? "\n⚠ 리눅스와 Windows 서버가 섞여 있습니다. 같은 명령이 양쪽에서 동작하는지 확인하세요." : "";
        // 설정에서 켠 경우, 대상 셸 모두에서 읽기 전용으로 판별되는 명령은 승인 창 없이 실행
        var autoApproved = SettingsStore.Current.AutoApproveReadOnly
            && (linux.Count == 0 || ReadOnlyCommands.IsReadOnly(command, CommandShell.Sh))
            && (windows.Count == 0 || ReadOnlyCommands.IsReadOnly(command, useCmd ? CommandShell.Cmd : CommandShell.PowerShell));
        if (!autoApproved)
        {
            var approved = await OnUi(() => ApprovalDialog.Ask(this, $"Claude 가 서버 {targets.Count}대에 명령 실행을 요청합니다",
                "승인하면 아래 서버들에 접속해 명령을 실행하고 결과를 Claude 에게 돌려줍니다." + mixed +
                (skipped.Count > 0 ? $"\n제외: {skipped.Count}건 (중지·미일치)" : ""), labels, command));
            if (!approved)
            {
                AuditLog.Write("run_command", labels, command, "거부");
                throw new McpToolException("사용자가 실행을 거부했습니다.");
            }
        }

        var sshTask = SshExec.RunAsync(linux, command, timeout, CancellationToken.None);
        var winTask = WinRm.RunAsync(windows, command, !useCmd, timeout, CancellationToken.None);
        await Task.WhenAll(sshTask, winTask);
        var results = sshTask.Result.Concat(winTask.Result).ToList();
        var okCount = results.Count(r => r.Ok);
        AuditLog.Write("run_command", labels, command, $"{(autoApproved ? "자동 승인(읽기 전용)" : "승인")}·실행 (성공 {okCount} / 실패 {results.Count - okCount})");

        var sb = new StringBuilder($"실행 완료: 성공 {okCount} / 실패 {results.Count - okCount} (총 {results.Count}대)\n");
        if (skipped.Count > 0) sb.AppendLine("제외: " + string.Join("; ", skipped));
        foreach (var r in results.OrderBy(r => r.Ok).ThenBy(r => r.Host.DisplayName))
        {
            sb.AppendLine();
            sb.AppendLine($"## {r.Host.DisplayName} ({r.Host.PrimaryAddress}{(r.Host.HasPublicIp ? $" / 공인 {r.Host.PublicIp}" : "")}) — {(r.ExitCode is { } c ? $"exit {c}" : "실패")}, {r.ElapsedMs / 1000.0:0.0}s");
            if (r.Output.Length > 0) sb.AppendLine(r.Output.TrimEnd());
            if (r.Error.Length > 0) sb.AppendLine("[stderr/오류] " + r.Error.TrimEnd());
        }
        return sb.ToString();
    }
}
