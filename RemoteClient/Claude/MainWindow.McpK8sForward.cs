using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nubis;

public partial class MainWindow
{
    // =================== MCP: 쿠버네티스 포트 포워딩 ===================
    // 이 PC 의 포트(127.0.0.1)를 여는 동작이라 start 는 사용자 승인 + 감사 로그. 포워딩은 클러스터 탭에 딸린다 (탭을 닫으면 멈춤)

    private McpTool K8sPortForwardTool() => new("k8s_port_forward",
        "쿠버네티스 포트 포워딩(kubectl port-forward)을 열고·보고·닫는다. 포워딩은 클러스터 탭에 딸려 탭 위 목록에 보이고, 탭을 닫으면 멈춘다. " +
        "action: list(기본) = 열린 클러스터 탭들의 포워딩 목록 (session_id | 주소 → 대상 | 상태) · " +
        "start = cluster 의 target(pod/x · svc/x · deploy/x)의 remote_port 를 이 PC 의 127.0.0.1:local_port 로 연다 (사용자 승인 필요, 감사 로그). " +
        "local_port 를 생략하면 remote_port 와 같은 번호, 쓰이고 있으면 빈 포트. 그 클러스터의 탭이 열려 있으면 거기에(여럿이면 session_id 로 고름), 없으면 새로 연다. " +
        "결과까지 최대 20초 기다린다 (연결됨 · 실패 사유 · 아직 시작 중). 다른 PC 에서는 접속할 수 없다(127.0.0.1 만). " +
        "stop = local_port 의 포워딩을 멈춘다 (session_id 로 좁힐 수 있음). 클러스터 리소스는 바꾸지 않는다.",
        Schema(new
        {
            action = new { type = "string", @enum = new[] { "list", "start", "stop" } },
            cluster = new { type = "string", description = "start: 클러스터 이름 또는 id (list_k8s_clusters)" },
            session_id = new { type = "integer", description = "클러스터 탭 session_id (start: 이 탭에 연다, stop: 이 탭에서만)" },
            target = new { type = "string", description = "start: pod/이름 · svc/이름 · deploy/이름 (종류를 생략하면 파드)" },
            @namespace = new { type = "string", description = "start: 네임스페이스 (생략하면 클러스터 기본 네임스페이스)" },
            remote_port = new { type = "integer", description = "start: 파드·서비스 쪽 포트 (서비스면 서비스 포트)" },
            local_port = new { type = "integer", description = "start: 이 PC 포트 (생략하면 자동) · stop: 멈출 포워딩의 로컬 포트" },
        }),
        a => Arg(a, "action").Trim().ToLowerInvariant() switch
        {
            "" or "list" => OnUi<object>(ListK8sForwards),
            "start" => StartK8sForwardFromClaude(a),
            "stop" => OnUi<object>(() => StopK8sForwardFromClaude(a)),
            _ => throw new McpToolException("action 은 list · start · stop 중 하나입니다."),
        });

    private IEnumerable<(SessionTab Tab, K8sSessionView View)> K8sClusterTabs() =>
        _tabs.Where(t => t.View is K8sSessionView).Select(t => (t, (K8sSessionView)t.View));

    private string ListK8sForwards()
    {
        var sb = new StringBuilder();
        foreach (var (tab, view) in K8sClusterTabs())
            foreach (var f in view.Forwards)
                sb.AppendLine($"session_id={tab.Id} | {tab.Title} | {f.Label} | -n {f.Namespace} | {Describe(f)}" +
                              (f.BrowserUrl is { } u ? $" | {u}" : ""));
        return sb.Length > 0 ? sb.ToString().TrimEnd() : "열린 포트 포워딩이 없습니다.";
    }

    /// <summary>상태 + 마지막 오류 (있으면)</summary>
    private static string Describe(K8sForward f) => StateText(f.State) + (f.LastError.Length > 0 ? " · " + f.LastError : "");

    private static string StateText(PortForwardState s) => s switch
    {
        PortForwardState.Active => "연결됨",
        PortForwardState.Starting => "시작 중",
        PortForwardState.Failed => "끝남",
        _ => "중지됨",
    };

