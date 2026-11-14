using System;
using System.Security.Cryptography;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class OpenSshKnownHostsTests
{
    private static readonly string KeyB64 = TestKeys.RsaPublic.Split(' ')[1];
    private static readonly byte[] Key = Convert.FromBase64String(KeyB64);

    // 같은 형식(ssh-rsa)의 다른 키
    private static readonly string OtherB64 = new TestRsa().Public.Split(' ')[1];

    private sealed class TestRsa
    {
        public string Public { get; }
        public TestRsa()
        {
            using var rsa = RSA.Create(2048);
            var p = rsa.ExportParameters(false);
            static byte[] Mp(byte[] v) => (v[0] & 0x80) != 0 ? [0, .. v] : v;
            using var ms = new System.IO.MemoryStream();
            void B(byte[] b) { var l = BitConverter.GetBytes(b.Length); Array.Reverse(l); ms.Write(l); ms.Write(b); }
            B(Encoding.ASCII.GetBytes("ssh-rsa")); B(Mp(p.Exponent!)); B(Mp(p.Modulus!));
            Public = "ssh-rsa " + Convert.ToBase64String(ms.ToArray());
        }
    }

    private static string Hashed(string token)
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        var mac = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(token));
        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(mac)}";
    }

    [Fact]
    public void 같은_키면_신뢰한다() =>
        Assert.Equal(OpenSshKnownHosts.Verdict.Match, OpenSshKnownHosts.Check([$"web-01,10.0.0.5 ssh-rsa {KeyB64} me@pc"], "10.0.0.5", 22, Key));

    [Fact]
    public void 포트가_22_가_아니면_대괄호_형식으로_찾는다()
    {
        string[] lines = [$"[10.0.0.5]:50022 ssh-rsa {KeyB64}"];
        Assert.Equal(OpenSshKnownHosts.Verdict.Match, OpenSshKnownHosts.Check(lines, "10.0.0.5", 50022, Key));
        Assert.Equal(OpenSshKnownHosts.Verdict.Unknown, OpenSshKnownHosts.Check(lines, "10.0.0.5", 22, Key));
    }

    [Fact]
    public void 해시된_호스트도_찾는다() =>
        Assert.Equal(OpenSshKnownHosts.Verdict.Match, OpenSshKnownHosts.Check([$"{Hashed("[db.local]:2222")} ssh-rsa {KeyB64}"], "db.local", 2222, Key));

    [Fact]
    public void 같은_형식의_다른_키만_있으면_바뀐_키다() =>
        Assert.Equal(OpenSshKnownHosts.Verdict.Mismatch, OpenSshKnownHosts.Check([$"10.0.0.5 ssh-rsa {OtherB64}"], "10.0.0.5", 22, Key));

    [Fact]
    public void 다른_형식의_키만_있으면_판단하지_않는다() =>
        Assert.Equal(OpenSshKnownHosts.Verdict.Unknown,
            OpenSshKnownHosts.Check(["10.0.0.5 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDlNHCWbzj7Fy6b/lfpzgzS8V9pD1vU0zlS2I5bZ8x1Z"], "10.0.0.5", 22, Key));

    [Fact]
    public void 폐기된_키는_거부한다() =>
        Assert.Equal(OpenSshKnownHosts.Verdict.Revoked, OpenSshKnownHosts.Check([$"@revoked * ssh-rsa {KeyB64}", $"10.0.0.5 ssh-rsa {KeyB64}"], "10.0.0.5", 22, Key));

    [Theory]
    [InlineData("*.example.com", "web.example.com", true)]
    [InlineData("10.0.0.?", "10.0.0.7", true)]
    [InlineData("*.example.com,!bad.example.com", "bad.example.com", false)]
    [InlineData("Web-01", "web-01", true)]
    [InlineData("web-01", "web-011", false)]
    public void 와일드카드와_부정_패턴(string list, string token, bool expected) => Assert.Equal(expected, OpenSshKnownHosts.HostsMatch(list, token));

    [Fact]
    public void 주석_빈줄_인증기관_줄과_깨진_줄은_건너뛴다() =>
        Assert.Equal(OpenSshKnownHosts.Verdict.Match, OpenSshKnownHosts.Check(
            ["# comment", "", $"@cert-authority * ssh-rsa {OtherB64}", "10.0.0.5 ssh-rsa !!notbase64!!", "10.0.0.5", $"10.0.0.5 ssh-rsa {KeyB64}"],
            "10.0.0.5", 22, Key));
}
