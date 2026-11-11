using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Nubis;

/// <summary>
/// Claude 패널(MCP get_settings / update_settings)이 다룰 수 있는 앱 설정 목록과 값 검증.
/// - 화면·편의 설정: 바로 적용 (감사 로그)
/// - 보안 설정(Security): 앱 승인 창을 거친다
/// - Claude 가 켜면 안 되는 설정(자동 승인): 켜기는 거부, 끄기만 허용
/// 클라우드 인증 파일 경로처럼 계정과 관련된 설정은 목록에 넣지 않는다.
/// </summary>
public static class SettingsCatalog
{
    public sealed record Def(
        string Key,
        string Label,
        string Type,          // bool | int | string
        string Help,
        bool Security,
        Func<AppSettings, object> Get,
        Func<JsonElement, object> Parse,
        Action<AppSettings, object> Set);

    public sealed record Change(Def Def, object Old, object New)
    {
        public string Text => $"{Def.Label} ({Def.Key}): {Show(Old)} → {Show(New)}";
    }

    /// <summary>기본 포트는 클라우드 호스트 포트도 함께 바꿔야 해서 MainWindow 가 따로 적용한다</summary>
    public static readonly string[] PortKeys = { nameof(AppSettings.DefaultSshPort), nameof(AppSettings.DefaultRdpPort) };

    /// <summary>설치된 글꼴 확인 (UI 가 아닌 곳·테스트에서는 바꿀 수 있게)</summary>
    public static Func<string, bool> FontInstalled { get; set; } = name =>
        System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.Source.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<Def> All { get; } = new List<Def>
    {
        new("TerminalTheme", "터미널 테마", "string",
            "테마 id: " + string.Join(", ", TerminalThemes.All.Select(t => $"{t.Id}({t.Name}{(t.IsLight ? ", 밝은" : "")})")),
            false, s => s.TerminalTheme,
            v => TerminalThemes.All.FirstOrDefault(t => t.Id.Equals(Str(v), StringComparison.OrdinalIgnoreCase) ||
                                                        t.Name.Equals(Str(v), StringComparison.OrdinalIgnoreCase))?.Id
                 ?? throw new ArgumentException($"없는 테마입니다. 가능한 값: {string.Join(", ", TerminalThemes.All.Select(t => t.Id))}"),
            (s, v) => s.TerminalTheme = (string)v),
        new("FontFamily", "터미널 글꼴", "string", "이 PC 에 설치된 고정폭 글꼴 이름 (예: Cascadia Mono, D2Coding, Consolas)",
            false, s => s.FontFamily,
            v => FontInstalled(Str(v).Trim()) ? Str(v).Trim() : throw new ArgumentException($"'{Str(v)}' 글꼴이 설치되어 있지 않습니다."),
            (s, v) => s.FontFamily = (string)v),
        new("FontSize", "터미널 글꼴 크기", "int", "9 ~ 28 (px)",
            false, s => s.FontSize, v => Int(v, 9, 28), (s, v) => s.FontSize = (int)v),
        new("AutoContrast", "글자색 자동 대비 보정", "bool", "배경과 대비가 낮은 글자색을 읽을 수 있게 조정",
            false, s => s.AutoContrast, Bool, (s, v) => s.AutoContrast = (bool)v),
        new("HighlightIp", "구문 강조: IP 주소", "bool", "", false, s => s.HighlightIp, Bool, (s, v) => s.HighlightIp = (bool)v),
        new("HighlightNumbers", "구문 강조: 숫자", "bool", "", false, s => s.HighlightNumbers, Bool, (s, v) => s.HighlightNumbers = (bool)v),
        new("HighlightKeywords", "구문 강조: 상태 키워드", "bool", "error/warn/ok 등", false, s => s.HighlightKeywords, Bool, (s, v) => s.HighlightKeywords = (bool)v),
        new("TerminalGpu", "터미널 GPU 가속", "bool", "켜면 대량 출력이 빨라지지만 메모리 약 70MB 더 사용. 앱을 다시 시작해야 적용",
            false, s => s.TerminalGpu, Bool, (s, v) => s.TerminalGpu = (bool)v),
        new("DefaultSshPort", "기본 SSH 포트", "int", "새 호스트·클라우드 동기화에 쓸 포트. 바꾸면 포트를 따로 지정하지 않은 클라우드 호스트도 함께 변경",
            false, s => s.DefaultSshPort, v => Int(v, 1, 65535), (s, v) => s.DefaultSshPort = (int)v),
        new("DefaultRdpPort", "기본 RDP 포트", "int", "DefaultSshPort 와 같음 (RDP)",
            false, s => s.DefaultRdpPort, v => Int(v, 1, 65535), (s, v) => s.DefaultRdpPort = (int)v),
        new("SftpAutoOpen", "SSH 접속 시 SFTP 패널 자동 열기", "bool", "",
            false, s => s.SftpAutoOpen, Bool, (s, v) => s.SftpAutoOpen = (bool)v),
        new("SftpFollowTerminal", "SFTP 패널이 터미널 폴더 따라가기", "bool", "명령 실행 뒤 프롬프트(user@host:/경로$, [user@host 폴더]$)나 OSC 7 로 현재 폴더를 알아냄",
            false, s => s.SftpFollowTerminal, Bool, (s, v) => s.SftpFollowTerminal = (bool)v),
        // SftpEditor(편집기 프로그램 경로)는 넣지 않는다 — 바꿀 수 있으면 파일을 열 때 임의 프로그램을 실행시키는 통로가 된다
        new("RdpCertWarning", "RDP 서버 인증서 경고", "bool", "끄면 인증서를 확인할 수 없는 서버에도 경고 없이 접속 (보안 설정 — 사용자 승인 필요)",
            true, s => s.RdpCertWarning, Bool, (s, v) => s.RdpCertWarning = (bool)v),
        new("SshHostKeyPrompt", "SSH 새 서버 호스트키 확인 창", "bool", "켜면 처음 보는 서버의 호스트키를 확인 후 저장 (보안 설정 — 사용자 승인 필요)",
            true, s => s.SshHostKeyPrompt, Bool, (s, v) => s.SshHostKeyPrompt = (bool)v),
        new("AutoApproveReadOnly", "읽기 전용 명령 자동 승인", "bool",
            "Claude 의 run_command 중 조회 명령을 승인 없이 실행. Claude 는 끄기만 할 수 있고, 켜기는 사용자가 Settings 화면에서 직접 해야 한다",
            true, s => s.AutoApproveReadOnly,
            v => Bool(v) is true ? throw new ArgumentException("자동 승인은 Claude 가 켤 수 없습니다. 사용자에게 Settings → Claude 에서 직접 켜 달라고 안내하세요.") : false,
            (s, v) => s.AutoApproveReadOnly = (bool)v),
    };

