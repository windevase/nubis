using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Nubis;
using Renci.SshNet;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// ssh-agent 프로토콜: 테스트 안에서 가짜 에이전트(네임드 파이프)를 띄워 키 목록·서명을 주고받고,
/// 받은 서명을 SSH.NET 이 같은 키로 검증하는지 본다 (실제 에이전트·사용자 키는 쓰지 않음).
/// </summary>
[Collection("ssh-agent")]
public class SshAgentTests
{
    [Fact]
    public async Task 에이전트의_키로_만든_서명을_SSH_NET_이_검증한다()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(TestKeys.Rsa);
        var blob = Convert.FromBase64String(TestKeys.RsaPublic.Split(' ')[1]);
        var pipe = "nubis-test-agent-" + Guid.NewGuid().ToString("N");
        var server = Task.Run(() => FakeAgent(pipe, rsa, blob, requests: 2));

        var old = SshAgent.PipeOverride;
        SshAgent.PipeOverride = pipe;
        try
        {
            var keys = SshAgent.ListKeys(5000);
            var key = Assert.Single(keys);
            Assert.Equal("ssh-rsa", key.Type);
            Assert.Equal("test@nubis", key.Comment);
            Assert.Equal(blob, key.Blob);

            var data = Encoding.UTF8.GetBytes("session-id + userauth request");
            var sig = SshAgent.Sign(blob, data, 4);   // rsa-sha2-512

            // 같은 키를 SSH.NET 으로 읽어 그 서명을 검증
            using var pk = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(TestKeys.Rsa)));
            var alg = pk.HostKeyAlgorithms.First(a => a.Name == "rsa-sha2-512");
            Assert.True(alg.VerifySignature(data, sig));
            Assert.Equal(alg.Data, blob);
        }
        finally { SshAgent.PipeOverride = old; }
        await server;
    }

    /// <summary>요청 n 개에 답하는 가짜 에이전트 (11 → 12 키 목록, 13 → 14 rsa-sha2-512 서명)</summary>
    private static void FakeAgent(string name, RSA rsa, byte[] blob, int requests)
    {
        for (var i = 0; i < requests; i++)
        {
            using var s = new NamedPipeServerStream(name, PipeDirection.InOut, 1);
            s.WaitForConnection();
            var len = U32(Read(s, 4), 0);
            var req = Read(s, (int)len);
            using var reply = new MemoryStream();
            if (req[0] == 11)
            {
                reply.WriteByte(12);
                W32(reply, 1);
                WBytes(reply, blob);
                WBytes(reply, Encoding.UTF8.GetBytes("test@nubis"));
            }
            else
            {
                // 13: string blob, string data, uint32 flags
                var blobLen = (int)U32(req, 1);
                var dataLen = (int)U32(req, 5 + blobLen);
                var data = req.AsSpan(9 + blobLen, dataLen).ToArray();
                var raw = rsa.SignData(data, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
                using var sig = new MemoryStream();
                WBytes(sig, Encoding.ASCII.GetBytes("rsa-sha2-512"));
                WBytes(sig, raw);
                reply.WriteByte(14);
                WBytes(reply, sig.ToArray());
            }
            var body = reply.ToArray();
            var frame = new MemoryStream();
            W32(frame, (uint)body.Length);
            frame.Write(body);
            s.Write(frame.ToArray());
            s.Flush();
            s.WaitForPipeDrain();
        }
    }

    private static byte[] Read(Stream s, int n) { var b = new byte[n]; s.ReadExactly(b); return b; }
    private static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    private static void W32(Stream s, uint v) => s.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
    private static void WBytes(Stream s, byte[] b) { W32(s, (uint)b.Length); s.Write(b); }
}
