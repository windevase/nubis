using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Nubis;

public enum CommandShell { Sh, PowerShell, Cmd }

/// <summary>
/// Claude 의 run_command 자동 승인용 "읽기 전용 명령" 판별.
/// 허용 목록 방식이라 목록에 없거나 애매하면 false (= 승인 창). 오판으로 쓰기 명령이 자동 실행되는 것보다
/// 읽기 명령이 승인 창을 한 번 더 띄우는 쪽을 택한다.
/// 셸 문법을 정확히 해석하려 하지 않는다 — 이 판별기와 셸의 해석이 갈릴 수 있는 구문(이스케이프·확장·치환 등)은 통째로 거부한다.
/// </summary>
public static class ReadOnlyCommands
{
    /// <summary>
    /// 이 판별은 정적 Regex.IsMatch 패턴을 40개 넘게 쓰는데, .NET 정적 Regex 캐시 기본 크기(15)로는 호출마다 패턴을 다시 해석한다.
    /// 캐시를 늘려 다시 해석하지 않게 한다 (캐시는 개수 한도가 있는 LRU 라 메모리는 일정)
    /// </summary>
    static ReadOnlyCommands()
    {
        if (Regex.CacheSize < 96) Regex.CacheSize = 96;
    }

    public static bool IsReadOnly(string command, CommandShell shell) => IsReadOnly(command, shell, out _);

    public static bool IsReadOnly(string command, CommandShell shell, out string reason)
    {
        reason = "";
        // 특수 공백·제어 문자 검사는 다듬기 전 원문으로 한다 — Trim() 은 앞뒤의 NBSP·U+3000 등을 지우지만,
        // 실제로 서버에 보내는 것은 원문이라 끝의 NBSP 가 인자가 된다 (cmd: net user administrator <NBSP> → 비밀번호 변경)
        if (command.Any(c => char.IsWhiteSpace(c) && c is not ' ' and not '\t' and not '\r' and not '\n'))
        { reason = "특수 공백 문자"; return false; }
        command = command.Trim(' ', '\t', '\r', '\n');
        if (command.Length == 0 || command.Length > 4000) { reason = "빈 명령이거나 너무 김"; return false; }
        // 제어 문자: 줄바꿈(여러 명령)·Ctrl-U/Ctrl-W(줄 지우기)·ESC·DEL·C1 등은 보이는 글자와 실제 실행이 달라질 수 있다 (send_to_terminal 도 이 검사에 기댐). 탭만 허용
        if (command.Any(c => (c < 0x20 && c != '\t') || (c >= 0x7f && c <= 0x9f) || c is '\u2028' or '\u2029'))
        { reason = "제어 문자"; return false; }
        // (특수 공백 문자 — NBSP · U+3000 등은 셸마다 단어를 나누는 기준이 달라(sh·cmd 는 나누지 않고 PowerShell 은 나눔) 맨 위에서 원문으로 거부했다)
        if (ApprovalDialog.IsDangerous(command)) { reason = "위험 명령 패턴"; return false; }
        return shell switch
        {
            CommandShell.Sh => CheckSh(command, out reason),
            CommandShell.PowerShell => CheckPowerShell(command, out reason),
            _ => CheckCmd(command, out reason),
        };
    }

    // ================================================================ 공통 도구

    private const string BothQuotes = "'\"";
    private const string DoubleQuoteOnly = "\"";   // cmd.exe 는 ' 를 따옴표로 보지 않는다

    private static readonly Regex QuotedRx = new("'[^']*'|\"[^\"]*\"", RegexOptions.Compiled);
    private static readonly Regex CmdQuotedRx = new("\"[^\"]*\"", RegexOptions.Compiled);

