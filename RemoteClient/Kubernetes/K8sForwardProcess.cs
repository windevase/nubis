using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;

namespace Nubis;

/// <summary>
/// 열린 kubectl port-forward 하나 (클러스터 탭의 포워딩 목록 행). kubectl 을 셸 없이 장시간 프로세스로 실행한다:
/// 인자는 ArgumentList, KUBECONFIG = 보안 임시 파일(실행 중에만 있고 끝나면 지움), --context 고정, KUBERC=off, 자식 환경 정리.
/// 프로세스는 ProcessJob(KILL_ON_JOB_CLOSE)에 넣어 앱이 비정상 종료해도 남지 않는다.
/// 파드 재시작 등으로 kubectl 이 끝나면 상태를 보이고 다시 시작할 수 있다 (서비스·디플로이먼트는 그때 파드를 다시 고른다)
/// </summary>
public sealed class K8sForward : IPortForwardRow
{
    private readonly K8sCluster _cluster;
    private readonly List<string> _args;
    private Process? _proc;
    /// <summary>실행 회차 — 이전 kubectl 의 늦게 온 출력·종료 이벤트가 다시 시작한 회차를 건드리지 않게</summary>
    private int _run;
    private SecureTempFile? _cfg;
    private bool _stopping;
    private string _lastError = "";
    private PortForwardState _state = PortForwardState.Stopped;
    private string _statusText = "";

    public K8sForward(K8sCluster cluster, string ns, K8sKind kind, string name, int local, int remote, string? scheme)
    {
        _cluster = cluster;
        Namespace = ns;
        Target = K8sPortForward.TargetRef(kind, name);
        LocalPort = local;
        RemotePort = remote;
        _args = K8sPortForward.Args(ns, kind, name, local, remote);
        if (KubectlArgs.Validate(_args) is { } bad) throw new ArgumentException(bad);
        BrowserUrl = K8sPortForward.BrowserUrl(local, scheme);
    }

    /// <summary>포워딩을 연 클러스터 (MCP 감사 로그 대상)</summary>
    public K8sCluster Cluster => _cluster;
    public string Namespace { get; }
    /// <summary>pod/x · deploy/x · svc/x</summary>
    public string Target { get; }
    public int LocalPort { get; }
    public int RemotePort { get; }
    public string Label => K8sPortForward.Label(LocalPort, Target, RemotePort);
    public string Detail => L.F("네임스페이스 {0}", Namespace);
    public string LocalAddress => K8sPortForward.LocalAddress(LocalPort);
    public string? BrowserUrl { get; }
    public PortForwardState State { get => _state; private set { if (_state == value) return; _state = value; Changed(); Changed(nameof(CanRestart)); Changed(nameof(IsRunning)); } }
    public string StatusText { get => _statusText; private set { if (_statusText == value) return; _statusText = value; Changed(); } }
    /// <summary>kubectl 이 마지막으로 찍은 오류 줄 (없으면 "") — MCP 결과용</summary>
    public string LastError => _lastError;
    public bool IsRunning => _state is PortForwardState.Starting or PortForwardState.Active;
    public bool CanRestart => !IsRunning;
    /// <summary>MCP 감사 로그 · 목록에 쓰는 명령줄 (kubectl --context x port-forward ...)</summary>
    public string CommandLine => "kubectl " + string.Join(" ", ContextArgs().Concat(_args).Select(K8sTerminal.Quote));

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>상태가 바뀜 (UI 스레드)</summary>
    public event Action<K8sForward>? StateChanged;

    private IEnumerable<string> ContextArgs() => _cluster.ContextName.Length > 0 ? new[] { "--context", _cluster.ContextName } : Array.Empty<string>();

