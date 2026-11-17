using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nubis;

/// <summary>
/// 쿠버네티스 관리 MCP 도구(update_k8s_cluster · find_k8s_clusters · import_k8s_clusters)의 UI 와 무관한 판단 (K8sMcpParityTests).
/// 메시지는 Claude 만 읽으므로 번역하지 않는다.
/// </summary>
internal static class McpK8sRules
{
    // ───────────── update_k8s_cluster ─────────────

    /// <summary>바꿀 값 (null = 그대로). Changes 는 승인 창·감사 로그·결과에 쓸 "항목: 전 → 후" 줄</summary>
    public sealed record ClusterEdit(string? Name, string? Namespace, string? Group, List<string> Changes)
    {
        public bool Empty => Changes.Count == 0;
    }

    /// <summary>
    /// update_k8s_cluster 인자 검사 (UI 이름 바꾸기 · 기본 네임스페이스 · 그룹과 같은 규칙):
    /// 이름은 비울 수 없고, 네임스페이스는 RFC 1123 label 또는 빈 값(kubeconfig 의 값 · 없으면 default), 클라우드 클러스터는 그룹을 바꿀 수 없다
    /// (UI 메뉴에도 없음 — 프로필 그룹을 따른다). 보이지 않는 제어·양방향 문자는 거부. 값이 지금과 같으면 바꾸지 않는다.
    /// 문제가 있으면 (null, 이유)
    /// </summary>
    public static (ClusterEdit? Edit, string? Error) ParseEdit(K8sCluster c, string? name, string? ns, string? group)
    {
        var changes = new List<string>();
        string? newName = null, newNs = null, newGroup = null;
        if (name != null)
        {
            var v = name.Trim();
            if (v.Length == 0) return (null, "name 은 비울 수 없습니다.");
            if (McpHelpers.HasInvisibleChars(v)) return (null, $"name 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(v)}");
            if (v != c.Name) { newName = v; changes.Add($"이름: {McpHelpers.VisibleControlChars(c.Name)} → {v}"); }
        }
        if (ns != null)
        {
            var v = ns.Trim();
            if (v.Length > 0 && !K8sCluster.IsValidNamespace(v))
                return (null, $"default_namespace '{McpHelpers.VisibleControlChars(v)}' 는 쿠버네티스 네임스페이스 이름이 아닙니다 (소문자·숫자·하이픈, 63자 이하). 빈 문자열은 kubeconfig 의 값(없으면 default).");
            if (v != c.DefaultNamespace)
            {
                newNs = v;
                changes.Add($"기본 네임스페이스: {Ns(c.DefaultNamespace)} → {Ns(v)}");
            }
        }
        if (group != null)
        {
            var v = group.Trim();
            if (c.IsCloud) return (null, "클라우드에서 가져온 클러스터는 그룹을 바꿀 수 없습니다 (클라우드 프로필 그룹을 따름 — UI 에도 없는 메뉴).");
            if (McpHelpers.HasInvisibleChars(v)) return (null, $"group 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(v)}");
            if (v != c.Group) { newGroup = v; changes.Add($"그룹: {(c.Group.Length > 0 ? McpHelpers.VisibleControlChars(c.Group) : "(없음)")} → {(v.Length > 0 ? v : "(없음)")}"); }
        }
        return (new ClusterEdit(newName, newNs, newGroup, changes), null);
    }

    private static string Ns(string v) => v.Length > 0 ? v : "(kubeconfig 값, 없으면 default)";

    /// <summary>승인을 기다리는 동안 다른 경로(UI 등)로 바뀐 항목이 있는가 — 사용자가 본 '전' 값과 달라졌으면 반영하지 않는다</summary>
    public static bool ChangedWhileWaiting(K8sCluster before, K8sCluster now, ClusterEdit e) =>
        (e.Name != null && now.Name != before.Name) ||
        (e.Namespace != null && now.DefaultNamespace != before.DefaultNamespace) ||
        (e.Group != null && (now.Group != before.Group || now.IsCloud));

    // ───────────── find_k8s_clusters ─────────────

