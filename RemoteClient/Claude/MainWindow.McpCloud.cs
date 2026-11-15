using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>
/// Claude 패널 MCP 도구: 가져올 수 있는 서버 출처 찾기(discover_sources), 클라우드 프로필 추가(cloud_add_profile), CLI 로그인(cloud_login).
/// 보안 원칙:
/// - 조회(discover_sources)는 이 PC 의 설정 파일과 클라우드 조회 API 만 쓴다 (승인 없음, 감사 로그만)
/// - 프로필 추가 · 로그인 명령 실행은 항상 승인 + 감사 로그
/// - 명령줄에 들어가는 값은 탐색으로 찾은 후보·추가된 프로필 중에서만 고르고, 허용 문자만 통과시킨다 (임의 문자열을 cmd 에 넣지 않음)
/// </summary>
public partial class MainWindow
{
    /// <summary>프로바이더 하나의 후보 탐색 제한 시간</summary>
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(20);

    /// <summary>discover_sources 결과에 프로바이더마다 보여 줄 후보 최대 개수 (GCP 프로젝트가 수백 개일 수 있음)</summary>
    private const int DiscoverListLimit = 50;

    private IEnumerable<McpTool> CloudDiscoveryTools()
    {
        yield return new McpTool("discover_sources",
            "이 PC 에서 서버 목록을 가져올 수 있는 출처를 찾아 보고한다 (아무것도 바꾸지 않음). " +
            "클라우드(AWS·Tencent·GCP·Azure·OCI): 찾은 프로필·프로젝트·구독 후보와 이미 추가됐는지, 로그인 필요 여부, 로그인 명령. " +
            "온프레미스: ~/.ssh/config · mRemoteNG 연결 파일 · Active Directory 도메인 가입 여부. " +
            "사용자가 '서버 가져와 줘' 처럼 요청하면 먼저 이 도구로 무엇이 있는지 확인한다. " +
            $"후보는 클라우드마다 {DiscoverListLimit}개까지 보여 준다 — 더 많으면 provider 로 한 클라우드만, filter 로 이름 일부, offset 으로 다음 페이지를 본다.",
            Schema(new
            {
                provider = new { type = "string", @enum = McpCloudHelpers.ProviderNames, description = "이 클라우드만 조회 (생략 시 전체 + 온프레미스)" },
                filter = new { type = "string", description = "후보 이름·표시 이름에 이 문자열이 들어간 것만 (대소문자 무시)" },
                offset = new { type = "integer", description = $"클라우드마다 건너뛸 후보 수 (다음 페이지, 기본 0, 한 번에 {DiscoverListLimit}개)" },
            }),
            DiscoverSourcesAsync);

        yield return new McpTool("cloud_add_profile",
            "클라우드 프로필(AWS/Tencent/OCI 프로필, GCP 프로젝트, Azure 구독)을 Nubis 에 추가하고 바로 동기화해 서버를 호스트로 가져온다. " +
            "profiles 는 discover_sources 가 보여 준 후보 이름(또는 표시 이름), 'all' 이면 그 클라우드의 아직 추가 안 된 후보 전부. " +
            "이미 추가된 프로필은 건너뛴다. 추가 전에 앱이 사용자 승인을 받는다. 클라우드 리소스는 바꾸지 않는다(조회 API 만 사용).",
            Schema(new
            {
                provider = new { type = "string", @enum = McpCloudHelpers.ProviderNames, description = "클라우드 종류" },
                profiles = new { type = "array", items = new { type = "string" }, description = "추가할 후보 이름 목록 또는 [\"all\"]" },
            }, "provider", "profiles"),
            CloudAddProfileAsync);

        yield return new McpTool("cloud_login",
            "클라우드 CLI 로그인 명령(aws sso login, gcloud auth application-default login, az login, oci session authenticate, tccli configure)을 " +
            "새 콘솔 창에서 실행한다 (앱의 로그인 버튼과 같음). 브라우저 로그인·키 입력은 사용자가 직접 마쳐야 하며 이 도구는 완료를 기다리지 않는다. " +
            "profile 은 추가된 프로필 또는 discover_sources 의 후보 이름 (AWS·OCI·Tencent 는 프로필별 로그인, AWS 는 필수). 실행 전에 앱이 사용자 승인을 받는다.",
            Schema(new
            {
                provider = new { type = "string", @enum = McpCloudHelpers.ProviderNames, description = "클라우드 종류" },
                profile = new { type = "string", description = "프로필 이름 (생략 시 그 클라우드의 첫 번째 추가된 프로필)" },
            }, "provider"),
            CloudLoginAsync);
    }

