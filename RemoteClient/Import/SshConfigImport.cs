using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nubis;

/// <summary>
/// OpenSSH 클라이언트 설정(~/.ssh/config, ssh_config(5)) 가져오기.
/// Host 블록의 구체적인 별칭마다 서버 한 대를 만든다. 값은 OpenSSH 와 같이 "파일 순서상 처음 얻은 값"을 쓰므로
/// 뒤쪽의 Host * 같은 와일드카드 블록은 앞에서 정하지 않은 값의 기본값이 된다.
/// Match 블록은 평가하지 않고 건너뛴다. 키 파일(IdentityFile)은 가져오지 않고 경로만 메모에 남긴다.
/// </summary>
public static class SshConfigImport
{
    /// <summary>Include 중첩 한도 (자기 자신을 다시 포함하는 순환도 여기서 끊긴다)</summary>
    public const int MaxIncludeDepth = 8;

    private const string GroupName = "SSH config";

    /// <summary>%USERPROFILE%\.ssh\config</summary>
    public static string DefaultPath => Path.Combine(SshDir, "config");

    private static string SshDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>
    /// 설정 텍스트를 읽는다. Include 의 상대 경로는 baseDir(없으면 ~/.ssh) 기준.
    /// blockPath: Include 로 읽을 파일 검사 (막을 이유를 돌려주면 건너뜀) — Claude(MCP) 가 넘긴 내용이 Include 로
    /// Nubis 데이터 폴더·개인키 등을 읽지 못하게 할 때 쓴다
    /// </summary>
    public static ImportResult Parse(string text, string? baseDir = null, Func<string, string?>? blockPath = null)
    {
        var st = new State(baseDir ?? SshDir) { BlockPath = blockPath };
        ParseText(text ?? "", "config", 0, st);
        return Build(st);
    }

    /// <summary>설정 파일을 읽는다. Include 의 상대 경로는 이 파일이 있는 폴더(보통 ~/.ssh) 기준</summary>
    public static ImportResult ParseFile(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var r = ImportResult.Empty();
            r.Warnings.Add(L.F("파일을 찾을 수 없습니다: {0}", path));
            return r;
        }

