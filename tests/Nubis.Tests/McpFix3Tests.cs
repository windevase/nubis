using System;
using System.IO;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class McpFix3Tests
{
    private static bool ReadOnly(string _) => true;

    // ---------- Windows OpenSSH 대상은 자동 승인하지 않는다 ----------

    [Theory]
    [InlineData("Windows Server 2022 Datacenter", true)]
    [InlineData("windows", true)]
    [InlineData("Microsoft WINDOWS 2019", true)]
    [InlineData("Ubuntu 22.04", false)]
    [InlineData("Amazon Linux 2023", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Windows_OS_판별(string? os, bool expected) =>
        Assert.Equal(expected, McpHelpers.IsWindowsOs(os));

    [Fact]
    public void Windows_SSH_대상은_읽기_전용이어도_자동_승인하지_않는다()
    {
        Assert.True(McpHelpers.CanAutoApproveTerminalInput(true, "echo 'x & net user a P1 /add & '", true, true, ReadOnly));
        Assert.False(McpHelpers.CanAutoApproveTerminalInput(true, "echo 'x & net user a P1 /add & '", true, true, ReadOnly, windowsHost: true));
        Assert.False(McpHelpers.CanAutoApproveTerminalInput(true, "ls", true, true, ReadOnly, localTerminal: false, windowsHost: true));
    }

    // ---------- wait_for_output: '이미 있음' 은 프롬프트로 돌아왔을 때만 ----------

    [Fact]
    public void 실행_중이면_이미_있던_패턴으로_끝내지_않는다()
    {
        // tail -f 의 첫 줄에 옛 ERROR — 아직 실행 중
        var tail = "user@web:~$ tail -f app.log\n2026-11-01 ERROR old failure\n2026-11-14 INFO ok";
        Assert.False(McpHelpers.AlreadyPresentAtPrompt(tail, "ERROR", false, atPrompt: false));
        // 알아보지 못한 원격 프롬프트 안 (화면 전체가 실행 중 출력처럼 보임)
        var ssh = "user@web:~$ ssh db\nLast login: ERROR\ndb-host$";
        Assert.False(McpHelpers.AlreadyPresentAtPrompt(ssh, "ERROR", false, atPrompt: false));
    }

    [Fact]
    public void 프롬프트로_돌아왔고_마지막_명령_출력에_있으면_바로_끝낸다()
    {
        var done = "user@web:~$ ls\nERROR old\nuser@web:~$ make\nbuild ok\nComplete!\nuser@web:~$";
        Assert.True(McpHelpers.AlreadyPresentAtPrompt(done, "Complete!", false, atPrompt: true));
        // 이전 명령의 출력은 세지 않는다
        Assert.False(McpHelpers.AlreadyPresentAtPrompt(done, "ERROR", false, atPrompt: true));
        // cd 로 프롬프트가 바뀌어 OutputSinceLastCommand 가 못 찾으면 마지막 프롬프트+명령 줄 뒤로 판단
        var cd = "user@web:~$ cd /app && make\nComplete!\nuser@web:/app$";
        Assert.True(McpHelpers.AlreadyPresentAtPrompt(cd, "Complete!", false, atPrompt: true));
        Assert.False(McpHelpers.AlreadyPresentAtPrompt(done, "", false, atPrompt: true));
    }

    // ---------- 입력 대기 줄: 진행률·콜론 끝 출력은 아님 ----------

    [Theory]
    [InlineData("Password:", true)]
    [InlineData("[sudo] password for user:", true)]
    [InlineData("Enter passphrase for key '/home/u/.ssh/id_ed25519':", true)]
    [InlineData("Verification code:", true)]
    [InlineData("비밀번호:", true)]
    [InlineData("Do you want to continue? [Y/n]", true)]
    [InlineData("Are you sure you want to continue connecting (yes/no/[fingerprint])?", true)]
    [InlineData("Overwrite existing file?", true)]
    [InlineData("db-host$", true)]
    [InlineData("[root@db ~]#", true)]
    [InlineData("C:\\>", true)]
    [InlineData("mysql>", true)]
    [InlineData("Downloading 45%", false)]                         // 진행률
    [InlineData("The following NEW packages will be installed:", false)]
    [InlineData("Mapping:", false)]                                // 'pin' 이 단어 안에 있음
    [InlineData("</html>", false)]                                 // curl 로 받은 HTML 끝
    [InlineData("<br>", false)]
    [InlineData("##########", false)]                              // 진행 막대
    [InlineData("Unpacking nginx (1.24) ...", false)]
    public void 입력_대기_줄_판별(string line, bool expected) =>
        Assert.Equal(expected, McpHelpers.LooksLikeInputWait(line));

    [Theory]
    [InlineData("user@web:~$ apt install -y nginx\nThe following NEW packages will be installed:", true)]
    [InlineData("user@web:~$ curl -sL https://example.com\n<html>\n</html>", true)]
    [InlineData("user@web:~$ wget x.tar.gz\nx.tar.gz  45%", true)]
    [InlineData("user@web:~$ sudo apt update\n[sudo] password for user:", false)]
    public void 진행률_콜론_태그로_끝나도_프롬프트_복귀를_기다린다(string screen, bool expected) =>
        Assert.Equal(expected, McpHelpers.RunningCommandFromKnownPrompt(screen));

    // ---------- 업로드: 열린 핸들 기준 재검사 ----------

    [Fact]
    public void 핸들_검사_하드_링크_폴더_차단_경로()
    {
        var data = Path.Combine(Path.GetTempPath(), "nubis-fix3-data-" + Guid.NewGuid().ToString("N"));
        Assert.Null(McpHelpers.UploadHandleCheck(@"D:\work\app.tar.gz", 1, false, data, null));
        Assert.NotNull(McpHelpers.UploadHandleCheck(@"D:\work\app.tar.gz", 2, false, data, null));   // id_rsa 하드 링크일 수 있음
        Assert.NotNull(McpHelpers.UploadHandleCheck(@"D:\work\dir", 1, true, data, null));
        Assert.NotNull(McpHelpers.UploadHandleCheck(null, 1, false, data, null));
        Assert.NotNull(McpHelpers.UploadHandleCheck(Path.Combine(data, "vault.json"), 1, false, data, null));
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        Assert.NotNull(McpHelpers.UploadHandleCheck(ssh, 1, false, data, null));
    }

    [Fact]
    public void 일반_파일은_같은_핸들로_읽고_데이터_폴더_파일은_거부()
    {
        var root = Path.Combine(Path.GetTempPath(), "nubis-fix3-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        try
        {
            var ok = Path.Combine(root, "a.txt");
            File.WriteAllText(ok, "hello");
            using (var fs = McpHelpers.OpenUploadFile(ok, data, null))
            using (var r = new StreamReader(fs))
                Assert.Equal("hello", r.ReadToEnd());

            var secret = Path.Combine(data, "hosts.json");
            File.WriteAllText(secret, "{}");
            Assert.Throws<InvalidOperationException>(() => McpHelpers.OpenUploadFile(secret, data, null).Dispose());
            Assert.Throws<InvalidOperationException>(() => McpHelpers.OpenUploadFile(Path.Combine(root, "missing.txt"), data, null).Dispose());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---------- 승인 뒤 다시 거르기 ----------

    [Fact]
    public void 승인_대기_중_사라진_항목을_나눈다()
    {
        var list = new[] { "a", "b", "c" }.ToList();
        var (present, gone) = McpHelpers.SplitStillPresent(new[] { "a", "x", "c" }, list.Contains);
        Assert.Equal(new[] { "a", "c" }, present);
        Assert.Equal(new[] { "x" }, gone);
    }
}
