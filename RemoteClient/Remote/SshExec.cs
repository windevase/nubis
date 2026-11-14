using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

public sealed record ExecResult(HostEntry Host, bool Ok, int? ExitCode, string Output, string Error, long ElapsedMs);

/// <summary>여러 서버에 비대화형 명령을 병렬 실행 (Claude run_command 용)</summary>
public static class SshExec
{
    private const int MaxOutput = 8000;

    /// <summary>
    /// 긴 출력은 앞부분 1/4 과 뒷부분 3/4 만 남긴다 (오류·결과는 보통 끝에 있으므로).
    /// 가운데 생략한 글자 수를 표시한다.
    /// </summary>
    public static string ClipOutput(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = max / 4;
        var tail = max - head;
        return s[..head] + $"\n... (출력이 길어 가운데 {s.Length - max}자 생략) ...\n" + s[^tail..];
    }

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
            if (string.IsNullOrWhiteSpace(h.Username) || (string.IsNullOrEmpty(h.Password) && !h.HasKey && !SshAuth.HasDefaultKeyFiles()))
                return new ExecResult(h, false, null, "", "저장된 계정 정보가 없습니다 (호스트 또는 클라우드 프로필 설정에 계정 저장 필요)", sw.ElapsedMilliseconds);

            // 점프 서버(ProxyJump)를 거치면 터널로 사설 주소에만 (비대화형 — 점프 서버가 OTP 를 요구하면 실패)
            using var tunnel = h.Jump is { } jump ? JumpTunnel.Open(jump, h.Address, h.Port, null, SshAuth.VerifyAcceptNew) : null;

            // 시도 순서: 사설 IP → 공인 IP. 연결이 안 될 때만 다음 주소로 (인증 실패·호스트키 불일치는 바로 중단)
            SshClient? client = null;
            var errors = new List<string>();
            string usedAddress = "";
            foreach (var addr in tunnel != null ? [h.Address] : h.AddressCandidates())
            {
                var target = h.Clone();
                target.Address = addr;
                var c = new SshClient(SshAuth.Build(target, h.Username, h.Password, tunnel: tunnel));
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

    /// <summary>
    /// 비대화형 SFTP 연결 (Claude sftp_* 도구용). 사설 → 공인 주소 순으로, 연결 실패일 때만 다음 주소.
    /// 인증 실패·호스트키 불일치는 바로 중단한다. 실패하면 사유를 담은 InvalidOperationException.
    /// </summary>
    public static SftpClient OpenSftp(HostEntry h, int timeoutSec = 15)
    {
        if (string.IsNullOrWhiteSpace(h.Username) || (string.IsNullOrEmpty(h.Password) && !h.HasKey && !SshAuth.HasDefaultKeyFiles()))
            throw new InvalidOperationException("저장된 계정 정보가 없습니다 (호스트·Vault·클라우드 프로필 설정에 계정 저장 필요)");
        var errors = new List<string>();
        // 점프 서버를 거치면 터널을 SFTP 클라이언트가 닫힐 때 함께 닫는다
        var tunnel = h.Jump is { } jump ? JumpTunnel.Open(jump, h.Address, h.Port, null, SshAuth.VerifyAcceptNew) : null;
        foreach (var addr in tunnel != null ? [h.Address] : h.AddressCandidates())
        {
            var target = h.Clone();
            target.Address = addr;
            var c = new TunneledSftpClient(SshAuth.Build(target, h.Username, h.Password, timeoutSec, tunnel: tunnel), tunnel);
            string? hostKeyError = null;
            c.HostKeyReceived += (_, e) => e.CanTrust = (hostKeyError = SshAuth.VerifyAcceptNew(target, e)) == null;
            try
            {
                c.Connect();
                return c;
            }
            catch (Exception ex)
            {
                c.Dispose();
                if (hostKeyError != null) throw new InvalidOperationException($"{addr}: {hostKeyError}");
                if (ex is SshAuthenticationException) throw new InvalidOperationException($"{addr}: 인증 실패 — {ex.Message}");
                errors.Add($"{addr} {ex.Message}");
            }
        }
        throw new InvalidOperationException("SFTP 연결 실패 — " + string.Join(" / ", errors));
    }

    /// <summary>점프 서버 터널을 쓰는 SFTP 연결 — 닫을 때 터널도 닫는다</summary>
    private sealed class TunneledSftpClient(ConnectionInfo info, JumpTunnel? tunnel) : SftpClient(info)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) tunnel?.Dispose();
        }
    }

    private static string Trim(string? s)
    {
        s ??= "";
        return ClipOutput(s, MaxOutput);
    }
}