    // =================== discover_sources ===================

    private sealed record ProviderDiscovery(ICloudProvider Provider, List<CloudCandidate>? Candidates, string? Error);

    /// <summary>후보 탐색 (제한 시간 · 요청 취소 반영). 시간 초과면 TimeoutException</summary>
    private static async Task<List<CloudCandidate>> DiscoverWithTimeoutAsync(ICloudProvider provider, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(DiscoverTimeout);
        // 파일을 읽는 프로바이더는 동기로 끝나므로 백그라운드에서 실행. 취소 토큰을 무시하는 SDK 호출도 있어 시간 제한을 따로 건다
        var work = Task.Run(() => provider.DiscoverAsync(cts.Token));
        // WaitAsync 는 먼저 끝나면 타이머·취소 등록을 바로 정리한다 (예전 Task.Delay 는 탐색이 끝난 뒤에도 제한 시간까지 타이머가 남았다)
        try { return await work.WaitAsync(DiscoverTimeout + TimeSpan.FromSeconds(2), outer); }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            // (탐색 자체가 TimeoutException 으로 끝난 경우는 원래 메시지 그대로 올린다 — 예전 동작)
            // 늦게 끝나는 탐색의 예외는 관찰만 한다 (UnobservedTaskException 방지)
            _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            outer.ThrowIfCancellationRequested();
            throw new TimeoutException($"{DiscoverTimeout.TotalSeconds:0}초 안에 응답이 없습니다");
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            throw new TimeoutException($"{DiscoverTimeout.TotalSeconds:0}초 안에 응답이 없습니다");
        }
        catch (OperationCanceledException)
        {
            // 요청 취소로 기다리기를 그만둔 경우에도 뒤에 끝나는 탐색의 예외를 관찰한다
            _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
    }

