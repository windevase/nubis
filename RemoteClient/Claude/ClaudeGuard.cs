using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nubis;

/// <summary>
/// Claude 패널에 넘기는 권한 거부 규칙 (claude --settings).
/// 계정·암호·키가 든 데이터 파일(hosts.json, cloud_profiles.json, vault.json)을 Claude 가 Read 도구나 셸 명령으로 읽지 못하게 하고,
/// 감사 로그·MCP 설정·이 규칙 파일은 고치지 못하게 한다.
/// 같은 Windows 계정에서 도는 프로그램이라 완전한 차단은 아니며(사용자가 다른 방법을 승인하면 우회 가능),
/// 실수나 프롬프트 주입으로 암호 파일을 건드리는 것을 막는 용도다. 암호 자체는 DPAPI 로 암호화되어 있다.
/// <para>
/// 이 규칙은 <b>2차 안전장치</b>일 뿐이다. 규칙은 단순한 부분 문자열·glob 패턴이라
/// 와일드카드(host?.json), 문자열 쪼개기('Protected'+'Data'), python 등 다른 인터프리터의 스크립트로 쉽게 돌아갈 수 있다 — 빈틈없게 만들려 하지 않는다.
/// 실제 보호는 (1) 데이터 파일 전체 DPAPI 암호화(SafeFile.WriteProtected) · (2) 원격에 영향을 주는 MCP 동작의 승인 창(ApprovalDialog) + 감사 로그 ·
/// (3) MCP 결과·로그에 비밀번호를 넣지 않는 것이다. 새 보호가 필요하면 이 규칙이 아니라 그쪽을 강화한다.
/// </para>
/// </summary>
public static class ClaudeGuard
{
    public const string FileName = "nubis-permissions.json";

    /// <summary>계정·암호가 저장되는 파일 (백업·임시 파일 포함하도록 뒤에 * 를 붙여 쓴다)</summary>
    private static readonly string[] SecretFiles = { "hosts.json", "cloud_profiles.json", "known_hosts.json", VaultStore.FileName, LoginState.FileName };

    /// <summary>
    /// Windows 경로를 Claude Code 권한 규칙의 절대 경로 형식으로 바꾼다. C:\Users\a → //c/Users/a
    /// 드라이브 경로가 아니면(UNC 등) null.
    /// </summary>
    public static string? ToRulePath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath).TrimEnd('\\');
        if (full.Length < 2 || full[1] != ':' || !char.IsLetter(full[0])) return null;
        return "//" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
    }

    /// <param name="dataDir">지금 쓰는 데이터 폴더 (로그인했으면 그 계정의 프로필 폴더)</param>
    /// <param name="rootDir">공통 폴더 (로그인 설정·로그인 기록, users\ 아래 다른 계정의 프로필). 생략하면 dataDir 와 같음</param>
    public static List<string> DenyRules(string dataDir, string? rootDir = null)
    {
        var rules = new List<string>();
        var root = ToRulePath(dataDir);
        var common = rootDir != null ? ToRulePath(rootDir) : null;
        if (common == root) common = null;
        // 같은 보호 규칙을 지금 데이터 폴더 · 공통 폴더 · 모든 계정 프로필(users\**)에 건다
        // (공통 settings.json 에는 로그인 설정이 있어, 고쳐서 로그인을 끄거나 소유자를 바꾸는 우회도 막는다)
        var bases = new List<string>();
        if (root != null) bases.Add(root);
        if (common != null) bases.Add(common);
        if ((common ?? root) is { } top) bases.Add($"{top}/users/**");

        foreach (var f in SecretFiles)
        {
            foreach (var b in bases)
                foreach (var tool in new[] { "Read", "Edit" })   // Edit 규칙이 Write 등 모든 쓰기 도구를 막는다
                    rules.Add($"{tool}({b}/{f}*)");
            // 셸로 우회하는 것도 막는다 (파일 이름이 그대로 들어간 명령만 — 와일드카드·문자열 조합 등은 못 막는 2차 안전장치)
            rules.Add($"Bash(*{f}*)");
            rules.Add($"PowerShell(*{f}*)");
        }
        foreach (var b in bases)
        {
            rules.Add($"Edit({b}/claude_audit.log*)");
            rules.Add($"Edit({b}/claude/mcp-config.json)");
            rules.Add($"Edit({b}/claude/{FileName})");
            rules.Add($"Edit({b}/claude/{ActiveContextFile})");
            // 앱 설정은 update_settings(검증·승인)로만 — 파일을 직접 고쳐 자동 승인을 켜는 우회를 막는다
            rules.Add($"Edit({b}/settings.json*)");
        }
        // 저장된 암호 복호화(DPAPI) 시도 — 키워드가 그대로 들어간 명령만 막는다 (2차 안전장치, 우회 가능)
        foreach (var kw in new[] { "ProtectedData", "CryptUnprotectData", "ProtectedPassword", "ProtectedSecret", "ProtectedKey", "ProtectedPassphrase", "AutoApproveReadOnly" })
        {
            rules.Add($"Bash(*{kw}*)");
            rules.Add($"PowerShell(*{kw}*)");
        }
        return rules;
    }

    /// <summary>앱이 현재 탭 정보를 써 두는 파일 (claude 작업 폴더 안)</summary>
    public const string ActiveContextFile = "active-session.txt";

    /// <summary>
    /// UserPromptSubmit hook 명령: 현재 탭 정보 파일을 출력한다 (출력이 사용자 메시지에 덧붙는 맥락이 된다).
    /// hook 을 bash·cmd·PowerShell 어느 셸이 실행해도 같게 동작하도록 powershell.exe 를 직접 부르고,
    /// 경로는 슬래시로 바꿔 백슬래시 이스케이프 문제를 피한다. 한글이 깨지지 않게 UTF-8 로 출력.
    /// </summary>
    public static string ContextHookCommand(string contextFile)
    {
        var p = Path.GetFullPath(contextFile).Replace('\\', '/').Replace("'", "''");
        return "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
               $"\"[Console]::OutputEncoding=[Text.Encoding]::UTF8; if (Test-Path -LiteralPath '{p}') {{ Get-Content -Raw -Encoding UTF8 -LiteralPath '{p}' }}\"";
    }

    public static string BuildSettingsJson(string dataDir, string? contextFile = null, string? rootDir = null)
    {
        var deny = DenyRules(dataDir, rootDir);
        object settings = contextFile == null
            ? new { permissions = new { deny } }
            : new
            {
                permissions = new { deny },
                hooks = new
                {
                    UserPromptSubmit = new[]
                    {
                        new { hooks = new[] { new { type = "command", command = ContextHookCommand(contextFile), timeout = 10 } } },
                    },
                },
            };
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }
}
