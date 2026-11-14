using System;
using System.IO;
using Nubis;
using Renci.SshNet.Common;
using Xunit;

namespace Nubis.Tests;

public class SshSftpFix3Tests
{
    // 바꿔치기 업로드는 내 파일(소유자·그룹이 임시 파일과 같음)에만 — 남의 파일은 원래 파일에 바로 덮어쓴다
    [Theory]
    [InlineData(1000, 1000, 1000, 1000, true)]
    [InlineData(33, 33, 1000, 1000, false)]     // www-data 소유 파일을 그룹 쓰기로 편집
    [InlineData(1000, 33, 1000, 1000, false)]   // 그룹을 맞추지 못함
    [InlineData(0, 0, 1000, 1000, false)]       // root 파일
    public void CanSwapReplace_내_파일만_바꿔치기(int oldUid, int oldGid, int tmpUid, int tmpGid, bool expected) =>
        Assert.Equal(expected, SftpPanel.CanSwapReplace(oldUid, oldGid, tmpUid, tmpGid));

    // 공개키만 써 본 시도의 끊김·시간 초과는 다시 시도해도 된다 (재부팅 중 pam_nologin, VPN 순단)
    [Fact]
    public void 인증을_시작하지_않았으면_네트워크_실패로_다시_시도()
    {
        var dropped = new SshConnectionException("Connection closed by remote host.");
        Assert.False(SshFailure.IsPermanent(dropped, authStarted: false));
        Assert.True(SshFailure.CanTryPublicIp(dropped, authStarted: false));
        // 인증 실패는 인증을 시작하지 않았다고 보더라도 다시 시도하지 않는다
        Assert.True(SshFailure.IsPermanent(new SshAuthenticationException("Permission denied (publickey)."), false));
    }

    // 점프 서버: 키를 신뢰했지만 인증 전(확인 창 대기 중 키 교환 시간 초과)은 네트워크, 인증을 시작한 뒤는 AuthPhase
    [Fact]
    public void 점프_서버_인증_전_실패는_네트워크()
    {
        Assert.Equal(JumpFailure.Network, SshFailure.JumpKind(new SshOperationTimeoutException("kex timeout"), false, false));
        Assert.Equal(JumpFailure.AuthPhase, SshFailure.JumpKind(new SshOperationTimeoutException("push timeout"), false, true));
    }

    // Duo "Passcode or option" 의 선택지 단어(push·sms·phone)는 기억한다 — 코드·비밀번호는 여전히 기억하지 않음
    [Theory]
    [InlineData("Passcode or option (1-3):", "push", true)]
    [InlineData("Passcode or option (1-3):", "sms", true)]
    [InlineData("Passcode or option (1-3):", "phone", true)]
    [InlineData("Passcode or option (1-3):", "2", true)]
    [InlineData("Passcode or option (1-3):", "123", false)]          // 3자리 이상 숫자
    [InlineData("Passcode or option (1-3):", "123456", false)]
    [InlineData("Passcode or option (1-3):", "toolongword", false)]  // 8자 초과
    [InlineData("Passcode or option (1-3):", "ab12", false)]         // 글자만
    [InlineData("Enter OTP:", "push", false)]                        // 선택지 질문이 아님
    [InlineData("Password or option:", "secret", false)]             // 비밀번호 질문
    public void CanRemember_선택지_단어(string prompt, string answer, bool expected) =>
        Assert.Equal(expected, KbdAuth.CanRemember(prompt, true, answer));

    // 권한 비트를 안 보내는 서버: 종류를 모르는 항목은 일반 파일로 받는다
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    public void IsSpecialFile_명시된_특수_파일만(bool socket, bool fifo, bool chr, bool blk, bool expected) =>
        Assert.Equal(expected, SftpPanel.IsSpecialFile(socket, fifo, chr, blk));

    [Fact]
    public void EditUploadTimeout_기본_2분에_크기_비례()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), SftpPanel.EditUploadTimeout(0));
        Assert.Equal(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(160), SftpPanel.EditUploadTimeout(10L * 1024 * 1024));
        Assert.Equal(TimeSpan.FromMinutes(2), SftpPanel.EditUploadTimeout(-5));
    }

    [Fact]
    public void AppendNotice_전송_결과에_알림을_붙인다()
    {
        Assert.Null(SftpPanel.AppendNotice(null, null));
        Assert.Equal("done", SftpPanel.AppendNotice("done", null));
        Assert.Equal("note", SftpPanel.AppendNotice(null, "note"));
        Assert.Equal("done\nnote", SftpPanel.AppendNotice("done", "note"));
    }

    [Fact]
    public void IsUnder_폴더_경계로_판단()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nubis", "sftp-edit");
        Assert.True(SftpPanel.IsUnder(Path.Combine(root, "host", "abcd1234"), root));
        Assert.True(SftpPanel.IsUnder(Path.Combine(root.ToUpperInvariant(), "h", "x"), root));
        Assert.False(SftpPanel.IsUnder(Path.Combine(Path.GetTempPath(), "Nubis", "sftp-edit2", "h"), root));
        Assert.False(SftpPanel.IsUnder(Path.Combine(Path.GetTempPath(), "Nubis", "sftp", "h"), root));
    }
}
