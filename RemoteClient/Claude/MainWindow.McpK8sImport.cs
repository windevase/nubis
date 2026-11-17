using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nubis;

// MCP 도구 import_k8s_clusters: Kubernetes 페이지의 'kubeconfig 가져오기' 와 같은 동작 (KubeconfigImport 공용).
// 파일은 앱이 읽어 DPAPI 로 저장하고, Claude 에게는 context 이름 · API 서버 · 인증 방식 · 이미 있음 여부만 돌려준다 (토큰 · 인증서 · 키 없음).
// 붙여넣기(텍스트) 출처는 일부러 받지 않는다 — Claude 가 만든 kubeconfig 를 그대로 등록하는 통로가 되지 않게 (파일 경로는 사용자가 승인 창에서 확인)
public partial class MainWindow
{
    private async Task<object> ImportK8sFromClaude(JsonElement a)
    {
        var source = Arg(a, "source").Trim();
        if (source.Length == 0) source = "default";
        // ~/.kube/config 처럼 홈 폴더 기준으로 적어도 되게 (kubectl 과 같은 표기)
        if (source.StartsWith("~/", StringComparison.Ordinal) || source.StartsWith("~\\", StringComparison.Ordinal))
            source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), source[2..]);
        var apply = BoolArg(a, "apply") == true;
        var requested = ListArg(a, "contexts");
        var group = Has(a, "group") ? Arg(a, "group").Trim() : "";
        if (McpHelpers.HasInvisibleChars(group)) throw new McpToolException($"group 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(group)}");
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        // ----- 원본 읽기 (UI 스레드 밖 — 파일 열기 · 경로 확인) -----
        var (label, src) = await Task.Run(() =>
        {
            if (source.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                // UI 가져오기 창의 기본 원본과 같다: KUBECONFIG 의 파일들 (없으면 ~/.kube/config). 사용자의 환경 변수라 Claude 가 정할 수 없다
                var paths = KubeconfigImport.DefaultPaths;
                return (string.Join("; ", paths), KubeconfigImport.ReadDefault(paths));
            }
            if (McpK8sRules.BlockedSourcePath(source, AppPaths.DataDir, AppPaths.RootDir, sshDir) is { } why) throw new McpToolException(why);
            var full = Path.GetFullPath(source);
            if (Directory.Exists(full)) throw new McpToolException($"폴더가 아니라 kubeconfig 파일 경로를 지정하세요: {full}");
            return (full, KubeconfigImport.ReadPath(full));
        });
        if (src.Error != null) throw new McpToolException(src.Error);
        KubeconfigDoc doc;
        try { doc = Kubeconfig.Load(src.Yaml); }
        catch (FormatException ex) { throw new McpToolException(ex.Message); }

        var infos = KubeconfigImport.Describe(doc, src.BaseDir, K8sStore.All());
        if (infos.Count == 0) return $"가져올 context 가 없습니다. 출처: {McpHelpers.VisibleControlChars(label)}";
        var (selected, unknown) = McpK8sRules.SelectContexts(infos, requested);
        if (unknown.Count > 0)
            throw new McpToolException($"이 kubeconfig 에 없는 context: {string.Join(", ", unknown.Select(McpHelpers.VisibleControlChars))}\n" +
                                       $"있는 context: {string.Join(", ", infos.Select(i => McpHelpers.VisibleControlChars(i.Context.Name)))}");
        var files = infos.ToDictionary(i => i.Context.Name, i => Kubeconfig.ReferencedFiles(doc, i.Context.Name, src.BaseDir), StringComparer.Ordinal);
        var counts = files.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        var preview = McpK8sRules.FormatPreview(label, infos, selected, counts);
        var importable = selected.Where(i => !i.AlreadyImported).ToList();
        var auditTargets = importable.Select(i => $"{McpHelpers.VisibleControlChars(i.Context.Name)} ({McpHelpers.VisibleControlChars(i.Context.Server ?? "-")})").ToList();

        if (!apply)
        {
            AuditLog.Write("import_k8s_clusters", auditTargets.Count > 0 ? auditTargets : new List<string> { "(대상 없음)" }, $"preview source={label}", "미리보기 (읽기만)");
            return preview + "\n\n등록하려면 같은 인자에 apply=true 를 붙여 다시 호출하세요 (사용자 승인 한 번). " +
                   (requested.Count == 0 ? "contexts 를 생략하면 '가져옴' 표시된 것(current-context, 등록된 클러스터가 없으면 전부)을 가져옵니다." : "");
        }
        if (importable.Count == 0) return "가져올 context 가 없습니다 (고른 것이 모두 이미 있음).\n" + preview;

        // 가져올 때 읽을 인증 파일 경로 검사 (승인 전) — UNC · 데이터 폴더 · ~/.ssh 는 인증으로 읽지 않는다
        string? CheckFiles()
        {
            foreach (var i in importable)
                foreach (var f in files[i.Context.Name])
                    if (McpK8sRules.BlockedReferencedFile(f, AppPaths.DataDir, AppPaths.RootDir, sshDir) is { } why)
                        return $"context '{McpHelpers.VisibleControlChars(i.Context.Name)}' 의 인증 파일 {McpHelpers.VisibleControlChars(f)}: {why}";
            return null;
        }
        if (await Task.Run(CheckFiles) is { } blocked) throw new McpToolException(blocked);

        var execs = importable.Where(i => i.ExecCommand != null).ToList();
        var lines = new List<string> { $"import_k8s_clusters source={label}" + (group.Length > 0 ? $" group=\"{group}\"" : "") };
        foreach (var i in importable)
        {
            lines.Add($"{McpHelpers.VisibleControlChars(i.Context.Name)} → {McpHelpers.VisibleControlChars(i.Context.Server ?? "-")} · {new K8sCluster { AuthKind = i.Auth }.AuthBadge}" +
                      (i.ExecCommand is { } cmd ? $" · exec 명령: {McpHelpers.VisibleControlChars(cmd)}" : ""));
            foreach (var f in files[i.Context.Name]) lines.Add($"    인증 파일: {McpHelpers.VisibleControlChars(f)}");
        }
        var command = string.Join("\n", lines);
        var description = L.T("kubeconfig 의 context 를 Nubis 쿠버네티스 클러스터로 등록합니다. 인증(토큰·인증서·키)은 이 PC 에 암호화해 저장하고 Claude 에게 보내지 않습니다.") +
                          "\n" + L.F("출처: {0}", McpHelpers.VisibleControlChars(label)) +
                          (execs.Count > 0 ? "\n" + L.T("⚠ exec 인증 명령이 있습니다. 이 클러스터에 kubectl 을 실행할 때마다 이 PC 에서 그 명령이 실행되니 아는 명령인지 확인하세요.") : "");
        var approvalTargets = auditTargets.Take(20).Concat(auditTargets.Count > 20 ? new[] { L.F("… 외 {0}개", auditTargets.Count - 20) } : Array.Empty<string>()).ToList();
        if (!await OnUiAsync(() => AskApprovalAsync("import_k8s_clusters", L.F("Claude 가 kubeconfig 에서 클러스터 {0}개 가져오기를 요청합니다", importable.Count),
                description, approvalTargets, command)))
        {
            AuditLog.Write("import_k8s_clusters", auditTargets, command, "거부");
            throw new McpToolException("사용자가 클러스터 가져오기를 거부했습니다.");
        }

        // ----- 승인 뒤: 인증 파일을 다시 확인하고 읽어 독립 kubeconfig 로 (UI 스레드 밖) -----
        var built = await Task.Run<(List<K8sCluster>? List, string? Error)>(() =>
        {
            if (CheckFiles() is { } why) return (null, why);
            var list = new List<K8sCluster>();
            foreach (var i in importable)
            {
                try { list.Add(KubeconfigImport.ToCluster(doc, i.Context, src.BaseDir, group)); }
                catch (FormatException ex) { return (null, $"{McpHelpers.VisibleControlChars(i.Context.Name)}: {ex.Message}"); }
            }
            return (list, null);
        });
        if (built.List == null)
        {
            AuditLog.Write("import_k8s_clusters", auditTargets, command, "승인·가져오기 실패: " + built.Error);
            throw new McpToolException("가져오지 못했습니다: " + built.Error);
        }

        // 저장 (UI 스레드 — 화면 갱신). 승인을 기다리는 동안 같은 클러스터가 생겼으면 건너뛴다 (UI 가져오기 창과 같은 중복 규칙)
        var (added, skipped, saveError) = await OnUi(() =>
        {
            var existing = K8sStore.All();
            var fresh = built.List.Where(c => !existing.Any(x => x.ContextName == c.ContextName && K8sSync.SameEndpoint(x.Endpoint, c.Endpoint))).ToList();
            var (n, err) = AddK8sClusters(fresh);
            if (n > 0) FlashHostStatus(L.F("클러스터 {0}개를 추가했습니다", n), ToastKind.Success);
            return (n, built.List.Count - fresh.Count, err);
        });
        AuditLog.Write("import_k8s_clusters", auditTargets, command,
            $"승인·등록 {added}개" + (skipped > 0 ? $" (대기 중 같은 클러스터가 생겨 {skipped}개 제외)" : "") + (saveError != null ? " · 저장 실패: " + saveError : ""));
        if (saveError != null && added == 0) throw new McpToolException("저장 실패: " + saveError);
        return $"클러스터 {added}개를 등록했습니다" + (skipped > 0 ? $" (승인을 기다리는 동안 같은 클러스터가 생겨 {skipped}개 제외)" : "") +
               (saveError != null ? $". 일부 저장 실패: {saveError}" : "") + ". list_k8s_clusters 로 확인하고 test_k8s_cluster 로 연결을 확인할 수 있습니다.";
    }
}
