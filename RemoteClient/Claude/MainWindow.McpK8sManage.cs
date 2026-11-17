using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nubis;

// MCP 도구: 쿠버네티스 클러스터 관리 — Kubernetes 페이지에서 손으로 하는 이름 바꾸기·기본 네임스페이스·그룹·삭제·연결 테스트·클라우드에서 찾기를
// Claude 도 할 수 있게 (K8sTools 에서 등록). 반영 로직은 UI 와 공용 (ChangeK8s · RemoveK8sCluster · RunK8sTestAsync · FindClustersAsync)
public partial class MainWindow
{
    private static string K8sLabel(K8sCluster c) =>
        $"{McpHelpers.VisibleControlChars(c.Name)}" + (c.Endpoint.Length > 0 ? $"  ({McpHelpers.VisibleControlChars(c.Endpoint)})" : "");

    // =================== update_k8s_cluster ===================

    /// <summary>
    /// 이름 · 기본 네임스페이스 · 그룹 변경. 승인을 받는다 — 기본 네임스페이스는 이후 run_kubectl · 클러스터 탭 터미널의 명령 대상을 바꾸고,
    /// 이름은 Claude 가 클러스터를 가리키는 이름이라 바뀌면 다른 클러스터로 착각할 수 있다. update_host · rename_group 도 승인을 받으므로 같은 정책
    /// </summary>
    private async Task<string> UpdateK8sFromClaude(JsonElement a)
    {
        var c = ResolveK8sCluster(Arg(a, "cluster"));
        string? Opt(string field) => Has(a, field) ? Arg(a, field) : null;
        var (edit, error) = McpK8sRules.ParseEdit(c, Opt("name"), Opt("default_namespace"), Opt("group"));
        if (edit == null) throw new McpToolException(error ?? "잘못된 인자입니다.");
        if (edit.Empty) return "바뀐 항목이 없습니다.";

        var label = K8sLabel(c);
        var text = string.Join("\n", edit.Changes);
        var description = L.F("클러스터 '{0}' 의 설정을 바꿉니다.", McpHelpers.VisibleControlChars(c.Name)) +
                          (edit.Namespace != null ? "\n" + L.T("기본 네임스페이스를 바꾸면 이후 이 클러스터에 실행하는 kubectl(클러스터 탭 터미널 · Claude)의 대상 네임스페이스가 바뀝니다.") : "");
        if (!await AskApprovalAsync("update_k8s_cluster", L.T("Claude 가 쿠버네티스 클러스터 설정 변경을 요청합니다"), description, new[] { label }, text))
        {
            AuditLog.Write("update_k8s_cluster", new[] { label }, text, "거부");
            throw new McpToolException("사용자가 클러스터 설정 변경을 거부했습니다.");
        }
        // 승인을 기다리는 동안 지워졌거나 같은 항목이 다른 경로로 바뀌었으면 사용자가 본 내용과 다르므로 반영하지 않는다
        if (K8sStore.Get(c.Id) is not { } now)
        {
            AuditLog.Write("update_k8s_cluster", new[] { label }, text, "승인 후 취소 (대기 중 클러스터가 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 클러스터가 목록에서 지워져 바꾸지 않았습니다.");
        }
        if (McpK8sRules.ChangedWhileWaiting(c, now, edit))
        {
            AuditLog.Write("update_k8s_cluster", new[] { label }, text, "승인 후 취소 (대기 중 같은 항목이 바뀜)");
            throw new McpToolException("승인을 기다리는 동안 같은 항목이 바뀌어 반영하지 않았습니다. list_k8s_clusters 로 확인한 뒤 다시 요청하세요.");
        }
        var result = ChangeK8s(c.Id, x =>
        {
            if (edit.Name != null) x.Name = edit.Name;
            if (edit.Namespace != null) x.DefaultNamespace = edit.Namespace;
            if (edit.Group != null) x.Group = edit.Group;
        }, out var saveError);
        switch (result)
        {
            case K8sChange.Gone:
                AuditLog.Write("update_k8s_cluster", new[] { label }, text, "승인 후 취소 (대기 중 클러스터가 지워짐)");
                throw new McpToolException("클러스터가 목록에서 지워져 바꾸지 않았습니다.");
            case K8sChange.Failed:
                AuditLog.Write("update_k8s_cluster", new[] { label }, text, "승인·저장 실패: " + saveError);
                throw new McpToolException("저장 실패: " + saveError);
        }
        AuditLog.Write("update_k8s_cluster", new[] { label }, text, "승인·변경");
        return $"바꿨습니다: {label}\n{text}";
    }