    private async Task<string> DiscoverSourcesAsync(JsonElement a)
    {
        var ct = McpRequest.Token;
        var providerArg = Arg(a, "provider").Trim();
        CloudProvider? only = null;
        if (providerArg.Length > 0)
            only = McpCloudHelpers.ParseProvider(providerArg)
                   ?? throw new McpToolException($"provider 는 {string.Join(", ", McpCloudHelpers.ProviderNames)} 중 하나입니다.");
        var filter = Arg(a, "filter").Trim();
        var offset = Math.Max(0, IntArg(a, "offset") ?? 0);

        // 프로바이더별 후보 탐색 (병렬)
        var discoveries = await Task.WhenAll(CloudProviders.All.Where(pr => only == null || pr.Kind == only).Select(async pr =>
        {
            try { return new ProviderDiscovery(pr, await DiscoverWithTimeoutAsync(pr, ct), null); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new McpToolException("Claude 쪽에서 요청이 취소되었습니다."); }
            catch (Exception ex) { return new ProviderDiscovery(pr, null, CloudProviders.FirstLine(ex.Message)); }
        }));

        // 이미 추가된 프로필 이름 (UI 스레드 컬렉션)
        var existing = await OnUi(() => _sections.ToDictionary(
            s => s.Provider, s => s.Profiles.Select(p => p.Name).ToList()));

        // 온프레미스 출처 (파일 읽기는 백그라운드). 한 클라우드만 볼 때는 생략
        var onPrem = only != null ? new List<string>() : await Task.Run(() =>
        {
            var lines = new List<string>();
            var sshPath = SshConfigImport.DefaultPath;
            if (File.Exists(sshPath))
            {
                var r = SshConfigImport.ParseFile(sshPath);
                lines.Add($"- ~/.ssh/config: 있음 ({sshPath}) — 서버 {r.Hosts.Count}개" + (r.Warnings.Count > 0 ? $", 경고 {r.Warnings.Count}건" : ""));
            }
            else lines.Add("- ~/.ssh/config: 없음");

            var mrPath = MRemoteNgImport.DefaultPath;
            if (File.Exists(mrPath))
            {
                var r = MRemoteNgImport.ParseFile(mrPath);
                lines.Add($"- mRemoteNG 연결 파일: 있음 ({mrPath}) — 서버 {r.Hosts.Count}개" +
                          (r.Warnings.Count > 0 ? $", 경고: {CloudProviders.FirstLine(r.Warnings[0])}" : ""));
            }
            else lines.Add("- mRemoteNG 연결 파일(confCons.xml): 기본 위치에 없음");

            var domain = ActiveDirectoryImport.CurrentDomain();
            lines.Add(domain != null
                ? $"- Active Directory: 도메인 '{domain}' 에 가입됨 — AD 컴퓨터 개체를 가져올 수 있음"
                : "- Active Directory: 도메인 가입 안 됨 (또는 확인 불가)");
            lines.Add("- 엑셀/CSV · Ansible 인벤토리: 자동으로 찾지 않음 — 사용자에게 파일 경로나 내용을 받아 import_hosts 로 가져온다");
            return lines;
        });

        var sb = new StringBuilder("## 클라우드\n");
        var fresh = 0;
        var needLogin = new List<string>();
        foreach (var d in discoveries)
        {
            var kind = d.Provider.Kind;
            var label = CloudProviders.Label(kind);
            var key = McpCloudHelpers.ProviderKey(kind);
            var path = CloudPaths.Effective(kind);
            var pathExists = CloudPaths.IsFolder(kind) ? Directory.Exists(path) : File.Exists(path);
            var added = existing.TryGetValue(kind, out var names) ? names : new List<string>();
            var addedSet = added.ToHashSet(StringComparer.OrdinalIgnoreCase);

            sb.Append($"[{label}] (provider={key}) 인증 경로 {path} ({(pathExists ? "있음" : "없음")}) · 추가된 프로필 {added.Count}개");
            sb.AppendLine($" · 로그인 명령 예: {d.Provider.LoginCommand(null)}");
            if (d.Candidates == null)
            {
                sb.AppendLine($"  로그인 필요/오류: {d.Error}");
                needLogin.Add(key);
                continue;
            }
            if (d.Candidates.Count == 0)
            {
                sb.AppendLine(pathExists
                    ? "  후보 없음 (설정은 있으나 찾은 프로필·프로젝트·구독이 없음)"
                    : "  후보 없음 — 이 PC 에 CLI 설정이 없어 보임 (CLI 설치·설정 또는 로그인 필요)");
                continue;
            }
            var page = McpCloudHelpers.PageCandidates(d.Candidates, filter, offset, DiscoverListLimit);
            fresh += page.Matched.Count(c => !addedSet.Contains(c.Name));
            if (filter.Length > 0) sb.AppendLine($"  '{filter}' 와 일치: {page.Matched.Count}개 (전체 {d.Candidates.Count}개)");
            if (page.Matched.Count == 0) continue;
            if (page.Shown.Count == 0)
            {
                sb.AppendLine($"  offset={offset} 은 범위를 벗어났습니다 (일치 {page.Matched.Count}개)");
                continue;
            }
            if (offset > 0) sb.AppendLine($"  ({offset + 1}~{offset + page.Shown.Count}번째)");
            foreach (var c in page.Shown)
            {
                var isAdded = addedSet.Contains(c.Name);
                var display = c.DisplayName.Equals(c.Name, StringComparison.OrdinalIgnoreCase) ? c.Name : $"{c.Name} ({c.DisplayName})";
                sb.AppendLine($"  - {display} — {(isAdded ? "추가됨" : "추가 안 됨")}");
            }
            if (page.Remaining > 0)
            {
                var restFresh = page.Matched.Skip(offset + page.Shown.Count).Count(c => !addedSet.Contains(c.Name));
                sb.AppendLine($"  ... 외 {page.Remaining}개 (추가 안 됨 {restFresh}개) — 다음: provider={key}, offset={offset + page.Shown.Count}" +
                              (filter.Length > 0 ? $", filter={filter}" : "") + " (또는 filter 로 좁히기)");
            }
        }

        if (only == null)
        {
            sb.AppendLine().AppendLine("## 온프레미스");
            foreach (var l in onPrem) sb.AppendLine(l);
        }

        sb.AppendLine().AppendLine("## 다음 단계");
        if (fresh > 0) sb.AppendLine($"- 추가 안 된 클라우드 후보 {fresh}개: 사용자가 원하는 것을 cloud_add_profile 로 추가 (추가하면 바로 동기화됨)");
        if (needLogin.Count > 0)
            sb.AppendLine($"- 로그인이 필요해 보이는 클라우드({string.Join(", ", needLogin)}): cloud_login 으로 로그인 창을 띄우고, 사용자가 브라우저 로그인을 마쳤다고 하면 discover_sources 를 다시 호출");
        sb.AppendLine("- 이미 추가된 프로필의 서버 목록 갱신은 cloud_sync");
        sb.AppendLine("- 온프레미스 서버(ssh config · mRemoteNG · AD · 엑셀/CSV · Ansible)는 import_hosts");

        AuditLog.Write("discover_sources", discoveries.Select(d => CloudProviders.Label(d.Provider.Kind)).Concat(only == null ? new[] { "온프레미스" } : Array.Empty<string>()),
            "discover", "조회");
        return sb.ToString();
    }

