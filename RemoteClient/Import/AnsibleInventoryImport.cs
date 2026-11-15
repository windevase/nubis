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

    // ───────────────────────── 호스트 패턴 ─────────────────────────

    /// <summary>"web[01:03].example.com:2222" → 이름 목록 + 포트. 범위 안의 콜론은 포트로 보지 않는다</summary>
    internal static (List<string> Names, int? Port, bool Truncated) ExpandHostPattern(string pattern, int limit)
    {
        int? port = null;
        var body = pattern;
        // 범위 괄호를 지운 뒤 콜론이 정확히 하나이고 뒤가 숫자면 name:port (IPv6 주소는 콜론이 여러 개라 제외)
        var masked = RangePattern.Replace(body, m => new string('x', m.Length));
        int colon = masked.LastIndexOf(':');
        if (colon > 0 && masked.IndexOf(':') == colon && colon < masked.Length - 1 && masked.Substring(colon + 1).All(char.IsAsciiDigit))
        {
            if (int.TryParse(body.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var p)) port = p;
            body = body.Substring(0, colon);
        }
        var names = new List<string>();
        bool truncated = Expand(body, names, limit);
        return (names, port, truncated);
    }

    /// <summary>범위를 재귀로 펼친다. limit 개를 넘으면 멈추고 true</summary>
    private static bool Expand(string pattern, List<string> output, int limit)
    {
        var m = RangePattern.Match(pattern);
        if (!m.Success) { if (output.Count >= limit) return true; output.Add(pattern); return false; }

        var head = pattern.Substring(0, m.Index);
        var tail = pattern.Substring(m.Index + m.Length);
        var start = m.Groups[1].Value;
        var end = m.Groups[2].Value;
        int step = 1;
        if (m.Groups[3].Success && (!int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)) step = 1;

        if (start.All(char.IsAsciiDigit) && end.All(char.IsAsciiDigit))
        {
            if (!long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                || !long.TryParse(end, NumberStyles.None, CultureInfo.InvariantCulture, out var e))
                return ExpandLiteral(pattern, output, limit);
            // 시작값에 앞자리 0 이 있으면 그 자릿수로 맞춘다 (Ansible 과 같음)
            var fmt = start.Length > 1 && start[0] == '0' ? "D" + start.Length.ToString(CultureInfo.InvariantCulture) : "D";
            for (long i = s; i <= e; i += step)
                if (Expand(head + i.ToString(fmt, CultureInfo.InvariantCulture) + tail, output, limit)) return true;
            return false;
        }
        if (start.Length == 1 && end.Length == 1 && char.IsAsciiLetter(start[0]) && char.IsAsciiLetter(end[0]))
        {
            for (int c = start[0]; c <= end[0]; c += step)
                if (Expand(head + (char)c + tail, output, limit)) return true;
            return false;
        }
        return ExpandLiteral(pattern, output, limit);
    }

    /// <summary>범위로 해석할 수 없는 괄호는 그대로 둔다</summary>
    private static bool ExpandLiteral(string pattern, List<string> output, int limit)
    {
        if (output.Count >= limit) return true;
        output.Add(pattern);
        return false;
    }

    // ───────────────────────── 셸 인자 · ProxyJump ─────────────────────────

    /// <summary>Python shlex 와 비슷한 분리: 따옴표 · 백슬래시 처리, comments 면 토큰 시작의 # 부터 무시</summary>
    internal static List<string> ShellSplit(string s, bool comments)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inToken = false;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else cur.Append(c);
                continue;
            }
            if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < s.Length && (s[i + 1] is '"' or '\\')) cur.Append(s[++i]);
                else cur.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inToken) { result.Add(cur.ToString()); cur.Clear(); inToken = false; }
                continue;
            }
            if (comments && c == '#' && !inToken) break;
            inToken = true;
            if (c is '\'' or '"') quote = c;
            else if (c == '\\' && i + 1 < s.Length) cur.Append(s[++i]);
            else cur.Append(c);
        }
        if (inToken) result.Add(cur.ToString());
        return result;
    }

    /// <summary>ssh 인자에서 점프 서버를 찾는다: -J x · -o ProxyJump=x · -o ProxyCommand="ssh -W %h:%p user@bastion"</summary>
    internal static (string? Jump, bool MultiHop) FindJumpHost(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return (null, false);
        var tokens = ShellSplit(args, comments: false);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            string? opt = null, jump = null;
            if (t == "-o" && i + 1 < tokens.Count) opt = tokens[++i];
            else if (t.StartsWith("-o", StringComparison.Ordinal) && t.Length > 2) opt = t.Substring(2);
            else if (t == "-J" && i + 1 < tokens.Count) jump = tokens[++i];
            else if (t.StartsWith("-J", StringComparison.Ordinal) && t.Length > 2) jump = t.Substring(2);

            if (opt != null)
            {
                int sep = opt.IndexOfAny(new[] { '=', ' ', '\t' });
                if (sep <= 0) continue;
                var key = opt.Substring(0, sep).Trim();
                var value = opt.Substring(sep + 1).Trim().TrimStart('=').Trim();
                if (key.Equals("ProxyJump", StringComparison.OrdinalIgnoreCase)) jump = value;
                else if (key.Equals("ProxyCommand", StringComparison.OrdinalIgnoreCase))
                {
                    var pc = FromProxyCommand(value);
                    if (pc != null) return (pc, false);
                }
            }
            if (string.IsNullOrWhiteSpace(jump) || jump.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;

            var hops = jump.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hops.Length == 0) continue;
            var last = hops[^1];
            if (last.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) last = last.Substring(6);
            return (last.TrimEnd('/'), hops.Length > 1);
        }
        return (null, false);
    }

    /// <summary>"ssh -W %h:%p -q -p 2222 user@bastion" → "user@bastion:2222". ssh 가 아니면 null</summary>
    private static string? FromProxyCommand(string command)
    {
        var tokens = ShellSplit(command, comments: false);
        if (tokens.Count == 0) return null;
        var exe = Path.GetFileName(tokens[0].Replace('\\', '/'));
        if (!exe.Equals("ssh", StringComparison.OrdinalIgnoreCase) && !exe.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase)) return null;
        const string valueFlags = "BbcDEeFIiJLlmOoPpQRSWw";
        string? user = null, port = null, dest = null;
        for (int i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Length >= 2 && t[0] == '-')
            {
                char f = t[1];
                if (valueFlags.IndexOf(f) < 0) continue; // -q · -A · -T 등 값 없는 플래그
                string? v = t.Length > 2 ? t.Substring(2) : (i + 1 < tokens.Count ? tokens[++i] : null);
                if (f == 'p') port = v;
                else if (f == 'l') user = v;
                continue;
            }
            dest = t;
            break; // 첫 위치 인자가 대상, 그 뒤는 원격 명령
        }
        if (string.IsNullOrEmpty(dest)) return null;
        if (dest.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) dest = dest.Substring(6).TrimEnd('/');
        if (user != null && !dest.Contains('@')) dest = user + "@" + dest;
        if (port != null && port != "22") dest += ":" + port;
        return dest;
    }

    // ───────────────────────── 인벤토리 모델 ─────────────────────────

    private readonly record struct VarValue(string Value, bool Unresolved);

    private sealed class GroupInfo
    {
        public required string Name { get; init; }
        public required int Order { get; init; }
        public Dictionary<string, VarValue> Vars { get; } = new(StringComparer.Ordinal);
        public List<string> Parents { get; } = new();
        public HashSet<YamlNode> YamlVisited { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class HostInfo
    {
        public required string Name { get; init; }
        public Dictionary<string, VarValue> Vars { get; } = new(StringComparer.Ordinal);
        /// <summary>직접 속한 그룹 (선언 순서)</summary>
        public List<string> Groups { get; } = new();
    }

    private sealed class Inventory
    {
        private readonly Dictionary<string, GroupInfo> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HostInfo> _hosts = new(StringComparer.Ordinal);
        private readonly List<HostInfo> _hostOrder = new();
        private readonly List<string> _warnings = new();
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
        private bool _secretSeen, _limitHit;

        public void Warn(string message)
        {
            if (_warned.Add(message)) _warnings.Add(message);
        }

        public GroupInfo GetGroup(string name)
        {
            if (!_groups.TryGetValue(name, out var g))
                _groups[name] = g = new GroupInfo { Name = name, Order = _groups.Count };
            return g;
        }

        /// <summary>지금까지 나온 그룹 이름 (사본)</summary>
        public List<string> GroupNames() => _groups.Keys.ToList();

        /// <summary>호스트 이름과 그 호스트 변수 사전 (host_vars 를 덮어쓸 곳)</summary>
        public List<(string Name, Dictionary<string, VarValue> Vars)> HostVarTargets() =>
            _hostOrder.Select(h => (h.Name, h.Vars)).ToList();

        public void AddChild(string parent, string child)
        {
            GetGroup(parent);
            var c = GetGroup(child);
            if (child == AllGroup || child == parent) return;
            if (!c.Parents.Contains(parent)) c.Parents.Add(parent);
        }

        /// <summary>비밀번호는 버리고, 우리가 쓰는 변수만 저장. 템플릿 · 암호화 값은 "해석 안 됨"으로 표시해 둔다</summary>
        public void SetVar(Dictionary<string, VarValue> target, string key, string value, bool tagged)
        {
            if (IsSecret(key)) { _secretSeen = true; return; }
            if (!RelevantVars.Contains(key)) return;
            bool unresolved = tagged || value.Contains("{{", StringComparison.Ordinal) || value.Contains("{%", StringComparison.Ordinal);
            target[key] = new VarValue(value, unresolved);
        }

        public void AddHostPattern(string group, string pattern, List<KeyValuePair<string, string>> vars)
        {
            var dict = new Dictionary<string, VarValue>(StringComparer.Ordinal);
            foreach (var kv in vars) SetVar(dict, kv.Key, kv.Value, tagged: false); // 따옴표는 ShellSplit 가 이미 벗겼다
            AddHostPattern(group, pattern, dict);
        }

        public void AddHostPattern(string group, string pattern, Dictionary<string, VarValue> vars)
        {
            GetGroup(group);
            // 한 패턴은 최대 MaxHosts 개까지만 펼치고, 새 호스트는 전체 한도 안에서만 받는다 (이미 있는 호스트는 다시 세지 않음)
            var (names, port, truncated) = ExpandHostPattern(pattern, MaxHosts);
            if (truncated) _limitHit = true;
            foreach (var name in names)
            {
                if (!_hosts.TryGetValue(name, out var h))
                {
                    if (_hostOrder.Count >= MaxHosts) { _limitHit = true; continue; }
                    _hosts[name] = h = new HostInfo { Name = name };
                    _hostOrder.Add(h);
                }
                if (!h.Groups.Contains(group)) h.Groups.Add(group);
                // name:port 축약은 호스트 변수 ansible_port 로 (같은 줄의 ansible_port 가 있으면 그것이 이김)
                if (port is int p) h.Vars["ansible_port"] = new VarValue(p.ToString(CultureInfo.InvariantCulture), false);
                foreach (var kv in vars) h.Vars[kv.Key] = kv.Value;
            }
        }

        private static bool IsSecret(string key) =>
            key.StartsWith("ansible_", StringComparison.Ordinal) && key.Contains("pass", StringComparison.OrdinalIgnoreCase);

        // ── 결과 만들기 ──

        private int Depth(string name, HashSet<string> visiting)
        {
            if (name == AllGroup) return 0;
            if (!_groups.TryGetValue(name, out var g) || !visiting.Add(name)) return 1;
            int d = 1;
            foreach (var p in g.Parents) d = Math.Max(d, Depth(p, visiting) + 1);
            visiting.Remove(name);
            return d;
        }

        private void Ancestors(string name, HashSet<string> acc)
        {
            if (!acc.Add(name) || !_groups.TryGetValue(name, out var g)) return;
            foreach (var p in g.Parents) Ancestors(p, acc);
        }

        /// <summary>"prod / web" 처럼 첫 번째 상위 그룹을 따라 올라간 경로 (all 제외)</summary>
        private string GroupPath(string name)
        {
            var parts = new List<string> { name };
            var seen = new HashSet<string>(StringComparer.Ordinal) { name };
            var cur = name;
            while (_groups.TryGetValue(cur, out var g))
            {
                var parent = g.Parents.FirstOrDefault(p => p != AllGroup && p != UngroupedGroup && !seen.Contains(p));
                if (parent == null) break;
                parts.Add(parent);
                seen.Add(parent);
                cur = parent;
            }
            parts.Reverse();
            return string.Join(" / ", parts);
        }

        public ImportResult Build()
        {
            var result = ImportResult.Empty();
            var unresolvedKeys = new SortedSet<string>(StringComparer.Ordinal);
            int skippedConn = 0;
            bool multiHop = false;
            var depthCache = new Dictionary<string, int>(StringComparer.Ordinal);
            int DepthOf(string g) => depthCache.TryGetValue(g, out var d) ? d : depthCache[g] = Depth(g, new HashSet<string>(StringComparer.Ordinal));

            foreach (var h in _hostOrder)
            {
                // 변수 합치기: all → 그룹(얕은 것부터, 같은 깊이는 이름순 — Ansible 과 같음) → 호스트
                var groups = new HashSet<string>(StringComparer.Ordinal);
                foreach (var g in h.Groups) Ancestors(g, groups);
                groups.Remove(AllGroup);
                var merged = new Dictionary<string, VarValue>(StringComparer.Ordinal);
                if (_groups.TryGetValue(AllGroup, out var all)) foreach (var kv in all.Vars) merged[kv.Key] = kv.Value;
                foreach (var g in groups.OrderBy(DepthOf).ThenBy(x => x, StringComparer.Ordinal))
                    foreach (var kv in _groups[g].Vars) merged[kv.Key] = kv.Value;
                foreach (var kv in h.Vars) merged[kv.Key] = kv.Value;

                string? Get(string key)
                {
                    if (!merged.TryGetValue(key, out var v)) return null;
                    if (v.Unresolved) { unresolvedKeys.Add(key); return null; }
                    var s = v.Value.Trim();
                    return s.Length == 0 ? null : s;
                }

                var conn = Get("ansible_connection");
                var connName = conn == null ? "ssh" : conn.Substring(conn.LastIndexOf('.') + 1);
                if (NonRemoteConnections.Contains(connName)) { skippedConn++; continue; }
                bool winrm = connName.Equals("winrm", StringComparison.OrdinalIgnoreCase) || connName.Equals("psrp", StringComparison.OrdinalIgnoreCase);

                int? port = null;
                var portText = Get("ansible_port") ?? Get("ansible_ssh_port");
                if (portText != null)
                {
                    if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535) port = p;
                    else Warn(L.F("포트 값이 올바르지 않아 무시했습니다: {0}", h.Name + " = " + portText));
                }

                // 그룹: 직접 속한 그룹 중 가장 깊은 것 (같으면 먼저 선언된 것). ungrouped 는 다른 그룹이 없을 때만
                var direct = h.Groups.Where(g => g != AllGroup).ToList();
                if (direct.Count > 1) direct.RemoveAll(g => g == UngroupedGroup);
                string chosen = UngroupedGroup;
                int best = -1;
                foreach (var g in direct)
                {
                    int d = DepthOf(g);
                    if (d > best) { best = d; chosen = g; }
                }
                var chosenAncestors = new HashSet<string>(StringComparer.Ordinal);
                Ancestors(chosen, chosenAncestors);
                var others = direct.Where(g => !chosenAncestors.Contains(g)).ToList();

                var notes = new List<string>();
                if (winrm) notes.Add(port is int wp ? "WinRM :" + wp.ToString(CultureInfo.InvariantCulture) : "WinRM");
                if (others.Count > 0) notes.Add(L.F("다른 그룹: {0}", string.Join(", ", others)));

                string? jump = null;
                if (!winrm)
                {
                    var (j, multi) = FindJumpHost(Get("ansible_ssh_common_args"));
                    if (j == null) (j, multi) = FindJumpHost(Get("ansible_ssh_extra_args"));
                    jump = j;
                    multiHop |= multi;
                }

                result.Hosts.Add(new ImportedHost
                {
                    Name = h.Name,
                    Address = Get("ansible_host") ?? Get("ansible_ssh_host") ?? h.Name,
                    // WinRM 포트(5985/5986)는 RDP 포트가 아니므로 넣지 않는다
                    Port = winrm ? null : port,
                    Protocol = winrm ? Protocol.Rdp : Protocol.Ssh,
                    Group = GroupPath(chosen),
                    Username = Get("ansible_user") ?? Get("ansible_ssh_user"),
                    JumpHost = jump,
                    Os = winrm ? "Windows" : null,
                    Note = notes.Count > 0 ? string.Join(" · ", notes) : null,
                });
            }

            // 점프 서버가 인벤토리의 다른 호스트(이름 · 주소)를 가리키면 그 항목 이름으로 바꾼다 (사용자 · 포트 지정이 없을 때만)
            // 이름 · 주소 → 첫 항목 (호스트 수천 대에서 줄마다 목록을 훑지 않게)
            var byName = new Dictionary<string, ImportedHost>(StringComparer.Ordinal);
            var byAddress = new Dictionary<string, ImportedHost>(StringComparer.Ordinal);
            foreach (var x in result.Hosts)
            {
                byName.TryAdd(x.Name, x);
                byAddress.TryAdd(x.Address, x);
            }
            for (int i = 0; i < result.Hosts.Count; i++)
            {
                var jh = result.Hosts[i].JumpHost;
                if (jh == null || jh.Contains('@') || jh.Contains(':')) continue;
                var target = byName.TryGetValue(jh, out var n) ? n : byAddress.TryGetValue(jh, out var ad) ? ad : null;
                if (target != null && !ReferenceEquals(target, result.Hosts[i])) result.Hosts[i] = result.Hosts[i] with { JumpHost = target.Name };
            }

            var warnings = new List<string>();
            if (_secretSeen) warnings.Add(L.T("인벤토리의 비밀번호(ansible_password 등)는 가져오지 않았습니다. 계정은 Vault 로 지정하세요."));
            if (unresolvedKeys.Count > 0) warnings.Add(L.F("Jinja 템플릿이나 암호화된 값은 해석하지 않고 건너뛰었습니다: {0}", string.Join(", ", unresolvedKeys)));
            if (skippedConn > 0) warnings.Add(L.F("SSH · WinRM 이 아닌 연결 방식(local · docker 등)의 호스트 {0}개는 건너뛰었습니다.", skippedConn));
            if (multiHop) warnings.Add(L.T("다단계 ProxyJump 는 마지막 점프 서버만 가져왔습니다."));
            if (_limitHit) warnings.Add(L.F("호스트가 {0}개를 넘어 나머지는 가져오지 않았습니다.", MaxHosts));
            warnings.AddRange(_warnings);
            result.Warnings.AddRange(warnings);
            return result;
        }
    }
}
