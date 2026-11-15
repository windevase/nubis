using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Nubis;

/// <summary>
/// 클러스터 탭: [도구 막대] [리소스 패널] [분할선] [kubectl 터미널(PowerShell, KUBECONFIG = 이 클러스터)].
/// 복호화한 kubeconfig 는 탭이 열려 있는 동안만 현재 사용자 전용 임시 파일로 두고, 탭을 닫으면 지운다.
/// </summary>
public sealed class K8sSessionView : UserControl, ISessionView
{
    private readonly K8sCluster _cluster;
    private readonly SecureTempFile _cfg;
    private readonly LocalTerminalView _term;
    private readonly ColumnDefinition _panelColumn = new() { Width = new GridLength(0) };
    private readonly GridSplitter _splitter;
    private K8sResourcePanel? _panel;
    private double _panelWidth = 460;

    public Guid ClusterId => _cluster.Id;
    public event Action<SessionStatus>? StatusChanged;
    /// <summary>파드 셸 탭 열기 (클러스터, 파드, 컨테이너)</summary>
    public event Action<K8sCluster, K8sItem, string?>? ShellRequested;
    /// <summary>파드 로그 탭 열기 (클러스터, 파드, 컨테이너, 이전 컨테이너)</summary>
    public event Action<K8sCluster, K8sItem, string?, bool>? LogsRequested;