    // =================== cloud_add_profile ===================

    private async Task<string> CloudAddProfileAsync(JsonElement a)
    {
        var ct = McpRequest.Token;
        var kind = McpCloudHelpers.ParseProvider(Arg(a, "provider"))
                   ?? throw new McpToolException($"provider 는 {string.Join(", ", McpCloudHelpers.ProviderNames)} 중 하나입니다.");
        var requested = ListArg(a, "profiles");
        if (requested.Count == 0) throw new McpToolException("profiles 가 필요합니다 (후보 이름 목록 또는 [\"all\"]).");
        var provider = CloudProviders.Get(kind);
        var label = CloudProviders.Label(kind);

        List<CloudCandidate> candidates;
        try { candidates = await DiscoverWithTimeoutAsync(provider, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new McpToolException("Claude 쪽에서 요청이 취소되었습니다."); }
        catch (Exception ex)
        {
            throw new McpToolException($"[{label}] 후보를 찾지 못했습니다: {CloudProviders.FirstLine(ex.Message)}\n" +
                                       $"로그인이 필요하면 cloud_login (provider={McpCloudHelpers.ProviderKey(kind)}) 후 다시 시도하세요.");
        }

        return await OnUiAsync(async () =>
        {
            var section = _sections.First(s => s.Provider == kind);
            var sel = McpCloudHelpers.SelectCandidates(candidates, section.Profiles.Select(p => p.Name), requested);
            var notes = new StringBuilder();
            if (sel.AlreadyAdded.Count > 0) notes.AppendLine($"이미 추가되어 건너뜀: {string.Join(", ", sel.AlreadyAdded)}");
            if (sel.Unknown.Count > 0)
                notes.AppendLine($"후보에 없음(또는 여러 개 일치): {string.Join(", ", sel.Unknown)} — 후보: " +
                                 string.Join(", ", candidates.Take(30).Select(c => c.Name)) + (candidates.Count > 30 ? " ..." : ""));
            if (sel.Pick.Count == 0) return "추가할 프로필이 없습니다.\n" + notes;

            string Target(CloudCandidate c) =>
                c.DisplayName.Equals(c.Name, StringComparison.OrdinalIgnoreCase) ? $"[{label}] {c.Name}" : $"[{label}] {c.DisplayName} ({c.Name})";
            var targets = sel.Pick.Select(Target).ToList();
            var command = $"cloud_add_profile {McpCloudHelpers.ProviderKey(kind)}";
            if (!await AskApprovalAsync("cloud_add_profile", L.F("Claude 가 클라우드 프로필 {0}개 추가를 요청합니다", sel.Pick.Count),
                    L.T("아래 클라우드 프로필을 추가하고 바로 동기화합니다. 그 계정의 서버 목록을 조회해 호스트로 가져오고, 이후 동기화 때마다 최신 상태로 맞춥니다. 클라우드 리소스는 바꾸지 않습니다."),
                    targets, command))
            {
                AuditLog.Write("cloud_add_profile", targets, command, "거부");
                throw new McpToolException("사용자가 프로필 추가를 거부했습니다.");
            }

            // 승인을 기다리는 동안 사용자가 같은 프로필을 추가했으면 빼고 추가한다
            var nowExisting = section.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var raced = sel.Pick.Where(c => nowExisting.Contains(c.Name)).Select(c => c.Name).ToList();
            var pick = sel.Pick.Where(c => !nowExisting.Contains(c.Name)).ToList();
            if (raced.Count > 0) notes.AppendLine($"승인을 기다리는 동안 이미 추가되어 건너뜀: {string.Join(", ", raced)}");
            if (pick.Count == 0)
            {
                AuditLog.Write("cloud_add_profile", targets, command, "승인 (대기 중 모두 이미 추가됨)");
                return "추가할 프로필이 없습니다.\n" + notes;
            }

            var added = await AddProfilesAsync(section, pick);
            string Line(CloudProfile p)
            {
                var hosts = _hosts.Where(h => h.CloudProfileId == p.Id).ToList();
                return $"[{p.ProviderText}] {p.GroupName} ({p.Name}) — 서버 {hosts.Count}대 (실행 {hosts.Count(h => !h.IsStopped)})" +
                       string.Concat(new[] { p.LastResult, p.StatusText }.Where(s => s.Length > 0).Select(s => " · " + s));
            }
            var result = string.Join("\n", added.Select(Line));
            AuditLog.Write("cloud_add_profile", added.Select(p => $"[{p.ProviderText}] {p.GroupName} ({p.Name})"), command,
                raced.Count == 0 ? "승인·추가" : $"승인·추가 {added.Count}개 (대기 중 이미 추가됨 {raced.Count}개)");
            return $"프로필 {added.Count}개를 추가하고 동기화했습니다:\n{result}" + (notes.Length > 0 ? "\n" + notes : "") +
                   "\n동기화가 실패한 프로필은 로그인이 필요할 수 있습니다 (cloud_login 후 cloud_sync).";
        });
    }

    // =================== cloud_login ===================

    private async Task<string> CloudLoginAsync(JsonElement a)
    {
        var ct = McpRequest.Token;
        var kind = McpCloudHelpers.ParseProvider(Arg(a, "provider"))
                   ?? throw new McpToolException($"provider 는 {string.Join(", ", McpCloudHelpers.ProviderNames)} 중 하나입니다.");
        var spec = Arg(a, "profile").Trim();
        var provider = CloudProviders.Get(kind);
        var label = CloudProviders.Label(kind);
        var key = McpCloudHelpers.ProviderKey(kind);

        // 로그인 대상 프로필: 추가된 프로필 → (없으면) 탐색 후보. 사용자가 준 문자열을 그대로 명령줄에 넣지 않는다
        var profile = await OnUi(() =>
        {
            var section = _sections.First(s => s.Provider == kind);
            if (spec.Length == 0) return section.Profiles.FirstOrDefault();
            return section.Profiles.FirstOrDefault(p => p.Name.Equals(spec, StringComparison.OrdinalIgnoreCase))
                   ?? section.Profiles.FirstOrDefault(p => p.GroupName.Equals(spec, StringComparison.OrdinalIgnoreCase));
        });
        if (profile == null && spec.Length > 0)
        {
            List<CloudCandidate> candidates;
            try { candidates = await DiscoverWithTimeoutAsync(provider, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new McpToolException("Claude 쪽에서 요청이 취소되었습니다."); }
            catch (Exception ex)
            {
                throw new McpToolException($"[{label}] '{spec}' 는 추가된 프로필이 아니고, 후보 목록도 조회하지 못했습니다 ({CloudProviders.FirstLine(ex.Message)}). " +
                                           "profile 없이 호출해 기본 로그인을 하세요.");
            }
            var c = McpCloudHelpers.FindCandidate(candidates, spec)
                    ?? throw new McpToolException($"[{label}] '{spec}' 와 일치하는 프로필이 없습니다. discover_sources 로 후보를 확인하세요.");
            profile = new CloudProfile { Provider = kind, Name = c.Name, DisplayName = c.DisplayName, TenantId = c.TenantId };
        }
        if (profile == null && kind == CloudProvider.Aws)
            throw new McpToolException("AWS 는 profile 이 필요합니다 (SSO 포털이 프로필마다 다를 수 있음). discover_sources 로 후보를 확인하세요.");

        // 명령줄 안전성: 프로필 이름·테넌트는 허용 문자만, 최종 명령에도 cmd 메타 문자가 없어야 한다
        if (profile != null && !McpCloudHelpers.IsSafeCliArg(profile.Name))
            throw new McpToolException($"프로필 이름 '{profile.Name}' 에 명령줄에 넣을 수 없는 문자가 있어 실행하지 않습니다. 앱의 Cloud 화면에서 직접 로그인하세요.");
        if (profile?.TenantId is { Length: > 0 } tenant && !McpCloudHelpers.IsSafeCliArg(tenant))
            throw new McpToolException("테넌트 ID 형식이 올바르지 않아 실행하지 않습니다.");
        var cmd = provider.LoginCommand(profile);
        if (!McpCloudHelpers.IsSafeCommandLine(cmd))
            throw new McpToolException($"로그인 명령에 허용되지 않는 문자가 있어 실행하지 않습니다: {cmd}");

        var target = profile != null ? $"[{label}] {profile.GroupName} ({profile.Name})" : $"[{label}]";
        return await OnUiAsync(async () =>
        {
            if (!await AskApprovalAsync("cloud_login", L.T("Claude 가 클라우드 CLI 로그인을 요청합니다"),
                    L.T("새 콘솔 창에서 아래 로그인 명령을 실행합니다. 브라우저가 열리면 직접 로그인을 마치세요. 입력한 계정 정보는 Claude 에게 전달되지 않습니다."),
                    new[] { target }, cmd))
            {
                AuditLog.Write("cloud_login", new[] { target }, cmd, "거부");
                throw new McpToolException("사용자가 로그인 명령 실행을 거부했습니다.");
            }
            // 승인한 명령 그대로 실행 (다시 만들지 않음)
            var msg = LaunchLoginCommand(cmd, out var started);
            _sections.First(s => s.Provider == kind).Message = msg;
            if (profile != null && _profiles.Contains(profile)) profile.StatusText = msg;
            AuditLog.Write("cloud_login", new[] { target }, cmd, started ? "승인·실행" : "승인·실행 실패");
            if (!started) throw new McpToolException($"로그인 명령을 실행하지 못했습니다: {msg}");
            return $"새 콘솔 창에서 '{cmd}' 를 실행했습니다. 사용자가 브라우저(또는 콘솔)에서 로그인을 직접 마쳐야 합니다 — 완료를 기다리지 않았습니다.\n" +
                   $"사용자가 로그인을 마쳤다고 하면 discover_sources 로 후보를 다시 확인하거나, 이미 추가된 프로필은 cloud_sync (profile=all 또는 이름) 로 동기화하세요.";
        });
    }
}

/// <summary>클라우드 MCP 도구의 UI 와 무관한 판단 로직 (테스트 대상)</summary>
internal static class McpCloudHelpers
{
    /// <summary>MCP 인자로 받는 프로바이더 이름 (스키마 enum)</summary>
    public static readonly string[] ProviderNames = { "aws", "tencent", "gcp", "azure", "oci" };

    public static string ProviderKey(CloudProvider p) => p switch
    {
        CloudProvider.Aws => "aws",
        CloudProvider.Tencent => "tencent",
        CloudProvider.Gcp => "gcp",
        CloudProvider.Azure => "azure",
        CloudProvider.Oci => "oci",
        _ => p.ToString().ToLowerInvariant(),
    };

    /// <summary>"aws" 등 → CloudProvider. 목록에 없으면 null (대소문자·앞뒤 공백 무시)</summary>
    public static CloudProvider? ParseProvider(string? s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "aws" => CloudProvider.Aws,
            "tencent" => CloudProvider.Tencent,
            "gcp" => CloudProvider.Gcp,
            "azure" => CloudProvider.Azure,
            "oci" => CloudProvider.Oci,
            _ => null,
        };
    }

    /// <summary>후보 선택 결과: 추가할 것 · 이미 추가된 요청 · 후보에 없는(또는 모호한) 요청</summary>
    public sealed record Selection(List<CloudCandidate> Pick, List<string> AlreadyAdded, List<string> Unknown);

    /// <summary>
    /// 이름(정확히, 대소문자 무시) 또는 표시 이름(정확히, 하나만 일치할 때)으로 후보 하나를 찾는다. 없거나 모호하면 null
    /// </summary>
    public static CloudCandidate? FindCandidate(IReadOnlyList<CloudCandidate> candidates, string spec)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0) return null;
        var byName = candidates.Where(c => c.Name.Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1) return byName.FirstOrDefault(c => c.Name.Equals(spec, StringComparison.Ordinal));
        var byDisplay = candidates.Where(c => c.DisplayName.Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        return byDisplay.Count == 1 ? byDisplay[0] : null;
    }

