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
            "끝에 이 PC 에서 찾은 kubectl 경로 한 줄. 인증 정보(kubeconfig·토큰)는 포함하지 않는다. " +
            "등록은 import_k8s_clusters(kubeconfig) · find_k8s_clusters(클라우드), 관리는 update_k8s_cluster · delete_k8s_cluster · test_k8s_cluster.",
            Schema(new { query = new { type = "string", description = "이름·주소·리전 부분 검색어 (생략하면 전체)" } }),
            a => OnUi(() => ListK8sClusters(Arg(a, "query"))));

        yield return new McpTool("run_kubectl",
            "등록된 클러스터 하나에 kubectl 을 실행하고 출력을 돌려준다 (이 PC 의 kubectl, 셸을 거치지 않음). 클러스터·인증은 앱이 고정하므로 " +
            "--kubeconfig · --context · --cluster · --user · -s/--server · --token · --as 는 쓸 수 없고, config view(--raw · --flatten 포함)도 저장된 인증이 나오므로 쓸 수 없다(주소·네임스페이스는 list_k8s_clusters). " +
            "이 PC 의 파일·프로그램을 쓰는 cp · kustomize · diff · edit · -k/--kustomize · --from-file · --from-env-file · --cert/--key 도 안 되고, -f 는 Nubis 데이터 폴더·~/.ssh 밖의 파일만. " +
            "대화형·끝나지 않는 명령(exec -it, attach, port-forward, proxy, debug, logs -f, --watch)은 안 된다 — 포트 포워딩은 k8s_port_forward, " +
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
            a => OnUiAsync(() => UnderRdpInputSectionAsync(() => OpenK8sFromClaude(a))));   // 새 탭이 선택되며 진행 중인 RDP 입력을 끊지 않게

        yield return K8sPortForwardTool();   // MainWindow.McpK8sForward.cs

        // ----- 관리 (Kubernetes 페이지의 손 동작과 같음 — MainWindow.McpK8sManage.cs · McpK8sImport.cs) -----

        yield return new McpTool("update_k8s_cluster",
            "등록된 클러스터의 이름 · 기본 네임스페이스 · 그룹을 바꾼다 (Kubernetes 페이지의 이름 바꾸기 · 기본 네임스페이스 · 그룹과 같음, 사용자 승인 필요). " +
            "넘긴 항목만 바뀐다. default_namespace 는 쿠버네티스 이름 규칙(소문자·숫자·하이픈), 빈 문자열이면 kubeconfig 의 값(없으면 default). " +
            "클라우드에서 가져온 클러스터는 그룹을 바꿀 수 없다 (클라우드 프로필 그룹을 따름). 클러스터 자체는 바꾸지 않는다.",
            Schema(new
            {
                cluster = new { type = "string", description = "클러스터 이름 또는 id (list_k8s_clusters)" },
                name = new { type = "string", description = "새 이름" },
                default_namespace = new { type = "string", description = "기본 네임스페이스 (빈 문자열 = kubeconfig 의 값)" },
                group = new { type = "string", description = "그룹 (빈 문자열 = 그룹 없음, 직접 가져온 클러스터만)" },
            }, "cluster"),
            a => OnUiAsync(() => UpdateK8sFromClaude(a)));

        yield return new McpTool("delete_k8s_cluster",
            "클러스터를 Nubis 목록에서 지운다 — 저장한 인증(kubeconfig)도 함께 지워지고 클러스터 자체에는 영향이 없다 (Kubernetes 페이지 삭제와 같음, 사용자 승인 필요). " +
            "클라우드에서 가져온 클러스터는 그 프로필의 클러스터 동기화가 켜져 있으면 다음 동기화 때 다시 나타날 수 있다.",
            Schema(new { cluster = new { type = "string", description = "클러스터 이름 또는 id" } }, "cluster"),
            a => OnUiAsync(() => DeleteK8sFromClaude(a)));

        yield return new McpTool("test_k8s_cluster",
            "클러스터 연결 테스트 (Kubernetes 페이지의 연결 테스트와 같음): kubectl version 으로 서버 버전, auth can-i list pods 로 파드 목록 권한을 확인하고 " +
            "페이지의 상태 점과 저장된 버전을 갱신한다. 읽기 전용이라 사용자가 읽기 전용 자동 승인을 켰으면 승인 없이, 아니면 승인 후 실행 (감사 로그는 항상).",
            Schema(new { cluster = new { type = "string", description = "클러스터 이름 또는 id" } }, "cluster"),
            a => OnUiAsync(() => TestK8sFromClaude(a)));

        yield return new McpTool("find_k8s_clusters",
            "클라우드 프로필로 쿠버네티스 클러스터(EKS·GKE·AKS·OKE·TKE)를 찾아 목록에 반영한다 (Kubernetes 페이지 '클라우드에서 찾기' 와 같음, 조회 API 만, 감사 로그). " +
            "찾은 프로필은 클러스터 동기화(sync_clusters)를 켜 앞으로 VM 동기화 때 함께 갱신된다. profiles 를 생략하면 화면의 처음 선택과 같다 " +
            "(클러스터 동기화가 켜진 프로필, 하나도 없으면 전부). 고르지 않은 프로필은 기본으로 그대로 두고, disable_unselected=true 면 그 프로필의 클러스터 동기화를 끄고, " +
            "remove_unselected_clusters=true 면 그 프로필의 클러스터를 목록에서 지우고 그 프로필의 클러스터 동기화도 끈다 (켜 두면 다음 동기화 때 다시 생기므로 — UI 와 같음). " +
            "클러스터 동기화가 실제로 바뀌거나(새로 켜짐·꺼짐) 클러스터를 지우면 사용자 승인 한 번, 이미 동기화가 켜진 프로필만 찾으면 승인 없이 조회. 여러 리전을 훑어 최대 3분 걸릴 수 있다.",
            Schema(new
            {
                profiles = new { type = "array", items = new { type = "string" }, description = "프로필 id(앞 8자 이상)·표시 이름·프로필 이름 (cloud_list_profiles). 생략하면 기본 선택" },
                disable_unselected = new { type = "boolean", description = "고르지 않은 프로필의 클러스터 동기화 끄기 (기본 false)" },
                remove_unselected_clusters = new { type = "boolean", description = "고르지 않은 프로필의 클러스터를 목록에서 지우기 (기본 false, 사용자 승인)" },
            }),
            a => OnUiAsync(() => FindK8sFromClaude(a)));

        yield return new McpTool("import_k8s_clusters",
            "kubeconfig 의 context 를 클러스터로 등록한다 (Kubernetes 페이지 'kubeconfig 가져오기' 와 같음). apply=false(기본)면 미리보기만: context 이름 · API 서버 · 인증 방식 · " +
            "네임스페이스 · 이미 있음 여부 (토큰·인증서·키는 돌려주지 않는다). apply=true 면 사용자 승인 한 번 뒤 앱이 파일을 읽어 인증을 이 PC 에 암호화 저장한다. " +
            "source: \"default\"(KUBECONFIG 환경 변수의 파일들, 없으면 ~/.kube/config) 또는 kubeconfig 파일 경로 (네트워크 경로 · Nubis 데이터 폴더 · ~/.ssh 불가). " +
            "kubeconfig 내용을 붙여 넣는 방식은 지원하지 않는다 — 사용자가 파일로 저장하게 안내. contexts 를 생략하면 화면과 같은 기본 선택(current-context, 등록된 클러스터가 없으면 전부). " +
            "같은 API 서버 + context 이름이 이미 있으면 건너뛴다.",
            Schema(new
            {
                source = new { type = "string", description = "\"default\" 또는 kubeconfig 파일 경로" },
                contexts = new { type = "array", items = new { type = "string" }, description = "가져올 context 이름 (생략하면 기본 선택)" },
                group = new { type = "string", description = "넣을 그룹 (생략 = 그룹 없음)" },
                apply = new { type = "boolean", description = "true = 등록 (사용자 승인), false/생략 = 미리보기" },
            }, "source"),
            ImportK8sFromClaude);
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
        {
            var none = new StringBuilder(query.Length == 0
                ? "등록된 클러스터가 없습니다. import_k8s_clusters(kubeconfig 가져오기) 또는 find_k8s_clusters(클라우드에서 찾기)로 등록할 수 있습니다.\n"
                : $"'{McpHelpers.VisibleControlChars(query)}' 와 일치하는 클러스터가 없습니다.\n");
            AppendKubectlLine(none);
            return none.ToString();
        }
        var sb = new StringBuilder($"{list.Count}개\n");
        foreach (var c in list)
        {
            var group = c.CloudProfileId is { } pid && profiles.TryGetValue(pid, out var p) ? $"[{p.ProviderText}] {p.GroupName}" : c.Group.Length > 0 ? c.Group : "직접 등록";
            sb.AppendLine($"{c.Id.ToString()[..8]} | {McpHelpers.VisibleControlChars(c.Name)} | {group} | {c.AuthBadge} | {c.Endpoint} | {(c.Version.Length > 0 ? c.Version : "-")} | " +
                          $"{(string.IsNullOrEmpty(c.Region) ? "-" : c.Region)} | {c.NamespaceOrDefault} | {(c.State ?? "-")}");
        }
        AppendKubectlLine(sb);
        return sb.ToString();
    }

    /// <summary>찾은 kubectl 경로 한 줄 (Kubernetes 페이지 위쪽 줄과 같은 정보). 없으면 설치 안내</summary>
    private static void AppendKubectlLine(StringBuilder sb)
    {
        if (Kubectl.Locate(SettingsStore.Current.KubectlPath) is { } path) sb.AppendLine($"kubectl: {path}");
        else sb.AppendLine("주의: 이 PC 에서 kubectl 을 찾지 못했습니다 — run_kubectl 은 실패합니다. 사용자에게 설치(winget install Kubernetes.kubectl)를 안내하세요.");
    }

    /// <summary>
    /// run_kubectl 에서 받지 않는 대화형·장시간 명령 (결과가 끝나지 않거나 편집기·포트를 연다).
    /// 하위 명령은 KubectlArgs.SubcommandIndex(cobra 처럼 루트의 값 받는 옵션 -n x · -v 6 등을 건너뜀)로 찾는다 (-n default proxy · -v 1 port-forward).
    /// 자리를 정할 수 없으면(모르는 옵션이 앞에 있음: -l -- edit · -A attach) 막을 명령이 끝 표시 앞 어디에 있든 거부한다.
    /// 옵션은 짧은 묶음(-pf · -Aw)·=값·_ 표기까지 본다
    /// </summary>
    internal static string? KubectlInteractiveReason(IReadOnlyList<string> args)
    {
        if (KubectlArgs.CommandIs(args, "attach", "port-forward", "proxy", "edit", "debug") is { } verb)
            return verb == "port-forward"
                ? "port-forward 는 장시간 명령이라 run_kubectl 로 실행할 수 없습니다. 포트 포워딩은 k8s_port_forward action=start (사용자 승인) 를 쓰세요."
                : $"'{verb}' 는 대화형·장시간 명령이라 run_kubectl 로 실행할 수 없습니다.";
        var before = args.Take(KubectlArgs.TerminatorIndex(args)).Select(x => x.Trim()).ToList();
        var at = KubectlArgs.SubcommandIndex(args);
        // 하위 명령을 확신할 수 없으면(모르는 옵션이 앞에 있음) 어디에든 있으면 그 명령으로 본다
        bool Is(string v) => at >= 0 ? args[at].Trim() == v : before.Contains(v);
        static (string Name, string? Value) Long(string x)
        {
            if (!x.StartsWith("--", StringComparison.Ordinal) || x.Length == 2) return ("", null);
            var eq = x.IndexOf('=');
            return (KubectlArgs.LongName(eq < 0 ? x : x[..eq]), eq < 0 ? null : x[(eq + 1)..]);
        }
        // pflag 의 bool 거짓 값 (--watch=false 는 끝나는 명령)
        static bool False(string? v) => v is "false" or "False" or "FALSE" or "f" or "F" or "0";
        if (Is("exec") && before.Any(x => KubectlArgs.ShortHas(x, "it") || Long(x) is { Name: "--stdin" or "--tty" } o && !False(o.Value)))
            return "exec -i/-t 는 대화형이라 run_kubectl 로 실행할 수 없습니다 — 비대화형 exec(예: exec pod -- ls /) 는 됩니다. 셸은 open_k8s_session kind=shell.";
        // --follow · --watch · --watch-only (어느 하위 명령이든: get · events · rollout status …), -w 묶음(-Aw), logs 의 -f 묶음(-pf · -f=true)
        // 짧은 -w=false · -f=false 는 끝나는 명령 (긴 --watch=false 와 같게)
        static bool ShortOff(string x, char c) => x.Length > 3 && x[0] == '-' && x[1] == c && x[2] == '=' && False(x[3..]);
        if (before.Any(x => KubectlArgs.ShortHas(x, "w") && !ShortOff(x, 'w') ||
                            Long(x) is var o && (o.Name.StartsWith("--follow", StringComparison.Ordinal) || o.Name.StartsWith("--watch", StringComparison.Ordinal)) && !False(o.Value)) ||
            Is("logs") && before.Any(x => KubectlArgs.ShortHas(x, "f") && !ShortOff(x, 'f')))
            return "-f/--follow/-w/--watch 는 끝나지 않아 run_kubectl 로 실행할 수 없습니다. 로그 탭은 open_k8s_session kind=logs.";
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
            var end = KubectlArgs.TerminatorIndex(list);
            var before = list.Take(end).ToList();
            if (ns.Length > 0 && !before.Any(x => KubectlArgs.ShortHas(x, "nA") || x.StartsWith("--namespace", StringComparison.Ordinal) || x.StartsWith("--all-namespaces", StringComparison.Ordinal)))
                list.InsertRange(end, new[] { "-n", ns });
            return (c, list);
        });
        if (KubectlArgs.Validate(args) is { } bad) throw new McpToolException(bad);
        if (KubectlInteractiveReason(args) is { } why) throw new McpToolException(why);
        if (string.IsNullOrEmpty(cluster.Kubeconfig)) throw new McpToolException($"클러스터 '{cluster.Name}' 의 인증을 풀 수 없습니다. 사용자에게 다시 가져오라고 안내하세요.");
        if (Kubectl.Locate(SettingsStore.Current.KubectlPath) == null) throw new McpToolException("이 PC 에서 kubectl 을 찾지 못했습니다. 사용자에게 설치(winget install Kubernetes.kubectl)를 안내하세요.");

        var timeout = Math.Clamp(IntArg(a, "timeout_sec") ?? 60, 5, 600);
        var line = "kubectl " + string.Join(" ", args.Select(x => K8sTerminal.Quote(x)));
        var label = K8sLabel(cluster);   // 엔드포인트도 kubeconfig 에서 온 값 — 보이지 않는 문자를 드러낸다
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
