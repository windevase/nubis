using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

public static class SshAuth
{
    /// <summary>비밀번호/키/키보드 인터랙티브 인증을 모두 담은 ConnectionInfo</summary>
    public static ConnectionInfo Build(HostEntry host, string user, string password, int timeoutSec = 15)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(host.KeyPath))
        {
            PrivateKeyFile key;
            try { key = new PrivateKeyFile(host.KeyPath); }
            catch (SshPassPhraseNullOrEmptyException) { key = new PrivateKeyFile(host.KeyPath, password); }
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        if (!string.IsNullOrEmpty(password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = password;
            };
            methods.Add(kbd);
        }
        if (methods.Count == 0) throw new InvalidOperationException("인증 정보(비밀번호 또는 키)가 없습니다.");
        return new ConnectionInfo(host.Address, host.Port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
    }

    public static string HostKeyId(HostEntry h) => $"{h.Address}:{h.Port}";
    public static string Fingerprint(HostKeyEventArgs e) => $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";

    /// <summary>
    /// 비대화형 연결용 호스트키 확인 (ssh 의 accept-new 와 같음): 처음 보는 서버는 저장 후 신뢰, 저장값과 다르면 거부.
    /// 거부 사유를 반환하고, 신뢰하면 null.
    /// </summary>
    public static string? VerifyAcceptNew(HostEntry h, HostKeyEventArgs e)
    {
        var id = HostKeyId(h);
        var fp = Fingerprint(e);
        var known = KnownHostStore.Get(id);
        if (known == null) { KnownHostStore.Set(id, fp); return null; }
        if (KnownHostStore.IsRejected(known))
            return "사용자가 이 서버의 호스트키를 거부한 적이 있습니다. 앱에서 직접 접속해 키를 확인·신뢰한 뒤 다시 시도하세요.";
        return known == fp ? null : $"호스트키가 저장된 값과 다릅니다 ({known} → {fp}). Known Hosts 에서 확인 후 삭제하세요.";
    }
}

public sealed record ExecResult(HostEntry Host, bool Ok, int? ExitCode, string Output, string Error, long ElapsedMs);

/// <summary>여러 서버에 비대화형 명령을 병렬 실행 (Claude run_command 용)</summary>
public static class SshExec
{
    private const int MaxOutput = 8000;

    public static async Task<List<ExecResult>> RunAsync(IEnumerable<HostEntry> hosts, string command, int timeoutSec, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(10);
        var tasks = hosts.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try { return await Task.Run(() => RunOne(h, command, timeoutSec), ct); }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static ExecResult RunOne(HostEntry h, string command, int timeoutSec)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(h.Username) || (string.IsNullOrEmpty(h.Password) && string.IsNullOrWhiteSpace(h.KeyPath)))
                return new ExecResult(h, false, null, "", "저장된 계정 정보가 없습니다 (호스트 또는 클라우드 프로필 설정에 계정 저장 필요)", sw.ElapsedMilliseconds);

            // 시도 순서: 사설 IP → 공인 IP. 연결이 안 될 때만 다음 주소로 (인증 실패·호스트키 불일치는 바로 중단)
            SshClient? client = null;
            var errors = new List<string>();
            string usedAddress = "";
            foreach (var addr in h.AddressCandidates())
            {
                var target = h.Clone();
                target.Address = addr;
                var c = new SshClient(SshAuth.Build(target, h.Username, h.Password));
                string? hostKeyError = null;
                // 비대화형이므로 처음 보는 서버는 저장 후 신뢰(accept-new), 저장값과 다르면 거부
                c.HostKeyReceived += (_, e) => e.CanTrust = (hostKeyError = SshAuth.VerifyAcceptNew(target, e)) == null;
                try
                {
                    c.Connect();
                    client = c;
                    usedAddress = addr;
                    break;
                }
                catch (Exception ex)
                {
                    c.Dispose();
                    if (hostKeyError != null) return new ExecResult(h, false, null, "", $"{addr}: {hostKeyError}", sw.ElapsedMilliseconds);
                    if (ex is SshAuthenticationException) return new ExecResult(h, false, null, "", $"{addr}: 인증 실패 — {ex.Message}", sw.ElapsedMilliseconds);
                    errors.Add($"{addr} {ex.Message}");
                }
            }
            if (client == null) return new ExecResult(h, false, null, "", "SSH 연결 실패 — " + string.Join(" / ", errors), sw.ElapsedMilliseconds);
            using var _ = client;
            var note = errors.Count > 0 ? $"[{usedAddress} 로 실행 — 앞선 시도 실패: {string.Join(" / ", errors)}]\n" : "";

            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSec);
            try { cmd.Execute(); }
            catch (SshOperationTimeoutException)
            {
                return new ExecResult(h, false, null, Trim(cmd.Result), $"{timeoutSec}초 안에 끝나지 않아 중단했습니다.", sw.ElapsedMilliseconds);
            }
            client.Disconnect();
            return new ExecResult(h, cmd.ExitStatus == 0, cmd.ExitStatus, Trim(cmd.Result), Trim(note + cmd.Error).Trim(), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ExecResult(h, false, null, "", ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static string Trim(string? s)
    {
        s ??= "";
        return s.Length <= MaxOutput ? s : s[..MaxOutput] + $"\n... (출력이 길어 {s.Length - MaxOutput}자 생략)";
    }
}