    private async Task<object> StartK8sForwardFromClaude(JsonElement a)
    {
        // 인자 해석·검사 (UI 스레드: 탭 목록·설정)
        var (cluster, ns, kind, name, remote, local, line) = await OnUi(() =>
        {
            var c = ResolveK8sCluster(Arg(a, "cluster"));
            if (string.IsNullOrEmpty(c.Kubeconfig)) throw new McpToolException($"클러스터 '{c.Name}' 의 인증을 풀 수 없습니다. 사용자에게 다시 가져오라고 안내하세요.");
            if (Kubectl.Locate(SettingsStore.Current.KubectlPath) == null) throw new McpToolException("이 PC 에서 kubectl 을 찾지 못했습니다. 사용자에게 설치(winget install Kubernetes.kubectl)를 안내하세요.");
            // 승인을 받기 전에 탭 지정이 맞는지부터 (승인한 뒤 엉뚱한 탭이라 실패하지 않게 — 실제 탭은 승인 뒤 다시 고른다)
            if (IntArg(a, "session_id") is { } sid0 && !K8sClusterTabs().Any(x => x.Tab.Id == sid0 && x.View.ClusterId == c.Id))
                throw new McpToolException($"session_id={sid0} 은(는) 클러스터 '{c.Name}' 의 클러스터 탭이 아닙니다.");
            var target = K8sPortForward.ParseTarget(Arg(a, "target"))
                         ?? throw new McpToolException("target 은 pod/이름 · svc/이름 · deploy/이름 이어야 합니다 (쿠버네티스 이름: 소문자·숫자·-·.).");
            var n = Arg(a, "namespace").Trim();
            if (n.Length == 0) n = c.NamespaceOrDefault;
            if (!K8sPortForward.IsK8sName(n)) throw new McpToolException("namespace 는 쿠버네티스 이름(소문자·숫자·-·.)이어야 합니다.");
            var r = IntArg(a, "remote_port") is { } rp && K8sPortForward.ValidPort(rp) ? rp : throw new McpToolException("remote_port(1~65535)가 필요합니다.");
            int l;
            if (IntArg(a, "local_port") is { } lp)
            {
                if (!K8sPortForward.ValidPort(lp)) throw new McpToolException("local_port 는 1~65535 입니다.");
                if (!K8sPortForward.IsPortAvailable(lp)) throw new McpToolException($"이 PC 의 포트 {lp} 은(는) 이미 쓰이고 있습니다. 다른 local_port 를 주거나 생략하세요.");
                l = lp;
            }
            else l = K8sPortForward.SuggestLocalPort(r, K8sPortForward.PortProbe()) ?? throw new McpToolException("빈 로컬 포트를 찾지 못했습니다. local_port 를 지정하세요.");
            var args = (c.ContextName.Length > 0 ? new List<string> { "--context", c.ContextName } : new List<string>())
                .Concat(K8sPortForward.Args(n, target.Kind, target.Name, l, r));
            return (c, n, target.Kind, target.Name, r, l, "kubectl " + string.Join(" ", args.Select(K8sTerminal.Quote)));
        });

        var label = K8sLabel(cluster);
        var what = K8sPortForward.Label(local, K8sPortForward.TargetRef(kind, name), remote);
        var approved = await OnUiAsync(() => AskApprovalAsync("k8s_port_forward",
            L.F("Claude 가 클러스터 '{0}' 의 포트 포워딩을 요청합니다", McpHelpers.VisibleControlChars(cluster.Name)),
            L.F("승인하면 이 PC 의 127.0.0.1:{0} 이(가) 클러스터 안의 {1} (네임스페이스 {2}) 포트 {3} 으로 연결됩니다. 이 PC 의 프로그램이 그 포트로 클러스터에 접속할 수 있습니다 (다른 PC 는 불가). 클러스터 탭의 목록에서 중지하거나 탭을 닫으면 멈춥니다.",
                local, K8sPortForward.TargetRef(kind, name), ns, remote),
            new[] { label }, line));
        if (!approved)
        {
            AuditLog.Write("k8s_port_forward", new[] { label }, line, "거부");
            throw new McpToolException("사용자가 포트 포워딩을 거부했습니다.");
        }
        AuditLog.Write("k8s_port_forward", new[] { label }, line, "승인·시작");

        int sessionId;
        K8sForward forward;
        try
        {
            (sessionId, forward) = await OnUiAsync(async () =>
            {
                SessionTab? FindTab()
                {
                    if (IntArg(a, "session_id") is { } sid)
                    {
                        var found = _tabs.FirstOrDefault(t => t.Id == sid);
                        if (found?.View is not K8sSessionView v0 || v0.ClusterId != cluster.Id)
                            throw new McpToolException($"session_id={sid} 은(는) 클러스터 '{cluster.Name}' 의 클러스터 탭이 아닙니다.");
                        return found;
                    }
                    return K8sClusterTabs().Where(x => x.View.ClusterId == cluster.Id).Select(x => x.Tab).FirstOrDefault();
                }
                // 탭이 없어 새로 열 때만 RDP 입력 구간 잠금 안에서 (새 탭이 선택되며 진행 중인 RDP 입력을 끊지 않게 — open_k8s_session 과 같음).
                // 잠금을 기다리는 사이 탭이 생겼을 수 있어 안에서 다시 찾는다
                var tab = FindTab() ?? await UnderRdpInputSectionAsync(() =>
                    FindTab() ?? OpenK8sTab(cluster) ?? throw new McpToolException("클러스터 탭을 열지 못했습니다 (앱에 안내 창이 떴습니다)."));
                // 잠금을 기다리는 사이 탭이 닫혔을 수 있다
                if (!_tabs.Contains(tab) || tab.View is not K8sSessionView)
                    throw new McpToolException("기다리는 사이 클러스터 탭이 닫혔습니다. 다시 요청하세요.");
                try { return (tab.Id, ((K8sSessionView)tab.View).StartForward(ns, kind, name, local, remote, null)); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new McpToolException(ex.Message); }
            });
        }
        catch (McpToolException ex)
        {
            // 승인 뒤 시작하지 못했으면 그것도 감사 로그에 (승인·시작만 남지 않게)
            AuditLog.Write("k8s_port_forward", new[] { label }, line, "실패: " + ex.Message);
            throw;
        }

        // 연결됨·실패까지 최대 20초 (그 뒤에도 시작 중이면 그대로 알린다)
        try
        {
            for (var i = 0; i < 100; i++)
            {
                if (await OnUi(() => forward.State) != PortForwardState.Starting) break;
                await Task.Delay(200, McpRequest.Token).ConfigureAwait(true);
            }
            // 끝남이면 kubectl 의 마지막 오류 줄이 종료 이벤트보다 늦게 올 수 있다 — 사유를 담게 잠깐 더 기다린다
            if (await OnUi(() => forward.State == PortForwardState.Failed && forward.LastError.Length == 0))
                await Task.Delay(500, McpRequest.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }   // Claude 가 요청을 취소해도 승인된 포워딩은 그대로, 감사 로그는 남긴다
        var (state, status) = await OnUi(() => (forward.State, Describe(forward)));
        AuditLog.Write("k8s_port_forward", new[] { label }, line, $"결과: {status}");
        var sb = new StringBuilder($"{what} (네임스페이스 {ns}) — {status}\nsession_id={sessionId} (클러스터 탭 위 목록에 보임)");
        if (state == PortForwardState.Active)
            sb.Append($"\n이 PC 에서 {K8sPortForward.LocalAddress(local)} 로 접속하면 됩니다. 닫기: k8s_port_forward action=stop local_port={local}");
        else if (state == PortForwardState.Failed)
            sb.Append("\n포워딩이 시작되지 않았습니다. 대상·포트·권한(pods/portforward)을 확인하세요.");
        return sb.ToString();
    }

    private string StopK8sForwardFromClaude(JsonElement a)
    {
        var port = IntArg(a, "local_port") ?? throw new McpToolException("local_port 가 필요합니다. action=list 로 확인하세요.");
        var sid = IntArg(a, "session_id");
        var hits = K8sClusterTabs().Where(x => sid == null || x.Tab.Id == sid)
            .SelectMany(x => x.View.Forwards.Where(f => f.LocalPort == port && f.IsRunning).Select(f => (x.Tab, f))).ToList();
        if (hits.Count == 0) throw new McpToolException($"로컬 포트 {port} 의 실행 중인 포트 포워딩이 없습니다. action=list 로 확인하세요.");
        foreach (var (tab, f) in hits)
        {
            f.Stop();
            AuditLog.Write("k8s_port_forward", new[] { K8sLabel(f.Cluster) }, f.CommandLine, "중지");
        }
        return string.Join("\n", hits.Select(h => $"중지했습니다: {h.f.Label} (session_id={h.Tab.Id})"));
    }
}
