using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>
/// kubectl 인자 검사 (화면 무관, KubectlArgsTests). 클러스터·인증은 앱이 고정하므로(KUBECONFIG + --context)
/// 인자로 다른 kubeconfig·서버·사용자·토큰을 지정하지 못하게 한다 — 그렇게 바꾸면 저장된 인증이 엉뚱한 서버로 가거나
/// (-s https://공격자), exec 플러그인을 가진 다른 kubeconfig 로 임의 프로그램이 실행될 수 있다.
/// 옵션 해석은 kubectl(cobra·pflag) 규칙을 따른다:
/// · 짧은 옵션은 묶어 쓸 수 있다 (-As x = -A -s x, -Asx 도 같다). 값을 받는 글자가 나오면 그 뒤는 값이다 (-lapp=s)
/// · 긴 옵션 이름의 _ 는 - 로 바뀐다 (--profile_output = --profile-output). 접두어 축약·대소문자 무시는 없다
/// · 값을 받는 옵션 바로 뒤의 -- 는 끝 표시가 아니라 그 옵션의 값이다 (-l -- --server=x 에서 --server 는 옵션)
/// · 첫 인자가 기본 명령이 아니면 PATH 의 kubectl-이름 프로그램(플러그인)을 실행한다
/// </summary>
public static class KubectlArgs
{
    /// <summary>클러스터·서버·인증을 바꾸는 긴 옵션</summary>
    private static readonly string[] AuthLong =
    {
        "--kubeconfig", "--context", "--cluster", "--user", "--server", "--token", "--as", "--as-group", "--as-uid",
        "--username", "--password", "--client-certificate", "--client-key", "--certificate-authority",
        "--insecure-skip-tls-verify", "--tls-server-name",
    };

    /// <summary>원격 서버 셸의 kubectl 에서는 허용하는 것 (그 서버 kubeconfig 안의 다른 context 고르기)</summary>
    private static readonly string[] ClusterSelect = { "--context", "--cluster", "--user" };

    /// <summary>
    /// 이 PC 의 파일을 읽거나 쓰거나 프로그램을 실행하는 긴 옵션: --profile-output(임의 경로에 덮어쓰기), --log-file·--log-dir(옛 버전 klog),
    /// --kuberc(1.33+ 설정 파일 — 별칭·기본 옵션을 바꾼다), --cache-dir, kustomize 의 --helm-command·--enable-alpha-plugins·--enable-exec(프로그램 실행)
    /// </summary>
    private static readonly string[] LocalLong =
    {
        "--profile-output", "--log-file", "--log-dir", "--kuberc", "--cache-dir", "--helm-command", "--enable-alpha-plugins", "--enable-exec",
    };

    /// <summary>kubectl debug 의 --profile 값 (전역 --profile 은 pprof 프로파일 — --profile-output 경로에 파일을 쓴다)</summary>
    private static readonly HashSet<string> DebugProfiles = new(StringComparer.Ordinal)
    {
        "legacy", "general", "baseline", "netadmin", "restricted", "sysadmin",
    };

    /// <summary>
    /// 어느 하위 명령에서든 값을 받는 짧은 옵션 (묶음 안에서 이 글자 뒤는 값). 하위 명령마다 뜻이 다른 글자(-f: logs 는 follow, apply 는 파일)는
    /// 넣지 않는다 — 값 없는 옵션으로 보고 뒤 글자를 계속 검사하는 쪽이 안전하다
    /// </summary>
    private const string ShortValue = "nlocLvks";

    /// <summary>어느 하위 명령에서든 값이 없는 짧은 옵션 (-- 앞에 와도 -- 를 값으로 가져가지 않는다)</summary>
    private const string ShortBool = "itqARwh";