    /// <summary>따옴표를 고려해 구분자(;, |, &&, || 등)로 명령을 나눈다. 따옴표 안의 구분자는 무시.</summary>
    private static List<string> SplitSegments(string s, string quotes, bool singleAmpIsSeparator, bool groupingIsSeparator = false)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (quotes.IndexOf(c) >= 0) { quote = c; sb.Append(c); continue; }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "&&" or "||") { parts.Add(sb.ToString()); sb.Clear(); i++; continue; }
            if (c is ';' or '|' or '\n' || (c == '&' && singleAmpIsSeparator) || (c is '{' or '}' or '(' or ')' && groupingIsSeparator))
            { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>
    /// 공백 기준 토큰 (따옴표 안 공백 유지, 따옴표 제거).
    /// - 빈 따옴표('' · "")도 빈 토큰으로 남긴다 — 셸은 빈 인자를 넘기므로(kubectl -n '' delete … 의 '' 가 -n 의 값), 버리면 하위 명령 판정이 어긋난다
    /// - 단어는 공백·탭(·줄바꿈)에서만 나눈다 — sh 는 NBSP·U+3000 같은 유니코드 공백에서 나누지 않는다.
    ///   그런 문자는 토큰에 그대로 남겨 "-n " 처럼 알아볼 수 없는 옵션이 되게 한다 (판정은 보수적으로 읽기 전용 아님)
    /// </summary>
    private static List<string> Tokens(string s, string quotes)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var has = false;   // 지금 토큰이 시작됐는가 (빈 따옴표만 있어도 토큰)
        char quote = '\0';
        foreach (var c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); continue; }
            if (quotes.IndexOf(c) >= 0) { quote = c; has = true; continue; }
            if (c is ' ' or '\t' or '\n' or '\r') { if (has || sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); has = false; } continue; }
            sb.Append(c);
            has = true;
        }
        if (has || sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    /// <summary>따옴표 안 내용을 지운 문자열 (구조 검사용). 짝이 안 맞는 따옴표는 그대로 남는다</summary>
    private static string StripQuoted(string s, bool cmd = false) => (cmd ? CmdQuotedRx : QuotedRx).Replace(s, cmd ? "\"\"" : "''");

    /// <summary>짝이 안 맞는 따옴표가 있다. StripQuoted 결과는 자리 표시로 '' · "" 를 넣으므로 그걸로 판단하면 안 된다 — 따옴표 문자열을 아예 지우고 남은 따옴표를 본다</summary>
    private static bool HasUnbalancedQuotes(string s, bool cmd = false) =>
        (cmd ? CmdQuotedRx : QuotedRx).Replace(s, "").IndexOfAny(cmd ? new[] { '"' } : new[] { '\'', '"' }) >= 0;

    /// <summary>큰따옴표 문자열들의 안쪽 내용 (sh·PowerShell 은 그 안에서도 치환·확장이 일어난다)</summary>
    private static IEnumerable<string> DoubleQuotedInner(string s) =>
        QuotedRx.Matches(s).Where(m => m.Value[0] == '"').Select(m => m.Value[1..^1]);

    private static string At(List<string> a, int i) => i >= 0 && i < a.Count ? a[i] : "";

    /// <summary>
    /// UNC·장치 경로로 보이는 글자열(\\, //, \/, /\ — Windows 는 / 를 \ 로 바꿔 해석)이 따옴표 안 포함 어디든 있다.
    /// \\서버\공유 는 읽기만 해도 그 서버로 NTLM 인증(해시)을 보낸다 (Get-ChildItem \\1.2.3.4\s, dir \\x\s, reg query \\x\HKLM)
    /// NT 경로 접두어 \??\ 도 포함 — Win32 파일 API 는 \??\UNC\서버\공유 를 그대로 받아 같은 SMB 접속이 된다 (dir \??\UNC\x\s)
    /// </summary>
    private static bool HasUncPath(string s) => Regex.IsMatch(s, @"[\\/]{2}|[\\/]\?\?[\\/]");

    /// <summary>
    /// PowerShell 에서 토큰을 이어 붙여 문자열을 만드는 형태 (따옴표 문자열은 \u0001 하나로 바꿔 본다):
    /// '\'"\h\s" · 'x'.y · \'\h\s' · $a$a · $a\h\s · x$a · ('\')\h\s · $$ · $^ · $? 같은 자동 변수.
    /// 인자 모드에서는 이어 붙은 토큰이 한 문자열이 되어 UNC 경로 등을 판별기 몰래 만들 수 있다
    /// </summary>
    private static bool PsAdjacentToken(string command)
    {
        var m = QuotedRx.Replace(command, "\u0001");
        return Regex.IsMatch(m, "\u0001[^\\s;|{}(),=\\]]")            // 문자열 뒤에 바로 다른 토큰
               || Regex.IsMatch(m, "[^\\s;|{}(),=!>\\u0001]\u0001")    // 다른 토큰 뒤에 바로 문자열
               || Regex.IsMatch(m, @"\$(?>[\w:]+)[^\s;|{}(),.=\]\[]")  // 변수 뒤에 바로 다른 토큰 ($a$a, $a\h). 이름은 끝까지 (되돌아가 $ab|c 로 보지 않게)
               || Regex.IsMatch(m, @"[^\s;|{}(),=!>\[]\$")             // 다른 토큰 뒤에 바로 변수 (\$a)
               || Regex.IsMatch(m, @"\$(?![A-Za-z_(])")                // $$ · $^ · $? 등 자동 변수
               || Regex.IsMatch(m, @"\)[^\s;|{}()\].,=]");             // 괄호 식 뒤에 바로 다른 토큰
    }

    /// <summary>-abc 같은 짧은 옵션(묶음 포함)</summary>
    private static bool IsShortOpt(string x) => x.Length > 1 && x[0] == '-' && x[1] != '-';

    /// <summary>짧은 옵션 묶음 안에 금지 글자가 하나라도 있다 (-uo, -tK 처럼 묶어 쓴 경우 포함)</summary>
    private static bool ShortHas(string x, string letters) => IsShortOpt(x) && x.Skip(1).Any(c => letters.IndexOf(c) >= 0);

    /// <summary>
    /// --xxx 긴 옵션이 금지 이름의 접두어다. GNU getopt_long 은 모호하지 않은 접두어(--out → --output)를 받아들이므로
    /// 모호한 접두어까지 포함해 막는다.
    /// </summary>
    /// <summary>
    /// kubectl 인자가 Secret 리소스를 가리키는가 (secret · secrets · secret/이름 · pods,secrets 처럼 쉼표 목록 · secret.v1 등)
    /// </summary>
    internal static bool KubectlSecretRef(string x)
    {
        if (x.StartsWith("-", StringComparison.Ordinal)) return false;
        foreach (var part in x.ToLowerInvariant().Split(','))
        {
            var kind = part.Split('/', 2)[0];
            kind = kind.Split('.', 2)[0];
            if (kind is "secret" or "secrets") return true;
        }
        return false;
    }

    private static bool LongPrefix(string x, params string[] names)
    {
        if (!x.StartsWith("--", StringComparison.Ordinal) || x.Length < 3) return false;
        var name = x[2..];
        var eq = name.IndexOf('=');
        if (eq >= 0) name = name[..eq];
        return name.Length > 0 && names.Any(n => n.StartsWith(name, StringComparison.Ordinal));
    }

    /// <summary>
    /// 하위 명령 앞의 전역 옵션을 건너뛴다. 알려진 값 없는 플래그(flags, '=' 로 끝나면 --x=값 형태)와
    /// 알려진 값 받는 옵션(valueOpts, 다음 토큰을 값으로 소비)만 허용하고, 그 밖의 옵션이 앞에 오면 false
    /// (kubectl -n get delete … 처럼 옵션 값이 하위 명령으로 오인되는 것 방지).
    /// </summary>
    private static bool SubStart(List<string> a, out int i, string[] flags, string[]? valueOpts = null)
    {
        i = 0;
        while (i < a.Count && a[i].StartsWith('-'))
        {
            var x = a[i];
            if (valueOpts != null && Array.IndexOf(valueOpts, x) >= 0) { if (i + 1 >= a.Count) return false; i += 2; continue; }
            if (valueOpts != null && valueOpts.Any(v => v.StartsWith("--", StringComparison.Ordinal) && x.StartsWith(v + "=", StringComparison.Ordinal))) { i++; continue; }
            if (!flags.Any(f => f.EndsWith('=') ? x.StartsWith(f, StringComparison.Ordinal) && x.Length > f.Length : x == f)) return false;
            i++;
        }
        return true;
    }

    /// <summary>하위 명령이 있는 도구에서, 하위 명령 뒤에 쓰기 동사가 끼어 있으면 거부 (애매하면 거부)</summary>
    private static readonly HashSet<string> MutatingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete", "del", "rm", "rmi", "remove", "erase", "purge", "run", "exec", "install", "reinstall", "uninstall",
        "localinstall", "groupinstall", "apply", "kill", "stop", "start", "restart", "reload", "try-restart",
        "reload-or-restart", "condrestart", "force-reload", "set", "unset", "edit", "patch", "scale", "create", "update",
        "upgrade", "downgrade", "distro-sync", "autoremove", "prune", "push", "pull", "cp", "attach", "commit", "build",
        "tag", "label", "annotate", "rollout", "drain", "cordon", "uncordon", "taint", "replace", "expose", "autoscale",
        "port-forward", "proxy", "debug", "mask", "unmask", "enable", "disable", "isolate", "reset-failed",
        "daemon-reload", "daemon-reexec", "revert", "import", "load", "save", "export", "rename", "pause", "unpause",
        "undo", "rollback", "redo", "clean", "makecache", "swap", "shell", "mark", "login", "logout", "certificate",
        "add", "flush", "deleteall", "change", "append", "prepend", "restore", "enter", "poweroff", "reboot", "halt",
        "suspend", "hibernate", "kexec", "rescue", "emergency", "freeze", "thaw", "bind", "set-property", "preset",
        "preset-all", "reenable", "terminate", "up", "down", "store",
    };

    private static bool NoMutating(List<string> a, int from) => !a.Skip(from).Any(MutatingVerbs.Contains);

    // ================================================================ Linux sh

    private static readonly HashSet<string> ShSimple = new(StringComparer.Ordinal)
    {
        "ls", "ll", "cat", "tac", "head", "tail", "grep", "egrep", "fgrep", "zgrep", "zcat", "bzcat", "xzcat",
        "wc", "cut", "tr", "column", "nl", "rev", "fold", "diff", "cmp", "df", "du", "free", "uptime", "uname",
        "whoami", "id", "groups", "ps", "pgrep", "pidof", "pstree", "vmstat", "iostat", "mpstat", "sar", "netstat",
        "ss", "lsof", "lsblk", "blkid", "findmnt", "printenv", "which", "whereis", "type", "stat", "file", "w", "who",
        "last", "lastb", "lastlog", "nproc", "lscpu", "lsmem", "lspci", "lsusb", "lsmod", "getent", "nslookup", "dig",
        "host", "traceroute", "tracepath", "echo", "printf", "pwd", "basename", "dirname", "realpath", "readlink",
        "md5sum", "sha1sum", "sha256sum", "sha512sum", "cksum", "getenforce", "sestatus", "true", "false", "test", "[",
        "sleep", "cd", "history", "uniq", "arch", "tty", "locale", "ulimit", "jq", "base64",
        "chronyc", "ntpq", "ethtool", "mount", "swapon", "numfmt", "seq",
        "clear",   // 화면만 지운다 (clear; ls 처럼 조회 앞에 붙여 쓰는 경우가 많다)
    };

    private static readonly HashSet<string> ShSubcommands = new(StringComparer.Ordinal)
    {
        "systemctl", "service", "docker", "podman", "kubectl", "ip", "rpm", "dpkg", "dpkg-query", "apt", "apt-cache",
        "yum", "dnf", "pip", "pip3", "crontab", "journalctl", "dmesg", "date", "hostname", "hostnamectl", "timedatectl",
        "localectl", "loginctl", "top", "ping", "arp", "route", "ifconfig", "find", "sort", "awk", "gawk", "mawk",
    };

    /// <summary>
    /// 인자에 따옴표 밖 와일드카드(* ? [)를 써도 되는 명령. 어떤 옵션·인자가 와도 파일을 쓰거나 다른 명령을 실행하지 않는 것만
    /// (ls·cat·grep·head·tail·wc·diff·du·stat·해시 등). find(-delete)·sort(-o)·date(-s)·uniq(두 번째 인자=출력 파일)·
    /// 하위 명령 도구(systemctl 등)·특수 검사가 있는 명령은 빠져 있다.
    /// </summary>
    private static readonly HashSet<string> ShGlobOk = new(StringComparer.Ordinal)
    {
        "ls", "ll", "cat", "tac", "head", "tail", "grep", "egrep", "fgrep", "zgrep", "zcat", "bzcat", "xzcat", "wc", "diff", "cmp",
        "du", "stat", "md5sum", "sha1sum", "sha256sum", "sha512sum", "cksum", "basename", "dirname", "realpath", "readlink",
        "echo", "test", "[", "cd",
    };

    /// <summary>경로로 부른 명령은 시스템 실행 파일 폴더만 (/tmp/x/ls 처럼 이름만 같은 다른 프로그램 방지)</summary>
    private static readonly string[] SafeBinDirs = { "/bin/", "/usr/bin/", "/sbin/", "/usr/sbin/", "/usr/local/bin/", "/usr/local/sbin/" };

    /// <summary>큰따옴표 안에서 허용하는 변수 확장: "$HOME" · "$USER" (이름 뒤에 글자가 이어지면 다른 변수), 문자열 끝의 $ (글자 그대로)</summary>
    private static readonly Regex ShSafeVarRx = new(@"\$(?:(?:HOME|USER)(?!\w)|$)", RegexOptions.Compiled);

    private static bool CheckSh(string command, out string reason)
    {
        reason = "";
        var bare = StripQuoted(command);   // 작은따옴표·큰따옴표 내용을 지운 구조
        if (HasUnbalancedQuotes(command)) { reason = "따옴표 짝이 맞지 않음"; return false; }
        // 큰따옴표 안에서도 명령 치환·변수 확장·히스토리 확장이 일어난다. 백슬래시는 \" 로 따옴표 해석을 어긋나게 하므로 거부
        foreach (var inner in DoubleQuotedInner(command))
            if (inner.IndexOfAny(new[] { '`', '\\', '!' }) >= 0 || inner.Contains("$(") || inner.Contains("${") || inner.Contains("$["))
            { reason = "큰따옴표 안 명령 치환·확장"; return false; }
        // 변수 확장: $_ (직전 명령의 마지막 인자) · $1 · $OLDPWD · $IFS 등은 이 판별기가 보지 못한 값(-exec, link set … 등)으로 바뀐다
        //  (echo -exec; find / $_ sh -c 'id' ';' → find / -exec sh -c 'id' ';').
        // 작은따옴표 밖의 $ 는 모두 거부. 단 큰따옴표 안의 "$HOME" · "$USER" (이 명령 안에서 바꿀 수 없음)와 끝의 $ (글자 그대로)만 허용
        foreach (var inner in DoubleQuotedInner(command))
            if (ShSafeVarRx.Replace(inner, "").Contains('$')) { reason = "큰따옴표 안 변수 확장"; return false; }
        if (bare.Contains('$')) { reason = "따옴표 밖 변수 확장($)"; return false; }
        // 따옴표 밖: 명령/프로세스 치환, ${..}·$[..] 확장, $'..' (안에서 \' 이스케이프), 백슬래시 이스케이프, 히스토리 확장(!), 중괄호 확장
        if (bare.IndexOfAny(new[] { '`', '\\', '!', '{', '}' }) >= 0 || bare.Contains("$(") || bare.Contains("${") || bare.Contains("$[") ||
            bare.Contains("$''") || bare.Contains("<(") || bare.Contains(">(") || bare.Contains("<<"))
        { reason = "명령 치환·확장·here-doc·이스케이프"; return false; }
        // 따옴표 밖 괄호: 서브셸 ( cmd ), zsh glob 한정자 *(e:'cmd':) 는 코드를 실행하고, PowerShell·cmd 원격 셸에서는 하위 식이 된다
        if (bare.IndexOfAny(new[] { '(', ')' }) >= 0) { reason = "따옴표 밖 괄호 (서브셸·glob 한정자)"; return false; }
        // 옵션 자리의 와일드카드는 현재 폴더 파일명(-delete 등)으로 펼쳐질 수 있다
        if (Regex.IsMatch(bare, @"(^|\s)-[^\s=]*[*?\[]")) { reason = "옵션 자리의 와일드카드"; return false; }
        // 버리는 리디렉션만 허용, 나머지 '>' 는 파일 쓰기
        var noDevNull = Regex.Replace(bare, @"(\d|&)?>>?\s*/dev/null(?![^\s;|&)])|2>&1|1>&2", " ");
        if (noDevNull.Contains('>')) { reason = "파일로 출력(>)"; return false; }
        if (noDevNull.Replace("&&", "  ").Contains('&')) { reason = "백그라운드 실행(&)"; return false; }

        foreach (var seg in SplitSegments(command, BothQuotes, singleAmpIsSeparator: false))
        {
            var t = Tokens(seg, BothQuotes);
            // 빈 인자('' "")는 읽기 전용으로 보지 않는다 — 하위 명령 자리의 빈 인자를 "인자 없음" 과 구분하지 못하는 판정이 있다 (ip '' add …)
            if (t.Any(x => x.Length == 0)) { reason = "빈 인자"; return false; }
            // sudo / timeout / nice 앞부분 건너뛰기
            while (t.Count > 0)
            {
                if (t[0] == "sudo") { t.RemoveAt(0); while (t.Count > 0 && t[0] is "-n" or "-E" or "-H") t.RemoveAt(0); continue; }
                if (t[0] == "timeout" && t.Count > 1) { t.RemoveRange(0, 2); continue; }
                if (t[0] == "nice") { t.RemoveAt(0); continue; }
                break;
            }
            if (t.Count == 0) { reason = "명령 없음"; return false; }
            if (t[0].Contains('=')) { reason = "환경변수 지정"; return false; }
            var cmd = t[0];
            if (cmd.Contains('/'))
            {
                var dir = SafeBinDirs.FirstOrDefault(d => cmd.StartsWith(d, StringComparison.Ordinal));
                if (dir == null || cmd.Contains("..") || cmd[dir.Length..].Contains('/')) { reason = $"시스템 폴더 밖의 실행 파일: {cmd}"; return false; }
                cmd = cmd[dir.Length..];
            }
            var args = t.Skip(1).ToList();
            if (!ShSimple.Contains(cmd) && !ShSubcommands.Contains(cmd)) { reason = $"허용 목록에 없는 명령: {cmd}"; return false; }
            // 따옴표 밖 와일드카드는 현재 폴더 파일명으로 펼쳐진다 → 파일명(-delete, -ofile 등)이 옵션·하위 명령·추가 인자가 될 수 있어
            // ShArgsOk 의 검사(펼치기 전 토큰 기준)가 무의미해진다. 쓰기 옵션이 없는 명령(ShGlobOk)만 허용
            if (!ShGlobOk.Contains(cmd) && StripQuoted(seg).IndexOfAny(new[] { '*', '?', '[' }) >= 0)
            { reason = $"따옴표 밖 와일드카드 (펼쳐진 파일명이 옵션이 될 수 있음): {seg}"; return false; }
            if (!ShArgsOk(cmd, args)) { reason = $"쓰기 가능한 옵션/하위 명령: {seg}"; return false; }
        }
        return true;
    }

    private static readonly string[] SystemctlFlags =
    {
        "--no-pager", "--all", "-a", "--full", "-l", "--failed", "--user", "--system", "--no-legend", "--plain", "-q", "--quiet",
        "--type=", "--state=", "--output=",
    };

    private static readonly string[] CtlFlags = { "--no-pager", "--all", "-a", "--full", "-l", "--static", "--transient", "--pretty", "--value", "--no-ask-password" };

    private static readonly string[] IpOptions =
    {
        "-4", "-6", "-s", "-d", "-o", "-j", "-p", "-c", "-h", "-br", "-brief", "-stats", "-statistics", "-details", "-oneline",
        "-json", "-pretty", "-color", "-human",
    };

    private static readonly HashSet<string> IpMutating = new(StringComparer.Ordinal)
    {
        "add", "del", "delete", "set", "flush", "change", "replace", "append", "prepend", "restore", "save", "exec", "deleteall", "update",
    };

    /// <summary>
    /// systemd 도구(systemctl·hostnamectl·timedatectl·localectl·loginctl)의 원격 대상 지정: -H/--host 는 이 서버의 ssh 키로
    /// 다른 서버에 접속하고, -M/--machine 은 다른 컨테이너를 대상으로 한다. getopt 라 하위 명령 뒤에 와도 적용되므로 위치와 관계없이 거부
    /// </summary>
    private static bool SystemdRemote(List<string> a) =>
        a.Any(x => ShortHas(x, "HM") || LongPrefix(x, "host", "machine"));

    private static bool ShArgsOk(string cmd, List<string> a)
    {
        if (cmd is "systemctl" or "hostnamectl" or "timedatectl" or "localectl" or "loginctl" && SystemdRemote(a)) return false;
        switch (cmd)
        {
            case "systemctl":
            {
                if (!SubStart(a, out var i, SystemctlFlags)) return false;
                return At(a, i) is "" or "status" or "is-active" or "is-enabled" or "is-failed" or "list-units" or "list-unit-files"
                    or "list-timers" or "list-sockets" or "list-jobs" or "show" or "cat" or "list-dependencies" or "get-default"
                    && NoMutating(a, i + 1);
            }
            case "service":
                return (a.Count == 1 && a[0] == "--status-all") || (a.Count == 2 && !a[0].StartsWith('-') && a[1] == "status");
            case "docker":
            case "podman":
            {
                // -H/--host 로 데몬 주소를 바꾸지 않는다: ssh://사용자@서버 면 이 서버의 ssh 키로 다른 서버에 접속하고,
                // tcp:// 면 다른 데몬에 접속한다 (어디에 붙는지 알 수 없음). 위치와 관계없이 거부 (환경 변수 DOCKER_HOST= 는 앞에서 거부됨)
                if (a.Any(x => x.StartsWith("-H", StringComparison.Ordinal) || LongPrefix(x, "host"))) return false;
                // podman 의 전역 옵션은 하위 명령 뒤에도 쓸 수 있다 (podman ps --url ssh://사용자@다른서버 → 이 서버의 키로 접속,
                // --identity 키 파일 지정, --root·--runroot·--tmpdir 등은 그 경로에 저장소를 만든다)
                if (a.Any(x => LongPrefix(x, "url", "identity", "remote", "root", "runroot", "tmpdir", "storage-driver", "storage-opt",
                                   "hooks-dir", "module", "config", "conmon", "network-cmd-path", "volumepath", "imagestore"))) return false;
                if (!SubStart(a, out var i, new[] { "-D", "--debug" }, new[] { "--context", "-c", "--connection" })) return false;
                var sub = At(a, i);
                var ok = sub is "" or "ps" or "images" or "logs" or "inspect" or "stats" or "version" or "info" or "top" or "port" or "history" or "diff"
                         || (sub is "container" or "image" or "network" or "volume" or "system" or "compose"
                             && At(a, i + 1) is "ls" or "ps" or "inspect" or "logs" or "top" or "port" or "history" or "df" or "info" or "images");
                return ok && NoMutating(a, i + 1);
            }
            case "kubectl":
            {
                if (a.Any(x => x.StartsWith("--kubeconfig", StringComparison.Ordinal))) return false;   // exec 자격 증명 플러그인 실행 가능
                // -s/--server 로 API 서버 주소를 바꾸면 kubeconfig 의 토큰·클라이언트 인증서가 그 서버로 간다 (kubectl get pods -s https://공격자).
                // 옵션은 run_kubectl 과 같은 pflag 해석(KubectlArgs.Check)으로 본다: 짧은 옵션 묶음(-As x · -Asx · -ws x), 긴 이름의 _ 표기
                // (--tls_server_name · --profile_output), 값 옵션 뒤의 --(-l -- --server=x), 로컬 파일 읽기·쓰기(-o go-template-file= ·
                // --profile-output · --log-file · --kuberc), 플러그인(kubectl-이름 실행). 그 서버 kubeconfig 안의 --context·--cluster·--user 선택만 허용
                if (KubectlArgs.Check(a, fixedCluster: false) != null) return false;
                if (a.Contains("--")) return false;   // 조회 명령에는 -- 가 필요 없다 (값인지 끝 표시인지 해석이 엇갈릴 수 있어 승인 받기)
                if (a.Any(x => (x.StartsWith("-s", StringComparison.Ordinal) && !x.StartsWith("--", StringComparison.Ordinal)) ||
                               KubectlArgs.ShortHas(x, "s") || LongPrefix(KubectlArgs.LongName(x), "server", "tls-server-name"))) return false;
                // 하위 명령 찾기는 run_kubectl 과 같은 규칙(KubectlArgs.SubcommandIndex): 값 받는 전역 옵션(-n x · -v 6 · --context x)은 값까지 건너뛰고,
                // 모르는 옵션이 앞에 있으면 어디가 하위 명령인지 확신할 수 없어 승인 받기
                var i = KubectlArgs.SubcommandIndex(a);
                if (i < 0) return false;
                var sub = At(a, i);
                // --raw · --flatten 은 config view 에서 클라이언트 키·토큰을 가리지 않고 출력한다 (kubectl view.go: Flatten 이면 가리기를 건너뜀) —
                // config 판정보다 먼저 막는다 (예전에는 config 를 먼저 허용하고 돌려줘 'kubectl config view --raw' 가 자동 승인됐다)
                if (a.Any(x => x == "dump" || KubectlArgs.LongName(x).StartsWith("--raw", StringComparison.Ordinal) ||
                               KubectlArgs.LongName(x).StartsWith("--flatten", StringComparison.Ordinal) ||
                               KubectlArgs.LongName(x).StartsWith("--output-directory", StringComparison.Ordinal))) return false;
                // -f/--filename · -k/--kustomize 는 로컬 파일(또는 URL)을 읽는다 — 읽기 오류에 내용 일부가 찍힐 수 있다. logs 의 -f 는 --follow
                if (sub != "logs" && a.Any(x => KubectlArgs.ShortHas(x, "fk") || LongPrefix(KubectlArgs.LongName(x), "filename", "kustomize"))) return false;
                if (sub == "logs" && a.Any(x => KubectlArgs.ShortHas(x, "k") || LongPrefix(KubectlArgs.LongName(x), "filename", "kustomize"))) return false;
                // Secret 내용(base64 값)을 출력에 담는 조회는 자동 승인하지 않는다 (get secret -o yaml, describe 는 값을 가리지만 같이 막는다)
                if (a.Skip(i + 1).Any(KubectlSecretRef)) return false;
                // config view 는 옵션이 없어도 가리는 것이 *-data · token 뿐이라(client-go ShortenConfig) basic 인증 password ·
                // auth-provider(OIDC) refresh-token · client-secret · exec env 는 그대로 나온다 → 자동 승인하지 않는다 (이름만 보이는 두 가지만)
                if (sub == "config") return At(a, i + 1) is "get-contexts" or "current-context" && NoMutating(a, i + 2);
                if (sub == "auth") return At(a, i + 1) == "can-i";   // can-i 는 권한 확인만 (뒤에 delete 등이 와도 실행 아님)
                // --cache-dir 는 그 경로에 discovery 캐시 파일을 쓴다 (sudo 면 아무 폴더에나)
                if (a.Any(x => x.StartsWith("--cache-dir", StringComparison.Ordinal))) return false;
                return sub is "get" or "describe" or "logs" or "top" or "version" or "explain" or "api-resources" or "api-versions" or "cluster-info" or "events"
                       && NoMutating(a, i + 1);
            }
            case "ip":
            {
                // ip 는 하위 명령 약어를 받는다 (ip a d = addr del, ip link s = set) → 조회 동사는 정확한 철자만
                var i = 0;
                while (i < a.Count && a[i].StartsWith('-'))
                {
                    var o = a[i].StartsWith("--", StringComparison.Ordinal) ? a[i][1..] : a[i];
                    if (Array.IndexOf(IpOptions, o) < 0 && !o.StartsWith("-color=", StringComparison.Ordinal) && !o.StartsWith("-c=", StringComparison.Ordinal)) return false;
                    i++;
                }
                if (At(a, i) == "") return true;
                return At(a, i + 1) is "" or "show" or "sh" or "sho" or "list" or "lst" or "ls" or "get"
                       && !a.Skip(i + 1).Any(IpMutating.Contains);
            }
            case "rpm":
            {
                if (a.Any(x => x.Contains("%(") || x.Contains("lua:"))) return false;
                bool IsQueryOpt(string x) => (IsShortOpt(x) && x[1] == 'q' && x.Skip(2).All(c => "ailcdfpRsv".IndexOf(c) >= 0))
                                             || x.StartsWith("--query", StringComparison.Ordinal);
                if (!a.Any(IsQueryOpt)) return false;   // -q 없이 -i 는 설치
                return a.All(x => !x.StartsWith('-') || IsQueryOpt(x) || x is "-a" or "-i" or "-l" or "-c" or "-d" or "-f" or "-p" or "-R"
                    or "--qf" or "--last" or "--changelog" or "--provides" or "--requires" or "--scripts" or "--whatprovides" or "--whatrequires"
                    or "--info" or "--list" or "--all");
            }
            case "dpkg":
                return a.Count > 0 && a[0] is "-l" or "-s" or "-L" or "-S" or "--list" or "--status" or "--listfiles" or "--search" or "-p" or "--print-avail"
                       && a.Skip(1).All(x => !x.StartsWith('-'));
            case "dpkg-query":
                return true;
            case "apt":
            case "apt-cache":
            {
                // -o 로 APT 설정(훅 등)을 바꾸거나 -c 로 다른 설정 파일을 읽지 못하게.
                // -p/--pkg-cache · -s/--src-cache 는 캐시 파일 경로 — 캐시를 다시 만들 때 그 경로에 쓴다 (sudo 면 아무 파일이나 덮어씀).
                // 묶음(-qo, -qp)도 거부
                if (a.Any(x => ShortHas(x, "ocps") || LongPrefix(x, "option", "config-file", "pkg-cache", "src-cache"))) return false;
                if (!SubStart(a, out var i, new[] { "-q", "--quiet" })) return false;
                var sub = At(a, i);
                var ok = cmd == "apt"
                    ? sub is "list" or "show" or "search" or "policy"
                    : sub is "show" or "showpkg" or "showsrc" or "search" or "policy" or "depends" or "rdepends" or "madison" or "stats" or "pkgnames" or "unmet";
                return ok && NoMutating(a, i + 1);
            }
            case "yum":
            case "dnf":
            {
                if (a.Any(x => x.StartsWith("--setopt", StringComparison.Ordinal) || (IsShortOpt(x) && x.StartsWith("-c", StringComparison.Ordinal)) ||
                               LongPrefix(x, "config", "installroot", "downloaddir", "destdir"))) return false;
                if (!SubStart(a, out var i, new[] { "-q", "--quiet", "-C", "--cacheonly", "-v", "--verbose" })) return false;
                var sub = At(a, i);
                if (sub == "history") return At(a, i + 1) is "" or "list" or "info" or "summary" && NoMutating(a, i + 1);
                return sub is "list" or "info" or "search" or "check-update" or "repolist" or "provides" && NoMutating(a, i + 1);
            }
            case "pip":
            case "pip3":
            {
                if (a.Any(x => LongPrefix(x, "log", "log-file"))) return false;   // --log 파일 쓰기
                if (!SubStart(a, out var i, Array.Empty<string>())) return false;
                return At(a, i) is "list" or "show" or "freeze" && NoMutating(a, i + 1);
            }
            case "crontab":
                return a.Count == 1 && a[0] == "-l" || (a.Count == 3 && a[0] == "-u" && a[2] == "-l") || (a.Count == 3 && a[0] == "-l" && a[1] == "-u");
            case "journalctl":
                return JournalArgsOk(a);
            case "dmesg":
                return !a.Any(x => ShortHas(x, "cCDEn") || LongPrefix(x, "clear", "read-clear", "console-off", "console-on", "console-level"));
            case "date":
                foreach (var x in a)
                {
                    if (x.StartsWith('+')) continue;   // 출력 형식
                    if (x.StartsWith("--", StringComparison.Ordinal))
                    {
                        if (LongPrefix(x, "set")) return false;
                        if (x is "--utc" or "--universal" or "--debug" or "--rfc-2822" or "--rfc-email" ||
                            x.StartsWith("--rfc-3339=", StringComparison.Ordinal) || x.StartsWith("--iso-8601", StringComparison.Ordinal) ||
                            x.StartsWith("--date=", StringComparison.Ordinal)) continue;
                        return false;
                    }
                    // -u, -R, -d<날짜>, -I[형식] 만 (-s 묶음·-s<날짜> 거부). 그 밖의 인자(MMDDhhmm 등)는 시각 설정
                    if (Regex.IsMatch(x, @"^-[uR]*(d.+)?$") || Regex.IsMatch(x, @"^-[uR]*I(date|hours|minutes|seconds|ns)?$")) continue;
                    return false;
                }
                return true;
            case "hostname":
                return a.All(x => x is "-f" or "-s" or "-d" or "-i" or "-I" or "-a" or "-A" or "-y" or "--fqdn" or "--long" or "--short"
                    or "--domain" or "--ip-address" or "--all-ip-addresses" or "--alias" or "--all-fqdns" or "--nis");
            case "hostnamectl":
            {
                if (!SubStart(a, out var i, CtlFlags)) return false;
                return At(a, i) is "" or "status" && NoMutating(a, i + 1);
            }
            case "timedatectl":
            {
                if (!SubStart(a, out var i, CtlFlags)) return false;
                return At(a, i) is "" or "status" or "show" or "list-timezones" or "timesync-status" or "show-timesync" && NoMutating(a, i + 1);
            }
            case "localectl":
            {
                if (!SubStart(a, out var i, CtlFlags)) return false;
                return At(a, i) is "" or "status" or "list-locales" or "list-keymaps" && NoMutating(a, i + 1);
            }
            case "loginctl":
            {
                if (!SubStart(a, out var i, CtlFlags)) return false;
                var sub = At(a, i);
                return (sub is "" or "session-status" or "user-status" or "seat-status" || sub.StartsWith("list-", StringComparison.Ordinal) ||
                        sub.StartsWith("show-", StringComparison.Ordinal)) && NoMutating(a, i + 1);
            }
            case "top":
                return a.Any(x => x == "-b" || x.StartsWith("-b", StringComparison.Ordinal));
            case "ping":
                return a.Any(x => x == "-c" || x.StartsWith("-c", StringComparison.Ordinal));
            case "arp":
                return !a.Any(x => ShortHas(x, "dsf") || LongPrefix(x, "delete", "set", "file"));
            case "route":
                return a.All(x => x is "-n" or "-e" or "-ee" or "-v" or "-F" or "-C" or "--numeric" or "--extend" or "--verbose");
            case "ifconfig":
                return a.Count <= 1 || (a.Count == 2 && a[0] == "-a");
            case "find":
                return !a.Any(x => x is "-delete" or "-exec" or "-execdir" or "-ok" or "-okdir" or "-fprint" or "-fprint0" or "-fprintf" or "-fls");
            case "sort":
                return !a.Any(x => ShortHas(x, "o") || LongPrefix(x, "output", "compress-program"));
            case "awk":
            case "gawk":
            case "mawk":
            {
                // 옵션은 -F 구분자, -v 변수만 (-f 프로그램 파일, -i/-l/@load 확장, -o/-p/-d 파일 쓰기 등 거부)
                var i = 0;
                while (i < a.Count && a[i].StartsWith('-'))
                {
                    var x = a[i];
                    if (x is "-F" or "-v") { i += 2; continue; }
                    if (x.StartsWith("-F", StringComparison.Ordinal) || (x.StartsWith("-v", StringComparison.Ordinal) && x.Length > 2)) { i++; continue; }
                    if (x == "--") { i++; break; }
                    return false;
                }
                if (i >= a.Count) return false;
                // 프로그램 안의 system(…)(공백 허용), 파이프(| "cmd", "cmd" | getline), 리디렉션(print > "f"), getline, @load 등 거부
                return !a.Any(x => Regex.IsMatch(x, @"\bsystem\b") || x.Contains("getline") || x.IndexOfAny(new[] { '|', '>', '@' }) >= 0 ||
                                   x.StartsWith("/inet", StringComparison.Ordinal));
            }
            case "uniq":
                return a.Count(x => !x.StartsWith('-')) <= 1;   // 두 번째 인자는 출력 파일
            case "mount":
                // 목록 조회만: mount, mount -l, mount [-l] -t 형식
                return a.Count == 0 || (a.Count == 1 && a[0] == "-l") || (a.Count == 2 && a[0] == "-t" && !a[1].StartsWith('-')) ||
                       (a.Count == 3 && a[0] == "-l" && a[1] == "-t" && !a[2].StartsWith('-'));
            case "swapon":
                return a.All(x => x is "-s" or "--show" or "--summary");
            case "ethtool":
                return (a.Count == 1 && !a[0].StartsWith('-')) ||
                       (a.Count == 2 && a[0] is "-i" or "-S" or "-k" or "-g" or "-a" or "-c" or "-T" or "--driver" or "--statistics" or "--show-features" && !a[1].StartsWith('-'));
            case "chronyc":
            {
                // -m (여러 명령) 등 다른 옵션 거부, 조회 명령 하나만
                var subs = a.Where(x => !x.StartsWith('-')).ToList();
                return subs.Count == 1 && subs[0] is "tracking" or "sources" or "sourcestats" or "activity" or "clients"
                       && a.Where(x => x.StartsWith('-')).All(x => x is "-n" or "-c" or "-v");
            }
            case "ntpq":
                return a.All(x => x is "-p" or "-n" or "-pn" or "-np");
            case "ss":
                return !a.Any(x => ShortHas(x, "KD") || LongPrefix(x, "kill", "diag"));   // -K 소켓 종료, -D 파일로 덤프
            case "history":
                return a.All(x => x.Length > 0 && x.All(char.IsDigit));   // -c -d -w -a -r -n -s -p 는 기록 변경·파일 쓰기
            case "sar":
                return !a.Any(x => ShortHas(x, "o"));   // -o 파일 쓰기
            case "file":
                return !a.Any(x => ShortHas(x, "C") || LongPrefix(x, "compile"));
            case "printf":
                return !a.Any(x => x.StartsWith("-v", StringComparison.Ordinal));   // -v 변수 대입 (PATH 등)
            case "ulimit":
                return a.All(x => x.StartsWith('-'));   // 값을 주면 한도 변경
            case "lastlog":
                return !a.Any(x => ShortHas(x, "CS") || LongPrefix(x, "clear", "set"));
            case "blkid":
                // -g 캐시 정리, -c <파일> 캐시 파일 지정 · -w <파일> 캐시 쓰기 (sudo blkid -c /etc/fstab 은 fstab 을 캐시로 덮어씀)
                return !a.Any(x => ShortHas(x, "gcw") || LongPrefix(x, "garbage-collect", "cache-file", "write-cache"));
            default:
                return true;
        }
    }

    // journalctl 은 조회 옵션 허용 목록 방식 (--cursor-file=경로 는 커서를 파일로 쓰고, --flush·--rotate·--vacuum-*·--sync·
    // --relinquish-var·--setup-keys·--update-catalog 등도 쓰기). 긴 옵션은 정확한 이름만 — 접두어 약어(--cursor-f 등)는 거부
    private static readonly HashSet<string> JournalLongFlags = new(StringComparer.Ordinal)
    {
        "no-pager", "follow", "reverse", "catalog", "pager-end", "list-boots", "disk-usage", "utc", "quiet", "dmesg", "all", "full",
        "no-full", "no-hostname", "merge", "system", "user", "fields",
    };

    private static readonly HashSet<string> JournalLongValue = new(StringComparer.Ordinal)
    {
        "unit", "user-unit", "since", "until", "priority", "output", "grep", "identifier", "output-fields", "field", "facility",
    };

    /// <summary>선택 인자 (--lines[=N], --boot[=ID]). 값은 '=' 로만 붙는다고 보고 다음 토큰은 소비하지 않는다</summary>
    private static readonly HashSet<string> JournalLongOptional = new(StringComparer.Ordinal) { "lines", "boot" };

    private const string JournalShortFlags = "xefrqkalmN";
    private const string JournalShortValue = "upogtSUF";

    private static bool JournalArgsOk(List<string> a)
    {
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            // 일치 조건(FIELD=값, 실행 파일 경로)과 -b -1 · -n -5 같은 음수 값
            if (!x.StartsWith('-') || Regex.IsMatch(x, @"^-\d+$")) continue;
            if (x == "--") return false;
            if (x.StartsWith("--", StringComparison.Ordinal))
            {
                var name = x[2..];
                var eq = name.IndexOf('=');
                var hasValue = eq >= 0;
                if (hasValue) name = name[..eq];
                if (JournalLongFlags.Contains(name)) { if (hasValue) return false; continue; }
                if (JournalLongOptional.Contains(name)) continue;
                if (JournalLongValue.Contains(name))
                {
                    if (!hasValue) { if (i + 1 >= a.Count) return false; i++; }   // --since today
                    continue;
                }
                return false;
            }
            // 짧은 옵션 묶음 (-xeu nginx, -n50, -fu nginx)
            for (var k = 1; k < x.Length; k++)
            {
                var c = x[k];
                if (JournalShortFlags.IndexOf(c) >= 0) continue;
                if (c is 'n' or 'b') break;   // 선택 인자: 묶음의 나머지가 값 (-n50, -b-1)
                if (JournalShortValue.IndexOf(c) >= 0)
                {
                    if (k == x.Length - 1) { if (i + 1 >= a.Count) return false; i++; }   // -u nginx (나머지가 있으면 그게 값: -unginx)
                    break;
                }
                return false;
            }
        }
        return true;
    }

    // ================================================================ PowerShell

    private static readonly HashSet<string> PsVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get", "Test", "Select", "Where", "Sort", "Format", "Measure", "Group", "Compare", "ConvertTo", "ConvertFrom",
        "Resolve", "ForEach",
    };

    /// <summary>Test 동사는 Test-ComputerSecureChannel -Repair, Test-ClusterResourceFailure 처럼 변경하는 것도 있어 아는 것만</summary>
    private static readonly HashSet<string> PsTestAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test-Path", "Test-Connection", "Test-NetConnection", "Test-WSMan",
    };

    /// <summary>
    /// Get 동사지만 바꾸는 일을 할 수 있는 cmdlet (모듈 별칭 포함): PSWindowsUpdate 의 Get-WindowsUpdate(-Install · -Download · -AutoReboot · -Hide),
    /// 그 별칭 Get-WUList, Install-WindowsUpdate 의 별칭 Get-WUInstall
    /// </summary>
    private static readonly HashSet<string> PsGetDenied = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get-WindowsUpdate", "Get-WUList", "Get-WUInstall",
    };

    private static readonly HashSet<string> PsSpecific = new(StringComparer.OrdinalIgnoreCase)
    {
        "Write-Output", "Write-Host", "Out-String", "Out-Null", "Out-Host",
    };

    private static readonly HashSet<string> PsAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "dir", "ls", "gci", "cat", "gc", "type", "ps", "gps", "gsv", "gwmi", "gcim", "select", "where", "sort", "ft", "fl",
        "fw", "measure", "group", "%", "?", "foreach", "echo", "write", "gi", "gp", "gdr", "gl", "pwd", "gm", "cd", "sl",
    };

    private static readonly HashSet<string> WinNative = new(StringComparer.OrdinalIgnoreCase)
    {
        "ipconfig", "hostname", "whoami", "systeminfo", "netstat", "tasklist", "query", "quser", "qwinsta", "nslookup",
        "ping", "tracert", "pathping", "route", "arp", "sc", "reg", "schtasks", "wmic", "getmac", "driverquery", "w32tm",
        "bcdedit", "cmdkey", "klist", "findstr", "find", "where", "ver", "vol", "net", "manage-bde",
    };

    private static bool CheckPowerShell(string command, out string reason)
    {
        reason = "";
        // PowerShell 은 ‘ ’ ‚ ‛ “ ” „ 를 따옴표로, – — ― 를 '-' 로 받아들인다 → 이 판별기와 해석이 갈리므로 거부
        if (command.Any(c => c >= '‐' && c <= '‟')) { reason = "유니코드 따옴표·대시"; return false; }
        var bare = StripQuoted(command);
        if (HasUnbalancedQuotes(command)) { reason = "따옴표 짝이 맞지 않음"; return false; }
        // 큰따옴표 문자열 안의 $( ) @( ) ${ } 는 실행된다. ` 는 이스케이프(`" 로 따옴표 해석을 어긋나게 함)
        foreach (var inner in DoubleQuotedInner(command))
            if (inner.Contains('`') || inner.Contains("$(") || inner.Contains("@(") || inner.Contains("${"))
            { reason = "문자열 안 하위 식"; return false; }
        if (bare.Contains('`') || bare.Contains("${") || bare.Contains("<#") || bare.Contains("--%"))
        { reason = "이스케이프·블록 주석·특수 구문"; return false; }
        if (bare.Contains("::")) { reason = ".NET 정적 메서드 호출"; return false; }
        if (HasUncPath(command)) { reason = "UNC 경로 (원격 서버로 NTLM 인증 전송)"; return false; }
        // UNC 경로를 식으로 만들어 넘기는 길도 막는다 (literal \\ 검사를 피함):
        //  '\'+'\h\s' · '{0}{0}h\s' -f '\' · '\'*2 · [char]92 · [wmi]/[adsi] 형식 변환 · $pwd.Path[2] 색인 ·
        //  이어 붙은 토큰 '\'"\h\s" · \'\h\s' · $a$a · $a\h\s · ('\')\h\s · 큰따옴표 안 "$a$a\h"
        if (bare.Contains('+')) { reason = "문자열 연결(+)"; return false; }
        if (Regex.IsMatch(bare, @"(?<![\w$-])-(?:f|[ic]?replace|[ic]?split|join)(?![\w-])", RegexOptions.IgnoreCase))
        { reason = "문자열 연산자(-f, -join, -replace, -split)"; return false; }
        if (Regex.IsMatch(bare, @"(?:['"")]|\$[\w:]+)\s*\*\s*[\d($'""]")) { reason = "문자열 반복(*)"; return false; }
        if (Regex.IsMatch(bare, @"\[\s*[A-Za-z]")) { reason = "형식 리터럴([type])"; return false; }
        if (Regex.IsMatch(bare, @"(?:\$[\w:]+|\))(?:\.\w+)*\[")) { reason = "색인([n])"; return false; }
        if (DoubleQuotedInner(command).Any(i => i.Contains('$'))) { reason = "큰따옴표 안 변수 확장"; return false; }
        if (PsAdjacentToken(command)) { reason = "이어 붙은 토큰 (문자열·변수 연결)"; return false; }
        // -ComputerName · -CimSession · -Session 은 원격 컴퓨터에 접속(인증)한다 (gwmi -ComputerName 공격자 서버 → NTLM 해시 전송)
        if (Regex.IsMatch(bare, @"(?<![\w$-])-(?:cn|co(?:m(?:p(?:u(?:t(?:e(?:r(?:n(?:a(?:m(?:e)?)?)?)?)?)?)?)?)?)?|cims\w*|pssession\w*|session\w*)(?![\w-])", RegexOptions.IgnoreCase))
        { reason = "원격 컴퓨터 지정(-ComputerName·-CimSession·-Session)"; return false; }
        // ComputerName 이 위치 인자인 cmdlet 도 있다: Test-WSMan 공격자 -Authentication Negotiate 는 그 서버로 인증을 보낸다.
        // AD cmdlet 의 -Server(Get-ADUser -Server 공격자)는 LDAP 로 현재 계정 인증을 보낸다. 접두어 약어(-ser · -auth)도 같이
        if (Regex.IsMatch(bare, @"(?<![\w$-])-(?:au(?:t(?:h\w*)?)?|certificatet\w*)(?![\w-])", RegexOptions.IgnoreCase))
        { reason = "다른 서버로 인증 전송(-Authentication·-CertificateThumbprint)"; return false; }
        // -Server 는 Resolve-DnsName 만 예외 — 질의할 DNS 서버 지정(인증 없음)이라 흔한 조회(Resolve-DnsName x -Server 8.8.8.8)를 막지 않는다.
        // 그 문장이 Resolve-DnsName 하나로만 이뤄져야 한다 (스크립트 블록·괄호·변수로 다른 cmdlet 을 끼워 넣지 못하게)
        foreach (var seg in Regex.Split(bare, @"[;|&\r\n]"))
        {
            if (!Regex.IsMatch(seg, @"(?<![\w$-])-ser(?:v(?:e(?:r)?)?)?(?![\w-])", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(seg, @"^\s*Resolve-DnsName(?![\w-])[^{}()$@\[\]]*$", RegexOptions.IgnoreCase)) continue;
            reason = "다른 서버로 인증 전송(-Server)"; return false;
        }
        // Test-Connection(5.1) 의 -Source 는 그 컴퓨터의 WMI 에 접속(인증)해 거기서 ping 한다
        if (Regex.IsMatch(bare, @"(?<![\w$-])Test-Connection(?![\w-])", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(bare, @"(?<![\w$-])-so\w*(?![\w-])", RegexOptions.IgnoreCase))
        { reason = "Test-Connection -Source (원격 WMI 접속)"; return false; }
        // .Method( ) 와 .'Method'( ) · ."Method"( ) · .$name( ) 같은 동적 멤버 호출
        if (Regex.IsMatch(bare, @"\.\s*[A-Za-z_]\w*\s*\(") || Regex.IsMatch(bare, @"\.\s*['$(]")) { reason = "메서드 호출"; return false; }
        var noAmp = Regex.Replace(bare, @"\d?>&\d", " ").Replace("&&", "  ");
        if (noAmp.Contains('&')) { reason = "호출 연산자(&)"; return false; }
        if (Regex.IsMatch(bare, @"(^|[;|{(=,]\s*)\.(\s|$)")) { reason = "dot-source"; return false; }
        if (Regex.IsMatch(bare, @"\b(iex|icm|saps|start|Invoke-\w+)\b", RegexOptions.IgnoreCase)) { reason = "명령 실행 cmdlet"; return false; }
        // % · foreach · ForEach-Object 뒤에는 스크립트 블록 { } 만 (블록 안은 아래에서 구간별 검사).
        //  - foreach ($f in rm C:\x -Recurse) { } : foreach 문. "$f in rm …" 구간이 $ 로 시작해 식으로 건너뛰어지지만 in 뒤는 파이프라인 실행
        //  - % ('Kill') · ForEach-Object ("Ki"+"ll") : 괄호 식으로 넘긴 멤버 이름(메서드) 실행
        // % 를 나머지 연산자로 쓴 식($a % 2)도 같이 거부된다 (승인 창으로)
        if (Regex.IsMatch(bare, @"(?<![\w$-])(?:%|foreach|ForEach-Object)(?![\w-])(?>\s*)(?![{|;})]|$)", RegexOptions.IgnoreCase))
        { reason = "foreach 문·ForEach-Object 의 스크립트 블록 아닌 인자"; return false; }
        var noNull = Regex.Replace(bare, @"(\d|\*)?>\s*\$null(?![\w:])|2>&1", " ", RegexOptions.IgnoreCase);
        if (noNull.Contains('>')) { reason = "파일로 출력(>)"; return false; }

        // 명령 전체에서 cmdlet 이름(동사-명사)을 모두 검사 — 스크립트 블록 { } 안까지 포함
        foreach (Match m in Regex.Matches(bare, @"(?<![\w$-])([A-Za-z]+)-([A-Za-z][A-Za-z0-9]*)\b"))
        {
            var name = m.Value;
            if (PsSpecific.Contains(name)) continue;
            if (!PsVerbs.Contains(m.Groups[1].Value) || PsGetDenied.Contains(name)) { reason = $"읽기 전용이 아닌 cmdlet: {name}"; return false; }
            if (m.Groups[1].Value.Equals("Test", StringComparison.OrdinalIgnoreCase) && !PsTestAllowed.Contains(name))
            { reason = $"확인되지 않은 Test cmdlet: {name}"; return false; }
        }

        // ( ) { } 도 구분자로 나눠, 인자 자리의 (rm x) · $(del x) · 스크립트 블록 안의 명령까지 각각 명령으로 검사
        foreach (var seg in SplitSegments(command, BothQuotes, singleAmpIsSeparator: false, groupingIsSeparator: true))
        {
            var s = seg;
            Match am;
            while ((am = Regex.Match(s, @"^\$[A-Za-z_]\w*\s*=\s*")).Success) s = s[am.Length..];   // 단순 변수 대입 ($env:·$global: 등 범위 지정은 아래에서 거부)
            s = s.Trim();
            if (s.Length == 0) continue;
            if (StripQuoted(s).Contains('=')) { reason = $"대입·해시 테이블: {seg}"; return false; }   // $h['k'] = rm x, $x.y = …, $env:PATH = …
            var lead = s[0];
            if (lead is '$' or '@' or '[' or '\'' or '"' or '-' or '!' || char.IsDigit(lead)) continue;   // 식 모드 (명령 이름 아님)
            if (Regex.IsMatch(s, @"^\.[A-Za-z_]\w*(\s|$)")) continue;   // (…).Count 같은 속성 접근 (메서드 호출은 위에서 거부)
            var t = Tokens(s, BothQuotes);
            if (t.Any(x => x.Length == 0)) { reason = "빈 인자"; return false; }
            if (t.Count == 0) continue;
            var first = t[0];
            if (first is "%" || first.Equals("foreach", StringComparison.OrdinalIgnoreCase) || first.Equals("ForEach-Object", StringComparison.OrdinalIgnoreCase))
            {
                // 스크립트 블록 형태만 (블록 안은 별도 구간으로 검사됨). % Kill / ForEach-Object -MemberName Delete 는 메서드 실행
                if (t.Count > 1) { reason = $"ForEach-Object 멤버 이름 실행: {seg}"; return false; }
                continue;
            }
            if (Regex.IsMatch(first, @"^[A-Za-z]+-[A-Za-z][A-Za-z0-9]*$")) continue;   // cmdlet (위에서 검사)
            if (first.Equals("sc", StringComparison.OrdinalIgnoreCase)) { reason = "PowerShell 5.1 의 sc 는 Set-Content 별칭 (sc.exe 로)"; return false; }
            if (PsAliases.Contains(first)) continue;
            var exe = first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? first[..^4] : first;
            if (WinNative.Contains(exe))
            {
                // 기본 명령의 인자는 글자 그대로만. 괄호·배열·변수·스플랫으로 넘긴 값은 위 구간 나누기에서 따로 떨어져
                // 식으로 건너뛰어지므로 WinArgsOk 가 보지 못한다 — net user ('bob','pw','/add') · $a='bob','pw','/add'; net user $a · net user @a
                if (bare.IndexOfAny(new[] { '$', '@', '(', ')', '{', '}', ',', '[' }) >= 0)
                { reason = $"기본 명령에 식·변수·배열 인자: {seg}"; return false; }
                // 인자 안의 큰따옴표: PowerShell 5.1 은 " 가 든 인자를 이스케이프 없이 통째로 감싸 넘겨, 받는 프로그램이 여러 인자로 나눈다
                // (net user 'bob" "pw" "/add' → net.exe 는 bob · pw · /add 를 받음). 판정기가 본 인자 경계와 달라지므로 받지 않는다
                if (t.Skip(1).Any(x => x.IndexOf('"') >= 0 || x.IndexOf('\\') >= 0 && x.IndexOf('\'') >= 0))
                { reason = $"기본 명령 인자에 따옴표: {seg}"; return false; }
                if (!WinArgsOk(exe, t.Skip(1).ToList())) { reason = $"쓰기 가능한 옵션: {seg}"; return false; }
                continue;
            }
            reason = $"허용 목록에 없는 명령: {first}";
            return false;
        }
        return true;
    }

    // ================================================================ cmd

    private static bool CheckCmd(string command, out string reason)
    {
        reason = "";
        var bare = StripQuoted(command, cmd: true);   // cmd 는 큰따옴표만 따옴표
        if (HasUnbalancedQuotes(command, cmd: true)) { reason = "따옴표 짝이 맞지 않음"; return false; }
        if (bare.Contains('^')) { reason = "이스케이프(^)"; return false; }   // ^" ^& 로 따옴표·구분자 해석이 어긋남
        if (HasUncPath(command)) { reason = "UNC 경로 (원격 서버로 NTLM 인증 전송)"; return false; }
        // %VAR% 확장은 파싱 전에 (따옴표 안에서도) 일어난다 → 단순 %이름% 만 허용, %VAR:a=b% · %VAR:~n% 치환과 !지연 확장! 거부
        if (command.Contains('!')) { reason = "지연 확장(!)"; return false; }
        if (Regex.Replace(command, @"%[A-Za-z_][\w()]*%", "").Contains('%')) { reason = "변수 치환·부분 문자열 확장(%)"; return false; }
        var noNul = Regex.Replace(bare, @"(\d)?>\s*nul(?![^\s&|)])|2>&1", " ", RegexOptions.IgnoreCase);
        if (noNul.Contains('>')) { reason = "파일로 출력(>)"; return false; }
        if (bare.Contains('%') && Regex.IsMatch(bare, @"%[^%\s]+%.*\(")) { reason = "변수 확장 실행"; return false; }
        foreach (var seg in SplitSegments(command, DoubleQuoteOnly, singleAmpIsSeparator: true))
        {
            var t = Tokens(seg, DoubleQuoteOnly);
            if (t.Any(x => x.Length == 0)) { reason = "빈 인자"; return false; }
            if (t.Count == 0) continue;
            var first = t[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[0][..^4] : t[0];
            var args = t.Skip(1).ToList();
            if (first.Equals("dir", StringComparison.OrdinalIgnoreCase) || first.Equals("type", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("echo", StringComparison.OrdinalIgnoreCase) ||
                // sort 는 /o 출력 파일 옵션이 있어 WinArgsOk 의 인자 경계 검사·조각 분할을 거친다 (sort /+1\" /o out.txt /+1\" x 우회)
                first.Equals("sort", StringComparison.OrdinalIgnoreCase) && WinArgsOk("sort", args) ||
                first.Equals("set", StringComparison.OrdinalIgnoreCase) && !seg.Contains('='))
                continue;
            if (WinNative.Contains(first) && WinArgsOk(first, args)) continue;
            reason = $"허용 목록에 없는 명령/옵션: {seg}";
            return false;
        }
        return true;
    }

    /// <summary>Windows 기본 명령의 읽기 전용 사용 형태</summary>
    private static bool WinArgsOk(string exe, List<string> a)
    {
        // 인자 경계: 판정기가 본 인자 하나가 받는 프로그램에서 여럿으로 나뉠 수 있다 —
        // query.exe 는 quser·qwinsta 를 부르며 인자를 따옴표 없이 다시 이어 붙이고, PowerShell 5.1 은 끝이 \ 인 공백 포함 인자를
        // "a b\" 로 감싸(\ 이스케이프 없음) 다음 인자와 합쳐지며, cmd 의 x\" … \" 는 받는 쪽에서 \" 가 글자 따옴표가 된다.
        // 그래서 ① 경계를 흐리는 형태(큰따옴표, 공백이 있고 \ 로 끝남, \ 뒤 공백)는 받지 않고
        // ② 아래 옵션 검사는 각 인자를 공백으로 나눈 조각으로 한다 (query user 'x /server:host' 의 /server 도 보이게)
        if (a.Any(x => x.IndexOf('"') >= 0 || x.Contains("\\ ") || x.Contains("\\\t") ||
                       x.EndsWith('\\') && x.Any(c => c is ' ' or '\t')))
            return false;
        var origCount = a.Count;   // 인자 수는 원래 인자로 센다 — net localgroup "Remote Desktop Users" 는 인자 2개 (net.exe 는 다시 나누지 않음)
        a = a.SelectMany(x => x.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
        string A(int i) => i < a.Count ? a[i].ToLowerInvariant() : "";
        bool Has(params string[] xs) => a.Any(x => xs.Any(y => x.Equals(y, StringComparison.OrdinalIgnoreCase)));
        bool HasRx(string pattern) => a.Any(x => Regex.IsMatch(x, pattern, RegexOptions.IgnoreCase));
        // 원격 컴퓨터 지정은 그 서버로 NTLM 인증을 보낸다 (공격자 서버면 해시 유출): /node: (wmic), /server: (query·quser),
        // /computer: (w32tm), /s 서버 (tasklist·systeminfo·schtasks·getmac·driverquery — findstr 의 /s 는 하위 폴더라 제외)
        if (HasRx(@"^[-/](node|server|computer)(:|$)")) return false;
        if (exe.ToLowerInvariant() is "tasklist" or "systeminfo" or "schtasks" or "getmac" or "driverquery" && HasRx(@"^[-/]s$")) return false;
        switch (exe.ToLowerInvariant())
        {
            case "ipconfig": return !HasRx(@"^[-/](release|renew|flushdns|registerdns|setclassid)");
            // sc \\server query 는 UNC 검사(HasUncPath)에서 이미 거부된다
            case "sc": return A(0) is "query" or "queryex" or "qc" or "qdescription" or "qfailure" or "qtriggerinfo" or "qprivs" or "qsidtype" or "enumdepend" or "getdisplayname" or "getkeyname";
            case "reg": return A(0) == "query";
            case "schtasks": return A(0) is "/query" or "-query" && !HasRx(@"^[-/](create|delete|change|run|end)");
            case "wmic":
                if (HasRx(@"^/(output|append|record)")) return false;   // 결과를 파일로
                if (a.Any(x => x.StartsWith("/format", StringComparison.OrdinalIgnoreCase) &&
                               !Regex.IsMatch(x, @"^/format:(list|table|csv|value|htable|hform|rawxml|xml|textvaluelist)$", RegexOptions.IgnoreCase)))
                    return false;   // 원격 XSL(스크립트 실행) 방지
                return a.Any(x => x.Equals("get", StringComparison.OrdinalIgnoreCase) || x.StartsWith("list", StringComparison.OrdinalIgnoreCase))
                       && !Has("call", "create", "delete", "set");
            case "route": return A(0) == "print" && !HasRx(@"^[-/][fp]$");
            case "arp": return A(0) is "-a" or "-g" or "/a" && !HasRx(@"^[-/][ds]$");
            case "w32tm": return (A(0).StartsWith("/query") || A(0) == "/tz" || A(0) == "/stripchart") && !HasRx(@"^/(config|resync|register|unregister|debug)");
            case "bcdedit": return a.All(x => !(x.StartsWith('/') || x.StartsWith('-')) || x.Equals("/enum", StringComparison.OrdinalIgnoreCase) || x.Equals("/v", StringComparison.OrdinalIgnoreCase));
            case "cmdkey": return a.Count > 0 && a.All(x => x.StartsWith("/list", StringComparison.OrdinalIgnoreCase));
            case "klist": return !Has("purge", "purge_bind", "add_bind");
            case "manage-bde": return A(0) is "-status" or "/status" && a.Skip(1).All(x => !x.StartsWith('-') && !x.StartsWith('/'));
            case "net":
                if (a.Any(x => x.StartsWith('/') || x.StartsWith('-') || x.Contains('*') || x.IndexOfAny(new[] { ',', ';', '=' }) >= 0)) return false;
                return (A(0) is "start" or "share" or "session" or "statistics" or "config" or "view" or "use" or "accounts" && a.Count == 1)
                       || (A(0) is "localgroup" or "user" or "group" && origCount <= 2);
            case "query": return A(0) is "user" or "session" or "process" or "termserver";
            case "sort": return !HasRx(@"^[-/]o");   // /o 출력 파일 (긴 형태 /output 포함)
            default: return true;   // hostname, whoami, systeminfo, netstat, tasklist, quser, nslookup, ping, tracert, getmac ...
        }
    }
}
