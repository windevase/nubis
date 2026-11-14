using Nubis;
using Xunit;

namespace Nubis.Tests;

public class KbdAuthTests
{
    [Theory]
    [InlineData("Password: ", true)]
    [InlineData("(current) UNIX password: ", true)]
    [InlineData("ubuntu@web-01's password: ", true)]
    [InlineData("Passwort: ", true)]
    [InlineData("비밀번호: ", true)]
    [InlineData("암호:", true)]
    [InlineData("Verification code: ", false)]
    [InlineData("One-time password (OATH) for `user': ", false)]
    [InlineData("OTP: ", false)]
    [InlineData("Password + Token: ", false)]
    [InlineData("Enter PASSCODE: ", false)]
    [InlineData("New password: ", false)]
    [InlineData("Retype new password: ", false)]
    [InlineData("새 비밀번호: ", false)]
    [InlineData("비밀번호 확인: ", false)]
    [InlineData("인증번호: ", false)]
    [InlineData("Duo two-factor login. Passcode or option (1-2): ", false)]
    public void 로그인_비밀번호_질문만_고른다(string prompt, bool expected) =>
        Assert.Equal(expected, KbdAuth.IsPasswordPrompt(prompt));

    [Fact]
    public void 비밀번호는_한_번만_자동으로_답한다()
    {
        var state = new KbdAuth.State();
        var first = KbdAuth.AutoFill(["Password: "], "pw", state);
        Assert.Equal("pw", first[0]);
        // 서버가 다시 물으면 틀렸다는 뜻 — 같은 비밀번호를 또 보내지 않는다 (계정 잠금 방지)
        var again = KbdAuth.AutoFill(["Password: "], "pw", state);
        Assert.Null(again[0]);
    }

    [Fact]
    public void OTP_질문은_자동으로_답하지_않는다()
    {
        var state = new KbdAuth.State();
        var r = KbdAuth.AutoFill(["Password: ", "Verification code: "], "pw", state);
        Assert.Equal("pw", r[0]);
        Assert.Null(r[1]);

        var otpOnly = KbdAuth.AutoFill(["Verification code: "], "pw", new KbdAuth.State());
        Assert.Null(otpOnly[0]);
    }

    [Fact]
    public void 비밀번호가_없으면_자동_응답하지_않는다()
    {
        var state = new KbdAuth.State();
        Assert.Null(KbdAuth.AutoFill(["Password: "], "", state)[0]);
        Assert.False(state.PasswordUsed);
    }

    [Fact]
    public void 비대화형_오류는_원인을_구분한다()
    {
        Assert.Contains("틀렸을 수", KbdAuth.NonInteractiveError("Password: ", passwordUsed: true));
        Assert.Contains("OTP", KbdAuth.NonInteractiveError("Verification code: ", passwordUsed: true));
    }

    [Theory]
    [InlineData("Permission denied (publickey).", false, true, "Vault")]
    [InlineData("Permission denied (publickey).", true, false, "authorized_keys")]
    [InlineData("Permission denied (keyboard-interactive).", false, true, "잠")]
    [InlineData("No suitable authentication method found to complete authentication (publickey,keyboard-interactive).", false, true, "잠")]
    [InlineData("Permission denied (gssapi-with-mic).", false, true, "gssapi-with-mic")]
    public void 실패_원인_안내(string message, bool hasKey, bool hasPassword, string expectedPart) =>
        Assert.Contains(expectedPart, KbdAuth.FailureHint(message, hasKey, hasPassword));

    // SSH.NET 업데이트로 내부 Session 속성이 바뀌면 SFTP 가 터미널 연결을 공유하지 못하고 별도 연결(재인증)로 넘어간다
    [Fact]
    public void SFTP_세션_공유가_가능한_SSH_NET_버전이다() => Assert.True(SharedSftpClient.Supported);

    [Fact]
    public void 허용_방식이_없는_메시지는_안내하지_않는다() =>
        Assert.Null(KbdAuth.FailureHint("인증 입력이 취소되었습니다.", false, true));
}
