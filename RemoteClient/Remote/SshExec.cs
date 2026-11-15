using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <param name="Note">실행 방식 안내 (예: 열린 탭의 연결로 실행) — 오류가 아니므로 stderr 와 따로 보여 준다</param>
public sealed record ExecResult(HostEntry Host, bool Ok, int? ExitCode, string Output, string Error, long ElapsedMs, string? Note = null);

/// <summary>여러 서버에 비대화형 명령을 병렬 실행 (Claude run_command 용)</summary>
public static class SshExec
{
    private const int MaxOutput = 8000;

    /// <summary>
    /// 긴 출력은 앞부분 1/4 과 뒷부분 3/4 만 남긴다 (오류·결과는 보통 끝에 있으므로).
    /// 가운데 생략한 글자 수를 표시한다. 서로게이트 쌍(이모지 등)은 반으로 자르지 않는다.
    /// </summary>
    public static string ClipOutput(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = max / 4;
        var tailStart = s.Length - (max - head);
        if (head > 0 && char.IsHighSurrogate(s[head - 1])) head--;
        if (tailStart < s.Length && char.IsLowSurrogate(s[tailStart])) tailStart++;
        return s[..head] + $"\n... (출력이 길어 가운데 {tailStart - head}자 생략) ...\n" + s[tailStart..];
    }

    /// <param name="shared">호스트 Id → 열린 터미널 탭의 로그인된 연결. 있으면 새로 접속하지 않고 그 연결에 명령 채널만 연다 (OTP 서버도 다시 인증 없이)</param>
    /// <param name="ct">취소하면 아직 시작하지 않은 서버에는 명령을 보내지 않고, 실행 중인 명령에는 중단 신호(SIGTERM)를 보낸다</param>
    public static async Task<List<ExecResult>> RunAsync(IEnumerable<HostEntry> hosts, string command, int timeoutSec, CancellationToken ct,
        IReadOnlyDictionary<Guid, (SshClient Client, string User)>? shared = null)
    {
        using var gate = new SemaphoreSlim(10);
        var tasks = hosts.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await Task.Run(() =>
                    shared != null && shared.TryGetValue(h.Id, out var s) && RunShared(h, s.Client, s.User, command, timeoutSec, ct) is { } r
                        ? r : RunOne(h, command, timeoutSec, ct), ct);
            }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>
    /// 열린 터미널 탭의 SSH 연결 위에 exec 채널을 열어 실행한다. 연결은 터미널 것이라 닫지 않는다.
    /// 탭 연결이 그사이 끊겼으면 null — 호출하는 쪽이 새 연결로 넘어간다 (명령은 아직 실행되지 않았음).
    /// 채널 열기 실패(서버의 세션 수 제한 등)를 포함해 Execute 이후의 오류는 결과로 돌려주고 다시 실행하지 않는다
    /// </summary>
    private static ExecResult? RunShared(HostEntry h, SshClient client, string user, string command, int timeoutSec, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        SshCommand cmd;
        try
        {
            if (!client.IsConnected) return null;
            cmd = client.CreateCommand(command);
        }
        catch { return null; }
        using (cmd)
        {
            var note = $"열린 터미널 탭의 연결로 실행 — 계정 {user}";
            if (ct.IsCancellationRequested) return new ExecResult(h, false, null, "", "취소되어 실행하지 않았습니다.", sw.ElapsedMilliseconds, note);
            try
            {
                var r = ExecCapped(cmd, timeoutSec, ct);
                return new ExecResult(h, r.Failure == null && r.Exit == 0, r.Failure == null ? r.Exit : null, r.Output,
                    JoinError(r.Failure, r.Error), sw.ElapsedMilliseconds, note);
            }
            catch (Exception ex)
            {
                // 채널을 열기 전에 실패했을 수도, 실행 중에 끊겼을 수도 있어 새 연결로 다시 실행하지 않는다 (중복 실행 방지)
                return new ExecResult(h, false, null, "", "실행 중 오류 — " + ex.Message, sw.ElapsedMilliseconds, note);
            }
        }
    }

