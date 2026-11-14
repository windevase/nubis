using System;
using System.Collections.Generic;
using Nubis;
using Renci.SshNet.Common;
using Xunit;

namespace Nubis.Tests;

public class SshAuthFix2Tests
{
    // 화면에 보이는 질문이라도 OTP·코드는 기억하지 않는다 — 고정 선택지(Push = 1)·안내 질문만
    [Theory]
    [InlineData("Choose [1] Push, or [2] TOTP:", true, "1", true)]
    [InlineData("Passcode or option (1-3):", true, "1", true)]          // Duo 선택지 번호
    [InlineData("Passcode or option (1-3):", true, "123456", false)]    // Duo 패스코드
    [InlineData("Verification code:", true, "482913", false)]
    [InlineData("Enter OTP:", true, "abc", false)]
    [InlineData("Token:", true, "7", true)]
    [InlineData("Select region:", true, "tokyo", true)]
    [InlineData("Select region:", true, "1234567", false)]               // 6자리 이상 숫자는 질문과 상관없이
    [InlineData("Press enter to continue", false, "", true)]
    [InlineData("Password:", false, "secret", false)]                    // 가려진 질문의 값
    [InlineData("Password:", true, "secret", false)]                     // 보이게 묻는 비밀번호도
    public void CanRemember_OTP와_비밀값은_기억하지_않는다(string prompt, bool echoed, string answer, bool expected) =>
        Assert.Equal(expected, KbdAuth.CanRemember(prompt, echoed, answer));

    [Theory]
    [InlineData("New password:", true, false)]
    [InlineData("Enter new UNIX password:", true, false)]
    [InlineData("새 비밀번호:", true, false)]
    [InlineData("Retype new password:", false, true)]
    [InlineData("Re-enter new password:", false, true)]
    [InlineData("새 비밀번호 확인:", false, true)]
    [InlineData("(current) UNIX password:", false, false)]
    [InlineData("Verification code:", false, false)]
    public void 새_비밀번호_질문_구분(string prompt, bool isNew, bool isConfirm)
    {
        Assert.Equal(isNew, KbdAuth.IsNewPasswordPrompt(prompt));
        Assert.Equal(isConfirm, KbdAuth.IsConfirmPasswordPrompt(prompt));
    }

    // PAM 은 새 비밀번호와 확인을 다른 라운드로 묻기도 한다 — State 로 이어서 확정
    [Fact]
    public void TrackNewPassword_라운드가_나뉘어도_같은_값이면_확정한다()
    {
        var state = new KbdAuth.State();
        Assert.Null(KbdAuth.TrackNewPassword(state, [("(current) UNIX password:", "old")]));
        Assert.Null(KbdAuth.TrackNewPassword(state, [("New password:", "N3w!pass")]));
        Assert.Equal("N3w!pass", KbdAuth.TrackNewPassword(state, [("Retype new password:", "N3w!pass")]));
    }

    [Fact]
    public void TrackNewPassword_한_번에_와도_확정한다()
    {
        var state = new KbdAuth.State();
        Assert.Equal("abc", KbdAuth.TrackNewPassword(state, [("새 비밀번호:", "abc"), ("새 비밀번호 확인:", "abc")]));
    }

    [Fact]
    public void TrackNewPassword_값이_다르거나_비면_확정하지_않는다()
    {
        var state = new KbdAuth.State();
        Assert.Null(KbdAuth.TrackNewPassword(state, [("New password:", "a1"), ("Retype new password:", "a2")]));
        var empty = new KbdAuth.State();
        Assert.Null(KbdAuth.TrackNewPassword(empty, [("New password:", ""), ("Retype new password:", "")]));
        // 확인 질문만 오면(새 비밀번호를 모름) 확정하지 않는다
        Assert.Null(KbdAuth.TrackNewPassword(new KbdAuth.State(), [("Retype new password:", "x")]));
    }

    // 비밀번호·OTP 답을 보낸 뒤(인증 시작)의 시간 초과·끊김은 네트워크 오류로 보지 않는다 — 공인 IP 재시도·자동 재연결 금지
    // (SshSftpFix3: 기준이 "호스트키를 받음" 에서 "인증 시작(AuthStarted)" 으로 바뀌어 인자 이름을 authStarted 로)
    [Fact]
    public void 인증_단계의_시간초과는_재시도하지_않는다()
    {
        var timeout = new SshOperationTimeoutException("timeout");
        var dropped = new SshConnectionException("Client not connected.");
        Assert.True(SshFailure.IsNetwork(timeout));
        Assert.True(SshFailure.CanTryPublicIp(timeout, authStarted: false));
        Assert.False(SshFailure.IsPermanent(timeout, authStarted: false));

        Assert.False(SshFailure.CanTryPublicIp(timeout, authStarted: true));
        Assert.True(SshFailure.IsPermanent(timeout, authStarted: true));
        Assert.True(SshFailure.IsPermanent(dropped, authStarted: true));
    }

    [Fact]
    public void 인증_실패와_키_오류는_항상_영구_실패다()
    {
        Assert.True(SshFailure.IsPermanent(new SshAuthenticationException("Permission denied (password)."), false));
        var keyError = new SshKeyLoadException("bad key", new SshPassPhraseNullOrEmptyException("pp"));
        Assert.True(SshFailure.IsPermanent(keyError, false));
        Assert.False(SshFailure.CanTryPublicIp(keyError, false));
        Assert.IsAssignableFrom<InvalidOperationException>(keyError);   // 기존 호출하는 쪽이 그대로 잡는다
    }

    [Theory]
    [InlineData(false, false, JumpFailure.Network)]
    [InlineData(false, true, JumpFailure.AuthPhase)]
    [InlineData(true, true, JumpFailure.HostKey)]
    public void 점프_서버_실패_종류(bool keyRejected, bool keyReceived, JumpFailure expected)
    {
        Assert.Equal(expected, SshFailure.JumpKind(new SshConnectionException("x"), keyRejected, keyReceived));
        Assert.Equal(expected == JumpFailure.Network, !new JumpTunnelException("x", expected).IsPermanent);
    }

    [Fact]
    public void 점프_서버_인증_실패는_호스트키를_받았어도_Auth() =>
        Assert.Equal(JumpFailure.Auth, SshFailure.JumpKind(new SshAuthenticationException("denied"), false, true));

    // 키 암호가 없는 등 로컬 키를 읽지 못하면 연결 실패가 아닌 키 오류로 구분된다 (자동 재연결하지 않음)
    [Fact]
    public void 키를_읽지_못하면_SshKeyLoadException()
    {
        var host = new HostEntry { Address = "h", Port = 22, Protocol = Protocol.Ssh, KeyMaterial = TestKeys.RsaEncrypted };
        Assert.Throws<SshKeyLoadException>(() => SshAuth.Build(host, "u", ""));
    }

    // 기억한 답으로 실패한 다음 시도는 기억한 답을 쓰지 않는다
    [Fact]
    public void SkipRemembered_이면_기억한_답을_쓰지_않는다()
    {
        var remembered = new Dictionary<string, string> { ["Choose:"] = "1" };
        var prompter = new AuthPrompter(_ => null, remembered: remembered);
        Assert.Equal("1", prompter.Remembered("Choose:"));
        prompter.SkipRemembered = true;
        Assert.Null(prompter.Remembered("Choose:"));
    }
}
