using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nubis;

/// <summary>
/// 설정 안내용 MCP 도구: get_setup_status(무엇을 했고 무엇이 남았는지 — 비밀 값 없음), open_app_page(해당 화면 열기).
/// Claude 가 "시작하기" 의 질문을 받아 사용자와 같은 화면을 보며 한 단계씩 안내할 때 쓴다.
/// 로그인(SSO) 설정은 Claude 가 바꿀 수 없다 — 사용자가 직접 입력·저장한다.
/// </summary>
public partial class MainWindow
{
    private IEnumerable<McpTool> SetupTools()
    {
        yield return new McpTool("get_setup_status",
            "Nubis 설정 상태 요약 (비밀 값 없음): 화면 언어, 시작 화면 로그인(SSO) 사용 여부·제공자·현재 프로필, 호스트 수(SSH/RDP), " +
            "클라우드 프로필 수와 이 PC 의 클라우드 CLI 인증 파일 유무, Vault 계정 수, 저장된 호스트키 수, SFTP·자동 승인 설정. " +
            "사용자가 설정 안내를 부탁하면 먼저 이걸로 무엇이 남았는지 확인하세요.",
            Schema(new { }),
            _ => OnUi(SetupStatus));

        yield return new McpTool("open_app_page",
            "Nubis 앱에서 화면을 연다 (사용자와 같은 화면을 보며 안내할 때). page: hosts · new_host(새 호스트 편집 창) · cloud · vault · known_hosts · " +
            "settings · settings_language · settings_login(시작 화면 로그인/SSO) · settings_sftp · settings_claude · onboarding(시작하기 안내). " +
            "사용자가 직접 입력할 창: vault_new(새 Vault 계정 창, name · username 으로 미리 채우기 가능) · vault_edit:<계정 이름 또는 id> · " +
            "cloud_profile:<프로필 이름 또는 id>(프로필 설정 창) · host_edit:<호스트 이름·IP·id>(호스트 편집 창). " +
            "비밀번호·키는 Claude 가 넣을 수 없으므로 사용자가 그 창에 직접 입력·저장한다. 화면·창만 열며 아무것도 바꾸지 않는다 (저장은 사용자가 함).",
            Schema(new
            {
                page = new { type = "string", description = "화면 이름 (위 목록) 또는 vault_edit:… · cloud_profile:… · host_edit:…" },
                name = new { type = "string", description = "vault_new: 미리 채울 계정 이름" },
                username = new { type = "string", description = "vault_new: 미리 채울 사용자명" },
            }, "page"),
            a => OnUi(() =>
            {
                var page = Arg(a, "page").Trim();
                if (OpenEditDialogPage(page, a) is { } opened)
                {
                    AuditLog.Write("open_app_page", new[] { McpHelpers.VisibleControlChars(page) }, "(입력 창 열기)", "조회");
                    return opened;
                }
                if (!OpenAppPage(page)) throw new McpToolException($"알 수 없는 화면입니다: {page}");
                AuditLog.Write("open_app_page", new[] { page }, "(화면 열기)", "조회");
                return $"'{page}' 화면을 열었습니다. 사용자가 지금 그 화면을 보고 있습니다.";
            }));
    }