    /// <summary>
    /// 값이 없는(또는 값 생략 가능한 NoOptDefVal) 긴 옵션 — 바로 뒤의 -- 를 값으로 가져가지 않는다.
    /// 목록에 없는 긴 옵션은 값을 받는 것으로 보아 뒤의 -- 를 끝 표시로 보지 않는다 (그 뒤까지 검사 — 안전한 쪽)
    /// </summary>
    private static readonly HashSet<string> LongBool = new(StringComparer.Ordinal)
    {
        "--stdin", "--tty", "--quiet", "--attach", "--rm", "--command", "--leave-stdin-open", "--privileged", "--share-processes",
        "--replace", "--same-node", "--arguments-only", "--keep-annotations", "--keep-labels", "--keep-liveness", "--keep-readiness",
        "--keep-startup", "--keep-init-containers", "--expose", "--force", "--save-config", "--show-managed-fields", "--wait",
        "--recursive", "--all-namespaces", "--all", "--dry-run", "--cascade", "--validate", "--overwrite", "--local", "--record",
        "--allow-missing-template-keys", "--disable-compression", "--match-server-version", "--warnings-as-errors", "--help",
        "--server-side", "--force-conflicts", "--prune", "--ignore-not-found", "--no-headers", "--show-labels", "--show-kind",
        "--watch", "--watch-only", "--previous", "--follow", "--timestamps", "--prefix", "--all-containers", "--ignore-errors",
    };

