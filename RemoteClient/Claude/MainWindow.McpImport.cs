using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>
/// Claude 패널 MCP 도구: import_hosts — 엑셀·CSV 표 · ~/.ssh/config · mRemoteNG · Ansible 인벤토리 · Active Directory 에서 호스트 가져오기.
/// 흐름은 가져오기 창과 같다 (파서 → ImportMerge.Plan → ApplyTo · SaveHosts · RefreshHosts).
/// 보안 원칙:
/// - apply=false(기본)는 미리보기만 (목록을 바꾸지 않음). AD 조회는 서버에 접속하므로 감사 로그에 "조회" 로 남긴다
/// - apply=true 는 다시 읽고(앞선 미리보기를 믿지 않음) 사용자 승인 한 번을 받은 뒤 등록, 승인·거부를 감사 로그에 남긴다
/// - 비밀번호는 어떤 출처에서도 가져오지 않고, MCP 인자로도 받지 않는다. AD 를 다른 계정으로 조회할 때는 Vault 계정(ad_vault)을
///   앱 안에서만 풀어 쓰고(조회 전 승인 — Claude 가 정한 서버로 계정이 가므로), 결과·로그에는 넣지 않는다
/// - 결과에 파일 내용 일부가 나오므로 Nubis 데이터 폴더 · 개인키는 읽지 않는다 (McpImport.BlockedPathReason)
/// </summary>
public partial class MainWindow
{
    private IEnumerable<McpTool> ImportTools()
    {
        yield return new McpTool("import_hosts",
            "다른 곳의 서버 목록을 읽어 호스트로 등록한다. source: " +
            "table = 엑셀·CSV 표 (text 에 붙여 넣은 표, 첫 줄 머리글 권장 — 이름·주소·포트·종류·그룹·사용자·OS·메모·점프 서버 열을 한국어/영어 이름으로 인식. 또는 path 에 CSV/TSV 텍스트 파일, .xlsx 는 불가) / " +
            "ssh_config = OpenSSH 설정 (path 생략 시 ~/.ssh/config, Include 지원, 또는 text) / " +
            "mremoteng = mRemoteNG 연결 파일 (path 생략 시 %APPDATA%\\mRemoteNG\\confCons.xml, 또는 text) / " +
            "ansible = Ansible 인벤토리 INI·YAML (text 또는 path) / " +
            "ad = Active Directory 컴퓨터 개체 (domain 생략 시 이 PC 의 도메인, server · search_base · servers_only · skip_disabled · active_within_days · resolve_ip; " +
            "기본은 현재 Windows 로그인으로 조회, 다른 계정이면 ad_vault 에 Vault 계정 — 조회 전에 앱이 사용자 승인을 받음). " +
            "흐름: 먼저 apply=false(기본)로 불러 미리보기(상태별 개수 New/Existing/Duplicate/Invalid, 줄 목록, 경고)를 사용자에게 보여 주고, " +
            "확인받으면 같은 인자에 apply=true 를 더해 다시 부른다 — 앱이 다시 읽고 사용자 승인을 받아 등록한다. 기본은 New 줄 전체, select 에 이름·주소(또는 주소:포트) 목록을 주면 그 줄만. " +
            "update_existing=true 면 이미 있는 호스트(주소·포트·종류가 같음)의 이름·그룹·사용자명을 갱신한다 (클라우드 동기화 호스트는 바꾸지 않음). " +
            "group_prefix 는 가져온 그룹 앞에 붙일 이름, group 은 모든 호스트를 이 그룹으로(\"\" = 그룹 없음). " +
            "비밀번호·키는 어떤 출처에서도 가져오지 않으며 비밀번호 인자도 없다 — 계정은 ssh_vault / rdp_vault 에 Vault 계정(list_vault)을 지정한다.",
            Schema(new
            {
                source = new { type = "string", @enum = new[] { "table", "ssh_config", "mremoteng", "ansible", "ad" } },
                text = new { type = "string", description = "붙여 넣은 내용 (table: 표 · ansible: 인벤토리 · ssh_config / mremoteng 도 가능). path 와 함께 쓰지 않음" },
                path = new { type = "string", description = "이 PC 의 파일 경로 (ssh_config / mremoteng 는 생략 시 기본 위치). Nubis 데이터 폴더·개인키·네트워크 경로(UNC)는 읽지 않음" },
                domain = new { type = "string", description = "ad: 도메인 DNS 이름 (예: corp.example.com). 생략 시 이 PC 의 도메인" },
                server = new { type = "string", description = "ad: 도메인 컨트롤러 주소 (생략 시 도메인 이름으로 DNS 조회, 'host:port' 가능)" },
                search_base = new { type = "string", description = "ad: 검색 기준 DN (생략 시 도메인 루트)" },
                servers_only = new { type = "boolean", description = "ad: Windows Server 와 Windows 가 아닌 OS 만 (기본 true)" },
                skip_disabled = new { type = "boolean", description = "ad: 사용 안 함 컴퓨터 제외 (기본 true)" },
                active_within_days = new { type = "integer", description = "ad: 최근 N일 안에 로그온한 컴퓨터만 (1~3650)" },
                resolve_ip = new { type = "boolean", description = "ad: DNS 이름을 IP 로 바꿔 등록 (기본 false)" },
                use_current_user = new { type = "boolean", description = "ad: 현재 Windows 로그인으로 조회 (기본 true). false 면 ad_vault 필요" },
                ad_vault = new { type = "string", description = "ad: 조회에 쓸 Vault 계정 이름 또는 id (사용자명·비밀번호는 앱 안에서만 사용)" },
                group_prefix = new { type = "string", description = "가져온 그룹 앞에 붙일 이름 ('이름 / 가져온 그룹')" },
                group = new { type = "string", description = "모든 호스트를 이 그룹으로 (\"\" = 그룹 없음). group_prefix 와 함께 쓰지 않음" },
                ssh_vault = new { type = "string", description = "가져온 SSH 호스트에 연결할 Vault 계정 이름 또는 id" },
                rdp_vault = new { type = "string", description = "가져온 RDP 호스트에 연결할 Vault 계정 이름 또는 id" },
                update_existing = new { type = "boolean", description = "이미 있는 호스트의 이름·그룹·사용자명 갱신 (기본 false)" },
                select = new { type = "array", items = new { type = "string" }, description = "등록할 줄의 이름·주소·'주소:포트' 목록 (생략 시 등록 가능한 줄 전체)" },
                apply = new { type = "boolean", description = "false(기본) = 미리보기만, true = 사용자 승인 후 등록" },
            }, "source"),
            ImportHostsFromClaude);
    }