    /// <summary>kubectl 을 실행한다 (UI 스레드에서). 실행 자체가 실패하면 예외 없이 Failed 상태로</summary>
    public void Start()
    {
        if (IsRunning) return;
        _stopping = false;
        _lastError = "";
        var run = ++_run;
        State = PortForwardState.Starting;
        StatusText = L.T("시작하는 중…");
        Process? started = null;
        try
        {
            var exe = Kubectl.Locate(SettingsStore.Current.KubectlPath)
                      ?? throw new FileNotFoundException(L.T("kubectl 을 찾지 못했습니다. 설치하거나(winget install Kubernetes.kubectl) Kubernetes 페이지에서 경로를 지정하세요."));
            // 이 앱의 다른 포워딩이 시작 중(아직 포트를 열기 전)이어도 겹치지 않게 먼저 잡는다 — 끝나면 OnExited · 실패 처리에서 놓는다
            if (!K8sPortForward.TryReserve(LocalPort, this))
                throw new InvalidOperationException(L.F("로컬 포트 {0} 은(는) 다른 포트 포워딩이 쓰고 있습니다.", LocalPort));
            if (!K8sPortForward.IsLocalPortFree(LocalPort))
                throw new InvalidOperationException(L.F("로컬 포트 {0} 을(를) 이미 다른 프로그램이 쓰고 있습니다.", LocalPort));
            _cfg = SecureTempFile.Create(SecureTempFile.K8sDir, Kubectl.EffectiveKubeconfig(_cluster));
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (var a in ContextArgs().Concat(_args)) psi.ArgumentList.Add(a);
            var persistent = ChildEnvironment.PersistentNames();
            foreach (var k in psi.Environment.Keys.ToList())
                if (ChildEnvironment.ShouldDrop(k, persistent)) psi.Environment.Remove(k);
            psi.Environment["KUBECONFIG"] = _cfg.Path;
            psi.Environment["KUBERC"] = "off";

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnLine(run, e.Data, stderr: false);
            p.ErrorDataReceived += (_, e) => OnLine(run, e.Data, stderr: true);
            p.Exited += (_, _) => Ui(() => OnExited(p));
            p.Start();
            started = p;
            ProcessJob.Add(p);
            _proc = p;
            try { p.StandardInput.Close(); } catch { }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            // 실행한 뒤에 실패했으면 kubectl 을 남기지 않는다
            if (started != null)
            {
                try { if (!started.HasExited) started.Kill(entireProcessTree: true); } catch { }
                try { started.Dispose(); } catch { }
            }
            CleanupConfig();
            K8sPortForward.Release(LocalPort, this);
            _proc = null;
            State = PortForwardState.Failed;
            StatusText = CloudProviders.FirstLine(ex.Message);
            StateChanged?.Invoke(this);
        }
    }

    /// <summary>멈춘다 (프로세스 트리를 끝내고 임시 kubeconfig 를 지운다). 이미 멈췄으면 아무것도 하지 않는다</summary>
    public void Stop()
    {
        _stopping = true;
        var p = _proc;
        if (p == null)
        {
            if (IsRunning) { State = PortForwardState.Stopped; StatusText = L.T("중지됨"); }
            return;
        }
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        // Exited 이벤트가 상태·임시 파일을 정리한다. 앱 종료 중이라 디스패처가 돌지 않아도 임시 파일은 바로 지운다
        try
        {
            if (p.WaitForExit(2000))
            {
                CleanupConfig();
                K8sPortForward.Release(LocalPort, this);
            }
        }
        catch { }
    }

    public void Restart()
    {
        if (IsRunning) return;
        Start();
    }

    private void OnLine(int run, string? line, bool stderr)
    {
        if (line == null) return;
        if (!stderr && K8sPortForward.ParseForwarding(line) != null)
        {
            Ui(() =>
            {
                if (run != _run || _state != PortForwardState.Starting) return;
                State = PortForwardState.Active;
                StatusText = L.T("연결됨");
                StateChanged?.Invoke(this);
            });
            return;
        }
        if (K8sPortForward.IsNoise(line)) return;
        var clean = K8sPortForward.CleanError(line);
        if (clean.Length == 0) return;
        Ui(() =>
        {
            if (run != _run) return;
            _lastError = clean;
            // 실행 중에도 오류(연결 하나 실패 등)를 보이되 상태는 그대로 — 끝나면 OnExited 가 마지막 오류로 바꾼다
            if (IsRunning && stderr) StatusText = _state == PortForwardState.Active ? L.F("연결됨 · {0}", clean) : clean;
            // Exited 는 출력 파이프를 다 읽기 전에 올 수 있다 — 종료 뒤에 도착한 오류 줄(포트 바인드 실패 등)도 상태에 보인다
            else if (_state == PortForwardState.Failed && stderr) StatusText = L.F("끝남 · {0}", clean);
        });
    }

    private void OnExited(Process p)
    {
        if (!ReferenceEquals(_proc, p)) return;
        int code;
        try { code = p.ExitCode; } catch { code = -1; }
        _proc = null;
        try { p.Dispose(); } catch { }
        CleanupConfig();
        K8sPortForward.Release(LocalPort, this);
        if (_stopping)
        {
            State = PortForwardState.Stopped;
            StatusText = L.T("중지됨");
        }
        else
        {
            State = PortForwardState.Failed;
            StatusText = _lastError.Length > 0 ? L.F("끝남 · {0}", _lastError) : L.F("끝남 (종료 코드 {0})", code);
        }
        StateChanged?.Invoke(this);
    }

    private void CleanupConfig()
    {
        var c = _cfg;
        _cfg = null;
        try { c?.Dispose(); } catch { }
    }

    private static void Ui(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) { a(); return; }
        try { d.BeginInvoke(a); } catch { }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