    /// <summary>kubectl 기본 명령 (이 밖의 첫 인자는 플러그인 kubectl-이름 실행이 된다)</summary>
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "annotate", "api-resources", "api-versions", "apply", "attach", "auth", "autoscale", "certificate", "cluster-info",
        "completion", "config", "cordon", "cp", "create", "debug", "delete", "describe", "diff", "drain", "edit", "events",
        "exec", "explain", "expose", "get", "help", "kustomize", "label", "logs", "options", "patch", "plugin", "port-forward",
        "proxy", "replace", "rollout", "run", "scale", "set", "taint", "top", "uncordon", "version", "wait", "alpha",
    };

    /// <summary>kubectl create 의 기본 하위 명령·별칭 (1.30+ 는 이 밖의 이름이면 kubectl-create-이름 플러그인을 찾는다)</summary>
    private static readonly HashSet<string> CreateSubs = new(StringComparer.Ordinal)
    {
        "clusterrole", "clusterrolebinding", "configmap", "cm", "cronjob", "cj", "deployment", "deploy", "ingress", "ing", "job",
        "namespace", "ns", "poddisruptionbudget", "pdb", "priorityclass", "pc", "quota", "resourcequota", "role", "rolebinding",
        "secret", "service", "svc", "serviceaccount", "sa", "token",
    };

    /// <summary>문제가 있으면 이유(화면·Claude 에 보이는 문구), 없으면 null</summary>
    public static string? Validate(IReadOnlyList<string> args) => Check(args, fixedCluster: true);

    /// <summary>
    /// 인자 검사. fixedCluster=false 는 원격 서버 셸의 kubectl 판정용(ReadOnlyCommands) — 그 서버 kubeconfig 안의
    /// --context·--cluster·--user 선택만 허용하고 나머지(서버·인증·로컬 파일·플러그인)는 똑같이 막는다
    /// </summary>
    internal static string? Check(IReadOnlyList<string> args, bool fixedCluster)
    {
        if (args.Count == 0) return L.T("kubectl 인자가 없습니다.");
        if (args.Count > 200) return L.T("kubectl 인자가 너무 많습니다.");
        // 플러그인: kubectl 은 앞쪽의 옵션 아닌 인자들로 kubectl-<이름> 을 PATH 에서 찾아 실행한다 (krew 등 포함)
        if (!args[0].StartsWith('-') && !Builtins.Contains(args[0]))
            return L.F("kubectl 플러그인({0})은 실행할 수 없습니다 — 기본 kubectl 명령만 쓸 수 있습니다.", args[0]);
        if (args[0] == "create" && args.Count > 1 && !args[1].StartsWith('-') && !CreateSubs.Contains(args[1]))
            return L.F("kubectl 플러그인({0})은 실행할 수 없습니다 — 기본 kubectl 명령만 쓸 수 있습니다.", "create " + args[1]);
        var allowed = fixedCluster ? Array.Empty<string>() : ClusterSelect;
        var end = TerminatorIndex(args);
        if (fixedCluster && ConfigSecretReason(args, end) is { } secret) return secret;
        for (var i = 0; i < end; i++)
        {
            var raw = args[i];
            if (raw.Any(c => c == '\0' || c == '\r' || c == '\n')) return L.T("kubectl 인자에 줄바꿈·NUL 문자를 넣을 수 없습니다.");
            var a = raw.Trim();
            var next = i + 1 < args.Count ? args[i + 1] : null;
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (a.Length == 2) continue;   // 앞 옵션의 값으로 쓰인 -- (끝 표시는 TerminatorIndex 가 정한다)
                var eq = a.IndexOf('=');
                var name = LongName(eq < 0 ? a : a[..eq]);
                var value = eq < 0 ? next : a[(eq + 1)..];
                if (name == "--profile")
                {
                    // kubectl debug --profile=general 은 허용, 그 밖(pprof: heap·cpu 등)은 --profile-output 경로에 파일을 쓴다
                    if (value is { } pv && DebugProfiles.Contains(pv)) continue;
                    return L.F("{0} 은(는) 쓸 수 없습니다 — 이 PC 의 파일을 읽거나 쓰거나 프로그램을 실행할 수 있습니다.", name);
                }
                if (Array.IndexOf(allowed, name) < 0 && Matches(name, AuthLong))
                    return L.F("{0} 은(는) 쓸 수 없습니다 — 클러스터와 인증은 Nubis 에 등록한 값으로 고정됩니다.", name);
                if (Matches(name, LocalLong))
                    return L.F("{0} 은(는) 쓸 수 없습니다 — 이 PC 의 파일을 읽거나 쓰거나 프로그램을 실행할 수 있습니다.", name);
                if (name == "--output" && value != null && FileOutput(value))
                    return L.F("{0} 은(는) 쓸 수 없습니다 — 이 PC 의 파일을 읽거나 쓰거나 프로그램을 실행할 수 있습니다.", "-o " + OutputFormat(value));
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                // 짧은 옵션 묶음: 글자마다 옵션 — 값을 받는 글자가 나오면 나머지(= 뒤, 비었으면 다음 인자)는 값
                for (var k = 1; k < a.Length; k++)
                {
                    var c = a[k];
                    if (c == 's') return L.T("-s 는 쓸 수 없습니다 — 클러스터와 인증은 Nubis 에 등록한 값으로 고정됩니다.");
                    var hasEq = k + 1 < a.Length && a[k + 1] == '=';
                    if (c == 'o')
                    {
                        var ov = k + 1 < a.Length ? a[(hasEq ? k + 2 : k + 1)..] : next;
                        if (ov != null && FileOutput(ov))
                            return L.F("{0} 은(는) 쓸 수 없습니다 — 이 PC 의 파일을 읽거나 쓰거나 프로그램을 실행할 수 있습니다.", "-o " + OutputFormat(ov));
                        break;
                    }
                    if (hasEq || ShortValue.IndexOf(c) >= 0) break;   // -x=값 · 값을 받는 글자
                }
            }
        }
        return null;
    }

    /// <summary>
    /// kubectl config view 에서 비밀값을 가리지 않고 내보내는 옵션. --raw 는 토큰·클라이언트 키·인증서를 그대로,
    /// --flatten 은 파일을 *-data 로 넣으면서 가리기(ShortenConfig)를 건너뛴다 (kubectl view.go: Flatten 이면 Shorten 하지 않음)
    /// </summary>
    private static readonly string[] ConfigSecretFlags = { "--raw", "--flatten" };

    /// <summary>
    /// 앱이 저장한 kubeconfig(임시 파일로 KUBECONFIG 에 넘긴다)를 출력하는 형태인가 — run_kubectl 결과는 Claude 에게 가므로 막는다
    /// (ClaudeGuard 원칙: 저장된 인증은 Claude 에게 넘기지 않는다). 끝 표시 앞 인자에 config 가 있고
    /// · --raw · --flatten(=값 · _ 표기 · 대소문자 무관)이 있거나
    /// · view 가 있으면 거부 — 옵션 없는 config view 도 가리는 것은 *-data · token 뿐이라(client-go ShortenConfig)
    ///   basic 인증 password · auth-provider(OIDC) 의 refresh-token · client-secret · exec env 는 그대로 나온다.
    ///   클러스터 주소·네임스페이스는 list_k8s_clusters 로 볼 수 있어 config view 가 필요 없다.
    /// 어느 인자가 하위 명령인지까지 해석하지 않고 보수적으로 본다 — config 가 없는 get --raw /api 는 kube API 경로 조회라 허용
    /// </summary>
    private static string? ConfigSecretReason(IReadOnlyList<string> args, int end)
    {
        var before = args.Take(end).Select(x => x.Trim()).ToList();
        if (!before.Contains("config", StringComparer.Ordinal)) return null;
        foreach (var x in before)
        {
            if (!x.StartsWith("--", StringComparison.Ordinal) || x.Length == 2) continue;
            var eq = x.IndexOf('=');
            var name = LongName(eq < 0 ? x : x[..eq]);
            if (Array.IndexOf(ConfigSecretFlags, name) >= 0)
                return L.F("kubectl config view {0} 은(는) 쓸 수 없습니다 — Nubis 에 저장한 클러스터 인증(토큰·키)이 그대로 출력됩니다.", name);
        }
        if (before.Contains("view", StringComparer.Ordinal))
            return L.T("kubectl config view 는 쓸 수 없습니다 — Nubis 에 저장한 kubeconfig 의 인증 일부(비밀번호·OIDC 토큰 등)는 가려지지 않고 출력됩니다. 클러스터 주소·네임스페이스는 Nubis 클러스터 목록에서 보세요.");
        return null;
    }

    /// <summary>
    /// 옵션 해석이 끝나는 위치 (-- 의 위치, 없으면 인자 수). 값을 받는(또는 모르는) 옵션 바로 뒤의 -- 는 그 옵션의 값이라
    /// 끝 표시가 아니다 — pflag 는 -l -- --server=x 에서 -- 를 -l 의 값으로 가져가고 --server 를 옵션으로 해석한다
    /// </summary>
    internal static int TerminatorIndex(IReadOnlyList<string> args)
    {
        var pending = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (pending) { pending = false; continue; }
            if (a == "--") return i;
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (!a.Contains('=') && !LongBool.Contains(LongName(a))) pending = true;
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                for (var k = 1; k < a.Length; k++)
                {
                    if (k + 1 < a.Length && a[k + 1] == '=') break;
                    if (ShortBool.IndexOf(a[k]) >= 0) continue;
                    if (k == a.Length - 1) pending = true;   // 값을 받는(또는 모르는) 글자로 끝남 → 다음 인자가 값
                    break;
                }
            }
        }
        return args.Count;
    }

    /// <summary>긴 옵션 이름 정규화 (pflag 의 _ → -, 비교는 보수적으로 소문자)</summary>
    internal static string LongName(string x) => x.ToLowerInvariant().Replace('_', '-');

    /// <summary>
    /// 짧은 옵션 묶음(-Af 등)에 이 글자들이 옵션으로 들어 있다. 값을 받는 글자(-l, -o …) 뒤는 값이라 보지 않는다.
    /// 뜻을 모르는 글자는 값 없는 옵션으로 보고 계속 본다 (놓치지 않는 쪽)
    /// </summary>
    internal static bool ShortHas(string x, string letters)
    {
        if (x.Length < 2 || x[0] != '-' || x[1] == '-') return false;
        for (var k = 1; k < x.Length; k++)
        {
            if (letters.IndexOf(x[k]) >= 0) return true;
            if ((k + 1 < x.Length && x[k + 1] == '=') || ShortValue.IndexOf(x[k]) >= 0) return false;
        }
        return false;
    }

    /// <summary>정확히 같거나 줄여 쓴 접두어(6자 이상 — kubectl 은 축약을 받지 않지만 보수적으로 막는다)</summary>
    private static bool Matches(string name, string[] list) =>
        list.Any(b => b == name || (name.Length >= 6 && b.StartsWith(name, StringComparison.Ordinal)));

    private static string OutputFormat(string v) => v.Split('=', 2)[0].Trim().ToLowerInvariant();

    /// <summary>-o 값이 로컬 파일을 읽는 형식인가 (go-template-file · jsonpath-file · templatefile · custom-columns-file 등)</summary>
    private static bool FileOutput(string v) => OutputFormat(v).EndsWith("file", StringComparison.Ordinal);

    /// <summary>
    /// ReadOnlyCommands(sh 규칙)로 판정할 한 줄 명령. 셸을 거치지 않고 실행하지만, 판정은 보수적으로 같은 규칙을 쓴다.
    /// 따옴표로 감쌀 수 없는 값(작은따옴표 포함)이 있으면 null — 읽기 전용으로 보지 않는다
    /// </summary>
    public static string? AsShellLine(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder("kubectl");
        foreach (var a in args)
        {
            sb.Append(' ');
            if (a.Length > 0 && Regex.IsMatch(a, @"^[A-Za-z0-9._/:=@,+%-]+$")) sb.Append(a);
            else if (a.Contains('\'')) return null;
            else sb.Append('\'').Append(a).Append('\'');
        }
        return sb.ToString();
    }

    /// <summary>읽기 전용(get·describe·logs·top 등)인가 — 사용자가 켠 AutoApproveReadOnly 의 자동 승인 대상</summary>
    public static bool IsReadOnly(IReadOnlyList<string> args) =>
        Validate(args) == null && AsShellLine(args) is { } line && ReadOnlyCommands.IsReadOnly(line, CommandShell.Sh);

    /// <summary>
    /// 한 줄 명령을 인자로 나눈다 (Claude·화면에서 "get pods -n kube-system" 처럼 받을 때). 작은·큰따옴표 묶음만 지원.
    /// 맨 앞의 "kubectl" 은 뺀다
    /// </summary>
    public static List<string> Split(string line)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        var has = false;
        foreach (var ch in line)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else cur.Append(ch);
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; has = true; continue; }
            if (char.IsWhiteSpace(ch))
            {
                if (has || cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); has = false; }
                continue;
            }
            cur.Append(ch);
        }
        if (quote != '\0') throw new FormatException(L.T("따옴표가 닫히지 않았습니다."));
        if (has || cur.Length > 0) list.Add(cur.ToString());
        if (list.Count > 0 && Path.GetFileNameWithoutExtension(list[0]).Equals("kubectl", StringComparison.OrdinalIgnoreCase)) list.RemoveAt(0);
        return list;
    }
}