    private async Task<string> ImportHostsFromClaude(JsonElement a)
    {
        var ct = McpRequest.Token;
        var args = McpImport.ParseArgs(a);
        var (result, label, detail) = await LoadImportSourceAsync(args, ct);
        if (ct.IsCancellationRequested) throw new McpToolException("Claude 쪽에서 요청이 취소되어 실행하지 않았습니다.");
        return args.Apply
            ? await OnUiAsync(() => ApplyImportFromClaudeAsync(args, result, label, detail))
            : await OnUi(() =>
            {
                var plan = ImportMerge.Plan(result.Hosts, _hosts, ImportDefaultsFor(args, out _, out _));
                var (selected, unmatched) = McpImport.SelectRows(plan.Rows, args.Select, args.UpdateExisting);
                var text = McpImport.FormatPreview(detail, plan.Rows, selected, result.Warnings, args.UpdateExisting);
                return unmatched.Count > 0 ? McpImport.UnmatchedMessage(plan.Rows, unmatched, args.UpdateExisting) + "\n\n" + text : text;
            });
    }

    /// <summary>등록 옵션 → ImportDefaults (Vault 이름 해석 — 없거나 여러 개면 McpToolException). UI 스레드에서 호출</summary>
    private ImportDefaults ImportDefaultsFor(McpImportArgs args, out VaultEntry? sshVault, out VaultEntry? rdpVault)
    {
        sshVault = args.SshVault != null ? ResolveVault(args.SshVault) : null;
        rdpVault = args.RdpVault != null ? ResolveVault(args.RdpVault) : null;
        var d = ImportDefaults.FromSettings() with { SshVaultId = sshVault?.Id, RdpVaultId = rdpVault?.Id };
        return args.Group != null ? d with { GroupOverride = args.Group } : d with { GroupPrefix = args.GroupPrefix };
    }