    /// <summary>
    /// 요청한 이름을 후보에서 고른다. "all" 이 있으면 아직 추가 안 된 후보 전부.
    /// 이미 추가된 프로필(이름 기준, 대소문자 무시 — UI 의 프로필 추가와 같은 기준)은 건너뛴다.
    /// </summary>
    public static Selection SelectCandidates(IReadOnlyList<CloudCandidate> candidates, IEnumerable<string> existingNames, IReadOnlyList<string> requested)
    {
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pick = new List<CloudCandidate>();
        var already = new List<string>();
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Take(CloudCandidate c, string asked)
        {
            if (existing.Contains(c.Name)) { if (!already.Contains(asked, StringComparer.OrdinalIgnoreCase)) already.Add(asked); return; }
            if (seen.Add(c.Name)) pick.Add(c);
        }

        if (requested.Any(r => r.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var c in candidates)
                if (!existing.Contains(c.Name) && seen.Add(c.Name)) pick.Add(c);
            return new Selection(pick, already, unknown);
        }

        foreach (var raw in requested)
        {
            var r = raw.Trim();
            if (r.Length == 0) continue;
            var c = FindCandidate(candidates, r);
            if (c == null) { if (!unknown.Contains(r, StringComparer.OrdinalIgnoreCase)) unknown.Add(r); continue; }
            Take(c, r);
        }
        return new Selection(pick, already, unknown);
    }

