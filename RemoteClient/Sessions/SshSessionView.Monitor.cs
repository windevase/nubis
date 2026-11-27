using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Renci.SshNet;

namespace Nubis;

/// <summary>
/// SSH 탭: 실시간 서버 모니터 (CPU · 메모리 · 네트워크). 도구 막대 버튼으로 탭마다 켜고 끈다 (기본 꺼짐, 저장하지 않음).
/// 탭의 SSH 연결 위에 exec 채널로 고정된 읽기 전용 명령(<see cref="ServerStats.LinuxCommand"/>)을 2초마다 실행한다 — 다시 인증하지 않는다 (OTP 서버도).
/// 탭이 보일 때만(고른 탭 · 나란히 보기의 다른 쪽) 읽고, 숨겨지거나 끊기면 멈추고 다시 보이거나 다시 연결되면 이어 간다.
/// 값은 화면에 보일 마지막 값과 스파크라인용 최근 몇 개만 메모리에 둔다 — 기록·저장하지 않는다.
/// 읽기 · 계산은 백그라운드, 화면 갱신은 UI 스레드 (루프 자체가 UI 스레드에서 돌고 명령 실행만 Task.Run)
/// </summary>
public sealed partial class SshSessionView
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(2);
    /// <summary>읽기에 실패하면 (제한 셸 · /proc 없음 · 채널 수 제한) 이 간격으로 천천히 다시 시도</summary>
    private static readonly TimeSpan MonitorRetryInterval = TimeSpan.FromSeconds(10);

    private Button? _monButton;
    private ServerMonitorBar? _monBar;
    private bool _monOn;
    /// <summary>루프 세대 — 멈추면 올려 진행 중이던 루프가 결과를 버리고 끝나게 한다</summary>
    private int _monRun;
    private bool _monLoopActive;
    private CancellationTokenSource? _monCts;
    /// <summary>앞 샘플과 그것을 읽은 연결 (다시 연결되면 새 연결이라 차이를 계산하지 않는다)</summary>
    private ServerSample? _monPrev;
    private long _monPrevAt;
    private SshClient? _monClient;
    private ServerStats? _monLast;
    private DateTime _monLastAt;
    private string? _monError;
    /// <summary>
    /// 실행 중인 상태 명령 (없으면 null) — 탭을 빠르게 숨겼다 보이거나(탭 전환 · capture_screen · 나란히 보기) 껐다 켜도
    /// 느린 서버에 exec 채널이 겹겹이 쌓이지 않게 한 번에 하나만 (sshd MaxSessions 를 다 써 SFTP·run_command·포워딩 채널이 막히지 않게)
    /// </summary>
    private Task? _monProbe;
    /// <summary>마지막 상태 명령을 시작한 시각 (Stopwatch) — 다시 시작해도 MonitorInterval 보다 자주 읽지 않게</summary>
    private long _monProbeAt;

    /// <summary>모니터 켜짐 (도구 막대 버튼 · 띠의 닫기)</summary>
    public bool MonitorOn => _monOn;

    private FrameworkElement BuildMonitorButton()
    {
        _monButton = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Content = "",
            FontSize = 15,
            Width = 30,
            Height = 30,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = L.T("서버 모니터 (CPU · 메모리 · 네트워크)"),
        };
        _monButton.Click += (_, _) => ToggleMonitor();
        return _monButton;
    }

    /// <summary>터미널 열: [모니터 띠(접힘)] 위 [터미널]</summary>
    private FrameworkElement BuildTerminalArea()
    {
        var area = new Grid();
        area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        area.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_term, 1);
        area.Children.Add(_term);
        IsVisibleChanged += (_, _) => UpdateMonitorRunning();
        StatusChanged += _ => UpdateMonitorRunning();
        return area;
    }

    public void ToggleMonitor(bool? on = null)
    {
        var want = on ?? !_monOn;
        if (want == _monOn) return;
        _monOn = want;
        if (want)
        {
            if (_monBar == null)
            {
                _monBar = new ServerMonitorBar();
                _monBar.CloseRequested += () => ToggleMonitor(false);
                Grid.SetRow(_monBar, 0);
                ((Grid)_term.Parent).Children.Add(_monBar);
            }
            _monBar.Reset();
            _monBar.ShowStatus(L.T("읽는 중…"));   // 껐다 켤 때 앞의 오류·상태 글이 남지 않게
            _monBar.Visibility = Visibility.Visible;
            _monButton!.Foreground = (Brush)Application.Current.Resources["AccentText"];
            _monButton.ToolTip = L.T("서버 모니터 끄기");
        }
        else
        {
            if (_monBar != null) _monBar.Visibility = Visibility.Collapsed;
            _monButton!.ClearValue(ForegroundProperty);
            _monButton.ToolTip = L.T("서버 모니터 (CPU · 메모리 · 네트워크)");
            _monPrev = null;
            _monLast = null;
            _monError = null;
        }
        UpdateMonitorRunning();
    }

    /// <summary>켜져 있고 · 탭이 보이고 · 연결됨이면 읽기 루프를 돌리고, 아니면 멈춘다 (UI 스레드)</summary>
    private void UpdateMonitorRunning()
    {
        var want = _monOn && !_closed && IsVisible && _status == SessionStatus.Connected;
        if (want && !_monLoopActive)
        {
            _monLoopActive = true;
            // 다시 시작(다시 보임 · 다시 연결): 오류가 아니면 "일시 정지 · 연결되지 않음" 글을 지운다
            if (_monBar != null && _monError == null) _monBar.ShowStatus(_monLast != null ? null : L.T("읽는 중…"));
            var cts = new CancellationTokenSource();
            _monCts = cts;
            _ = MonitorLoopAsync(++_monRun, cts.Token);
        }
        else if (!want && _monLoopActive)
            StopMonitorLoop();
        if (!want && _monOn && _monBar != null && !_closed)
            _monBar.ShowStatus(_status != SessionStatus.Connected ? L.T("연결되지 않음") : L.T("일시 정지 (탭이 보이지 않음)"));
    }

    private void StopMonitorLoop()
    {
        _monRun++;
        _monLoopActive = false;
        var cts = _monCts;
        _monCts = null;
        try { cts?.Cancel(); } catch { }
        cts?.Dispose();
    }

    private async Task MonitorLoopAsync(int run, CancellationToken ct)
    {
        try
        {
            while (run == _monRun && !ct.IsCancellationRequested)
            {
                var delay = MonitorInterval;
                // 창을 최소화하면 보이지 않지만 IsVisible 은 그대로다 — 읽지 않고 기다린다
                // 연결 직후엔 로그인 계정 대입이 상태 알림보다 조금 늦을 수 있어 SharedConnection 이 잠깐 비어 있다 — 그때는 기다린다
                if (Window.GetWindow(this) is not { WindowState: WindowState.Minimized } && SharedConnection is { } conn)
                {
                    var client = conn.Client;
                    if (!ReferenceEquals(client, _monClient))
                    {
                        _monClient = client;
                        _monPrev = null;
                        _monLast = null;   // 앞 연결의 값을 새 연결의 값처럼 보고하지 않게 (get_server_stats)
                        _monBar?.Reset();
                    }
                    if (SshServerOs.Classify(client.ConnectionInfo?.ServerVersion) == SshServerOs.Windows)
                    {
                        // Windows OpenSSH: PowerShell 을 2초마다 띄우면 그 자체가 서버 CPU 를 눈에 띄게 써 값이 왜곡된다 — 이번엔 지원하지 않는다
                        _monError = L.T("Windows 서버는 아직 지원하지 않습니다 (Linux 만)");
                        _monBar?.ShowStatus(_monError, error: true);
                        return;
                    }
                    // 앞 루프(멈춘 뒤 결과를 버리는 중)의 명령이 아직 돌고 있으면 끝나길 기다린다 — 한 번에 하나만
                    if (_monProbe is { IsCompleted: false } pending)
                    {
                        try { await pending.WaitAsync(ct); } catch (OperationCanceledException) { return; }
                        continue;
                    }
                    // 다시 시작해도 간격을 지킨다 (빠른 탭 전환마다 바로 읽지 않게)
                    var wait = MonitorInterval - Stopwatch.GetElapsedTime(_monProbeAt);
                    if (_monProbeAt != 0 && wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, ct);
                        continue;
                    }
                    _monProbeAt = Stopwatch.GetTimestamp();
                    var probe = Task.Run(() => ProbeServer(client), CancellationToken.None);
                    _monProbe = probe;
                    var (sample, error) = await probe;
                    if (run != _monRun || ct.IsCancellationRequested || !ReferenceEquals(client, _monClient)) return;
                    // 명령 중에 연결이 끊겼거나 다시 연결 중이면(상태는 바뀌었지만 알림이 아직) 성공·실패 모두 버린다 —
                    // 앞 연결의 값을 지금 값처럼 보이지도, 오류로 보이지도 않고 상태 알림(연결되지 않음)에 맡긴다
                    if (!ReferenceEquals(SharedConnection?.Client, client)) continue;
                    var now = Stopwatch.GetTimestamp();
                    if (sample != null)
                    {
                        var stats = ServerStats.Compute(_monPrev, sample, Stopwatch.GetElapsedTime(_monPrevAt, now).TotalSeconds);
                        _monPrev = sample;
                        _monPrevAt = now;
                        _monLast = stats;
                        _monLastAt = DateTime.Now;
                        _monError = null;
                        _monBar?.Show(stats);
                        _monBar?.ShowStatus(null);
                    }
                    else
                    {
                        _monPrev = null;
                        _monLast = null;
                        _monError = error;
                        _monBar?.Reset();
                        _monBar?.ShowStatus(error, error: true);
                        delay = MonitorRetryInterval;
                    }
                }
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine("서버 모니터: " + ex); }
        finally
        {
            // 이 루프가 아직 현재 세대면(스스로 끝남: 끊김 · Windows) 다음 UpdateMonitorRunning 이 새로 시작할 수 있게
            if (run == _monRun) { _monLoopActive = false; _monCts?.Dispose(); _monCts = null; }
        }
    }

    /// <summary>탭의 SSH 연결에서 상태 명령을 한 번 실행 (백그라운드). 실패는 화면에 보일 짧은 사유로 돌려준다</summary>
    private static (ServerSample? Sample, string? Error) ProbeServer(SshClient client)
    {
        try
        {
            using var cmd = client.CreateCommand(ServerStats.LinuxCommand);
            cmd.CommandTimeout = TimeSpan.FromSeconds(10);
            cmd.Execute();
            var output = cmd.Result ?? "";
            if (output.Length > ServerStats.MaxOutputChars) return (null, L.T("서버 응답이 너무 커서 무시했습니다"));
            if (ServerStats.Parse(output) is { } sample) return (sample, null);
            var err = FirstLine(cmd.Error);
            return (null, err != null ? L.F("서버 상태를 읽지 못했습니다: {0}", err) : L.T("이 서버에서 /proc 을 읽지 못했습니다 (Linux 서버만 지원)"));
        }
        catch (Exception ex)
        {
            return (null, L.F("서버 상태를 읽지 못했습니다: {0}", FirstLine(ex.Message) ?? ex.GetType().Name));
        }
    }

    private static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var line = s.Trim().Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    /// <summary>탭 닫기: 루프를 멈춘다 (실행 중이던 명령은 시간 제한 안에 끝나고 결과는 버려진다)</summary>
    private void CloseMonitor()
    {
        _monOn = false;
        StopMonitorLoop();
        _monPrev = null;
        _monClient = null;
    }

    /// <summary>Claude get_server_stats — 모니터가 이미 읽어 둔 값만 돌려준다 (이 도구 때문에 서버에 명령을 보내지 않는다)</summary>
    public string GetServerStatsText()
    {
        var head = $"서버: {_host.DisplayName} ({_host.Address}:{_host.Port}) · 연결: {MainWindow.StatusText(_status)}";
        if (!_monOn)
            return head + "\n서버 모니터가 꺼져 있어 값이 없습니다. 사용자가 SSH 탭 왼쪽 도구 막대의 '서버 모니터' 버튼을 켜면 2초마다 읽습니다 " +
                   "(이 도구는 서버에 명령을 보내지 않음 — 켜 달라고 안내하거나, 사용자 승인을 받아 run_command 로 확인).";
        if (_monError != null) return head + "\n서버 모니터 오류: " + _monError;
        if (_monLast == null) return head + "\n서버 모니터가 켜져 있지만 아직 값이 없습니다 (2초 뒤 다시 확인).";
        var age = (DateTime.Now - _monLastAt).TotalSeconds;
        var paused = !_monLoopActive || Window.GetWindow(this) is { WindowState: WindowState.Minimized }
            ? " — 모니터 일시 정지 중(탭이 보이지 않음 · 창 최소화 · 연결 끊김)이라 최신 값이 아님" : "";
        return head + $"\n{_monLast.Describe()}\n측정: {age:0}초 전{paused}. CPU · 네트워크는 2초 간격 두 샘플의 차이, 메모리는 캐시·버퍼를 뺀 사용량.";
    }
}