    // =================== delete_k8s_cluster ===================

    private async Task<string> DeleteK8sFromClaude(JsonElement a)
    {
        var c = ResolveK8sCluster(Arg(a, "cluster"));
        var label = K8sLabel(c);
        // 승인 창 문구는 UI 삭제 확인 창과 같다 (K8sDeleteMessage 공용)
        var description = K8sDeleteMessage(McpHelpers.VisibleControlChars(c.Name), c.IsCloud);
        var command = $"클러스터 삭제: {label} — Nubis 목록과 저장한 인증만 지움 (클러스터 자체는 그대로)" +
                      (c.IsCloud ? "\n클라우드에서 가져온 클러스터라 그 프로필의 클러스터 동기화가 켜져 있으면 다음 동기화 때 다시 나타날 수 있음" : "");
        if (!await AskApprovalAsync("delete_k8s_cluster", L.T("Claude 가 쿠버네티스 클러스터 삭제를 요청합니다"), description, new[] { label }, command))
        {
            AuditLog.Write("delete_k8s_cluster", new[] { label }, command, "거부");
            throw new McpToolException("사용자가 클러스터 삭제를 거부했습니다.");
        }
        if (K8sStore.Get(c.Id) == null)
        {
            AuditLog.Write("delete_k8s_cluster", new[] { label }, command, "승인 (대기 중 이미 지워짐)");
            return $"승인을 기다리는 동안 이미 지워진 클러스터입니다: {label}";
        }
        if (RemoveK8sCluster(c.Id) is { } error)
        {
            AuditLog.Write("delete_k8s_cluster", new[] { label }, command, "승인·삭제 실패: " + error);
            throw new McpToolException("저장 실패: " + error);
        }
        AuditLog.Write("delete_k8s_cluster", new[] { label }, command, "승인·삭제");
        var p = c.CloudProfileId is { } pid ? _profiles.FirstOrDefault(x => x.Id == pid) : null;
        return $"지웠습니다: {label}" +
               (p is { SyncClusters: true } ? $"\n주의: 프로필 {CloudProfileLabel(p)} 의 클러스터 동기화가 켜져 있어 다음 동기화 때 다시 나타날 수 있습니다 (끄려면 cloud_update_profile sync_clusters=false)." : "");
    }

    // =================== test_k8s_cluster ===================

