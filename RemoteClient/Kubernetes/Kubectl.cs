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
/// </summary>
public static class KubectlArgs
{
    private static readonly string[] BlockedLong =
    {
        "--kubeconfig", "--context", "--cluster", "--user", "--server", "--token", "--as", "--as-group", "--as-uid",
        "--username", "--password", "--client-certificate", "--client-key", "--certificate-authority",
        "--insecure-skip-tls-verify", "--tls-server-name", "--cache-dir",
    };

    /// <summary>문제가 있으면 이유(화면·Claude 에 보이는 문구), 없으면 null</summary>
    public static string? Validate(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return L.T("kubectl 인자가 없습니다.");
        if (args.Count > 200) return L.T("kubectl 인자가 너무 많습니다.");
        foreach (var raw in args)
        {
            if (raw == "--") break;   // 이 뒤는 컨테이너 안에서 실행할 명령 (exec ... -- sh)
            if (raw.Any(c => c == '\0' || c == '\r' || c == '\n')) return L.T("kubectl 인자에 줄바꿈·NUL 문자를 넣을 수 없습니다.");
            var a = raw.Trim();
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a.Split('=', 2)[0].ToLowerInvariant();
                // 접두어로도 막는다 (--server 대신 --serv= 처럼 줄여 쓰는 경우)
                if (BlockedLong.Any(b => b == name || (name.Length >= 6 && b.StartsWith(name, StringComparison.Ordinal))))
                    return L.F("{0} 은(는) 쓸 수 없습니다 — 클러스터와 인증은 Nubis 에 등록한 값으로 고정됩니다.", name);
            }
            // -s / -shttps://... (짧은 --server)
            else if (a.Length >= 2 && a[0] == '-' && a[1] == 's')
                return L.T("-s 는 쓸 수 없습니다 — 클러스터와 인증은 Nubis 에 등록한 값으로 고정됩니다.");
        }
        return null;
    }

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
    public sealed record Result(int ExitCode, string Output, string Error, bool TimedOut);

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

        using var p = new Process { StartInfo = psi };
        p.Start();
        p.StandardInput.Close();   // 입력을 기다리는 명령(exec -i 없이 대화형 등)이 멈추지 않게
        var outTask = ReadCappedAsync(p.StandardOutput, maxOutput);
        var errTask = ReadCappedAsync(p.StandardError, maxOutput / 4);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }
        var output = await outTask;
        var error = await errTask;
        return new Result(timedOut ? -1 : p.ExitCode, output, error, timedOut);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int max)
    {
        // 앞부분과 뒷부분만 남긴다 (SshExec.ClipOutput 과 같은 모양)
        var head = new StringBuilder();
        var tail = new StringBuilder();
        var dropped = 0L;
        var buf = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            if (head.Length < max / 4) { var take = Math.Min(n, max / 4 - head.Length); head.Append(buf, 0, take); if (take < n) tail.Append(buf, take, n - take); }
            else tail.Append(buf, 0, n);
            var keep = max - max / 4;
            if (tail.Length > keep * 2) { dropped += tail.Length - keep; tail.Remove(0, tail.Length - keep); }
        }
        if (tail.Length > max - max / 4) { dropped += tail.Length - (max - max / 4); tail.Remove(0, tail.Length - (max - max / 4)); }
        return dropped > 0 ? head + $"\n... (출력이 길어 가운데 {dropped}자 생략) ...\n" + tail : head.ToString() + tail;
    }
}
