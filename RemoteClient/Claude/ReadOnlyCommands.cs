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
    public static bool IsReadOnly(string command, CommandShell shell) => IsReadOnly(command, shell, out _);

    public static bool IsReadOnly(string command, CommandShell shell, out string reason)
    {
        reason = "";
        command = command.Trim();
        if (command.Length == 0 || command.Length > 4000) { reason = "빈 명령이거나 너무 김"; return false; }
        // 제어 문자: 줄바꿈(여러 명령)·Ctrl-U/Ctrl-W(줄 지우기)·ESC·DEL·C1 등은 보이는 글자와 실제 실행이 달라질 수 있다 (send_to_terminal 도 이 검사에 기댐). 탭만 허용
        if (command.Any(c => (c < 0x20 && c != '\t') || (c >= 0x7f && c <= 0x9f) || c is '\u2028' or '\u2029'))
        { reason = "제어 문자"; return false; }
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

    /// <summary>공백 기준 토큰 (따옴표 안 공백 유지, 따옴표 제거)</summary>
    private static List<string> Tokens(string s, string quotes)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (var c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); continue; }
            if (quotes.IndexOf(c) >= 0) { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
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

    /// <summary>-abc 같은 짧은 옵션(묶음 포함)</summary>
    private static bool IsShortOpt(string x) => x.Length > 1 && x[0] == '-' && x[1] != '-';

    /// <summary>짧은 옵션 묶음 안에 금지 글자가 하나라도 있다 (-uo, -tK 처럼 묶어 쓴 경우 포함)</summary>
    private static bool ShortHas(string x, string letters) => IsShortOpt(x) && x.Skip(1).Any(c => letters.IndexOf(c) >= 0);

    /// <summary>
    /// --xxx 긴 옵션이 금지 이름의 접두어다. GNU getopt_long 은 모호하지 않은 접두어(--out → --output)를 받아들이므로
    /// 모호한 접두어까지 포함해 막는다.
    /// </summary>
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

    private static bool CheckSh(string command, out string reason)
    {
        reason = "";
        var bare = StripQuoted(command);   // 작은따옴표·큰따옴표 내용을 지운 구조
        if (HasUnbalancedQuotes(command)) { reason = "따옴표 짝이 맞지 않음"; return false; }
        // 큰따옴표 안에서도 명령 치환·변수 확장·히스토리 확장이 일어난다. 백슬래시는 \" 로 따옴표 해석을 어긋나게 하므로 거부
        foreach (var inner in DoubleQuotedInner(command))
            if (inner.IndexOfAny(new[] { '`', '\\', '!' }) >= 0 || inner.Contains("$(") || inner.Contains("${") || inner.Contains("$["))
            { reason = "큰따옴표 안 명령 치환·확장"; return false; }
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

    private static bool ShArgsOk(string cmd, List<string> a)
    {
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
                if (!SubStart(a, out var i, new[] { "-D", "--debug" }, new[] { "-H", "--host", "--context", "-c", "--connection" })) return false;
                var sub = At(a, i);
                var ok = sub is "" or "ps" or "images" or "logs" or "inspect" or "stats" or "version" or "info" or "top" or "port" or "history" or "diff"
                         || (sub is "container" or "image" or "network" or "volume" or "system" or "compose"
                             && At(a, i + 1) is "ls" or "ps" or "inspect" or "logs" or "top" or "port" or "history" or "df" or "info" or "images");
                return ok && NoMutating(a, i + 1);
            }
            case "kubectl":
            {
                if (a.Any(x => x.StartsWith("--kubeconfig", StringComparison.Ordinal))) return false;   // exec 자격 증명 플러그인 실행 가능
                if (!SubStart(a, out var i, new[] { "-A", "--all-namespaces" }, new[] { "-n", "--namespace", "--context", "--cluster", "--user" })) return false;
                var sub = At(a, i);
                if (sub == "config") return At(a, i + 1) is "view" or "get-contexts" or "current-context" && NoMutating(a, i + 2);
                if (sub == "auth") return At(a, i + 1) == "can-i";   // can-i 는 권한 확인만 (뒤에 delete 등이 와도 실행 아님)
                if (a.Any(x => x == "dump" || x.StartsWith("--raw", StringComparison.Ordinal) || x.StartsWith("--output-directory", StringComparison.Ordinal))) return false;
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
                // -o 로 APT 설정(훅 등)을 바꾸거나 -c 로 다른 설정 파일을 읽지 못하게
                if (a.Any(x => x.StartsWith("-o", StringComparison.Ordinal) || x.StartsWith("-c", StringComparison.Ordinal) ||
                               LongPrefix(x, "option", "config-file"))) return false;
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
                return !a.Any(x => ShortHas(x, "g") || LongPrefix(x, "garbage-collect"));
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
            if (!PsVerbs.Contains(m.Groups[1].Value)) { reason = $"읽기 전용이 아닌 cmdlet: {name}"; return false; }
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
            if (WinNative.Contains(exe)) { if (!WinArgsOk(exe, t.Skip(1).ToList())) { reason = $"쓰기 가능한 옵션: {seg}"; return false; } continue; }
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
        // %VAR% 확장은 파싱 전에 (따옴표 안에서도) 일어난다 → 단순 %이름% 만 허용, %VAR:a=b% · %VAR:~n% 치환과 !지연 확장! 거부
        if (command.Contains('!')) { reason = "지연 확장(!)"; return false; }
        if (Regex.Replace(command, @"%[A-Za-z_][\w()]*%", "").Contains('%')) { reason = "변수 치환·부분 문자열 확장(%)"; return false; }
        var noNul = Regex.Replace(bare, @"(\d)?>\s*nul(?![^\s&|)])|2>&1", " ", RegexOptions.IgnoreCase);
        if (noNul.Contains('>')) { reason = "파일로 출력(>)"; return false; }
        if (bare.Contains('%') && Regex.IsMatch(bare, @"%[^%\s]+%.*\(")) { reason = "변수 확장 실행"; return false; }
        foreach (var seg in SplitSegments(command, DoubleQuoteOnly, singleAmpIsSeparator: true))
        {
            var t = Tokens(seg, DoubleQuoteOnly);
            if (t.Count == 0) continue;
            var first = t[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[0][..^4] : t[0];
            var args = t.Skip(1).ToList();
            if (first.Equals("dir", StringComparison.OrdinalIgnoreCase) || first.Equals("type", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("echo", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("sort", StringComparison.OrdinalIgnoreCase) && !args.Any(x => x.StartsWith("/o", StringComparison.OrdinalIgnoreCase) || x.StartsWith("-o", StringComparison.OrdinalIgnoreCase)) ||
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
        string A(int i) => i < a.Count ? a[i].ToLowerInvariant() : "";
        bool Has(params string[] xs) => a.Any(x => xs.Any(y => x.Equals(y, StringComparison.OrdinalIgnoreCase)));
        bool HasRx(string pattern) => a.Any(x => Regex.IsMatch(x, pattern, RegexOptions.IgnoreCase));
        switch (exe.ToLowerInvariant())
        {
            case "ipconfig": return !HasRx(@"^[-/](release|renew|flushdns|registerdns|setclassid)");
            case "sc": return A(0) is "query" or "queryex" or "qc" or "qdescription" or "qfailure" or "qtriggerinfo" or "qprivs" or "qsidtype" or "enumdepend" or "getdisplayname" or "getkeyname"
                              || (a.Count > 1 && A(0).StartsWith(@"\\") && A(1) is "query" or "queryex" or "qc" or "qdescription");   // sc \\server query
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
                       || (A(0) is "localgroup" or "user" or "group" && a.Count <= 2);
            case "query": return A(0) is "user" or "session" or "process" or "termserver";
            default: return true;   // hostname, whoami, systeminfo, netstat, tasklist, quser, nslookup, ping, tracert, getmac ...
        }
    }
}