    /// <summary>
    /// UI 연결 테스트와 같다 (RunK8sTestAsync 공용 — 상태 점·버전 반영). 클러스터를 바꾸지 않는 조회라 사용자가 켠 읽기 전용 자동 승인을 따르고,
    /// 아니면 승인을 받는다 (run_kubectl get 과 같은 정책 — 인증 플러그인이 이 PC 에서 실행되고 클러스터 서버에 접속한다). 감사 로그는 항상
    /// </summary>
    private async Task<string> TestK8sFromClaude(JsonElement a)
    {
        var c = ResolveK8sCluster(Arg(a, "cluster"));
        var label = K8sLabel(c);
        const string command = "kubectl version -o json · kubectl auth can-i list pods";
        if (Kubectl.Locate(SettingsStore.Current.KubectlPath) == null)
            throw new McpToolException("이 PC 에서 kubectl 을 찾지 못했습니다. 사용자에게 설치(winget install Kubernetes.kubectl)를 안내하세요.");
        if (string.IsNullOrEmpty(c.Kubeconfig))
            throw new McpToolException($"클러스터 '{McpHelpers.VisibleControlChars(c.Name)}' 의 인증을 풀 수 없습니다. 사용자에게 다시 가져오라고 안내하세요.");
        var autoApproved = SettingsStore.Current.AutoApproveReadOnly;
        if (!autoApproved &&
            !await AskApprovalAsync("test_k8s_cluster", L.F("Claude 가 클러스터 '{0}' 연결 테스트를 요청합니다", McpHelpers.VisibleControlChars(c.Name)),
                L.T("승인하면 이 PC 의 kubectl 로 서버 버전과 파드 목록 권한을 확인합니다. 클러스터는 바꾸지 않습니다."), new[] { label }, command))
        {
            AuditLog.Write("test_k8s_cluster", new[] { label }, command, "거부");
            throw new McpToolException("사용자가 연결 테스트를 거부했습니다.");
        }
        // 승인을 기다리는 동안 지워졌거나 인증이 바뀌었을 수 있다 — 최신 저장본으로
        if (K8sStore.Get(c.Id) is not { } cur)
        {
            AuditLog.Write("test_k8s_cluster", new[] { label }, command, "승인 후 취소 (대기 중 클러스터가 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 클러스터가 목록에서 지워졌습니다.");
        }
        var how = autoApproved ? "자동 승인(읽기 전용)" : "승인";
        AuditLog.Write("test_k8s_cluster", new[] { label }, command, how + "·실행 시작");
        K8sTestResult r;
        try { r = await RunK8sTestAsync(cur, McpRequest.Token); }
        catch (OperationCanceledException)
        {
            AuditLog.Write("test_k8s_cluster", new[] { label }, command, "요청 취소");
            throw new McpToolException("Claude 쪽에서 요청이 취소되어 연결 테스트를 멈췄습니다.");
        }
        AuditLog.Write("test_k8s_cluster", new[] { label }, command,
            how + "·" + (r.Ok ? $"연결됨 {r.Version}" + (r.PodsOk ? "" : " (파드 목록 권한 없음)") : r.TimedOut ? "시간 초과" : "연결 실패"));
        if (r.Ok)
            return $"연결됨: {label} — 쿠버네티스 {r.Version} · 파드 목록 권한 {(r.PodsOk ? "있음" : "없음 (auth can-i list pods = no)")}. " +
                   "Kubernetes 페이지의 상태 점과 버전을 갱신했습니다.";
        return $"연결 실패: {label} — {(r.TimedOut ? "응답이 없습니다 (시간 초과)." : r.Error)}\n상태 점을 실패로 표시했습니다.";
    }

    // =================== find_k8s_clusters ===================