    /// <summary>discover_sources 한 페이지: filter 에 맞는 후보 전체(Matched), 이번에 보여 줄 것(Shown), 그 뒤에 남은 수</summary>
    public sealed record CandidatePage(List<CloudCandidate> Matched, List<CloudCandidate> Shown, int Remaining);

    /// <summary>후보를 이름·표시 이름 부분 일치(대소문자 무시)로 거른 뒤 offset 부터 limit 개를 고른다</summary>
    public static CandidatePage PageCandidates(IReadOnlyList<CloudCandidate> candidates, string? filter, int offset, int limit)
    {
        filter = (filter ?? "").Trim();
        var matched = filter.Length == 0
            ? candidates.ToList()
            : candidates.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                    c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        offset = Math.Max(0, offset);
        var shown = matched.Skip(offset).Take(Math.Max(1, limit)).ToList();
        return new CandidatePage(matched, shown, Math.Max(0, matched.Count - offset - shown.Count));
    }

    // 프로필 이름 · 테넌트 ID 로 허용하는 문자 (cmd 메타 문자 · 공백 · 따옴표 없음).
    // '/' 는 cmd 메타 문자가 아니고 AWS 프로필 이름(예: org/admin)에 쓰이므로 허용 — 첫 글자는 영숫자라 "/k" 같은 스위치로 읽히지 않는다
    private static readonly Regex SafeArg = new(@"^[A-Za-z0-9][A-Za-z0-9._@+\-/]{0,127}\z", RegexOptions.CultureInvariant);

    // 로그인 명령 전체로 허용하는 문자 (공백 · 옵션 · 경로 구분자까지만, & | < > ^ % ! " ( ) 등 없음)
    private static readonly Regex SafeCommand = new(@"^[A-Za-z0-9 ._@+\-=:/]{1,300}\z", RegexOptions.CultureInvariant);

    public static bool IsSafeCliArg(string? s) => !string.IsNullOrEmpty(s) && SafeArg.IsMatch(s);

    public static bool IsSafeCommandLine(string? s) => !string.IsNullOrEmpty(s) && SafeCommand.IsMatch(s);

    /// <summary>
    /// 로그인 명령을 cmd 로 실행해도 되는지 (UI 로그인 버튼 · MCP cloud_login 공용 기준).
    /// 프로필 이름 · 테넌트 ID 는 있으면 허용 문자만, 최종 명령에도 cmd 메타 문자가 없어야 한다
    /// </summary>
    public static bool IsSafeLogin(string? profileName, string? tenantId, string? command) =>
        (string.IsNullOrEmpty(profileName) || IsSafeCliArg(profileName)) &&
        (string.IsNullOrEmpty(tenantId) || IsSafeCliArg(tenantId)) &&
        IsSafeCommandLine(command);
}