    /// <summary>
    /// 찾을 프로필 id. specs 가 비면 UI '클라우드에서 찾기' 의 처음 체크와 같은 규칙(K8sProfilePick.DefaultChecked: 클러스터 동기화가 켜진 프로필,
    /// 하나도 없으면 전부). spec 마다 cloud 프로필 지정 규칙(CloudProfileEdit.Match)으로 정확히 하나여야 한다. 문제가 있으면 (null, 이유)
    /// </summary>
    public static (HashSet<Guid>? Ids, string? Error) ResolveFindProfiles(IReadOnlyList<CloudProfile> profiles, IReadOnlyList<string> specs)
    {
        if (specs.Count == 0) return (K8sProfilePick.DefaultChecked(profiles), null);
        var ids = new HashSet<Guid>();
        foreach (var raw in specs)
        {
            var spec = raw.Trim();
            if (spec.Length == 0) continue;
            var hit = CloudProfileEdit.Match(profiles, spec);
            if (hit.Count == 0) return (null, $"'{McpHelpers.VisibleControlChars(spec)}' 와 일치하는 클라우드 프로필이 없습니다. cloud_list_profiles 로 확인하세요.");
            if (hit.Count > 1)
                return (null, $"'{McpHelpers.VisibleControlChars(spec)}' 가 프로필 {hit.Count}개와 일치합니다. id 로 지정하세요: " +
                              string.Join(", ", hit.Select(p => $"{p.Id.ToString()[..8]} [{p.ProviderText}] {McpHelpers.VisibleControlChars(p.GroupName)}")));
            ids.Add(hit[0].Id);
        }
        if (ids.Count == 0) return (null, "profiles 에 프로필 이름이 없습니다. 생략하면 UI 와 같은 기본 선택(클러스터 동기화가 켜진 프로필, 없으면 전부)입니다.");
        return (ids, null);
    }

    /// <summary>
    /// MCP 찾기 계획: UI 와 달리 고르지 않은 프로필의 클러스터 동기화 끄기(disableUnselected)·클러스터 제거(removeUnselected)는
    /// 인자로 요청했을 때만 (Claude 가 profiles 를 일부만 넘겼다고 사용자가 켜 둔 동기화가 꺼지지 않게)
    /// </summary>
    public static K8sProfilePick.Plan FindPlan(IEnumerable<CloudProfile> profiles, IReadOnlySet<Guid> selected, IEnumerable<K8sCluster> clusters,
        bool disableUnselected, bool removeUnselected)
    {
        var plan = K8sProfilePick.Make(profiles, selected, clusters, removeUnselected);
        return disableUnselected ? plan : plan with { Disable = new List<CloudProfile>() };
    }

    /// <summary>승인 뒤 실제로 지울 클러스터: 승인 창에 보인 것 중 아직 있고, 그 프로필이 여전히 '고르지 않은' 프로필인 것만</summary>
    public static List<K8sCluster> StillRemovable(IEnumerable<K8sCluster> approved, IEnumerable<K8sCluster> current, IReadOnlySet<Guid> selected)
    {
        var now = current.Select(c => c.Id).ToHashSet();
        return approved.Where(c => now.Contains(c.Id) && c.CloudProfileId is { } pid && !selected.Contains(pid)).ToList();
    }

    // ───────────── import_k8s_clusters ─────────────

    /// <summary>
    /// source 파일 경로 검사 — import_hosts 와 같은 규칙(McpImport.BlockedPathReason, ~/.ssh 불허): 장치 경로 · UNC(이 PC 의 Windows 자격 증명이
    /// Claude 가 정한 서버로 가지 않게) · Nubis 데이터 폴더 · 개인키 이름 거부. ~/.kube 는 막지 않는다 — UI 가져오기가 기본으로 읽는 곳이고,
    /// 이 도구는 파일 내용(토큰·인증서)을 Claude 에게 돌려주지 않고 앱 안에서 DPAPI 로 저장만 한다 (미리보기는 context 이름·주소·인증 방식뿐)
    /// </summary>
    public static string? BlockedSourcePath(string path, string dataDir, string? rootDir, string sshDir) =>
        McpImport.BlockedPathReason(path, dataDir, rootDir, sshDir, allowSshDir: false);