    // =================== 출처 읽기 ===================

    /// <summary>출처를 읽는다 (백그라운드 스레드). label = 승인 창에 보일 출처 이름(번역), detail = 결과·감사 로그용 설명(경로 포함)</summary>
    private async Task<(ImportResult Result, string Label, string Detail)> LoadImportSourceAsync(McpImportArgs args, CancellationToken ct)
    {
        if (args.Source == McpImportSource.Ad) return await FetchAdForImportAsync(args, ct);

        var label = args.Source switch
        {
            McpImportSource.Table => L.T("엑셀·CSV"),
            McpImportSource.SshConfig => "~/.ssh/config",
            McpImportSource.MRemoteNg => "mRemoteNG",
            _ => L.T("Ansible 인벤토리"),
        };
        var name = McpImport.SourceName(args.Source);
        // Include · group_vars 로 따라 읽는 파일도 path 와 같은 규칙으로 막는다 (Claude 가 넘긴 text 의 Include 포함)
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Func<string, string?> includeGuard = p => McpImport.BlockedFollowPathReason(p, AppPaths.DataDir, AppPaths.RootDir, sshDir,
            allowSshDir: args.Source == McpImportSource.SshConfig);
        try
        {
            // 파서는 파일 읽기 · 큰 입력 처리로 오래 걸릴 수 있어 백그라운드에서
            return await Task.Run(() =>
            {
                if (args.Text != null)
                {
                    var r = args.Source switch
                    {
                        McpImportSource.Table => TableImport.Parse(args.Text),
                        McpImportSource.SshConfig => SshConfigImport.Parse(args.Text, null, includeGuard),
                        McpImportSource.MRemoteNg => MRemoteNgImport.Parse(args.Text),
                        _ => AnsibleInventoryImport.Parse(args.Text),
                    };
                    return (r, label, $"{name} (붙여 넣은 내용)");
                }

                var path = args.Path ?? (args.Source == McpImportSource.SshConfig ? SshConfigImport.DefaultPath : MRemoteNgImport.DefaultPath);
                var (full, text) = ReadImportFile(path, args.Source);
                var dir = Path.GetDirectoryName(full);
                var result = args.Source switch
                {
                    McpImportSource.Table => TableImport.Parse(text),
                    // Include 의 상대 경로는 이 파일이 있는 폴더 기준 (ParseFile 과 같음)
                    McpImportSource.SshConfig => SshConfigImport.Parse(text, dir, includeGuard),
                    // XML 선언의 인코딩을 따르도록 파일 스트림으로 연다
                    McpImportSource.MRemoteNg => MRemoteNgImport.ParseFile(full),
                    // 인벤토리 옆의 group_vars · host_vars 도 (화면의 파일 불러오기와 같음)
                    _ => AnsibleInventoryImport.Parse(text, dir, includeGuard),
                };
                return (result, $"{label} ({full})", $"{name} ({full})");
            });
        }
        catch (Exception ex) when (ex is not McpToolException and not OperationCanceledException)
        {
            throw new McpToolException($"{name} 을 읽지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>경로 검사 후 파일을 텍스트로 읽는다 (데이터 폴더·개인키·너무 큰 파일 거부)</summary>
    private static (string Full, string Text) ReadImportFile(string path, McpImportSource source)
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (McpImport.BlockedPathReason(path, AppPaths.DataDir, AppPaths.RootDir, sshDir, source == McpImportSource.SshConfig) is { } why)
            throw new McpToolException(why);
        var full = Path.GetFullPath(path.Trim());
        if (!File.Exists(full))
            throw new McpToolException(Directory.Exists(full) ? $"폴더가 아니라 파일 경로를 지정하세요: {full}" : $"파일을 찾을 수 없습니다: {full}");
        var info = new FileInfo(full);
        if (info.Length > McpImport.MaxFileBytes) throw new McpToolException($"파일이 너무 큽니다 ({info.Length:N0} 바이트, 최대 {McpImport.MaxFileBytes:N0}).");
        string text;
        // BOM · UTF-8 · 시스템 ANSI(한국어 엑셀 CSV 는 CP949) 순으로 판별
        try { text = ImportText.ReadFile(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpToolException($"파일을 읽을 수 없습니다: {full} ({ex.Message})");
        }
        if (McpImport.LooksLikePrivateKey(text)) throw new McpToolException("개인키가 들어 있는 파일은 읽을 수 없습니다.");
        return (full, text);
    }

    /// <summary>
    /// AD 조회. 기본은 현재 Windows 로그인. ad_vault 면 그 계정의 사용자명·비밀번호를 앱 안에서만 풀어 쓰고,
    /// Claude 가 정한 서버로 계정이 가므로 조회 전에 사용자 승인을 받는다. 조회·거부·실패는 감사 로그에 남긴다.
    /// </summary>
    private async Task<(ImportResult Result, string Label, string Detail)> FetchAdForImportAsync(McpImportArgs args, CancellationToken ct)
    {
        var ownDomain = ActiveDirectoryImport.CurrentDomain();
        var domain = args.Domain ?? ownDomain
            ?? throw new McpToolException("이 PC 의 도메인을 알 수 없습니다. domain 을 지정하세요.");
        var target = args.Server != null ? $"{domain} (DC {args.Server})" : domain;
        var command = $"ldap domain={domain}" + (args.Server != null ? $" server={args.Server}" : "") +
                      (args.SearchBase != null ? $" base={args.SearchBase}" : "") +
                      $" servers_only={args.ServersOnly} skip_disabled={args.SkipDisabled}" +
                      (args.ActiveWithinDays is { } days ? $" active_within_days={days}" : "") + $" resolve_ip={args.ResolveIp}";

        string? user = null, password = null;
        // 현재 Windows 로그인(Negotiate)으로 Claude 가 정한 서버·다른 도메인에 접속하면 그 서버가 이 PC 사용자의 Windows 인증(NTLM)을 받는다
        // (네트워크 경로(UNC)를 읽지 않는 것과 같은 이유) — 이 PC 의 도메인을 DNS 로 찾는 기본 조회가 아니면 조회 전에 승인
        if (args.AdVault == null && (args.Server != null || ownDomain == null || !domain.TrimEnd('.').Equals(ownDomain.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
        {
            var auditTargets = new[] { $"AD {target}", "현재 Windows 로그인" };
            var ok = await OnUiAsync(() => AskApprovalAsync("import_hosts", L.T("Claude 가 현재 Windows 로그인으로 Active Directory 조회를 요청합니다"),
                L.T("이 PC 의 도메인이 아니거나 서버를 직접 지정한 조회입니다. 현재 Windows 로그인 정보로 아래 서버에 인증해 컴퓨터 목록을 읽습니다. 사내 도메인 컨트롤러가 아니면 그 서버가 Windows 인증(NTLM)을 받게 되니 주소를 확인하세요."),
                new[] { L.F("도메인: {0}", domain), L.F("도메인 컨트롤러: {0}", args.Server ?? domain) }, command));
            if (!ok)
            {
                AuditLog.Write("import_hosts", auditTargets, command, "AD 조회 거부");
                throw new McpToolException("사용자가 현재 Windows 로그인으로 AD 조회하는 것을 거부했습니다.");
            }
            AuditLog.Write("import_hosts", auditTargets, command, "AD 조회 승인");
        }
        if (args.AdVault != null)
        {
            string vaultSpec = args.AdVault;
            var (vaultName, vaultUser) = await OnUiAsync(async () =>
            {
                var v = ResolveVault(vaultSpec) ?? throw new McpToolException($"Vault 계정 '{vaultSpec}' 을 찾을 수 없습니다.");
                if (v.Username.Trim().Length == 0) throw new McpToolException($"Vault 계정 '{v.Name}' 에 사용자명이 없습니다.");
                if (!v.HasPassword) throw new McpToolException($"Vault 계정 '{v.Name}' 에 비밀번호가 없습니다 (AD 조회는 비밀번호 계정만).");
                var auditTargets = new[] { $"AD {target}", $"Vault {v.Name}" };
                if (!await AskApprovalAsync("import_hosts", L.T("Claude 가 Vault 계정으로 Active Directory 조회를 요청합니다"),
                        L.T("아래 Vault 계정의 사용자명·비밀번호로 도메인 컨트롤러에 로그인해 컴퓨터 목록을 읽습니다. 서버가 사내 도메인 컨트롤러가 맞는지 확인하세요. 비밀번호는 Claude 에 전달되지 않습니다."),
                        new[] { L.F("Vault 계정: {0} ({1})", v.Name, v.Username), L.F("도메인 컨트롤러: {0}", args.Server ?? domain) }, command))
                {
                    AuditLog.Write("import_hosts", auditTargets, command, "AD 조회 거부");
                    throw new McpToolException("사용자가 Vault 계정으로 AD 조회하는 것을 거부했습니다.");
                }
                // 승인 창을 기다리는 동안 계정이 지워졌거나 비밀번호가 바뀌었을 수 있어 승인 뒤에 읽는다
                if (VaultStore.Get(v.Id) is not { } now || !Secret.TryUnprotect(now.ProtectedPassword, out var plain) || plain.Length == 0)
                {
                    AuditLog.Write("import_hosts", auditTargets, command, "승인 후 취소 (Vault 계정을 읽을 수 없음)");
                    throw new McpToolException("Vault 계정의 비밀번호를 읽을 수 없어 조회하지 않았습니다 (계정이 지워졌거나 다른 PC 에서 저장됨).");
                }
                password = plain;
                return (now.Name, now.Username.Trim());
            });
            user = vaultUser;
            target += $" · Vault {vaultName}";
        }

        var options = new AdImportOptions(domain, args.Server, user, password, args.AdVault == null, args.SearchBase,
            args.ServersOnly, args.SkipDisabled, args.ActiveWithinDays, args.ResolveIp);
        ImportResult result;
        try
        {
            result = await ActiveDirectoryImport.FetchAsync(options, ct);
        }
        catch (OperationCanceledException)
        {
            AuditLog.Write("import_hosts", new[] { $"AD {target}" }, command, "요청 취소");
            throw new McpToolException("Claude 쪽에서 요청이 취소되어 AD 조회를 멈췄습니다.");
        }
        catch (Exception ex) when (ex is not McpToolException)
        {
            // 오류 메시지에는 비밀번호가 들어가지 않는다 (ActiveDirectoryImport 원칙)
            AuditLog.Write("import_hosts", new[] { $"AD {target}" }, command, "AD 조회 실패: " + ex.Message);
            throw new McpToolException("Active Directory 조회 실패: " + ex.Message);
        }
        AuditLog.Write("import_hosts", new[] { $"AD {target}" }, command, $"조회 {result.Hosts.Count}대");
        return (result, $"Active Directory ({target})", $"ad ({target})");
    }

    // =================== 등록 (UI 스레드) ===================

    private async Task<string> ApplyImportFromClaudeAsync(McpImportArgs args, ImportResult source, string label, string detail)
    {
        var d = ImportDefaultsFor(args, out var sshVault, out var rdpVault);
        var plan = ImportMerge.Plan(source.Hosts, _hosts, d);
        var (selected, unmatched) = McpImport.SelectRows(plan.Rows, args.Select, args.UpdateExisting);
        if (unmatched.Count > 0)
            throw new McpToolException(McpImport.UnmatchedMessage(plan.Rows, unmatched, args.UpdateExisting) + "\napply=false 로 미리보기를 다시 확인하세요.");
        if (selected.Count == 0)
            return "등록할 호스트가 없습니다 (새 호스트가 없거나 모두 이미 있음). " +
                   McpImport.FormatPreview(detail, plan.Rows, selected, source.Warnings, args.UpdateExisting, maxRows: 50);

        var adds = selected.Count(r => r.Status == ImportStatus.New);
        var updates = selected.Count(r => r.Status == ImportStatus.Existing);
        var command = $"import_hosts source={McpImport.SourceName(args.Source)} · {detail}\n" +
                      $"add={adds} update={updates}" +
                      (args.Group != null ? $" group=\"{args.Group}\"" : args.GroupPrefix != null ? $" group_prefix=\"{args.GroupPrefix}\"" : "") +
                      $" ssh_vault={sshVault?.Name ?? "-"} rdp_vault={rdpVault?.Name ?? "-"}";
        var auditTargets = selected.Select(r => $"{r.NameText} ({r.ProtocolText} {r.AddressText}:{r.PortText})").ToList();

        var description = L.T("가져온 서버를 Nubis 호스트 목록에 등록합니다. 비밀번호는 가져오지 않으며, 계정은 지정한 Vault 계정으로만 연결됩니다. 주소와 Vault 계정이 맞는지 확인하세요.") +
                          "\n" + L.F("출처: {0}", label) +
                          "\n" + L.F("새 호스트 {0}개 추가 · 기존 호스트 {1}개 갱신 (이름·그룹·사용자명)", adds, updates);
        if (!await AskApprovalAsync("import_hosts", L.F("Claude 가 호스트 {0}개 가져오기를 요청합니다", selected.Count),
                description, McpImport.ApprovalTargets(selected), command))
        {
            AuditLog.Write("import_hosts", auditTargets, command, "거부");
            throw new McpToolException("사용자가 가져오기를 거부했습니다.");
        }

        // 승인 창을 기다리는 동안 지정한 Vault 계정이 지워졌으면 사용자가 본 내용과 달라지므로 등록하지 않는다
        if ((sshVault != null && VaultStore.Get(sshVault.Id) == null) || (rdpVault != null && VaultStore.Get(rdpVault.Id) == null))
        {
            AuditLog.Write("import_hosts", auditTargets, command, "승인 후 취소 (대기 중 Vault 계정이 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 지정한 Vault 계정이 지워져 등록하지 않았습니다. list_vault 로 확인한 뒤 다시 요청하세요.");
        }

        // 승인을 기다리는 동안 갱신 대상이 목록에서 지워졌으면 그 줄은 빼낸다 (갱신으로 승인받은 줄을 새로 추가하지 않게).
        // 새 호스트로 승인받은 줄이 대기 중 목록에 생긴 경우는 ImportMerge.Apply 가 등록 직전에 다시 대조해 건너뛴다
        var final = selected.Where(r => r.Status != ImportStatus.Existing || (r.Existing != null && _hosts.Contains(r.Existing))).ToList();
        var applied = new ImportPlanResult(plan, final, args.UpdateExisting).ApplyTo(_hosts);
        if (applied.Added + applied.Updated > 0)
        {
            SaveHosts();
            RefreshHosts();
        }
        var changedWhileWaiting = (selected.Count - final.Count) + Math.Max(0, adds - applied.Added);
        AuditLog.Write("import_hosts", auditTargets, command,
            $"승인·등록 {applied.Added}개 · 갱신 {applied.Updated}개" + (changedWhileWaiting > 0 ? $" (대기 중 목록이 바뀌어 {changedWhileWaiting}개 제외)" : ""));
        return McpImport.FormatApplied(applied, source.Warnings, changedWhileWaiting);
    }
}
