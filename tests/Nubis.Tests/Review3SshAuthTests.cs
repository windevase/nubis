using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Nubis;
using Renci.SshNet;
using Renci.SshNet.Common;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 3차 리뷰: 서버 배너에 따른 ssh-rsa(SHA-1) 정책(옛 서버 호환) · 암호 걸린 기본 키 안내 · 저장값 null 처리 · SSH 예외 설명.
/// SshAgent.PipeOverride · SshAuth.DefaultKeyDir 를 바꾸므로 같은 collection 에서
/// </summary>
[Collection("ssh-agent")]
public class Review3SshAuthTests
{
    private static PrivateKeyFile Load(string pem) => new(new MemoryStream(Encoding.UTF8.GetBytes(pem)));

    private static void WithKeyDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Nubis.Tests.sshdir", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var old = SshAuth.DefaultKeyDir;
        var oldPipe = SshAgent.PipeOverride;
        SshAuth.DefaultKeyDir = dir;
        SshAgent.PipeOverride = "nubis-tests-no-agent-" + Guid.NewGuid().ToString("N");   // 이 PC 의 실제 ssh-agent 를 쓰지 않게
        try { body(dir); }
        finally
        {
            SshAuth.DefaultKeyDir = old;
            SshAgent.PipeOverride = oldPipe;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void SetServerVersion(ConnectionInfo ci, string version)
    {
        var setter = typeof(ConnectionInfo).GetProperty("ServerVersion")!.GetSetMethod(nonPublic: true);
        Assert.NotNull(setter);
        setter!.Invoke(ci, [version]);
    }

    // ----- ssh-rsa(SHA-1) 정책 -----

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13", false, SshAuth.Sha1Rsa.Drop)]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13", true, SshAuth.Sha1Rsa.Last)]
    [InlineData("SSH-2.0-OpenSSH_10.0", false, SshAuth.Sha1Rsa.Drop)]
    [InlineData("SSH-2.0-OpenSSH_7.2p2", false, SshAuth.Sha1Rsa.Drop)]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", false, SshAuth.Sha1Rsa.Drop)]
    [InlineData("SSH-2.0-OpenSSH_7.1", false, SshAuth.Sha1Rsa.First)]
    [InlineData("SSH-2.0-OpenSSH_5.3", true, SshAuth.Sha1Rsa.First)]
    [InlineData("SSH-2.0-dropbear_2019.78", false, SshAuth.Sha1Rsa.Last)]
    [InlineData("SSH-2.0-Cisco-1.25", false, SshAuth.Sha1Rsa.Last)]
    [InlineData(null, false, SshAuth.Sha1Rsa.Last)]
    public void 서버_배너로_ssh_rsa_정책을_정한다(string? banner, bool specified, SshAuth.Sha1Rsa expected) =>
        Assert.Equal(expected, SshAuth.Sha1RsaFor(banner, specified));

    [Fact]
    public void 옛_서버면_ssh_rsa_를_먼저_내민다()
    {
        using var a = Load(TestKeys.Rsa);
        var picked = SshAuth.PickAlgorithms(a.HostKeyAlgorithms, SshAuth.Sha1Rsa.First);
        Assert.Equal(["ssh-rsa", "rsa-sha2-512", "rsa-sha2-256"], picked.Select(x => x.Name).ToArray());
    }

    // 2차 점검에서 기본 키의 ssh-rsa 를 아예 빼 버려 기본 id_rsa 로 붙던 옛 서버(OpenSSH 7.2 미만·네트워크 장비)가 안 되던 문제
    [Fact]
    public void 기본_키는_서버_배너를_보고_ssh_rsa_를_고른다() => WithKeyDir(dir =>
    {
        File.WriteAllText(Path.Combine(dir, "id_rsa"), TestKeys.Rsa);
        var ci = SshAuth.Build(new HostEntry { Address = "h", Port = 22, Protocol = Protocol.Ssh }, "u", "pw");
        var m = Assert.IsType<PrivateKeyAuthenticationMethod>(ci.AuthenticationMethods[0]);
        var src = Assert.Single(m.KeyFiles);

        // 배너를 아직 모르면 SHA-2 먼저, ssh-rsa 맨 뒤
        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"], src.HostKeyAlgorithms.Select(x => x.Name).ToArray());

        SetServerVersion(ci, "SSH-2.0-OpenSSH_9.6");
        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256"], src.HostKeyAlgorithms.Select(x => x.Name).ToArray());

        SetServerVersion(ci, "SSH-2.0-OpenSSH_5.3");
        Assert.Equal(["ssh-rsa", "rsa-sha2-512", "rsa-sha2-256"], src.HostKeyAlgorithms.Select(x => x.Name).ToArray());
    });

    [Fact]
    public void 지정한_키는_새_OpenSSH_에도_ssh_rsa_를_맨_뒤에_남긴다()
    {
        var host = new HostEntry { Address = "h", Port = 22, Protocol = Protocol.Ssh, KeyMaterial = TestKeys.Rsa };
        var ci = SshAuth.Build(host, "u", "");
        var src = Assert.Single(Assert.IsType<PrivateKeyAuthenticationMethod>(ci.AuthenticationMethods[0]).KeyFiles);
        SetServerVersion(ci, "SSH-2.0-OpenSSH_9.6");
        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"], src.HostKeyAlgorithms.Select(x => x.Name).ToArray());
    }

    // ----- 암호 걸린 기본 키 안내 -----

    [Fact]
    public void 암호_걸린_기본_키는_건너뛰고_이름을_알려_준다() => WithKeyDir(dir =>
    {
        File.WriteAllText(Path.Combine(dir, "id_ed25519"), TestKeys.RsaEncrypted);   // 암호 걸린 PKCS#8
        File.WriteAllText(Path.Combine(dir, "id_rsa"), TestKeys.Rsa);
        var encrypted = new List<string>();
        Assert.Single(SshAuth.LoadDefaultKeys(encrypted));
        Assert.Equal(["id_ed25519"], encrypted.ToArray());

        var skipped = SshAuth.SkippedEncryptedDefaultKeys();
        Assert.Equal(["id_ed25519"], skipped.ToArray());
        Assert.Contains("id_ed25519", SshAuth.SkippedKeysHint(skipped));
        Assert.Null(SshAuth.SkippedKeysHint([]));
    });

    [Fact]
    public void 암호_걸린_OpenSSH_형식_기본_키도_건너뛴_것으로_센다() => WithKeyDir(dir =>
    {
        if (TestKeys.Ed25519(TestKeys.Passphrase) is not { } k) return;   // ssh-keygen 이 없는 환경
        File.WriteAllText(Path.Combine(dir, "id_ed25519"), k.Private);
        var encrypted = new List<string>();
        Assert.Empty(SshAuth.LoadDefaultKeys(encrypted));
        Assert.Equal(["id_ed25519"], encrypted.ToArray());
    });

    // ----- 저장된 호스트키 -----

    [Fact]
    public void 저장값이_null_이어도_예외_없이_null()
    {
        Assert.Null(KnownHostStore.Normalize(null!));
        Assert.Equal("", KnownHostStore.Normalize(""));
        // 서로 다른 지문은 형식을 맞춰도 같아지지 않는다
        Assert.NotEqual(KnownHostStore.Normalize("rsa-sha2-512 SHA256:aaa"), KnownHostStore.Normalize("ssh-rsa SHA256:bbb"));
        // 거부 표시는 형식만 맞추고 거부 표시는 유지한다
        Assert.True(KnownHostStore.IsRejected(KnownHostStore.Normalize("REJECTED rsa-sha2-256 SHA256:aaa")));
        // 저장된 호스트키를 읽지 못함 표시는 그대로
        Assert.Equal(KnownHostStore.UnavailableMark, KnownHostStore.Normalize(KnownHostStore.UnavailableMark));
    }

    // ----- SSH 예외 설명 -----

    [Fact]
    public void SSH_예외를_알아볼_수_있는_설명으로_바꾼다()
    {
        var refused = SshErrors.Describe(new SocketException((int)SocketError.ConnectionRefused));
        Assert.Equal(L.T("서버가 연결을 거부했습니다 (포트가 닫혔거나 SSH 서비스가 꺼져 있음)."), refused);
        Assert.Equal(L.T("연결 시간이 초과되었습니다 (주소·방화벽·보안 그룹·VPN 을 확인하세요)."),
            SshErrors.Describe(new SocketException((int)SocketError.TimedOut)));
        // 감싼 예외는 안쪽을 본다
        Assert.Equal(refused, SshErrors.Describe(new AggregateException(new SocketException((int)SocketError.ConnectionRefused))));
        Assert.Equal(L.T("서버가 연결을 거부했습니다 (포트가 닫혔거나 SSH 서비스가 꺼져 있음)."),
            SshErrors.Describe(new InvalidOperationException("x", new SocketException((int)SocketError.ConnectionRefused))));

        // 앱이 만든 설명은 그대로
        Assert.Equal("키 오류", SshErrors.Describe(new SshKeyLoadException("키 오류", new Exception())));
        Assert.Equal("취소", SshErrors.Describe(new AuthPromptCancelledException("취소")));

        // 서버의 인증 실패 메시지는 괄호 안에 남긴다
        Assert.Contains("Permission denied (password).", SshErrors.Describe(new SshAuthenticationException("Permission denied (password).")));
        Assert.Contains("Key exchange negotiation failed.",
            SshErrors.Describe(new SshConnectionException("Key exchange negotiation failed.", Renci.SshNet.Messages.Transport.DisconnectReason.KeyExchangeFailed)));
        Assert.Equal(L.T("서버 응답 시간이 초과되었습니다."), SshErrors.Describe(new SshOperationTimeoutException("t")));

        // 모르는 예외는 원래 메시지 첫 줄
        Assert.Equal("first", SshErrors.Describe(new IOException("first\r\nsecond")));
    }
}