/// <summary>kubectl 찾기·실행 (클러스터의 kubeconfig 를 보안 임시 파일로 넘긴다)</summary>
public static class Kubectl
{
    public sealed record Result(int ExitCode, string Output, string Error, bool TimedOut)
    {
        /// <summary>표준 출력이 maxOutput 을 넘어 가운데를 잘랐다 (JSON 이면 깨진 것)</summary>
        public bool Truncated { get; init; }
    }

    /// <summary>kubectl.exe 경로. 설정 → PATH → 흔한 설치 위치 순. 없으면 null</summary>
    public static string? Locate(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            return File.Exists(p) ? p : null;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), "kubectl.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var p in new[]
                 {
                     Path.Combine(local, "Microsoft", "WinGet", "Links", "kubectl.exe"),
                     Path.Combine(pf, "Docker", "Docker", "resources", "bin", "kubectl.exe"),
                     Path.Combine(home, ".rd", "bin", "kubectl.exe"),
                     Path.Combine(home, "scoop", "shims", "kubectl.exe"),
                     @"C:\ProgramData\chocolatey\bin\kubectl.exe",
                 })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>CommandExists 결과 캐시 (명령 → 결과, 확인 시각). 카드 다시 그리기·가져오기 목록마다 PATH 전체를 뒤지지 않게</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Exists, long At)> CommandCache = new(StringComparer.OrdinalIgnoreCase);
    private const int CommandCacheMs = 30_000;

    /// <summary>
    /// PC 에 이 명령(exec 플러그인)이 있는지 — PATH 에서 .exe/.cmd/.bat/.ps1 를 찾는다.
    /// 결과는 30초 동안 캐시한다 (설치 직후 반영은 최대 30초 늦다)
    /// </summary>
    public static bool CommandExists(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var now = Environment.TickCount64;
        if (CommandCache.TryGetValue(command, out var hit) && now - hit.At < CommandCacheMs) return hit.Exists;
        var exists = CommandExistsUncached(command);
        if (CommandCache.Count > 256) CommandCache.Clear();   // 이상한 입력으로 끝없이 커지지 않게
        CommandCache[command] = (exists, now);
        return exists;
    }

    private static bool CommandExistsUncached(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command);
        var exts = Path.HasExtension(command) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat", ".ps1" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                try { if (File.Exists(Path.Combine(dir.Trim().Trim('"'), command + ext))) return true; }
                catch { }
            }
        return false;
    }

    /// <summary>
    /// 클러스터의 kubeconfig (기본 네임스페이스를 반영한 것). 이 값을 보안 임시 파일로 써서 KUBECONFIG 로 넘긴다
    /// </summary>
    public static string EffectiveKubeconfig(K8sCluster c) =>
        string.IsNullOrWhiteSpace(c.DefaultNamespace) ? c.Kubeconfig : Nubis.Kubeconfig.WithNamespace(c.Kubeconfig, c.ContextName, c.DefaultNamespace.Trim());

    /// <summary>
    /// kubectl 을 셸 없이 실행한다 (인자는 ArgumentList, kubeconfig 는 KUBECONFIG 임시 파일, --context 고정).
    /// 출력은 앞뒤만 남기고 자른다. 시간이 넘거나 취소하면 프로세스 트리를 끝낸다
    /// </summary>
    public static async Task<Result> RunAsync(K8sCluster cluster, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, int maxOutput = 60000)
    {
        if (KubectlArgs.Validate(args) is { } err) throw new ArgumentException(err);
        var exe = Locate(SettingsStore.Current.KubectlPath)
                  ?? throw new FileNotFoundException(L.T("kubectl 을 찾지 못했습니다. 설치하거나(winget install Kubernetes.kubectl) Kubernetes 페이지에서 경로를 지정하세요."));
        using var cfg = SecureTempFile.Create(SecureTempFile.K8sDir, EffectiveKubeconfig(cluster));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (cluster.ContextName.Length > 0) { psi.ArgumentList.Add("--context"); psi.ArgumentList.Add(cluster.ContextName); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        var persistent = ChildEnvironment.PersistentNames();
        foreach (var k in psi.Environment.Keys.ToList())
            if (ChildEnvironment.ShouldDrop(k, persistent)) psi.Environment.Remove(k);
        psi.Environment["KUBECONFIG"] = cfg.Path;
        // 1.33+ 의 kuberc(~/.kube/kuberc · KUBERC)는 별칭·기본 옵션을 더해 위 인자 검사를 비껴갈 수 있다 → 끈다
        psi.Environment["KUBERC"] = "off";

        using var p = new Process { StartInfo = psi };
        p.Start();
        p.StandardInput.Close();   // 입력을 기다리는 명령(exec -i 없이 대화형 등)이 멈추지 않게
        var outTask = ReadCappedAsync(p.StandardOutput, maxOutput);
        var errTask = ReadCappedAsync(p.StandardError, maxOutput / 4);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 취소·시간 초과 즉시(취소를 부른 스레드에서) 프로세스 트리를 끝낸다 — 앱 종료 중 UI 디스패처가 멈춰
        // await 뒤 코드가 돌지 않아도 kubectl 이 남지 않게. 아래 await 는 ConfigureAwait(false) 라 임시 kubeconfig(using cfg)
        // 삭제도 UI 스레드와 무관하게 스레드 풀에서 끝난다 (호출자의 await 는 각자 원래 컨텍스트로 돌아간다)
        using var killOnCancel = cts.Token.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }
        var output = await outTask.ConfigureAwait(false);
        var error = await errTask.ConfigureAwait(false);
        return new Result(timedOut ? -1 : p.ExitCode, output.Text, error.Text, timedOut) { Truncated = output.Truncated };
    }

    internal static async Task<(string Text, bool Truncated)> ReadCappedAsync(TextReader reader, int max)
    {
        // 앞부분과 뒷부분만 남긴다 (SshExec.ClipOutput 과 같은 모양)
        var head = new StringBuilder();
        var tail = new StringBuilder();
        var dropped = 0L;
        var buf = new char[8192];
        int n;
        // ConfigureAwait(false): 8KB 마다 UI 디스패처로 돌아가지 않게 (앱 종료 중 디스패처가 멈춰도 읽기·정리가 끝나게)
        while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
        {
            if (head.Length < max / 4) { var take = Math.Min(n, max / 4 - head.Length); head.Append(buf, 0, take); if (take < n) tail.Append(buf, take, n - take); }
            else tail.Append(buf, 0, n);
            var keep = max - max / 4;
            if (tail.Length > keep * 2) { dropped += tail.Length - keep; tail.Remove(0, tail.Length - keep); }
        }
        if (tail.Length > max - max / 4) { dropped += tail.Length - (max - max / 4); tail.Remove(0, tail.Length - (max - max / 4)); }
        return dropped > 0 ? (head + $"\n... (출력이 길어 가운데 {dropped}자 생략) ...\n" + tail, true) : (head.ToString() + tail, false);
    }
}