        var st = new State(Path.GetDirectoryName(full) ?? SshDir);
        if (!File.Exists(full))
        {
            st.Warnings.Add(L.F("파일을 찾을 수 없습니다: {0}", full));
            return Build(st);
        }
        string text;
        try { text = File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            st.Warnings.Add(L.F("파일을 읽을 수 없습니다: {0} ({1})", full, ex.Message));
            return Build(st);
        }
        ParseText(text, Path.GetFileName(full), 0, st);
        return Build(st);
    }

    // ───────────── 파싱 ─────────────

    private sealed class Block
    {
        /// <summary>null = 첫 Host 앞의 전역 설정 (모든 호스트에 적용)</summary>
        public List<string>? Patterns;
        /// <summary>Match 블록 — 내용을 적용하지 않는다</summary>
        public bool Ignored;
        public readonly List<KeyValuePair<string, List<string>>> Options = new();
    }

    private sealed class State
    {
        public State(string baseDir)
        {
            BaseDir = baseDir;
            Current = new Block();
            Blocks.Add(Current);
        }
        public readonly string BaseDir;
        public readonly List<Block> Blocks = new();
        public readonly List<string> Warnings = new();
        public Block Current;
        public bool MatchWarned;
        public Func<string, string?>? BlockPath;
    }

    private static void ParseText(string text, string source, int depth, State st)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            // 첫 줄의 BOM 제거
            if (line[0] == '﻿') { line = line.Substring(1).Trim(); if (line.Length == 0 || line[0] == '#') continue; }

            if (!SplitLine(line, out var key, out var args))
            {
                st.Warnings.Add(L.F("{0} {1}번째 줄: 따옴표가 닫히지 않아 건너뛰었습니다.", source, i + 1));
                continue;
            }
            if (key.Length == 0) continue;

            switch (key.ToLowerInvariant())
            {
                case "host":
                    st.Current = new Block { Patterns = args };
                    st.Blocks.Add(st.Current);
                    break;
                case "match":
                    if (!st.MatchWarned)
                    {
                        st.MatchWarned = true;
                        st.Warnings.Add(L.T("Match 블록은 지원하지 않아 건너뛰었습니다."));
                    }
                    st.Current = new Block { Ignored = true };
                    st.Blocks.Add(st.Current);
                    break;
                case "include":
                    foreach (var a in args) Include(a, depth, st);
                    break;
                default:
                    st.Current.Options.Add(new KeyValuePair<string, List<string>>(key.ToLowerInvariant(), args));
                    break;
            }
        }
    }

    /// <summary>Include 한 항목 처리 — 포함된 파일의 내용은 그 자리에 들어간 것처럼 읽는다</summary>
    private static void Include(string arg, int depth, State st)
    {
        if (depth + 1 > MaxIncludeDepth)
        {
            st.Warnings.Add(L.F("Include 가 너무 깊게 중첩되어 건너뛰었습니다: {0}", arg));
            return;
        }

        var path = ExpandHome(arg);
        if (!Path.IsPathRooted(path)) path = Path.Combine(st.BaseDir, path);
        // 파일 존재 확인(네트워크 경로면 접속이 일어남)보다 먼저 검사
        if (st.BlockPath?.Invoke(path) is { } blocked)
        {
            st.Warnings.Add(L.F("Include 파일을 건너뛰었습니다: {0} ({1})", path, blocked));
            return;
        }

        List<string> files;
        if (HasWildcard(path))
            files = Glob(path); // 일치하는 파일이 없어도 OpenSSH 처럼 조용히 넘어간다 (빈 conf.d 등)
        else if (File.Exists(path))
            files = new List<string> { path };
        else
        {
            st.Warnings.Add(L.F("Include 파일을 찾을 수 없습니다: {0}", path));
            return;
        }

        foreach (var file in files)
        {
            if (st.BlockPath?.Invoke(file) is { } why)
            {
                st.Warnings.Add(L.F("Include 파일을 건너뛰었습니다: {0} ({1})", file, why));
                continue;
            }
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                st.Warnings.Add(L.F("파일을 읽을 수 없습니다: {0} ({1})", file, ex.Message));
                continue;
            }
            var outer = st.Current;
            ParseText(text, Path.GetFileName(file), depth + 1, st);
            // 포함된 파일 안에서 Host/Match 가 바뀌었으면, Include 뒤의 줄은 원래 블록에 이어서 적용한다
            if (!ReferenceEquals(st.Current, outer))
            {
                st.Current = new Block { Patterns = outer.Patterns, Ignored = outer.Ignored };
                st.Blocks.Add(st.Current);
            }
        }
    }

    private static string ExpandHome(string p)
    {
        if (p == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Substring(2));
        return p;
    }

    private static bool HasWildcard(string s) => s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0;

    /// <summary>경로 조각마다 * ? 를 펼친다. 결과는 파일만, 조각 안에서는 이름순</summary>
    private static List<string> Glob(string path)
    {
        string root;
        try { root = Path.GetPathRoot(path) ?? ""; }
        catch (ArgumentException) { return new List<string>(); }
        var rest = path.Substring(root.Length);
        var segments = rest.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        var current = new List<string> { root };
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var next = new List<string>();
            foreach (var dir in current)
            {
                if (!HasWildcard(seg))
                {
                    next.Add(Path.Combine(dir, seg));
                    continue;
                }
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                var names = entries
                    .Select(e => Path.GetFileName(e))
                    // glob 과 같이 점으로 시작하는 이름은 패턴도 점으로 시작할 때만
                    .Where(n => n.Length > 0 && (n[0] != '.' || seg[0] == '.') && WildcardMatch(seg, n))
                    .OrderBy(n => n, StringComparer.Ordinal);
                foreach (var n in names) next.Add(Path.Combine(dir, n));
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current.Where(File.Exists).ToList();
    }

    /// <summary>
    /// "키 값..." 또는 "키=값..." 한 줄을 나눈다. 값은 공백으로 나뉘고 " ' 따옴표로 묶을 수 있다 (OpenSSH argv_split 과 같은 규칙).
    /// 따옴표가 닫히지 않으면 false.
    /// </summary>
    internal static bool SplitLine(string line, out string key, out List<string> args)
    {
        args = new List<string>();
        int i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '=') i++;
        key = line.Substring(0, i);

        // 키 뒤의 공백과 "=" 하나 (앞뒤 공백 허용)
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i < line.Length && line[i] == '=')
        {
            i++;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        }
        return SplitArgs(line.Substring(i), args);
    }

    private static bool SplitArgs(string s, List<string> args)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            // 따옴표 밖에서 # 로 시작하는 토큰부터는 주석
            if (s[i] == '#') break;

            var sb = new StringBuilder();
            char quote = '\0';
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (quote == '\0' && char.IsWhiteSpace(c)) break;
                // 역슬래시는 따옴표·역슬래시(·따옴표 밖의 공백) 앞에서만 이스케이프 — Windows 경로는 그대로 둔다
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == '"' || n == '\'' || n == '\\' || (quote == '\0' && char.IsWhiteSpace(n)))
                    {
                        sb.Append(n);
                        i++;
                        continue;
                    }
                }
                if (quote == '\0' && (c == '"' || c == '\'')) { quote = c; continue; }
                if (quote != '\0' && c == quote) { quote = '\0'; continue; }
                sb.Append(c);
            }
            if (quote != '\0') return false;
            args.Add(sb.ToString());
        }
        return true;
    }

    // ───────────── 결과 만들기 ─────────────

    private static ImportResult Build(State st)
    {
        var result = new ImportResult(new List<ImportedHost>(), st.Warnings);

        // 구체적인 별칭 (와일드카드·부정 패턴 제외), 처음 나온 순서, 대소문자 무시 중복 제거
        var aliases = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in st.Blocks)
        {
            if (b.Ignored || b.Patterns == null) continue;
            foreach (var p in b.Patterns)
            {
                if (p.Length == 0 || HasWildcard(p) || p.IndexOf('!') >= 0) continue;
                if (seen.Add(p)) aliases.Add(p);
            }
        }

        foreach (var alias in aliases)
            result.Hosts.Add(Resolve(alias, st));
        return result;
    }

    /// <summary>별칭에 적용되는 블록을 파일 순서대로 돌며 키마다 처음 얻은 값을 쓴다 (IdentityFile 은 모두 모은다)</summary>
    private static ImportedHost Resolve(string alias, State st)
    {
        var first = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var identities = new List<string>();
        string? proxyKey = null; // ProxyJump 와 ProxyCommand 중 먼저 나온 쪽 (OpenSSH 에서 둘은 서로 배타)
        List<string>? proxyArgs = null;

        foreach (var b in st.Blocks)
        {
            if (b.Ignored) continue;
            if (b.Patterns != null && !HostMatches(alias, b.Patterns)) continue;
            foreach (var opt in b.Options)
            {
                if (opt.Value.Count == 0) continue;
                switch (opt.Key)
                {
                    case "identityfile":
                        if (!identities.Contains(opt.Value[0], StringComparer.Ordinal)) identities.Add(opt.Value[0]);
                        break;
                    case "proxyjump":
                    case "proxycommand":
                        if (proxyKey == null) { proxyKey = opt.Key; proxyArgs = opt.Value; }
                        break;
                    default:
                        if (!first.ContainsKey(opt.Key)) first[opt.Key] = opt.Value;
                        break;
                }
            }
        }

        string address = alias;
        if (first.TryGetValue("hostname", out var hn) && hn[0].Length > 0)
            address = ExpandHostToken(hn[0], alias);

        int? port = null;
        if (first.TryGetValue("port", out var pv))
        {
            if (int.TryParse(pv[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p)
                && p >= 1 && p <= 65535)
                port = p;
            else
                st.Warnings.Add(L.F("{0}: 포트 값이 올바르지 않아 무시했습니다: {1}", alias, pv[0]));
        }

        string? user = first.TryGetValue("user", out var uv) && uv[0].Length > 0 ? uv[0] : null;

        string? jump = null;
        if (proxyKey == "proxyjump" && proxyArgs != null)
        {
            var hops = proxyArgs[0].Split(',');
            var hop = hops[0].Trim();
            if (hop.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) hop = hop[6..].TrimEnd('/');
            if (hop.Length > 0 && !hop.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                jump = hop;
                // Nubis 의 점프 서버는 한 단계뿐 — 첫 번째(이 PC 에서 바로 닿는) 점프 서버만 쓴다
                if (hops.Count(x => x.Trim().Length > 0) > 1)
                    st.Warnings.Add(L.F("{0}: 다단계 ProxyJump 는 첫 번째 점프 서버만 가져왔습니다.", alias));
            }
        }
        else if (proxyKey == "proxycommand" && proxyArgs != null
                 && !(proxyArgs.Count == 1 && proxyArgs[0].Equals("none", StringComparison.OrdinalIgnoreCase)))
        {
            st.Warnings.Add(L.F("{0}: ProxyCommand 는 지원하지 않아 점프 서버 없이 가져옵니다.", alias));
        }

        string? note = identities.Count == 0
            ? null
            : string.Join("; ", identities.Select(f => "IdentityFile: " + f));

        return new ImportedHost
        {
            Name = alias,
            Address = address,
            Port = port,
            Protocol = Nubis.Protocol.Ssh,
            Group = GroupName,
            Username = user,
            JumpHost = jump,
            Note = note,
        };
    }

    /// <summary>HostName 의 %h(별칭) · %% 치환. 다른 토큰은 그대로 둔다</summary>
    private static string ExpandHostToken(string value, string alias)
    {
        if (value.IndexOf('%') < 0) return value;
        var sb = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '%' && i + 1 < value.Length)
            {
                char n = value[i + 1];
                if (n == 'h') { sb.Append(alias); i++; continue; }
                if (n == '%') { sb.Append('%'); i++; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Host 패턴 목록 일치: 긍정 패턴 하나 이상 일치 + 부정(!) 패턴은 하나도 일치하지 않아야 한다</summary>
    internal static bool HostMatches(string host, IEnumerable<string> patterns)
    {
        bool matched = false;
        foreach (var raw in patterns)
        {
            if (raw.Length == 0) continue;
            bool negate = raw[0] == '!';
            var pat = negate ? raw.Substring(1) : raw;
            if (!WildcardMatch(pat, host)) continue;
            if (negate) return false;
            matched = true;
        }
        return matched;
    }

    /// <summary>* (0자 이상) · ? (한 글자) 와일드카드, 대소문자 무시</summary>
    internal static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++; t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
