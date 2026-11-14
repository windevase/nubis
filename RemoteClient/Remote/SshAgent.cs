using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Nubis;

/// <summary>
/// ssh-agent 의 키로 인증 (Windows OpenSSH 에이전트: \\.\pipe\openssh-ssh-agent, 또는 SSH_AUTH_SOCK 가 가리키는 파이프).
/// 개인키는 에이전트 밖으로 나오지 않는다 — 공개키 목록을 받고, 서명이 필요할 때 에이전트에 서명을 맡긴다.
/// 프로토콜 (draft-miller-ssh-agent): uint32 길이 + byte 종류 + 내용.
///   11 REQUEST_IDENTITIES → 12 IDENTITIES_ANSWER (uint32 개수, (string 공개키 blob, string 설명)*)
///   13 SIGN_REQUEST (string 공개키 blob, string 데이터, uint32 플래그) → 14 SIGN_RESPONSE (string 서명) / 5 FAILURE
/// </summary>
public static class SshAgent
{
    private const byte RequestIdentities = 11, IdentitiesAnswer = 12, SignRequest = 13, SignResponse = 14;
    private const uint RsaSha256 = 2, RsaSha512 = 4;

    /// <summary>에이전트 파이프 (null 이면 SSH_AUTH_SOCK 의 파이프 → openssh-ssh-agent). 테스트가 없는 이름으로 바꾼다</summary>
    public static string? PipeOverride { get; set; }

    private static string PipeName
    {
        get
        {
            if (PipeOverride != null) return PipeOverride;
            var sock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
            const string prefix = @"\\.\pipe\";
            return sock != null && sock.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? sock[prefix.Length..] : "openssh-ssh-agent";
        }
    }

    public sealed record Key(byte[] Blob, string Type, string Comment);

    /// <summary>키 목록 요청의 대기 시간 (연결·쓰기·읽기 전체) — 잠겼거나 응답하지 않는 에이전트에 멈추지 않게</summary>
    public const int ListTimeoutMs = 1000;
    /// <summary>서명 요청의 대기 시간 — 하드웨어 키(FIDO)·확인 요청이 있는 에이전트는 사용자가 누를 때까지 기다린다</summary>
    public const int SignTimeoutMs = 60_000;

