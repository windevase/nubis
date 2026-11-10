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
/// </summary>
public static class ClaudeGuard
{
    public const string FileName = "nubis-permissions.json";

    /// <summary>계정·암호가 저장되는 파일 (백업·임시 파일 포함하도록 뒤에 * 를 붙여 쓴다)</summary>
    private static readonly string[] SecretFiles = { "hosts.json", "cloud_profiles.json", VaultStore.FileName };

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

    public static List<string> DenyRules(string dataDir)
    {
        var rules = new List<string>();
        var root = ToRulePath(dataDir);
        foreach (var f in SecretFiles)
        {
            if (root != null)
                foreach (var tool in new[] { "Read", "Edit" })   // Edit 규칙이 Write 등 모든 쓰기 도구를 막는다
                    rules.Add($"{tool}({root}/{f}*)");
            // 셸로 우회하는 것도 막는다 (파일 이름이 들어간 명령)
            rules.Add($"Bash(*{f}*)");
            rules.Add($"PowerShell(*{f}*)");
        }
        if (root != null)
        {
            rules.Add($"Edit({root}/claude_audit.log*)");
            rules.Add($"Edit({root}/claude/mcp-config.json)");
            rules.Add($"Edit({root}/claude/{FileName})");
            rules.Add($"Edit({root}/claude/{ActiveContextFile})");
            // 앱 설정은 update_settings(검증·승인)로만 — 파일을 직접 고쳐 자동 승인을 켜는 우회를 막는다
            rules.Add($"Edit({root}/settings.json*)");
        }
        // 저장된 암호 복호화(DPAPI) 시도
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

    public static string BuildSettingsJson(string dataDir, string? contextFile = null)
    {
        var deny = DenyRules(dataDir);
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