    public K8sSessionView(K8sCluster cluster)
    {
        _cluster = cluster;
        _cfg = SecureTempFile.Create(SecureTempFile.K8sDir, Kubectl.EffectiveKubeconfig(cluster));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _term = new LocalTerminalView(() => K8sTerminal.ClusterShellCommand(cluster), home, L.T("PowerShell 이 종료되었습니다."),
            extraEnv: K8sTerminal.Environment(_cfg.Path), onClosed: () =>
            {
                _panel?.Shutdown();
                _cfg.Dispose();
            });
        _term.StatusChanged += s => StatusChanged?.Invoke(s);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(_panelColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var strip = BuildToolStrip();
        Grid.SetColumn(strip, 0);
        _splitter = new GridSplitter
        {
            Width = 4, Background = (System.Windows.Media.Brush)Application.Current.Resources["Line"],
            HorizontalAlignment = HorizontalAlignment.Center, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_splitter, 2);
        Grid.SetColumn(_term, 3);
        grid.Children.Add(strip);
        grid.Children.Add(_splitter);
        grid.Children.Add(_term);
        Content = grid;
        TogglePanel(true);
    }

    private FrameworkElement BuildToolStrip()
    {
        var res = Application.Current.Resources;
        var button = new Button
        {
            Style = (Style)res["IconButton"],
            Content = "",
            FontSize = 15,
            Width = 30,
            Height = 30,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = L.T("리소스 (파드 · 디플로이먼트 · 서비스 · 노드)"),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L.T("리소스 (파드 · 디플로이먼트 · 서비스 · 노드)"));
        button.Click += (_, _) => TogglePanel();
        var panel = new StackPanel();
        panel.Children.Add(button);
        return new Border
        {
            Width = 36,
            Background = (System.Windows.Media.Brush)res["SidebarBg"],
            BorderBrush = (System.Windows.Media.Brush)res["Line"],
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = panel,
        };
    }

    public void TogglePanel(bool? open = null)
    {
        var isOpen = _panelColumn.Width.Value > 0;
        var want = open ?? !isOpen;
        if (want == isOpen) return;
        if (want)
        {
            if (_panel == null)
            {
                _panel = new K8sResourcePanel(_cluster);
                _panel.CloseRequested += () => TogglePanel(false);
                _panel.ShellRequested += (pod, c) => ShellRequested?.Invoke(_cluster, pod, c);
                _panel.LogsRequested += (pod, c, prev) => LogsRequested?.Invoke(_cluster, pod, c, prev);
                // describe · YAML 은 오른쪽 터미널에 입력해 실행 (출력을 스크롤·검색·복사할 수 있게)
                _panel.TerminalCommand += cmd => { if (_term.SendText(cmd + "\r")) _term.FocusSession(); };
                Grid.SetColumn(_panel, 1);
                ((Grid)Content).Children.Add(_panel);
            }
            _panel.Visibility = Visibility.Visible;
            _panelColumn.MinWidth = 300;
            _panelColumn.Width = new GridLength(_panelWidth);
            _splitter.Visibility = Visibility.Visible;
        }
        else
        {
            _panelWidth = _panelColumn.ActualWidth > 0 ? _panelColumn.ActualWidth : _panelWidth;
            _panelColumn.MinWidth = 0;
            _panelColumn.Width = new GridLength(0);
            _splitter.Visibility = Visibility.Collapsed;
            if (_panel != null) _panel.Visibility = Visibility.Collapsed;
            _term.FocusSession();
        }
    }

    // ----- ISessionView: 터미널에 맡긴다 -----
    public void FocusSession() => _term.FocusSession();
    public void CloseSession() => _term.CloseSession();
    public Task<string?> ReadTerminalAsync(int lines) => _term.ReadTerminalAsync(lines);
    public Task<string?> GetSelectionAsync() => _term.GetSelectionAsync();
    public bool SendText(string text) => _term.SendText(text);
    public void Reconnect() => _term.Reconnect();
    public Task<bool> IsAtPromptAsync() => _term.IsAtPromptAsync();

    public async Task<string?> GetTerminalInfoAsync()
    {
        var info = await _term.GetTerminalInfoAsync();
        return $"쿠버네티스 클러스터 '{_cluster.Name}' 의 kubectl 터미널 (PowerShell, KUBECONFIG 가 이 클러스터로 설정됨, 기본 네임스페이스 {_cluster.NamespaceOrDefault})\n{info}";
    }
}

/// <summary>쿠버네티스 탭의 명령줄·환경변수 (화면 무관)</summary>
public static class K8sTerminal
{
    /// <summary>탭 자식 프로세스 환경: KUBECONFIG, 그리고 kubectl 이 PATH 밖에 있으면 그 폴더를 PATH 앞에</summary>
    public static IReadOnlyDictionary<string, string> Environment(string kubeconfigPath)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["KUBECONFIG"] = kubeconfigPath };
        if (Kubectl.Locate(SettingsStore.Current.KubectlPath) is { } exe && Path.GetDirectoryName(exe) is { } dir)
        {
            var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Split(';').Any(p => string.Equals(p.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                env["PATH"] = dir + ";" + path;
        }
        return env;
    }

    /// <summary>클러스터 터미널: PowerShell 에 이 클러스터를 알리는 안내 두 줄을 찍고 대기 (-EncodedCommand 라 이름에 따옴표가 있어도 안전)</summary>
    public static string ClusterShellCommand(K8sCluster c)
    {
        static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
        var line1 = L.F("⎈ {0}  ·  네임스페이스 {1}", c.Name, c.NamespaceOrDefault);
        var line2 = L.T("이 탭의 kubectl · k9s · helm 은 이 클러스터를 가리킵니다.");
        var script = $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Write-Host {Ps(line1)} -ForegroundColor Cyan; Write-Host {Ps(line2)} -ForegroundColor DarkGray";
        return "powershell.exe -NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>파드 셸: bash 가 있으면 bash, 없으면 sh</summary>
    public static List<string> ShellArgs(K8sCluster c, K8sItem pod, string? container)
    {
        var a = ContextArgs(c);
        a.AddRange(new[] { "exec", "-it", "-n", pod.Namespace, pod.Name });
        if (!string.IsNullOrEmpty(container)) { a.Add("-c"); a.Add(container); }
        a.AddRange(new[] { "--", "sh", "-c", "command -v bash >/dev/null 2>&1 && exec bash || exec sh" });
        return a;
    }

    public static List<string> LogsArgs(K8sCluster c, K8sItem pod, string? container, bool previous)
    {
        var a = ContextArgs(c);
        a.AddRange(new[] { "logs", "-f", "--tail=500", "-n", pod.Namespace, pod.Name });
        if (!string.IsNullOrEmpty(container)) { a.Add("-c"); a.Add(container); }
        if (previous) a.Add("--previous");
        return a;
    }

    private static List<string> ContextArgs(K8sCluster c) =>
        c.ContextName.Length > 0 ? new List<string> { "--context", c.ContextName } : new List<string>();

    /// <summary>CreateProcess 명령줄 (CommandLineToArgvW 규칙으로 인자 하나씩 감싼다)</summary>
    public static string CommandLine(string exe, IEnumerable<string> args) =>
        string.Join(" ", new[] { exe }.Concat(args).Select(Quote));

    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return arg;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>파드 셸·로그 탭 (kubectl 을 의사 콘솔로 직접 실행, 닫으면 kubectl 과 하위 프로세스까지 끝내고 임시 kubeconfig 를 지운다)</summary>
    public static LocalTerminalView PodView(K8sCluster c, List<string> args, string exitHint)
    {
        var exe = Kubectl.Locate(SettingsStore.Current.KubectlPath)
                  ?? throw new FileNotFoundException(L.T("kubectl 을 찾지 못했습니다. 설치하거나(winget install Kubernetes.kubectl) Kubernetes 페이지에서 경로를 지정하세요."));
        var cfg = SecureTempFile.Create(SecureTempFile.K8sDir, Kubectl.EffectiveKubeconfig(c));
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return new LocalTerminalView(() => CommandLine(exe, args), home, exitHint, killTreeOnClose: true,
            extraEnv: Environment(cfg.Path), onClosed: cfg.Dispose);
    }
}