    /// <summary>
    /// 에이전트 파이프가 있는가 (연결하지 않고 파이프 목록만 본다 — 빠름). 알 수 없으면 true (연결해 보게).
    /// 파이프가 없을 때 연결 시도는 대기 시간을 다 채우므로 먼저 확인한다.
    /// </summary>
    public static bool PipeExists()
    {
        var name = PipeName;
        if (name.IndexOfAny(['\\', '/']) >= 0) return true;   // 하위 경로가 있는 이름은 목록과 비교하기 어렵다
        try
        {
            foreach (var p in Directory.EnumerateFiles(@"\\.\pipe\"))
                if (string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return true; }
    }

    /// <summary>에이전트에 올라간 키 목록. 에이전트가 없거나 응답이 없으면 빈 목록 (예외 없음)</summary>
    public static IReadOnlyList<Key> ListKeys(int timeoutMs = ListTimeoutMs)
    {
        try
        {
            var reply = Request([RequestIdentities], timeoutMs);
            if (reply.Length == 0 || reply[0] != IdentitiesAnswer) return [];
            var r = new Reader(reply, 1);
            var count = r.UInt32();
            var keys = new List<Key>();
            for (var i = 0; i < count && i < 100; i++)
            {
                var blob = r.Bytes();
                var comment = Encoding.UTF8.GetString(r.Bytes());
                keys.Add(new Key(blob, new Reader(blob, 0).String(), comment));
            }
            return keys;
        }
        catch { return []; }
    }

    /// <summary>키 소스: 에이전트의 키마다 서명 알고리즘(HostAlgorithm). 키가 없으면 null</summary>
    public static IPrivateKeySource? KeySource()
    {
        if (!PipeExists()) return null;   // 에이전트가 없으면 대기 없이
        var keys = ListKeys();
        if (keys.Count == 0) return null;
        var algs = new List<HostAlgorithm>();
        foreach (var k in keys)
            foreach (var (name, flags) in SignatureAlgorithms(k.Type))
                algs.Add(new AgentHostAlgorithm(name, k.Blob, flags));
        return algs.Count == 0 ? null : new AgentKeySource(algs);
    }

    /// <summary>키 형식별로 서버에 내밀 서명 알고리즘. RSA 는 SHA-2 만 (ssh-rsa = SHA-1 은 OpenSSH 도 기본으로 끔)</summary>
    public static IEnumerable<(string Name, uint Flags)> SignatureAlgorithms(string keyType) => keyType switch
    {
        "ssh-rsa" => [("rsa-sha2-512", RsaSha512), ("rsa-sha2-256", RsaSha256)],
        "ssh-rsa-cert-v01@openssh.com" => [("rsa-sha2-512-cert-v01@openssh.com", RsaSha512), ("rsa-sha2-256-cert-v01@openssh.com", RsaSha256)],
        "ssh-dss" or "ssh-dss-cert-v01@openssh.com" => [],
        _ => [(keyType, 0u)],   // ssh-ed25519, ecdsa-sha2-*, sk-*, 그 인증서
    };

    /// <summary>에이전트에 서명을 맡긴다. 돌려주는 값은 SSH 서명 형식 (string 알고리즘 + string 서명) — SSH.NET 의 Sign 과 같다</summary>
    public static byte[] Sign(byte[] keyBlob, byte[] data, uint flags)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(SignRequest);
        WriteBytes(ms, keyBlob);
        WriteBytes(ms, data);
        WriteUInt32(ms, flags);
        // 하드웨어 키(FIDO)·확인 요청이 있는 에이전트는 사용자가 누를 때까지 기다린다
        var reply = Request(ms.ToArray(), SignTimeoutMs);
        if (reply.Length == 0 || reply[0] != SignResponse) throw new InvalidOperationException("ssh-agent 가 서명을 거부했습니다.");
        return new Reader(reply, 1).Bytes();
    }

    /// <summary>
    /// 요청 하나 (연결 → 쓰기 → 응답 읽기). timeoutMs 는 연결만이 아니라 읽기까지 전체에 적용한다 —
    /// 잠긴 에이전트·확인을 기다리는 에이전트가 답하지 않아도 무한히 멈추지 않게 (UI 스레드에서도 불림).
    /// </summary>
    private static byte[] Request(byte[] body, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var task = RequestAsync(pipe, body, cts.Token);
        // 취소가 파이프 입출력에 전달되지 않는 경우에도 기다림을 끝낸다 (pipe 는 using 으로 닫혀 남은 입출력이 끝남)
        if (Task.WhenAny(task, Task.Delay(timeoutMs + 500)).GetAwaiter().GetResult() != task)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException("ssh-agent 가 응답하지 않습니다.");
        }
        try { return task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw new TimeoutException("ssh-agent 가 응답하지 않습니다."); }
    }

    private static async Task<byte[]> RequestAsync(NamedPipeClientStream pipe, byte[] body, CancellationToken ct)
    {
        await pipe.ConnectAsync(ct).ConfigureAwait(false);
        var frame = new MemoryStream();
        WriteUInt32(frame, (uint)body.Length);
        frame.Write(body);
        await pipe.WriteAsync(frame.ToArray(), ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
        var head = new byte[4];
        await pipe.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        var len = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
        if (len > 256 * 1024) throw new InvalidDataException("ssh-agent 응답이 너무 깁니다.");
        var buf = new byte[len];
        await pipe.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf;
    }

    private static void WriteUInt32(Stream s, uint v) => s.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
    private static void WriteBytes(Stream s, byte[] b) { WriteUInt32(s, (uint)b.Length); s.Write(b); }

    private sealed class Reader(byte[] buf, int pos)
    {
        public uint UInt32()
        {
            if (pos + 4 > buf.Length) throw new InvalidDataException();
            var v = (uint)(buf[pos] << 24 | buf[pos + 1] << 16 | buf[pos + 2] << 8 | buf[pos + 3]);
            pos += 4;
            return v;
        }

        public byte[] Bytes()
        {
            var n = (int)UInt32();
            if (n < 0 || pos + n > buf.Length) throw new InvalidDataException();
            var b = buf.AsSpan(pos, n).ToArray();
            pos += n;
            return b;
        }

        public string String() => Encoding.ASCII.GetString(Bytes());
    }

    private sealed class AgentKeySource(IReadOnlyCollection<HostAlgorithm> algs) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = algs;
    }

    /// <summary>개인키 없이 에이전트에 서명을 맡기는 알고리즘. Data = 공개키 blob</summary>
    private sealed class AgentHostAlgorithm(string name, byte[] blob, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => blob;
        public override byte[] Sign(byte[] data) => SshAgent.Sign(blob, data, flags);
        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
    }
}
