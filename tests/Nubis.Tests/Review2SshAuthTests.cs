using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Nubis;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 2차 리뷰: 공개키 시도 정리(MaxAuthTries) · 호스트키 지문 형식(rsa-sha2-512 → ssh-rsa) · 다른 주소의 저장 키 비교 ·
/// 비밀번호 변경 확인 불일치 · OTP 실패 안내 · 에이전트 서명 실패 분류 · 점프 터널 리스너 닫기.
/// known_hosts.json 을 쓰므로 병렬 실행하지 않는 collection 에서 (HostFix2Tests 가 같은 파일을 잠시 망가뜨린다)
/// </summary>
[Collection("settings")]
public class Review2SshAuthTests
{
    private static PrivateKeyFile Load(string pem) => new(new MemoryStream(Encoding.UTF8.GetBytes(pem)));

    private static HostKeyEventArgs HostKey(string pem, string alg = "rsa-sha2-512")
    {
        var pk = Load(pem);
        return new HostKeyEventArgs((KeyHostAlgorithm)pk.HostKeyAlgorithms.First(a => a.Name == alg));
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}.test";

    // ----- 공개키 시도 정리 -----

    [Fact]
    public void 같은_키는_한_번만_내밀고_기본_키에서는_ssh_rsa_SHA1_을_뺀다()
    {
        using var a = Load(TestKeys.Rsa);
        using var b = Load(TestKeys.Rsa);   // 에이전트와 ~/.ssh 에 같은 키
        var picked = SshAuth.PickAlgorithms(a.HostKeyAlgorithms.Concat(b.HostKeyAlgorithms), allowSha1Rsa: false);
        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256"], picked.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void 지정한_키는_SHA2_먼저_ssh_rsa_는_맨_뒤()
    {
        using var a = Load(TestKeys.Rsa);
        // SSH.NET 의 원래 순서는 ssh-rsa 가 처음
        Assert.Equal("ssh-rsa", a.HostKeyAlgorithms.First().Name);
        var picked = SshAuth.PickAlgorithms(a.HostKeyAlgorithms, allowSha1Rsa: true);
        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"], picked.Select(x => x.Name).ToArray());

        var host = new HostEntry { Address = "h", Port = 22, Protocol = Protocol.Ssh, KeyMaterial = TestKeys.Rsa };
        var m = Assert.IsType<PrivateKeyAuthenticationMethod>(SshAuth.Build(host, "u", "").AuthenticationMethods[0]);
        var src = Assert.Single(m.KeyFiles);
        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"], src.HostKeyAlgorithms.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void 암호_걸린_키에_암호가_없으면_알아볼_수_있는_오류()
    {
        var host = new HostEntry { Address = "h", Port = 22, Protocol = Protocol.Ssh, KeyMaterial = TestKeys.RsaEncrypted };
        var ex = Assert.Throws<SshKeyLoadException>(() => SshAuth.Build(host, "u", ""));
        Assert.IsType<SshPassPhraseNullOrEmptyException>(ex.InnerException);
    }

    // 암호 걸린 PKCS#8 키는 SSH.NET 이 "암호 필요" 예외를 내지 않아 저장한 키 암호를 쓰지 못하던 문제
    [Fact]
    public void 암호_걸린_PKCS8_키도_저장한_키_암호로_연다()
    {
        var host = new HostEntry { Address = "h", Port = 22, Protocol = Protocol.Ssh, KeyMaterial = TestKeys.RsaEncrypted, KeyPassphrase = TestKeys.Passphrase };
        var m = Assert.IsType<PrivateKeyAuthenticationMethod>(SshAuth.Build(host, "u", "").AuthenticationMethods[0]);
        var blob = Assert.Single(m.KeyFiles).HostKeyAlgorithms.First().Data;
        Assert.Equal(TestKeys.RsaPublic, "ssh-rsa " + Convert.ToBase64String(blob));

        // 키 암호가 없으면 비밀번호를 키 암호로 (기존 동작)
        var viaPassword = new HostEntry { Address = "h", Port = 22, Protocol = Protocol.Ssh, KeyMaterial = TestKeys.RsaEncrypted };
        Assert.IsType<PrivateKeyAuthenticationMethod>(SshAuth.Build(viaPassword, "u", TestKeys.Passphrase).AuthenticationMethods[0]);

        // 틀린 암호는 키 오류
        var wrong = new HostEntry { Address = "h", Port = 22, Protocol = Protocol.Ssh, KeyMaterial = TestKeys.RsaEncrypted, KeyPassphrase = "wrong" };
        Assert.Throws<SshKeyLoadException>(() => SshAuth.Build(wrong, "u", ""));
    }

    // ----- 호스트키 지문 형식 -----

    [Theory]
    [InlineData("rsa-sha2-512 SHA256:abc", "ssh-rsa SHA256:abc")]
    [InlineData("rsa-sha2-256 SHA256:abc", "ssh-rsa SHA256:abc")]
    [InlineData("ssh-rsa SHA256:abc", "ssh-rsa SHA256:abc")]
    [InlineData("REJECTED rsa-sha2-512 SHA256:abc", "REJECTED ssh-rsa SHA256:abc")]
    [InlineData("rsa-sha2-512-cert-v01@openssh.com SHA256:abc", "ssh-rsa-cert-v01@openssh.com SHA256:abc")]
    [InlineData("ssh-ed25519 SHA256:abc", "ssh-ed25519 SHA256:abc")]
    [InlineData("x509 SHA256:ABCDEF", "x509 SHA256:ABCDEF")]
    [InlineData("REJECTED ", "REJECTED ")]
    [InlineData("", "")]
    public void 저장된_지문의_형식을_키_형식으로_맞춘다(string stored, string expected) =>
        Assert.Equal(expected, KnownHostStore.Normalize(stored));

    [Fact]
    public void 지문은_협상된_서명_알고리즘이_아니라_키_형식이다()
    {
        var e512 = HostKey(TestKeys.Rsa, "rsa-sha2-512");
        var e256 = HostKey(TestKeys.Rsa, "rsa-sha2-256");
        var eSha1 = HostKey(TestKeys.Rsa, "ssh-rsa");
        Assert.Equal("rsa-sha2-512", e512.HostKeyName);
        Assert.StartsWith("ssh-rsa SHA256:", SshAuth.Fingerprint(e512));
        Assert.Equal(SshAuth.Fingerprint(e512), SshAuth.Fingerprint(e256));
        Assert.Equal(SshAuth.Fingerprint(e512), SshAuth.Fingerprint(eSha1));
    }

    [Fact]
    public void 예전_형식으로_저장된_같은_키는_바뀐_키로_보지_않는다()
    {
        var e = HostKey(TestKeys.Rsa);
        var h = new HostEntry { Address = Unique("old"), Port = 22, Protocol = Protocol.Ssh };
        var id = SshAuth.HostKeyId(h);
        try
        {
            KnownHostStore.Set(id, $"rsa-sha2-512 SHA256:{e.FingerPrintSHA256}");
            Assert.Null(SshAuth.VerifyAcceptNew(h, e));

            // 다른 키는 여전히 거부
            using var other = RSA.Create(2048);
            Assert.NotNull(SshAuth.VerifyAcceptNew(h, HostKey(other.ExportRSAPrivateKeyPem())));
        }
        finally { KnownHostStore.Remove(id); }
    }

    // ----- 같은 서버의 다른 주소 -----

    private static HostEntry TwoAddressHost() =>
        new() { Address = Unique("pub"), PrivateIp = Unique("priv"), Port = 22, Protocol = Protocol.Ssh };

    [Fact]
    public void 사설_IP_로_신뢰한_키와_다른_키가_공인_IP_에서_오면_자동_신뢰하지_않는다()
    {
        var h = TwoAddressHost();
        h.PublicIp = h.Address;
        var privId = $"{h.PrivateIp}:22";
        var pubId = SshAuth.HostKeyId(h);
        using var other = RSA.Create(2048);
        try
        {
            KnownHostStore.Set(privId, SshAuth.Fingerprint(HostKey(other.ExportRSAPrivateKeyPem())));
            var err = SshAuth.VerifyAcceptNew(h, HostKey(TestKeys.Rsa));
            Assert.NotNull(err);
            Assert.Contains(privId, err);
            Assert.Null(KnownHostStore.Get(pubId));   // 저장하지 않음
        }
        finally { KnownHostStore.Remove(privId); KnownHostStore.Remove(pubId); }
    }

    [Fact]
    public void 다른_주소에_같은_키가_있으면_신뢰하고_저장한다()
    {
        var h = TwoAddressHost();
        var privId = $"{h.PrivateIp}:22";
        var pubId = SshAuth.HostKeyId(h);
        var e = HostKey(TestKeys.Rsa);
        try
        {
            KnownHostStore.Set(privId, SshAuth.Fingerprint(e));
            Assert.Equal(SshAuth.SiblingVerdict.Match, SshAuth.CheckSiblingKeys(h, SshAuth.Fingerprint(e)).Verdict);
            Assert.Null(SshAuth.VerifyAcceptNew(h, e));
            Assert.Equal(SshAuth.Fingerprint(e), KnownHostStore.Get(pubId));
        }
        finally { KnownHostStore.Remove(privId); KnownHostStore.Remove(pubId); }
    }

    [Fact]
    public void 다른_주소에서_거부한_키는_자동_신뢰하지_않는다()
    {
        var h = TwoAddressHost();
        var privId = $"{h.PrivateIp}:22";
        var pubId = SshAuth.HostKeyId(h);
        var e = HostKey(TestKeys.Rsa);
        try
        {
            KnownHostStore.Set(privId, KnownHostStore.RejectedMark(SshAuth.Fingerprint(e)));
            Assert.NotNull(SshAuth.VerifyAcceptNew(h, e));
            Assert.Null(KnownHostStore.Get(pubId));
        }
        finally { KnownHostStore.Remove(privId); KnownHostStore.Remove(pubId); }
    }

    [Fact]
    public void 다른_주소에_다른_키를_거부한_기록은_비교하지_않는다()
    {
        var h = TwoAddressHost();
        var privId = $"{h.PrivateIp}:22";
        var pubId = SshAuth.HostKeyId(h);
        using var other = RSA.Create(2048);
        try
        {
            KnownHostStore.Set(privId, KnownHostStore.RejectedMark(SshAuth.Fingerprint(HostKey(other.ExportRSAPrivateKeyPem()))));
            Assert.Equal(SshAuth.SiblingVerdict.Unknown, SshAuth.CheckSiblingKeys(h, SshAuth.Fingerprint(HostKey(TestKeys.Rsa))).Verdict);
        }
        finally { KnownHostStore.Remove(privId); KnownHostStore.Remove(pubId); }
    }

    // ----- 비밀번호 변경 · OTP 안내 -----

    [Fact]
    public void 새_비밀번호_확인이_세_번_틀리면_인증_실패가_아니라_취소다()
    {
        var asked = 0;
        var prompter = new AuthPrompter(_ => { asked++; return ["new-pass", "different"]; });
        var m = typeof(SshAuth).GetMethod("AskNewPassword", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ex = Assert.Throws<TargetInvocationException>(() => m.Invoke(null, [prompter]));
        Assert.IsType<AuthPromptCancelledException>(ex.InnerException);
        Assert.True(prompter.Cancelled);
        Assert.Equal(3, asked);
        Assert.Null(prompter.NewPassword);
    }

    [Fact]
    public void OTP_를_입력한_뒤_실패하면_입력값을_먼저_의심하게_안내한다()
    {
        const string msg = "Permission denied (publickey,keyboard-interactive).";
        var withPrompt = KbdAuth.FailureHint(msg, false, true, answeredPrompt: true);
        Assert.Contains("OTP", withPrompt);
        Assert.NotEqual(withPrompt, KbdAuth.FailureHint(msg, false, true));
        // password 만 허용하는 서버면 기존 안내
        Assert.DoesNotContain("OTP", KbdAuth.FailureHint("Permission denied (password).", false, true, answeredPrompt: true));
    }

    // ----- 에이전트 서명 실패 · 점프 터널 -----

    [Fact]
    public void 에이전트_서명_실패는_네트워크_오류가_아니라_다시_시도하지_않는_인증_실패다()
    {
        var ex = new SshAgentSignException("agent", new TimeoutException("t"));
        Assert.IsAssignableFrom<SshAuthenticationException>(ex);
        Assert.False(SshFailure.IsNetwork(ex));
        Assert.True(SshFailure.IsPermanent(ex, authStarted: false));
        Assert.False(SshFailure.CanTryPublicIp(ex, authStarted: false));
    }

    // SSH.NET 을 올리면 내부 필드 이름이 바뀌었는지 확인 (바뀌면 점프 터널 리스너가 세션 내내 열려 있게 된다)
    [Fact]
    public void 점프_터널은_SSH_NET_의_리스너_필드를_찾는다()
    {
        Assert.NotNull(JumpTunnel.ListenerField);
        Assert.Equal(typeof(System.Net.Sockets.Socket), JumpTunnel.ListenerField!.FieldType);
    }
}