    /// <summary>
    /// UI '클라우드에서 찾기' 와 같은 조회·반영 (FindClustersAsync 공용, 조회 API 만). 고른 프로필은 클러스터 동기화를 켠다 (UI 와 같음).
    /// 고르지 않은 프로필의 동기화 끄기 · 클러스터 제거는 인자로 요청했을 때만 (지우면 그 프로필의 동기화도 끈다 — UI 와 같음).
    /// 클러스터 동기화가 실제로 바뀌거나 클러스터를 지우면 승인 한 번, 이미 동기화가 켜진 프로필만 찾으면 조회뿐이라 승인 없이. 감사 로그는 항상
    /// </summary>
    private async Task<string> FindK8sFromClaude(JsonElement a)
    {
        if (_profiles.Count == 0)
            throw new McpToolException("등록한 클라우드 프로필이 없습니다. discover_sources · cloud_add_profile 로 먼저 추가하세요.");
        var specs = ListArg(a, "profiles");
        var (selected, error) = McpK8sRules.ResolveFindProfiles(_profiles.ToList(), specs);
        if (selected == null) throw new McpToolException(error ?? "잘못된 profiles 입니다.");
        var disable = BoolArg(a, "disable_unselected") == true;
        var remove = BoolArg(a, "remove_unselected_clusters") == true;
        if (_k8sFinding) throw new McpToolException("이미 클라우드에서 클러스터를 찾는 중입니다 (UI 또는 다른 요청). 끝난 뒤 다시 요청하세요.");

        var plan = McpK8sRules.FindPlan(_profiles.ToList(), selected, K8sStore.All(), disable, remove);
        var targets = plan.Fetch.Select(CloudProfileLabel).ToList();
        var command = $"find_k8s_clusters profiles={(specs.Count == 0 ? "(기본 선택)" : string.Join(",", targets))}" +
                      $" disable_unselected={(disable ? "true" : "false")} remove_unselected_clusters={(remove ? "true" : "false")}";
        var auditTargets = targets.Count > 0 ? targets : new List<string> { "(찾을 프로필 없음)" };
        if (plan.Fetch.Count == 0 && plan.Disable.Count == 0 && plan.Remove.Count == 0)
            return "찾을 프로필이 없습니다. profiles 로 프로필을 지정하세요 (cloud_list_profiles).";

        // 클러스터 동기화가 실제로 바뀌거나(켜짐·꺼짐) 클러스터를 지우면 승인 한 번 (cloud_update_profile sync_clusters 와 같은 정책).
        // 이미 동기화가 켜진 프로필만 찾는 경우는 조회뿐이라 승인 없이
        if (McpK8sRules.FindNeedsApproval(plan))
        {
            var enable = McpK8sRules.NewlyEnabled(plan);
            command += "\n" + string.Join("\n", McpK8sRules.FindChangeLines(plan, CloudProfileLabel));
            var desc = new List<string>();
            // 지우기 문구는 UI 확인 창과 같다
            if (plan.Remove.Count > 0)
                desc.Add(L.F("선택 해제한 프로필의 클러스터 {0}개를 목록에서 지울까요? 저장한 인증도 함께 지워집니다. 클러스터 자체에는 영향이 없습니다.", plan.Remove.Count));
            if (plan.Disable.Count > 0)
                desc.Add(plan.Remove.Count > 0
                    ? L.F("클라우드 프로필 {0}개의 클러스터 동기화를 끕니다. 지운 클러스터가 다음 동기화 때 다시 생기지 않고, 이 프로필의 클러스터는 더 이상 갱신하지 않습니다.", plan.Disable.Count)
                    : L.F("클라우드 프로필 {0}개의 클러스터 동기화를 끕니다. 이미 가져온 클러스터는 목록에 남고 더 이상 갱신하지 않습니다.", plan.Disable.Count));
            if (enable.Count > 0)
                desc.Add(L.F("클라우드 프로필 {0}개의 클러스터 동기화를 켭니다. 앞으로 VM 동기화 때 이 프로필의 클러스터도 함께 찾아 목록에 반영합니다 (조회 API 만).", enable.Count));
            if (plan.Fetch.Count > 0) desc.Add(L.F("승인하면 클라우드 프로필 {0}개에서 바로 클러스터를 찾습니다 (조회 API 만).", plan.Fetch.Count));
            var names = enable.Select(p => L.F("동기화 켬: {0}", CloudProfileLabel(p)))
                .Concat(plan.Disable.Select(p => L.F("동기화 끔: {0}", CloudProfileLabel(p))))
                .Concat(plan.Remove.Select(c => L.F("지움: {0}", McpHelpers.VisibleControlChars(c.Name)))).ToList();
            var title = plan.Remove.Count > 0 ? L.T("Claude 가 쿠버네티스 클러스터 제거를 요청합니다") : L.T("Claude 가 클러스터 동기화 설정 변경을 요청합니다");
            if (!await AskApprovalAsync("find_k8s_clusters", title, string.Join("\n", desc),
                    names.Take(20).Concat(names.Count > 20 ? new[] { L.F("… 외 {0}개", names.Count - 20) } : Array.Empty<string>()).ToList(),
                    command))
            {
                AuditLog.Write("find_k8s_clusters", auditTargets, command, "거부");
                throw new McpToolException("사용자가 거부했습니다. 클러스터 동기화도 바꾸지 않았고 찾기·제거도 하지 않았습니다." +
                    (enable.Count > 0 ? " 이미 클러스터 동기화가 켜진 프로필만 찾으면 승인 없이 조회합니다 (cloud_list_profiles 의 sync_clusters)." : "") +
                    (plan.Remove.Count > 0 ? " remove_unselected_clusters 없이 다시 요청할 수 있습니다." : ""));
            }
            // 승인을 기다리는 동안: 다른 찾기가 시작됐으면 겹치지 않게 멈추고, 지울 목록은 승인 창에 보인 것 중 아직 있는 것만
            if (_k8sFinding)
            {
                AuditLog.Write("find_k8s_clusters", auditTargets, command, "승인 후 취소 (대기 중 다른 찾기가 시작됨)");
                throw new McpToolException("승인을 기다리는 동안 다른 찾기가 시작되어 아무것도 바꾸지 않았습니다. 끝난 뒤 다시 요청하세요.");
            }
            plan = plan with { Remove = McpK8sRules.StillRemovable(plan.Remove, K8sStore.All(), selected) };
        }
        if (_k8sFinding) throw new McpToolException("이미 클라우드에서 클러스터를 찾는 중입니다. 끝난 뒤 다시 요청하세요.");
        // 대기 중 제거된 프로필은 빼고
        plan = plan with { Fetch = plan.Fetch.Where(_profiles.Contains).ToList(), Disable = plan.Disable.Where(_profiles.Contains).ToList() };

        var sb = new StringBuilder();
        // ApplyUnselected 는 동기화 끄기를 먼저 저장하고 지운다 — 지우기 저장이 실패해도 끈 것은 반영됨
        if (plan.Disable.Count > 0)
            sb.AppendLine($"클러스터 동기화를 끔: {string.Join(", ", plan.Disable.Select(CloudProfileLabel))}" +
                          (plan.Remove.Count > 0 && !disable ? " (지운 클러스터가 다음 동기화 때 다시 생기지 않게 — 지우기는 끄기를 겸함)" : ""));
        if (ApplyUnselected(plan) is { } removeError)
        {
            AuditLog.Write("find_k8s_clusters", auditTargets, command, "클러스터 제거 저장 실패: " + removeError);
            sb.AppendLine($"선택 해제한 프로필의 클러스터를 지우지 못했습니다 (저장 실패: {removeError}).");
        }
        else if (plan.Remove.Count > 0) sb.AppendLine($"선택 해제한 프로필의 클러스터 {plan.Remove.Count}개를 목록에서 지웠습니다.");
        if (plan.Fetch.Count == 0)
        {
            RefreshK8sIfVisible();
            AuditLog.Write("find_k8s_clusters", auditTargets, command, $"조회 없음 · 동기화 끔 {plan.Disable.Count} · 제거 {plan.Remove.Count}");
            return sb.Length > 0 ? sb.ToString().TrimEnd() : "할 일이 없습니다.";
        }

        AuditLog.Write("find_k8s_clusters", auditTargets, command, $"조회 시작 (동기화 끔 {plan.Disable.Count} · 제거 {plan.Remove.Count})");
        FlashHostStatus(L.F("클라우드 프로필 {0}개에서 클러스터를 찾는 중...", plan.Fetch.Count), ToastKind.Info);
        var r = await FindClustersAsync(plan.Fetch, McpRequest.Token);
        var outcome = r.Error != null ? "오류: " + r.Error : r.SaveError != null ? "목록 저장 실패: " + r.SaveError : $"클러스터 {r.Total}개 (새로 {r.Added}개)";
        AuditLog.Write("find_k8s_clusters", auditTargets, command, "조회·반영 " + outcome);
        if (r.Error != null) throw new McpToolException($"클러스터 찾기 실패: {r.Error}" + (sb.Length > 0 ? "\n" + sb.ToString().TrimEnd() : ""));
        if (r.SaveError != null) sb.AppendLine($"클러스터 목록을 저장하지 못했습니다: {r.SaveError}");
        else sb.AppendLine(r.Total == 0
            ? $"프로필 {r.Profiles}개에서 클러스터를 찾지 못했습니다."
            : $"클러스터 {r.Total}개를 찾았습니다 (새로 {r.Added}개). 고른 프로필은 클러스터 동기화를 켜 앞으로 VM 동기화 때 함께 갱신합니다.");
        foreach (var line in r.Lines) sb.AppendLine("- " + McpHelpers.VisibleControlChars(line));
        sb.Append("목록은 list_k8s_clusters 로 확인하세요.");
        return sb.ToString();
    }
}