    public static Def? Find(string key) =>
        All.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>get_settings 결과: 키 | 현재 값 | 형식 | 설명</summary>
    public static string Describe(AppSettings s)
    {
        var sb = new StringBuilder("키 | 현재 값 | 형식 | 이름 · 설명\n");
        foreach (var d in All)
            sb.AppendLine($"{d.Key} | {Show(d.Get(s))} | {d.Type} | {d.Label}{(d.Help.Length > 0 ? " · " + d.Help : "")}{(d.Security ? " [보안: 승인 필요]" : "")}");
        sb.Append("바꾸려면 update_settings 에 {\"changes\": {\"키\": 값}} 형식으로 넘기세요.");
        return sb.ToString();
    }

    /// <summary>
    /// 변경 요청을 검증해 실제로 바뀌는 항목만 돌려준다. 잘못된 키·값이 하나라도 있으면 아무것도 바꾸지 않도록 예외.
    /// </summary>
    public static List<Change> Plan(AppSettings current, JsonElement changes)
    {
        if (changes.ValueKind != JsonValueKind.Object || !changes.EnumerateObject().Any())
            throw new ArgumentException("changes 에 {\"키\": 값} 을 하나 이상 넣으세요. 키 목록은 get_settings 로 확인합니다.");
        var errors = new List<string>();
        var list = new List<Change>();
        foreach (var p in changes.EnumerateObject())
        {
            var d = Find(p.Name);
            if (d == null) { errors.Add($"{p.Name}: 바꿀 수 없는 설정입니다 (get_settings 로 키 확인)"); continue; }
            try
            {
                var value = d.Parse(p.Value);
                var old = d.Get(current);
                if (!Equals(old, value)) list.Add(new Change(d, old, value));
            }
            catch (Exception ex) { errors.Add($"{d.Key}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        return list;
    }

    // ----- 값 변환 -----

    private static string Str(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : throw new ArgumentException("문자열이어야 합니다.");

    private static object Bool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
        _ => throw new ArgumentException("true 또는 false 여야 합니다."),
    };

    private static object Int(JsonElement v, int min, int max)
    {
        int n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x)) n = x;
        else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var y)) n = y;
        else throw new ArgumentException("정수여야 합니다.");
        return n >= min && n <= max ? n : throw new ArgumentException($"{min} ~ {max} 사이여야 합니다.");
    }

    private static string Show(object v) => v switch
    {
        bool b => b ? "켜짐(true)" : "꺼짐(false)",
        string s => $"\"{s}\"",
        _ => v.ToString() ?? "",
    };
}
