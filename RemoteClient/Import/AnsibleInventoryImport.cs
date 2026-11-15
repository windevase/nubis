using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nubis;

/// <summary>
/// Ansible 인벤토리(INI · YAML) 가져오기. 형식은 내용으로 자동 판별한다.
/// 그룹 · 하위 그룹(children) · 그룹 변수(vars) · 호스트 범위(web[01:03]) · name:port 축약을 해석하고,
/// 변수 우선순위는 호스트 변수 > 그룹 변수(하위 그룹 > 상위 그룹) > all 변수.
/// 비밀번호 변수는 절대 가져오지 않는다. Jinja 템플릿 값은 해석하지 않고 건너뛴다.
/// YAML 은 YamlDotNet 표현 모델(YamlStream)로만 읽는다 — 타입 인스턴스화 없음.
/// </summary>
public static partial class AnsibleInventoryImport
{
    /// <summary>가져올 최대 호스트 수 (범위 확장 폭주 방지)</summary>
    public const int MaxHosts = 5000;
    /// <summary>group_vars · host_vars 변수 파일 한 개 최대 크기 / 읽을 최대 파일 수</summary>
    private const long MaxVarsFileBytes = 1024 * 1024;
    private const int MaxVarsFiles = 2000;

    private const string AllGroup = "all";
    private const string UngroupedGroup = "ungrouped";
    /// <summary>YAML 그룹 중첩 깊이 한도 (앵커 · 별칭 순환 방지)</summary>
    private const int MaxDepth = 64;

    /// <summary>우리가 쓰는 변수만 저장한다 (나머지 변수는 의미가 없으므로 무시)</summary>
    private static readonly HashSet<string> RelevantVars = new(StringComparer.Ordinal)
    {
        "ansible_host", "ansible_ssh_host", "ansible_port", "ansible_ssh_port", "ansible_user", "ansible_ssh_user",
        "ansible_connection", "ansible_ssh_common_args", "ansible_ssh_extra_args",
    };

    /// <summary>SSH · WinRM 이 아닌 연결 플러그인 — 원격 접속 대상이 아니므로 건너뛴다</summary>
    private static readonly HashSet<string> NonRemoteConnections = new(StringComparer.OrdinalIgnoreCase)
    {
        "local", "docker", "podman", "buildah", "kubectl", "oc", "chroot", "lxc", "lxd", "jail", "zone", "funcd", "saltstack",
        "qubes", "iocage", "incus", "libvirt_lxc", "nspawn",
    };

