using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

public partial class MainWindow
{
    // =================== MCP: 쿠버네티스 ===================
    // 클러스터는 Kubernetes 페이지에 등록된 것만. kubeconfig·토큰은 결과에 넣지 않고, kubectl 은 앱이 클러스터 인증을 고정해 실행한다

    private IEnumerable<McpTool> K8sTools()
    {
        yield return new McpTool("list_k8s_clusters",
            "Nubis 에 등록된 쿠버네티스 클러스터 목록 (Kubernetes 페이지). 형식: id | 이름 | 그룹 | 인증 방식 | API 주소 | 버전 | 리전 | 기본 네임스페이스 | 상태. " +
            "인증 정보(kubeconfig·토큰)는 포함하지 않는다. 클러스터 등록은 사용자가 Kubernetes 페이지에서 한다.",
            Schema(new { query = new { type = "string", description = "이름·주소·리전 부분 검색어 (생략하면 전체)" } }),
            a => OnUi(() => ListK8sClusters(Arg(a, "query"))));

        yield return new McpTool("run_kubectl",
            "등록된 클러스터 하나에 kubectl 을 실행하고 출력을 돌려준다 (이 PC 의 kubectl, 셸을 거치지 않음). 클러스터·인증은 앱이 고정하므로 " +
            "--kubeconfig · --context · --cluster · --user · -s/--server · --token · --as 는 쓸 수 없다. 대화형 명령(exec -it, attach, port-forward, proxy, edit, logs -f)은 안 된다 — " +
            "파드 셸·로그는 open_k8s_session 으로 탭을 연다. 실행 전에 앱이 사용자 승인을 받는다 (사용자가 읽기 전용 자동 승인을 켰고 get·describe·logs·top 등 읽기 전용이면 승인 생략, 감사 로그는 항상). " +
            "Secret 값 조회(get secret -o yaml 등)는 꼭 필요할 때만.",
            Schema(new
            {
                cluster = new { type = "string", description = "클러스터 이름 또는 id (list_k8s_clusters)" },
                args = new { type = "array", items = new { type = "string" }, description = "kubectl 인자 배열 (예: [\"get\",\"pods\",\"-n\",\"web\"]). command 와 둘 중 하나" },
                command = new { type = "string", description = "kubectl 뒤의 인자를 한 줄로 (예: \"get pods -n web -o wide\"). 따옴표 묶음 지원" },
                @namespace = new { type = "string", description = "네임스페이스 (인자에 -n/-A 가 없을 때 붙인다)" },
                timeout_sec = new { type = "integer", description = "제한 시간 (기본 60, 최대 600)" },
            }, "cluster"),
            RunKubectlFromClaude);

        yield return new McpTool("open_k8s_session",
            "클러스터 탭(왼쪽 파드 목록 + 오른쪽 kubectl 터미널), 파드 셸 탭, 파드 로그 탭을 앱에 연다. kind: cluster(기본) | shell | logs. " +
            "shell·logs 는 pod 와 namespace 가 필요하다. 연 탭의 터미널에 입력하려면 send_to_terminal (사용자 승인).",
            Schema(new
            {
                cluster = new { type = "string", description = "클러스터 이름 또는 id" },
                kind = new { type = "string", @enum = new[] { "cluster", "shell", "logs" } },
                pod = new { type = "string" },
                @namespace = new { type = "string" },
                container = new { type = "string", description = "컨테이너 (생략하면 기본 컨테이너)" },
                previous = new { type = "boolean", description = "logs: 재시작 전 컨테이너의 로그" },
            }, "cluster"),
            a => OnUi(() => OpenK8sFromClaude(a)));
    }

