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
            "화면만 열며 아무것도 바꾸지 않는다.",
            Schema(new { page = new { type = "string", @enum = new[] { "hosts", "new_host", "cloud", "vault", "known_hosts", "settings", "settings_language", "settings_login", "settings_sftp", "settings_claude", "onboarding" } } }, "page"),
            a => OnUi(() =>
            {
                var page = Arg(a, "page");
                if (!OpenAppPage(page)) throw new McpToolException($"알 수 없는 화면입니다: {page}");
                AuditLog.Write("open_app_page", new[] { page }, "(화면 열기)", "조회");
                return $"'{page}' 화면을 열었습니다. 사용자가 지금 그 화면을 보고 있습니다.";
            }));
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