    /// <summary>미리 채우는 값: 제어 문자(줄 바꿈 포함)를 빼고 길이 제한 — 입력 칸에 보이지 않는 글자가 숨어 들지 않게</summary>
    private static string PrefillText(string s)
    {
        var clean = new string(s.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)).ToArray()).Trim();
        if (clean.Length <= 128) return clean;
        // 서로게이트 쌍(이모지 등) 가운데에서 자르지 않는다
        var cut = char.IsHighSurrogate(clean[127]) ? 127 : 128;
        return clean[..cut];
    }

    // open_app_page 로 띄운 입력 창(모달)이 열려 있는 동안 또 띄우지 않는다 (창 위에 창이 겹겹이 쌓이지 않게)
    private bool _mcpEditDialogOpen;

    /// <summary>
    /// 사용자가 직접 입력할 창을 연다 (vault_new · vault_edit: · cloud_profile: · host_edit:). 이 종류가 아니면 null.
    /// 대상은 여기서 확인해 오류를 바로 돌려주고, 모달 창은 MCP 응답을 붙잡지 않게 나중에(BeginInvoke) 띄운다.
    /// 저장은 사용자가 창에서 직접 한다 — Claude 는 값을 넣거나 읽지 않는다 (비밀번호·키는 사용자 입력만).
    /// </summary>
    private string? OpenEditDialogPage(string page, System.Text.Json.JsonElement a)
    {
        var (kind, target) = page.IndexOf(':') is var i and > 0 ? (page[..i].Trim().ToLowerInvariant(), page[(i + 1)..].Trim()) : (page.ToLowerInvariant(), "");
        if (kind is "vault_new" or "vault_edit" or "cloud_profile" && _mcpEditDialogOpen)
            throw new McpToolException("open_app_page 로 연 입력 창이 아직 열려 있습니다. 사용자가 그 창을 저장하거나 닫은 뒤 다시 시도하세요.");
        Action? open = null;
        string result;
        switch (kind)
        {
            case "vault_new":
            {
                var name = PrefillText(Arg(a, "name"));
                var user = PrefillText(Arg(a, "username"));
                SelectHome();
                NavVault.IsChecked = true;
                open = () =>
                {
                    if (VaultEntryDialog.Create(this, name, user) is not { } entry) return;
                    SaveVault(entry);
                    RefreshVault();
                };
                result = "새 Vault 계정 창을 열었습니다. 사용자가 비밀번호·SSH 키를 직접 입력하고 저장합니다 (저장됐는지는 list_vault 로 확인).";
                break;
            }
            case "vault_edit":
            {
                var v = ResolveVaultStrict(target);
                SelectHome();
                NavVault.IsChecked = true;
                var id = v.Id;
                open = () => EditVaultEntry(id);
                result = $"Vault 계정 '{v.Name}' 수정 창을 열었습니다. 사용자가 직접 고치고 저장합니다.";
                break;
            }
            case "cloud_profile":
            {
                var p = ResolveProfileForPage(target);
                SelectHome();
                NavCloud.IsChecked = true;
                open = () => { if (_profiles.Contains(p)) EditProfile(p); };
                result = $"클라우드 프로필 [{p.ProviderText}] {p.GroupName} 설정 창을 열었습니다. 사용자가 직접 고치고 저장합니다.";
                break;
            }
            case "host_edit":
            {
                var h = ResolveOneHost(target);
                SelectHome();
                NavHosts.IsChecked = true;
                open = () => { if (_hosts.Contains(h)) OpenEditor(h); };
                result = $"호스트 '{h.DisplayName}' 편집 창을 열었습니다. 사용자가 직접 고치고 저장합니다.";
                break;
            }
            default:
                return null;
        }
        Dialog.RestoreIfMinimized(this);
        Activate();
        if (kind == "host_edit") Dispatcher.BeginInvoke(open, System.Windows.Threading.DispatcherPriority.Background);
        else
        {
            // 모달 창: 띄우기 전(대기 중)부터 닫힐 때까지 표시해 두 번째 요청을 막는다
            _mcpEditDialogOpen = true;
            var modal = open!;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { modal(); }
                finally { _mcpEditDialogOpen = false; }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        return result;
    }

    private CloudProfile ResolveProfileForPage(string spec)
    {
        if (spec.Length == 0) throw new McpToolException("cloud_profile:<프로필 이름 또는 id> 형식으로 지정하세요.");
        var hit = _profiles.Where(p => p.GroupName.Equals(spec, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(spec, StringComparison.OrdinalIgnoreCase) ||
                                       $"[{p.ProviderText}] {p.GroupName}".Equals(spec, StringComparison.OrdinalIgnoreCase) ||
                                       (spec.Length >= 8 && p.Id.ToString().StartsWith(spec, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        return hit.Count switch
        {
            1 => hit[0],
            0 => throw new McpToolException($"클라우드 프로필 '{spec}' 을 찾을 수 없습니다."),
            _ => throw new McpToolException($"'{spec}' 가 프로필 {hit.Count}개와 일치합니다. id 로 지정하세요: " +
                                            string.Join(", ", hit.Select(p => $"[{p.ProviderText}] {p.GroupName} ({p.Id.ToString()[..8]})"))),
        };
    }

    private string SetupStatus()
    {
        var s = SettingsStore.Current;
        var g = SettingsStore.Global;
        var login = g.Login;
        var sb = new StringBuilder();
        sb.AppendLine($"화면 언어: {(L.IsEnglish ? "English" : "한국어")}");
        sb.AppendLine(login.Enabled
            ? $"시작 화면 로그인: 사용 중 ({LoginConfig.ProviderLabel(login.Provider)}), 허용 {login.AllowedAccounts.Count}개" +
              (login.GitHubOrg.Length > 0 ? $" + Organization {login.GitHubOrg}" : "") + $", 로그인 유지 {login.RememberHours}시간, 오프라인 허용 {login.OfflineDays}일"
            : "시작 화면 로그인: 사용 안 함 (설정은 Settings → 시작 화면 로그인. Claude 는 이 설정을 바꿀 수 없음 — 사용자가 직접 Client ID 입력 후 '테스트 로그인 후 저장')");
        sb.AppendLine(AppPaths.UsingProfile
            ? $"현재 프로필: {Path.GetFileName(AppPaths.DataDir)}{(Path.GetFileName(AppPaths.DataDir) == login.OwnerKey ? " (소유자)" : "")}"
            : "현재 프로필: 공통 폴더 (계정별 프로필 없음)");
        var ssh = _hosts.Count(h => h.Protocol == Protocol.Ssh);
        var rdp = _hosts.Count(h => h.Protocol == Protocol.Rdp);
        sb.AppendLine($"호스트: {_hosts.Count}개 (SSH {ssh}, RDP {rdp}, 클라우드 동기화 {_hosts.Count(h => h.IsCloud)}, 계정 저장됨 {_hosts.Count(h => h.UsesVault || h.ProtectedPassword != null || h.HasKey)})");
        sb.AppendLine($"클라우드 프로필: {_profiles.Count}개" + (_profiles.Count > 0 ? " — " + string.Join(", ", _profiles.GroupBy(p => p.ProviderText).Select(x => $"{x.Key} {x.Count()}")) : ""));
        foreach (var kind in CloudProviders.All.Select(p => p.Kind))
        {
            var path = CloudPaths.Effective(kind);
            var present = CloudPaths.IsFolder(kind) ? Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() : File.Exists(path);
            sb.AppendLine($"  {CloudProviders.Label(kind)} CLI 인증 파일: {(present ? "있음" : "없음")} ({path})");
        }
        sb.AppendLine($"Vault 계정: {VaultStore.All().Count}개");
        sb.AppendLine($"저장된 호스트키(known_hosts): {KnownHostStore.Load().Count}개");
        sb.AppendLine($"SFTP: 자동 열기 {(s.SftpAutoOpen ? "켬" : "끔")}, 터미널 폴더 따라가기 {(s.SftpFollowTerminal ? "켬" : "끔")}, 편집기 {(s.SftpEditor.Length > 0 ? Path.GetFileName(s.SftpEditor) : "Windows 기본")}");
        sb.AppendLine($"Claude 읽기 전용 명령 자동 승인: {(s.AutoApproveReadOnly ? "켬" : "끔")}");
        sb.AppendLine($"열린 세션 탭: {_tabs.Count}개");
        return sb.ToString();
    }
}