    /// <summary>이름(정확히 → 부분 일치) 또는 id(앞 8자리 이상)로 클러스터 하나</summary>
    private static K8sCluster ResolveK8sCluster(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) throw new McpToolException("cluster 가 필요합니다. list_k8s_clusters 로 확인하세요.");
        var all = K8sStore.All();
        var hit = all.Where(c => c.Name.Equals(spec, StringComparison.OrdinalIgnoreCase) ||
                                 (spec.Length >= 8 && c.Id.ToString().StartsWith(spec, StringComparison.OrdinalIgnoreCase))).ToList();
        if (hit.Count == 0) hit = all.Where(c => c.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        return hit.Count switch
        {
            1 => hit[0],
            0 => throw new McpToolException($"'{spec}' 와 일치하는 클러스터가 없습니다. list_k8s_clusters 로 확인하세요."),
            _ => throw new McpToolException($"'{spec}' 가 클러스터 {hit.Count}개와 일치합니다. id 로 지정하세요: " +
                                            string.Join(", ", hit.Select(c => $"{c.Id.ToString()[..8]} {McpHelpers.VisibleControlChars(c.Name)}"))),
        };
    }

    private string ListK8sClusters(string query)
    {
        var profiles = _profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var list = K8sStore.All()
            .Where(c => query.Length == 0 || new[] { c.Name, c.Endpoint, c.Region, c.ContextName }.Any(s => s?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (list.Count == 0)
            return query.Length == 0
                ? "등록된 클러스터가 없습니다. 사용자에게 Nubis 의 Kubernetes 페이지에서 kubeconfig 가져오기 또는 '클라우드에서 찾기' 를 안내하세요 (open_app_page)."
                : $"'{query}' 와 일치하는 클러스터가 없습니다.";
        var sb = new StringBuilder($"{list.Count}개\n");
        foreach (var c in list)
        {
            var group = c.CloudProfileId is { } pid && profiles.TryGetValue(pid, out var p) ? $"[{p.ProviderText}] {p.GroupName}" : c.Group.Length > 0 ? c.Group : "직접 등록";
            sb.AppendLine($"{c.Id.ToString()[..8]} | {McpHelpers.VisibleControlChars(c.Name)} | {group} | {c.AuthBadge} | {c.Endpoint} | {(c.Version.Length > 0 ? c.Version : "-")} | " +
                          $"{(string.IsNullOrEmpty(c.Region) ? "-" : c.Region)} | {c.NamespaceOrDefault} | {(c.State ?? "-")}");
        }
        if (Kubectl.Locate(SettingsStore.Current.KubectlPath) == null)
            sb.AppendLine("주의: 이 PC 에서 kubectl 을 찾지 못했습니다 — run_kubectl 은 실패합니다. 사용자에게 설치(winget install Kubernetes.kubectl)를 안내하세요.");
        return sb.ToString();
    }

    /// <summary>run_kubectl 에서 받지 않는 대화형·장시간 명령 (결과가 끝나지 않거나 편집기·포트를 연다)</summary>
    internal static string? KubectlInteractiveReason(IReadOnlyList<string> args)
    {
        var before = args.TakeWhile(x => x != "--").ToList();
        var verb = before.FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal)) ?? "";
        if (verb is "attach" or "port-forward" or "proxy" or "edit" or "debug")
            return $"'{verb}' 는 대화형·장시간 명령이라 run_kubectl 로 실행할 수 없습니다.";
        if (verb == "exec" && before.Any(x => x is "-i" or "-t" or "-it" or "-ti" or "--stdin" or "--tty" || x.StartsWith("--stdin=", StringComparison.Ordinal) || x.StartsWith("--tty=", StringComparison.Ordinal)))
            return "exec -i/-t 는 대화형이라 run_kubectl 로 실행할 수 없습니다 — 비대화형 exec(예: exec pod -- ls /) 는 됩니다. 셸은 open_k8s_session kind=shell.";
        if ((verb == "logs" || verb == "get") && before.Any(x => x is "-f" or "--follow" or "-w" or "--watch" or "--watch-only" || x.StartsWith("--follow=", StringComparison.Ordinal) || x.StartsWith("--watch", StringComparison.Ordinal)))
            return "-f/--follow/--watch 는 끝나지 않아 run_kubectl 로 실행할 수 없습니다. 로그 탭은 open_k8s_session kind=logs.";
        return null;
    }