    /// <summary>
    /// kubeconfig 가 가리키는 인증 파일(certificate-authority · client-certificate · client-key · tokenFile) 검사.
    /// 이 파일들은 가져올 때 읽혀 클러스터 인증으로 저장되고 그 클러스터 서버로 쓰인다 — 그래서
    /// UNC(NTLM 자격 증명 유출) · 장치 경로 · Nubis 데이터 폴더(계정·Vault) · ~/.ssh(개인키)는 거부한다.
    /// *.key · *.pem · *.crt 이름은 kubeconfig 인증서의 보통 이름이라 허용 (내용은 Claude 에게 가지 않는다). 문제 없으면 null
    /// </summary>
    public static string? BlockedReferencedFile(string path, string dataDir, string? rootDir, string sshDir)
    {
        var t = path.Trim();
        if (McpImport.IsUncLike(t)) return "네트워크 경로(UNC)·장치 경로의 인증 파일은 읽지 않습니다.";
        if (!Path.IsPathRooted(t)) return "상대 경로 인증 파일은 kubeconfig 파일 위치를 알 때만 읽습니다.";
        if (t.Length > 2 && t.IndexOf(':', 2) >= 0) return "잘못된 경로입니다.";
        string full;
        try { full = Path.GetFullPath(t); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "잘못된 경로입니다."; }
        if (McpImport.IsUncLike(full)) return "네트워크 경로(UNC)의 인증 파일은 읽지 않습니다.";
        var final = McpHelpers.FinalPath(full);
        if (McpImport.IsUncLike(final)) return "네트워크 경로로 이어지는 링크는 읽지 않습니다.";
        static bool Under(string p, string dir)
        {
            var d = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase) || p.Equals(d.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        static bool UnderAny(string full, string final, string dir)
        {
            string d;
            try { d = Path.GetFullPath(dir); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            var dFinal = McpHelpers.FinalPath(d);
            return Under(full, d) || Under(final, d) || Under(full, dFinal) || Under(final, dFinal);
        }
        if (UnderAny(full, final, dataDir) || (rootDir != null && UnderAny(full, final, rootDir)))
            return "Nubis 데이터 폴더(계정·Vault·설정)의 파일은 인증 파일로 읽지 않습니다.";
        if (UnderAny(full, final, sshDir)) return "~/.ssh 폴더(개인키)의 파일은 인증 파일로 읽지 않습니다.";
        return null;
    }

    /// <summary>
    /// 가져올 context 고르기. requested 가 비면 UI 가져오기 창의 처음 체크(DefaultSelected)와 같다.
    /// 반환: (고른 것 — 이미 있는 것 포함, 없는 이름)
    /// </summary>
    public static (List<KubeconfigImport.ContextInfo> Selected, List<string> Unknown) SelectContexts(
        IReadOnlyList<KubeconfigImport.ContextInfo> infos, IReadOnlyList<string> requested)
    {
        if (requested.Count == 0) return (infos.Where(i => i.DefaultSelected).ToList(), new());
        var selected = new List<KubeconfigImport.ContextInfo>();
        var unknown = new List<string>();
        foreach (var name in requested.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var hit = infos.FirstOrDefault(i => i.Context.Name == name);
            if (hit == null) unknown.Add(name);
            else selected.Add(hit);
        }
        return (selected, unknown);
    }

    /// <summary>인증 방식 표시 (exec 명령은 파일 이름만 — 인자에는 값이 들어 있을 수 있어 넣지 않는다)</summary>
    public static string AuthText(KubeconfigImport.ContextInfo i) =>
        new K8sCluster { AuthKind = i.Auth }.AuthBadge + (i.ExecCommand is { } cmd ? $" (exec: {McpHelpers.VisibleControlChars(Path.GetFileName(cmd))})" : "");

    /// <summary>
    /// 미리보기 표 — context 이름 · API 서버 · 인증 방식 · 네임스페이스 · 상태만 (토큰 · 인증서 · 키 · exec 인자는 넣지 않는다)
    /// </summary>
    public static string FormatPreview(string sourceLabel, IReadOnlyList<KubeconfigImport.ContextInfo> infos,
        IReadOnlyCollection<KubeconfigImport.ContextInfo> selected, IReadOnlyDictionary<string, int> fileCounts, int maxRows = 100)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"출처: {McpHelpers.VisibleControlChars(sourceLabel)}");
        sb.AppendLine($"context {infos.Count}개 (이미 있음 {infos.Count(i => i.AlreadyImported)}개) — 가져올 대상 {selected.Count(i => !i.AlreadyImported)}개");
        sb.AppendLine("상태 | context | API 서버 | 인증 | 네임스페이스 | 인증 파일");
        foreach (var i in infos.Take(maxRows))
        {
            var state = i.AlreadyImported ? "이미 있음" : selected.Contains(i) ? "가져옴" : "선택 안 함";
            var c = i.Context;
            sb.AppendLine($"{state} | {McpHelpers.VisibleControlChars(c.Name)} | {McpHelpers.VisibleControlChars(c.Server ?? "-")} | {AuthText(i)} | " +
                          $"{(K8sCluster.ImportedNamespace(c.Namespace) is { Length: > 0 } ns ? ns : "default")} | {fileCounts.GetValueOrDefault(c.Name)}개");
        }
        if (infos.Count > maxRows) sb.AppendLine($"… 외 {infos.Count - maxRows}개");
        return sb.ToString().TrimEnd();
    }
}
