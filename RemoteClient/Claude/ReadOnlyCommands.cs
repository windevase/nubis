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
/// </summary>
public static class ReadOnlyCommands
{
    public static bool IsReadOnly(string command, CommandShell shell) => IsReadOnly(command, shell, out _);

    public static bool IsReadOnly(string command, CommandShell shell, out string reason)
    {
        reason = "";
        command = command.Trim();
        if (command.Length == 0 || command.Length > 4000) { reason = "빈 명령이거나 너무 김"; return false; }
        if (ApprovalDialog.IsDangerous(command)) { reason = "위험 명령 패턴"; return false; }
        return shell switch
        {
            CommandShell.Sh => CheckSh(command, out reason),
            CommandShell.PowerShell => CheckPowerShell(command, out reason),
            _ => CheckCmd(command, out reason),
        };
    }

    // ================================================================ 공통 도구

    /// <summary>따옴표를 고려해 구분자(;, |, &&, || 등)로 명령을 나눈다. 따옴표 안의 구분자는 무시.</summary>
    private static List<string> SplitSegments(string s, bool singleAmpIsSeparator, bool bracesAreSeparators = false)
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
            if (c is '\'' or '"') { quote = c; sb.Append(c); continue; }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "&&" or "||") { parts.Add(sb.ToString()); sb.Clear(); i++; continue; }
            if (c is ';' or '|' or '\n' || (c == '&' && singleAmpIsSeparator) || (c is '{' or '}' && bracesAreSeparators)) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>공백 기준 토큰 (따옴표 안 공백 유지, 따옴표 제거)</summary>
    private static List<string> Tokens(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (var c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    /// <summary>따옴표 안 내용을 지운 문자열 (구조 검사용)</summary>
    private static string StripQuoted(string s) =>
        Regex.Replace(s, "'[^']*'|\"[^\"]*\"", "''");

    private static bool AnyArg(IEnumerable<string> args, params string[] bad) =>
        args.Any(a => bad.Any(b => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                                   (b.StartsWith('-') && a.StartsWith(b + "=", StringComparison.OrdinalIgnoreCase))));

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

    private static bool CheckSh(string command, out string reason)
    {
        reason = "";
        var bare = StripQuoted(command);
        if (bare.Contains('`') || bare.Contains("$(") || bare.Contains("<(") || bare.Contains(">(") || bare.Contains("<<"))
        { reason = "명령 치환·here-doc"; return false; }
        // 버리는 리디렉션만 허용, 나머지 '>' 는 파일 쓰기
        var noDevNull = Regex.Replace(bare, @"(\d|&)?>>?\s*/dev/null|2>&1|1>&2", " ");
        if (noDevNull.Contains('>')) { reason = "파일로 출력(>)"; return false; }

        foreach (var seg in SplitSegments(command, singleAmpIsSeparator: false))
        {
            if (Regex.IsMatch(StripQuoted(seg), @"(^|\s)&(\s|$)")) { reason = "백그라운드 실행(&)"; return false; }
            var t = Tokens(seg);
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
            var cmd = t[0].Contains('/') ? t[0][(t[0].LastIndexOf('/') + 1)..] : t[0];
            var args = t.Skip(1).ToList();
            if (!ShSimple.Contains(cmd) && !ShSubcommands.Contains(cmd)) { reason = $"허용 목록에 없는 명령: {cmd}"; return false; }
            if (!ShArgsOk(cmd, args)) { reason = $"쓰기 가능한 옵션/하위 명령: {seg}"; return false; }
        }
        return true;
    }

    private static string FirstNonOption(List<string> args) => args.FirstOrDefault(a => !a.StartsWith('-')) ?? "";

    private static bool ShArgsOk(string cmd, List<string> a)
    {
        var sub = FirstNonOption(a);
        switch (cmd)
        {
            case "systemctl":
                return sub is "" or "status" or "is-active" or "is-enabled" or "is-failed" or "list-units" or "list-unit-files"
                    or "list-timers" or "list-sockets" or "list-jobs" or "show" or "cat" or "list-dependencies" or "get-default";
            case "service":
                return a.Contains("--status-all") || (a.Count == 2 && a[1] == "status");
            case "docker":
            case "podman":
            {
                var rest = a.Where(x => !x.StartsWith('-')).ToList();
                if (rest.Count == 0) return true;
                if (rest[0] is "ps" or "images" or "logs" or "inspect" or "stats" or "version" or "info" or "top" or "port" or "history" or "diff")
                    return true;
                return rest.Count > 1 && rest[0] is "container" or "image" or "network" or "volume" or "system" or "compose"
                       && rest[1] is "ls" or "ps" or "inspect" or "logs" or "top" or "port" or "history" or "df" or "info" or "config" or "images";
            }
            case "kubectl":
                if (sub == "config") return a.Any(x => x is "view" or "get-contexts" or "current-context");
                if (sub == "auth") return a.Contains("can-i");
                return sub is "get" or "describe" or "logs" or "top" or "version" or "explain" or "api-resources" or "api-versions" or "cluster-info" or "events";
            case "ip":
                return !a.Any(x => x is "add" or "del" or "delete" or "set" or "flush" or "change" or "replace" or "append" or "prepend" or "restore" or "save" or "exec" or "netns");
            case "rpm":
                return a.Count > 0 && a.All(x => !x.StartsWith('-') || x.StartsWith("-q") || x.StartsWith("--query") || x is "-a" or "-i" or "-l");
            case "dpkg":
                return a.Count > 0 && a[0] is "-l" or "-s" or "-L" or "-S" or "--list" or "--status" or "--listfiles" or "--search" or "-p" or "--print-avail";
            case "dpkg-query":
                return true;
            case "apt":
                return sub is "list" or "show" or "search" or "policy";
            case "apt-cache":
                return true;
            case "yum":
            case "dnf":
                return sub is "list" or "info" or "search" or "check-update" or "repolist" or "provides" or "history" && !a.Contains("undo") && !a.Contains("rollback") && !a.Contains("redo");
            case "pip":
            case "pip3":
                return sub is "list" or "show" or "freeze";
            case "crontab":
                return a.Count == 1 && a[0] == "-l" || (a.Count == 3 && a[0] == "-u" && a[2] == "-l") || (a.Count == 3 && a[0] == "-l" && a[1] == "-u");
            case "journalctl":
                return !a.Any(x => x.StartsWith("--vacuum") || x is "--rotate" or "--flush" or "--relinquish-var" or "--setup-keys" or "--sync");
            case "dmesg":
                return !a.Any(x => x is "-c" or "-C" or "-D" or "-E" or "-n" or "--clear" or "--read-clear" or "--console-off" or "--console-on" or "--console-level");
            case "date":
                return !a.Any(x => x is "-s" or "--set" || x.StartsWith("--set=")) && a.All(x => x.StartsWith('-') || x.StartsWith('+'));
            case "hostname":
                return a.All(x => x.StartsWith('-'));
            case "hostnamectl":
                return sub is "" or "status";
            case "timedatectl":
                return sub is "" or "status" or "show" or "list-timezones" or "timesync-status" or "show-timesync";
            case "localectl":
                return sub is "" or "status" or "list-locales" or "list-keymaps";
            case "loginctl":
                return sub is "" || sub.StartsWith("list-") || sub.StartsWith("show-") || sub is "session-status" or "user-status" or "seat-status";
            case "top":
                return a.Any(x => x == "-b" || x.StartsWith("-b"));
            case "ping":
                return a.Any(x => x == "-c" || x.StartsWith("-c"));
            case "arp":
                return !a.Any(x => x is "-d" or "-s" or "-f" or "--delete" or "--set" or "--file");
            case "route":
                return !a.Any(x => x is "add" or "del" or "delete" or "flush" or "change");
            case "ifconfig":
                return a.Count <= 1 || (a.Count == 2 && a[0] == "-a");
            case "find":
                return !a.Any(x => x is "-delete" or "-exec" or "-execdir" or "-ok" or "-okdir" or "-fprint" or "-fprint0" or "-fprintf" or "-fls");
            case "sort":
                return !a.Any(x => x == "-o" || x.StartsWith("-o") || x.StartsWith("--output"));
            case "awk":
            case "gawk":
            case "mawk":
                return !a.Any(x => x.Contains("system(") || x.Contains("getline") || x.Contains("| \"") || x.Contains("|\"") || x.Contains("print >")) && !a.Contains("-i");
            case "uniq":
                return a.Count(x => !x.StartsWith('-')) <= 1;   // 두 번째 인자는 출력 파일
            case "mount":
                return a.Count == 0 || a.All(x => x is "-l" or "-t" || !x.StartsWith('/') && !x.StartsWith('-') && x.Length < 12 && a.Contains("-t"));
            case "swapon":
                return a.All(x => x is "-s" or "--show" or "--summary");
            case "ethtool":
                return a.Count == 1 || (a.Count == 2 && a[0] is "-i" or "-S" or "-k" or "-g" or "-a" or "-c" or "-T" or "--driver" or "--statistics" or "--show-features");
            case "chronyc":
                return sub is "tracking" or "sources" or "sourcestats" or "activity" or "clients";
            case "ntpq":
                return a.All(x => x is "-p" or "-n" or "-pn" or "-np");
            default:
                return true;
        }
    }

    // ================================================================ PowerShell

    private static readonly HashSet<string> PsVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get", "Test", "Select", "Where", "Sort", "Format", "Measure", "Group", "Compare", "ConvertTo", "ConvertFrom",
        "Resolve", "ForEach",
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
        var bare = StripQuoted(command);
        if (bare.Contains("::")) { reason = ".NET 정적 메서드 호출"; return false; }
        if (Regex.IsMatch(bare, @"\.\s*[A-Za-z_]\w*\s*\(")) { reason = "메서드 호출"; return false; }
        if (Regex.IsMatch(bare, @"(^|[\s;|(])&\s*[\w'""$({]") || Regex.IsMatch(bare, @"(^|[;|]\s*)\.\s+\S")) { reason = "호출 연산자(&)·dot-source"; return false; }
        if (Regex.IsMatch(bare, @"\b(iex|icm|saps|start|Invoke-\w+)\b", RegexOptions.IgnoreCase)) { reason = "명령 실행 cmdlet"; return false; }
        var noNull = Regex.Replace(bare, @"(\d|\*)?>\s*\$null|2>&1", " ", RegexOptions.IgnoreCase);
        if (noNull.Contains('>')) { reason = "파일로 출력(>)"; return false; }

        // 명령 전체에서 cmdlet 이름(동사-명사)을 모두 검사 — 스크립트 블록 { } 안까지 포함
        foreach (Match m in Regex.Matches(bare, @"(?<![\w$-])([A-Za-z]+)-([A-Za-z][A-Za-z0-9]*)\b"))
        {
            var name = m.Value;
            if (PsSpecific.Contains(name)) continue;
            if (!PsVerbs.Contains(m.Groups[1].Value)) { reason = $"읽기 전용이 아닌 cmdlet: {name}"; return false; }
        }

        foreach (var seg in SplitSegments(command, singleAmpIsSeparator: false, bracesAreSeparators: true))
        {
            var s = Regex.Replace(seg, @"^\$[\w:]+\s*=\s*", "").TrimStart('(', ' ');   // $x = ... 대입, ( 로 시작
            var t = Tokens(s);
            if (t.Count == 0) continue;
            var first = t[0];
            if (first.StartsWith('$') || first.StartsWith('{') || first.StartsWith('}') || first.StartsWith('@') || first.StartsWith('[')) continue;   // 값·블록 (위에서 cmdlet 검사 끝남)
            if (Regex.IsMatch(first, @"^[A-Za-z]+-[A-Za-z][A-Za-z0-9]*$")) continue;   // cmdlet (위에서 검사)
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
        var bare = StripQuoted(command);
        var noNul = Regex.Replace(bare, @"(\d)?>\s*nul\b|2>&1", " ", RegexOptions.IgnoreCase);
        if (noNul.Contains('>')) { reason = "파일로 출력(>)"; return false; }
        if (bare.Contains('%') && Regex.IsMatch(bare, @"%[^%\s]+%.*\(")) { reason = "변수 확장 실행"; return false; }
        foreach (var seg in SplitSegments(command, singleAmpIsSeparator: true))
        {
            var t = Tokens(seg);
            if (t.Count == 0) continue;
            var first = t[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[0][..^4] : t[0];
            var args = t.Skip(1).ToList();
            if (first.Equals("dir", StringComparison.OrdinalIgnoreCase) || first.Equals("type", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("echo", StringComparison.OrdinalIgnoreCase) || first.Equals("sort", StringComparison.OrdinalIgnoreCase) && !args.Any(x => x.StartsWith("/o", StringComparison.OrdinalIgnoreCase)) ||
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
        switch (exe.ToLowerInvariant())
        {
            case "ipconfig": return !Has("/release", "/release6", "/renew", "/renew6", "/flushdns", "/registerdns", "/setclassid", "/setclassid6");
            case "sc": return A(0) is "query" or "queryex" or "qc" or "qdescription" or "qfailure" or "qtriggerinfo" or "qprivs" or "qsidtype" or "enumdepend" or "getdisplayname" or "getkeyname"
                              || (a.Count > 1 && A(1) is "query" or "queryex" or "qc" or "qdescription");   // sc \\server query
            case "reg": return A(0) == "query";
            case "schtasks": return Has("/query");
            case "wmic": return a.Any(x => x.Equals("get", StringComparison.OrdinalIgnoreCase) || x.StartsWith("list", StringComparison.OrdinalIgnoreCase))
                                && !Has("call", "create", "delete", "set");
            case "route": return A(0) == "print";
            case "arp": return A(0) is "-a" or "-g" or "/a";
            case "w32tm": return A(0).StartsWith("/query") || A(0) == "/tz" || A(0) == "/stripchart";
            case "bcdedit": return a.Count == 0 || A(0) is "/enum" or "/v";
            case "cmdkey": return Has("/list") || a.Any(x => x.StartsWith("/list", StringComparison.OrdinalIgnoreCase));
            case "klist": return !Has("purge", "purge_bind", "add_bind");
            case "manage-bde": return Has("-status", "/status");
            case "net":
                if (a.Any(x => x.StartsWith('/'))) return false;
                return (A(0) is "start" or "share" or "session" or "statistics" or "config" or "view" or "use" or "accounts" && a.Count == 1)
                       || (A(0) is "localgroup" or "user" or "group" && a.Count <= 2);
            case "query": return A(0) is "user" or "session" or "process" or "termserver";
            default: return true;   // hostname, whoami, systeminfo, netstat, tasklist, quser, nslookup, ping, tracert, getmac ...
        }
    }
}