    private static readonly Regex IniSection = new(@"^\[([^:\]\s]+)(?::(\w+))?\]\s*(?:[#;].*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex RangePattern = new(@"\[([0-9a-zA-Z]+):([0-9a-zA-Z]+)(?::(\d+))?\]", RegexOptions.CultureInvariant);
    private static readonly Regex YamlKeyLine = new(@"^[^\s#\[\]{},=]+:\s*(#.*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex YamlSectionLine = new(@"^\s+(hosts|children|vars):\s*(#.*)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 파일을 읽어 파싱한다. 인벤토리 옆의 group_vars/ · host_vars/ 변수 파일도 읽는다 (Ansible 과 같이 인벤토리 안 변수보다 우선).
    /// 파일을 못 읽으면 예외를 그대로 던진다 (호출하는 화면이 표시)
    /// </summary>
    public static ImportResult ParseFile(string path) =>
        Parse(ImportText.ReadFile(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    /// <summary>
    /// INI / YAML 을 자동 판별해 파싱한다. varsDir 를 주면 그 폴더의 group_vars/ · host_vars/ 를 읽는다.
    /// blockPath: 변수 파일 검사 (막을 이유를 돌려주면 건너뜀 — MCP 에서 데이터 폴더 등 차단용)
    /// </summary>
    public static ImportResult Parse(string text, string? varsDir = null, Func<string, string?>? blockPath = null)
    {
        text ??= "";
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        var inv = new Inventory();
        if (LooksLikeYaml(text)) ParseYaml(text, inv);
        else ParseIni(text, inv);
        if (!string.IsNullOrEmpty(varsDir)) LoadVarsDirs(inv, varsDir, blockPath);
        return inv.Build();
    }

    // ───────────────────────── 형식 판별 ─────────────────────────

    /// <summary>
    /// 첫 의미 있는 줄로 판단: [섹션] → INI, "---" · "{" · "키:" → YAML.
    /// 애매하면 hosts:/children:/vars: 들여쓰기 줄이 있는지 본다. 그 외는 INI (섹션 없는 호스트 목록 포함).
    /// </summary>
    internal static bool LooksLikeYaml(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith(';')) continue;
            if (IniSection.IsMatch(t)) return false;
            if (t == "---" || t.StartsWith('{') || t.StartsWith("%YAML", StringComparison.Ordinal)) return true;
            if (YamlKeyLine.IsMatch(line)) return true;
            break;
        }
        return lines.Any(l => YamlSectionLine.IsMatch(l.TrimEnd()));
    }

    // ───────────────────────── INI ─────────────────────────

    private static void ParseIni(string text, Inventory inv)
    {
        string group = UngroupedGroup;
        string kind = "hosts";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            var sec = IniSection.Match(line);
            if (sec.Success)
            {
                group = sec.Groups[1].Value;
                kind = sec.Groups[2].Success ? sec.Groups[2].Value : "hosts";
                if (kind is not ("hosts" or "children" or "vars"))
                {
                    inv.Warn(L.F("알 수 없는 섹션을 건너뛰었습니다: {0}", line));
                    kind = "skip";
                    continue;
                }
                inv.GetGroup(group);
                continue;
            }

            switch (kind)
            {
                case "hosts":
                {
                    var tokens = ShellSplit(line, comments: true);
                    if (tokens.Count == 0) continue;
                    var vars = new List<KeyValuePair<string, string>>();
                    for (int i = 1; i < tokens.Count; i++)
                    {
                        int eq = tokens[i].IndexOf('=');
                        if (eq <= 0) continue; // key=value 가 아닌 토큰은 Ansible 도 오류 — 무시
                        vars.Add(new(tokens[i].Substring(0, eq), tokens[i].Substring(eq + 1)));
                    }
                    inv.AddHostPattern(group, tokens[0], vars);
                    break;
                }
                case "children":
                {
                    var tokens = ShellSplit(line, comments: true);
                    if (tokens.Count == 0) continue;
                    inv.AddChild(group, tokens[0]);
                    break;
                }
                case "vars":
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var value = StripInlineComment(line.Substring(eq + 1)).Trim();
                    inv.SetVar(inv.GetGroup(group).Vars, key, Unquote(value), tagged: false);
                    break;
                }
            }
        }
    }

    /// <summary>vars 섹션 값의 " # 주석" 제거 (따옴표 안은 유지)</summary>
    private static string StripInlineComment(string s)
    {
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(s[i - 1]))) return s.Substring(0, i);
        }
        return s;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]) return s.Substring(1, s.Length - 2);
        return s;
    }

    // ───────────────────────── YAML ─────────────────────────

    private static void ParseYaml(string text, Inventory inv)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (Exception ex) when (ex is YamlException or ArgumentException or InvalidOperationException)
        {
            inv.Warn(L.F("YAML 인벤토리를 읽지 못했습니다: {0}", ex.Message));
            return;
        }
        if (stream.Documents.Count == 0) return;
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            inv.Warn(L.T("YAML 인벤토리 형식이 아닙니다 (최상위가 그룹 매핑이어야 합니다)."));
            return;
        }
        foreach (var kv in root.Children)
        {
            if (kv.Key is not YamlScalarNode { Value: { Length: > 0 } name }) continue;
            // 최상위 그룹은 all 의 하위 그룹 (all 자신 제외)
            if (name != AllGroup) inv.AddChild(AllGroup, name);
            WalkYamlGroup(inv, name, kv.Value, 0);
        }
    }

    private static void WalkYamlGroup(Inventory inv, string name, YamlNode node, int depth)
    {
        var group = inv.GetGroup(name);
        if (depth > MaxDepth || node is not YamlMappingNode map) return;
        if (!group.YamlVisited.Add(node)) return; // 같은 노드(별칭)를 두 번 걷지 않는다

        foreach (var kv in map.Children)
        {
            if (kv.Key is not YamlScalarNode { Value: { } section }) continue;
            switch (section)
            {
                case "vars":
                    if (kv.Value is YamlMappingNode vars) ReadYamlVars(inv, vars, group.Vars);
                    break;
                case "hosts":
                    if (kv.Value is YamlMappingNode hosts)
                        foreach (var h in hosts.Children)
                        {
                            if (h.Key is not YamlScalarNode { Value: { Length: > 0 } pattern }) continue;
                            var hv = new Dictionary<string, VarValue>(StringComparer.Ordinal);
                            if (h.Value is YamlMappingNode hm) ReadYamlVars(inv, hm, hv);
                            inv.AddHostPattern(name, pattern, hv);
                        }
                    break;
                case "children":
                    if (kv.Value is YamlMappingNode children)
                        foreach (var c in children.Children)
                        {
                            if (c.Key is not YamlScalarNode { Value: { Length: > 0 } child }) continue;
                            inv.AddChild(name, child);
                            WalkYamlGroup(inv, child, c.Value, depth + 1);
                        }
                    break;
            }
        }
    }

    private static void ReadYamlVars(Inventory inv, YamlMappingNode map, Dictionary<string, VarValue> target)
    {
        foreach (var kv in map.Children)
        {
            if (kv.Key is not YamlScalarNode { Value: { } key }) continue;
            if (kv.Value is not YamlScalarNode scalar) continue; // 목록 · 매핑 값은 쓰지 않는다
            // !vault 등 태그가 붙은 값(암호화 값)은 해석하지 않는다
            bool tagged = !scalar.Tag.IsEmpty && scalar.Tag.Value is not ("!" or "?")
                && !scalar.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal);
            inv.SetVar(target, key, scalar.Value ?? "", tagged);
        }
    }
}