    private async Task<object> RunKubectlFromClaude(JsonElement a)
    {
        var (cluster, args) = await OnUi(() =>
        {
            var c = ResolveK8sCluster(Arg(a, "cluster"));
            List<string> list;
            if (ListArg(a, "args") is { Count: > 0 } given) list = given.ToList();
            else
            {
                try { list = KubectlArgs.Split(Arg(a, "command")); }
                catch (FormatException ex) { throw new McpToolException(ex.Message); }
            }
            if (list.Count == 0) throw new McpToolException("args 또는 command 가 필요합니다.");
            var ns = Arg(a, "namespace").Trim();
            var before = list.TakeWhile(x => x != "--").ToList();
            if (ns.Length > 0 && !before.Any(x => x is "-n" or "-A" or "--all-namespaces" || x.StartsWith("--namespace", StringComparison.Ordinal) || x.StartsWith("-n=", StringComparison.Ordinal)))
            {
                var at = list.IndexOf("--");
                list.InsertRange(at < 0 ? list.Count : at, new[] { "-n", ns });
            }
            return (c, list);
        });
        if (KubectlArgs.Validate(args) is { } bad) throw new McpToolException(bad);
        if (KubectlInteractiveReason(args) is { } why) throw new McpToolException(why);
        if (string.IsNullOrEmpty(cluster.Kubeconfig)) throw new McpToolException($"클러스터 '{cluster.Name}' 의 인증을 풀 수 없습니다. 사용자에게 다시 가져오라고 안내하세요.");
        if (Kubectl.Locate(SettingsStore.Current.KubectlPath) == null) throw new McpToolException("이 PC 에서 kubectl 을 찾지 못했습니다. 사용자에게 설치(winget install Kubernetes.kubectl)를 안내하세요.");

        var timeout = Math.Clamp(IntArg(a, "timeout_sec") ?? 60, 5, 600);
        var line = "kubectl " + string.Join(" ", args.Select(x => K8sTerminal.Quote(x)));
        var label = $"{McpHelpers.VisibleControlChars(cluster.Name)}  ({cluster.Endpoint})";
        var secret = args.Any(ReadOnlyCommands.KubectlSecretRef);
        var autoApproved = SettingsStore.Current.AutoApproveReadOnly && KubectlArgs.IsReadOnly(args);
        if (!autoApproved)
        {
            var approved = await OnUiAsync(() => AskApprovalAsync("run_kubectl", L.F("Claude 가 클러스터 '{0}' 에 kubectl 실행을 요청합니다", McpHelpers.VisibleControlChars(cluster.Name)),
                L.T("승인하면 이 PC 의 kubectl 로 이 클러스터에 실행하고 결과를 Claude 에게 돌려줍니다.") +
                (secret ? L.T("\n⚠ Secret 을 조회합니다. 출력에 비밀번호·토큰이 담겨 Claude 에게 전달될 수 있습니다.") : "") +
                L.F("\n제한 시간: {0}초 (넘으면 중단)", timeout), new[] { label }, line));
            if (!approved)
            {
                AuditLog.Write("run_kubectl", new[] { label }, line, "거부");
                throw new McpToolException("사용자가 실행을 거부했습니다.");
            }
        }
        AuditLog.Write("run_kubectl", new[] { label }, line, $"{(autoApproved ? "자동 승인(읽기 전용)" : "승인")}·실행 시작 (제한 {timeout}초)");

        using var cts = new CancellationTokenSource();
        using var reqCancel = McpRequest.Token.Register(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
        await OnUi(() => { BeginRunStatus(cts, L.F("클러스터 '{0}' 에서 kubectl 실행 중: {1}", McpHelpers.VisibleControlChars(cluster.Name), FirstLine(line))); return 0; });
        Kubectl.Result r;
        try { r = await Kubectl.RunAsync(cluster, args, TimeSpan.FromSeconds(timeout), cts.Token); }
        catch (OperationCanceledException)
        {
            AuditLog.Write("run_kubectl", new[] { label }, line, "실행 후 사용자가 취소");
            throw new McpToolException("사용자가 실행을 취소했습니다. 이미 클러스터에 반영된 변경은 되돌려지지 않았을 수 있습니다.");
        }
        catch (Exception ex)
        {
            AuditLog.Write("run_kubectl", new[] { label }, line, $"실행 중 오류: {CloudProviders.FirstLine(ex.Message)}");
            throw new McpToolException(CloudProviders.FirstLine(ex.Message));
        }
        finally
        {
            await OnUi(() => { EndRunStatus(cts); return 0; });
        }
        AuditLog.Write("run_kubectl", new[] { label }, line,
            $"{(autoApproved ? "자동 승인(읽기 전용)" : "승인")}·실행 ({(r.TimedOut ? "시간 초과" : $"exit {r.ExitCode}")})");

        var sb = new StringBuilder($"{line}\n클러스터: {cluster.Name} ({cluster.Endpoint}) — {(r.TimedOut ? $"시간 초과({timeout}초)로 중단" : $"exit {r.ExitCode}")}\n");
        if (r.Output.Length > 0) sb.AppendLine().AppendLine(r.Output.TrimEnd());
        if (r.Error.Length > 0) sb.AppendLine().AppendLine("[stderr]").AppendLine(r.Error.TrimEnd());
        return sb.ToString();
    }

    private string OpenK8sFromClaude(JsonElement a)
    {
        var c = ResolveK8sCluster(Arg(a, "cluster"));
        var kind = Arg(a, "kind").Trim().ToLowerInvariant();
        if (kind is "" or "cluster")
        {
            var tab = OpenK8sTab(c) ?? throw new McpToolException("클러스터 탭을 열지 못했습니다 (kubectl 이 없거나 인증을 풀 수 없음 — 앱에 안내 창이 떴습니다).");
            AuditLog.Write("open_k8s_session", new[] { McpHelpers.VisibleControlChars(c.Name) }, "(클러스터 탭)", "열기");
            return $"클러스터 탭을 열었습니다: session_id={tab.Id}, {c.Name}. 오른쪽 터미널은 PowerShell(KUBECONFIG = 이 클러스터)입니다.";
        }
        if (kind is not ("shell" or "logs")) throw new McpToolException("kind 는 cluster · shell · logs 중 하나입니다.");
        var podName = Arg(a, "pod").Trim();
        var ns = Arg(a, "namespace").Trim();
        if (podName.Length == 0) throw new McpToolException("pod 가 필요합니다.");
        if (ns.Length == 0) ns = c.NamespaceOrDefault;
        // 파드·네임스페이스·컨테이너 이름은 쿠버네티스 이름 규칙 — kubectl 인자로 그대로 넘기므로 다른 문자는 받지 않는다
        static bool Name(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$");
        var container = Arg(a, "container").Trim();
        if (!Name(podName) || !Name(ns) || (container.Length > 0 && !Name(container)))
            throw new McpToolException("pod · namespace · container 는 쿠버네티스 이름(소문자·숫자·-·.)이어야 합니다.");
        var pod = new K8sItem(K8sKind.Pods, podName, ns, "", "", "", "", "", Array.Empty<string>(), true);
        var logs = kind == "logs";
        var opened = OpenK8sPodTab(c, pod, container.Length > 0 ? container : null, logs, BoolArg(a, "previous") == true)
                     ?? throw new McpToolException("탭을 열지 못했습니다 (kubectl 이 없거나 인증을 풀 수 없음 — 앱에 안내 창이 떴습니다).");
        AuditLog.Write("open_k8s_session", new[] { McpHelpers.VisibleControlChars(c.Name) }, $"({(logs ? "로그" : "셸")} 탭: {ns}/{podName}{(container.Length > 0 ? " -c " + container : "")})", "열기");
        return $"{(logs ? "로그" : "파드 셸")} 탭을 열었습니다: session_id={opened.Id}. 출력은 read_terminal, 입력은 send_to_terminal (승인 필요, 파드 안 셸이라 PowerShell 이 아님).";
    }
}