    private static ExecResult RunOne(HostEntry h, string command, int timeoutSec, CancellationToken ct)
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
                // 취소됐으면 다음 주소로 접속하지 않는다
                if (ct.IsCancellationRequested) return new ExecResult(h, false, null, "", "취소되어 실행하지 않았습니다.", sw.ElapsedMilliseconds);
                var target = h.Clone();
                target.Address = addr;
                var c = new SshClient(SshAuth.Build(target, h.Username, h.Password, tunnel: tunnel));
                string? hostKeyError = null;
                var reachedAuth = false;
                // 비대화형이므로 처음 보는 서버는 저장 후 신뢰(accept-new), 저장값과 다르면 거부
                c.HostKeyReceived += (_, e) => { reachedAuth = true; e.CanTrust = (hostKeyError = SshAuth.VerifyAcceptNew(target, e)) == null; };
                try
                {
                    c.Connect();
                    SshServerOs.Report(h, c);
                    client = c;
                    usedAddress = addr;
                    break;
                }
                catch (Exception ex)
                {
                    // 배너는 인증 전에 받으므로 인증(OTP 요구 등)에서 실패해도 서버 OS 는 기억한다
                    if (reachedAuth) SshServerOs.Report(h, c);
                    c.Dispose();
                    if (hostKeyError != null) return new ExecResult(h, false, null, "", $"{addr}: {hostKeyError}", sw.ElapsedMilliseconds);
                    if (ex is SshAuthenticationException) return new ExecResult(h, false, null, "", $"{addr}: 인증 실패 — {ex.Message}", sw.ElapsedMilliseconds);
                    // 호스트키를 받은 뒤(인증 단계)의 시간 초과·끊김은 다른 주소로 다시 시도하지 않는다 — 같은 계정으로 인증이 한 번 더 나가 잠금 위험
                    if (SshFailure.IsPermanent(ex, reachedAuth))
                        return new ExecResult(h, false, null, "", $"{addr}: 인증 단계에서 실패 — {ex.Message} (다른 주소로 재시도하지 않음)", sw.ElapsedMilliseconds);
                    errors.Add($"{addr} {ex.Message}");
                }
            }
            if (client == null) return new ExecResult(h, false, null, "", "SSH 연결 실패 — " + string.Join(" / ", errors), sw.ElapsedMilliseconds);
            using var _ = client;
            var note = errors.Count > 0 ? $"[{usedAddress} 로 실행 — 앞선 시도 실패: {string.Join(" / ", errors)}]\n" : "";
            // 접속하는 동안 취소됐으면 명령을 보내지 않는다
            if (ct.IsCancellationRequested) return new ExecResult(h, false, null, "", "취소되어 실행하지 않았습니다.", sw.ElapsedMilliseconds);

            using var cmd = client.CreateCommand(command);
            var r = ExecCapped(cmd, timeoutSec, ct);
            try { client.Disconnect(); } catch { }
            return new ExecResult(h, r.Failure == null && r.Exit == 0, r.Failure == null ? r.Exit : null, r.Output,
                Trim(note + JoinError(r.Failure, r.Error)).Trim(), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ExecResult(h, false, null, "", ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static string JoinError(string? failure, string error) =>
        (failure == null ? error : error.Trim().Length > 0 ? failure + "\n" + error : failure).Trim();

    /// <summary>출력(stdout+stderr)이 이만큼을 넘으면 명령을 중단한다 — 결과로는 앞뒤 일부만 돌려주므로 더 받아도 쓸모가 없다</summary>
    internal const long HardCapBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 명령을 실행하며 stdout/stderr 를 흘려 읽는다 (앞·뒷부분만 메모리에 남김 — 큰 출력도 메모리가 일정).
    /// 시간 초과·취소·출력 한도 초과·신호 종료면 Failure 에 사유를 담는다 (앞의 셋은 원격 명령에 중단 신호(SIGTERM)를 보냄).
    /// 그 밖의 오류(연결 끊김 등)는 예외로 던진다
    /// </summary>
    private static (int? Exit, string Output, string Error, string? Failure) ExecCapped(SshCommand cmd, int timeoutSec, CancellationToken ct)
    {
        cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSec);
        var stdout = new OutputCollector();
        var stderr = new OutputCollector();
        var exceeded = 0;
        // 시간 초과는 SSH.NET 이 원격 명령에 SIGTERM 을 보낸다 (CommandTimeout).
        // 사용자 취소는 토큰을 SSH.NET 에 넘기지 않고 직접 CancelAsync 를 스레드 풀에서 부른다 — 토큰을 넘기면 cts.Cancel() 을 부른 스레드
        // (실행 중 표시줄의 취소 버튼 = UI 스레드)에서 명령마다 CancelAsync 가 신호 응답을 최대 0.5초씩 기다려 서버 수만큼 화면이 멈춘다.
        // CancelAsync 는 이 명령 채널에만 신호를 보내고 닫는다 — 공유한 터미널 탭의 SSH 연결(세션)은 끊지 않는다
        var exec = cmd.ExecuteAsync();
        using var cancelReg = ct.Register(() => Task.Run(() => { try { cmd.CancelAsync(); } catch { } }));
        Task Pump(System.IO.Stream s, OutputCollector c) => Task.Factory.StartNew(() =>
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    c.Add(buf.AsSpan(0, n));
                    if (stdout.Total + stderr.Total > HardCapBytes && Interlocked.Exchange(ref exceeded, 1) == 0)
                        try { cmd.CancelAsync(); } catch { }
                }
            }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var pumps = new[] { Pump(cmd.OutputStream, stdout), Pump(cmd.ExtendedOutputStream, stderr) };
        string? failure = null;
        int? exit = null;
        try
        {
            exec.GetAwaiter().GetResult();
            exit = cmd.ExitStatus;
            // 신호로 끝나 종료 코드가 없으면 실패로 본다
            if (exit == null) failure = cmd.ExitSignal is { Length: > 0 } sig ? $"신호 {sig} 로 종료됐습니다." : "종료 코드를 받지 못했습니다.";
        }
        catch (SshOperationTimeoutException) { failure = $"{timeoutSec}초 안에 끝나지 않아 중단했습니다 (원격 명령에 중단 신호를 보냄)."; }
        catch (OperationCanceledException) when (Volatile.Read(ref exceeded) == 1)
        {
            failure = $"출력이 {HardCapBytes / 1024 / 1024}MB 를 넘어 중단했습니다 (원격 명령에 중단 신호를 보냄). 출력을 줄이는 명령으로 다시 실행하세요.";
        }
        catch (OperationCanceledException) { failure = "사용자가 취소해 중단했습니다 (원격 명령에 중단 신호를 보냄)."; }
        finally
        {
            // 명령이 끝나면(어떤 경로든) SSH.NET 이 출력 스트림을 닫아 읽기가 끝난다. 혹시 닫히지 않은 경로가 있어도
            // 읽기 스레드가 남지 않게 여기서도 닫는다 (PipeStream 은 닫힌 뒤에도 남은 데이터는 끝까지 읽힌다)
            try { cmd.OutputStream.Dispose(); cmd.ExtendedOutputStream.Dispose(); } catch { }
            try { Task.WaitAll(pumps, TimeSpan.FromSeconds(5)); } catch { }
        }
        return (exit, stdout.Text(MaxOutput), stderr.Text(MaxOutput), failure);
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
            TunneledSftpClient c;
            // 계정 정보(키 읽기 등)에서 실패해도 점프 터널을 남기지 않는다
            try { c = new TunneledSftpClient(SshAuth.Build(target, h.Username, h.Password, timeoutSec, tunnel: tunnel), tunnel); }
            catch { tunnel?.Dispose(); throw; }
            string? hostKeyError = null;
            var reachedAuth = false;
            c.HostKeyReceived += (_, e) => { reachedAuth = true; e.CanTrust = (hostKeyError = SshAuth.VerifyAcceptNew(target, e)) == null; };
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
                // 인증 단계의 시간 초과·끊김은 다른 주소로 다시 시도하지 않는다 (같은 계정으로 인증이 한 번 더 나감)
                if (SshFailure.IsPermanent(ex, reachedAuth))
                    throw new InvalidOperationException($"{addr}: 인증 단계에서 실패 — {ex.Message} (다른 주소로 재시도하지 않음)");
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

/// <summary>
/// 명령 출력 수집: 앞부분과 뒷부분만 메모리에 두고 가운데는 버린다 (몇 GB 출력도 메모리 일정).
/// 여러 스레드에서 Add · Text 를 불러도 된다
/// </summary>
internal sealed class OutputCollector(int headBytes = 8 * 1024, int tailBytes = 24 * 1024)
{
    private readonly object _gate = new();
    private readonly byte[] _head = new byte[headBytes];
    private int _headLen;
    private readonly byte[] _tail = new byte[tailBytes];
    private int _tailStart, _tailLen;
    private long _total;

    /// <summary>지금까지 받은 전체 바이트 수</summary>
    public long Total { get { lock (_gate) return _total; } }

    public void Add(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _total += data.Length;
            var n = Math.Min(data.Length, _head.Length - _headLen);
            if (n > 0) { data[..n].CopyTo(_head.AsSpan(_headLen)); _headLen += n; data = data[n..]; }
            if (data.Length == 0 || _tail.Length == 0) return;
            if (data.Length >= _tail.Length)
            {
                data[^_tail.Length..].CopyTo(_tail);
                _tailStart = 0;
                _tailLen = _tail.Length;
                return;
            }
            // 링 버퍼: 끝 위치부터 쓰고 넘치면 앞으로 감는다. 꽉 차면 가장 오래된 바이트부터 밀려난다
            var end = (_tailStart + _tailLen) % _tail.Length;
            var first = Math.Min(data.Length, _tail.Length - end);
            data[..first].CopyTo(_tail.AsSpan(end));
            data[first..].CopyTo(_tail);
            var len = _tailLen + data.Length;
            if (len > _tail.Length) { _tailStart = (_tailStart + len - _tail.Length) % _tail.Length; len = _tail.Length; }
            _tailLen = len;
        }
    }

    /// <summary>UTF-8 텍스트로 (maxChars 자 근처. 넘치면 앞 1/4 · 뒤 3/4 와 생략 표시)</summary>
    public string Text(int maxChars)
    {
        lock (_gate)
        {
            var tail = new byte[_tailLen];
            var first = Math.Min(_tailLen, _tail.Length - _tailStart);
            Array.Copy(_tail, _tailStart, tail, 0, first);
            Array.Copy(_tail, 0, tail, first, _tailLen - first);
            var skipped = _total - _headLen - _tailLen;
            if (skipped <= 0)
            {
                var all = new byte[_headLen + _tailLen];
                Array.Copy(_head, all, _headLen);
                Array.Copy(tail, 0, all, _headLen, tail.Length);
                return SshExec.ClipOutput(Encoding.UTF8.GetString(all), maxChars);
            }
            // 가운데를 버렸으면 경계에서 잘린 UTF-8 글자를 버린다 (앞부분 끝의 미완성 글자, 뒷부분 맨 앞의 이어지는 바이트)
            var head = Encoding.UTF8.GetString(_head, 0, _headLen).TrimEnd('�');
            var skip = 0;
            while (skip < tail.Length && skip < 3 && (tail[skip] & 0xC0) == 0x80) skip++;
            var tailText = Encoding.UTF8.GetString(tail, skip, tail.Length - skip);
            var headChars = maxChars / 4;
            var tailChars = maxChars - headChars;
            if (head.Length > headChars)
            {
                var k = headChars;
                if (k > 0 && char.IsHighSurrogate(head[k - 1])) k--;
                head = head[..k];
            }
            if (tailText.Length > tailChars)
            {
                var k = tailText.Length - tailChars;
                if (char.IsLowSurrogate(tailText[k])) k++;
                tailText = tailText[k..];
            }
            return head + $"\n... (출력이 길어 가운데 생략 — 전체 {_total:N0}바이트) ...\n" + tailText;
        }
    }
}
