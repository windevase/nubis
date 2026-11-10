using System;
using System.IO;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class McpHelpersTests
{
    [Fact]
    public void 출력_패턴_개수를_센다()
    {
        var text = "Installing...\nComplete!\nuser@web:~$ \nuser@web:~$ ";
        Assert.Equal(2, McpHelpers.CountMatches(text, "$ ", false));
        Assert.Equal(1, McpHelpers.CountMatches(text, "complete!", false));   // 대소문자 무시
        Assert.Equal(2, McpHelpers.CountMatches(text, @"^user@\w+:~\$ $", true));
        Assert.Equal(0, McpHelpers.CountMatches(text, "", false));
        Assert.Throws<ArgumentException>(() => McpHelpers.CountMatches(text, "([", true));
    }

    [Fact]
    public void 바이너리_파일을_구분한다()
    {
        Assert.False(McpHelpers.LooksBinary(Encoding.UTF8.GetBytes("2026-11-10 ERROR 한글 로그\n\tat x\r\n\u001b[31mred\u001b[0m")));
        Assert.True(McpHelpers.LooksBinary(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01 }));   // ELF
        var noisy = new byte[100];
        for (var i = 0; i < noisy.Length; i++) noisy[i] = (byte)(i % 3 == 0 ? 1 : 65);
        Assert.True(McpHelpers.LooksBinary(noisy));
        Assert.False(McpHelpers.LooksBinary(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void 계정_파일과_개인키는_업로드할_수_없다()
    {
        var data = @"C:\Users\a\AppData\Roaming\Nubis";
        Assert.NotNull(McpHelpers.BlockedUploadReason(@"C:\Users\a\AppData\Roaming\Nubis\vault.json", data));
        Assert.NotNull(McpHelpers.BlockedUploadReason(@"C:\Users\a\AppData\Roaming\Nubis", data));
        Assert.NotNull(McpHelpers.BlockedUploadReason(@"C:\Users\a\AppData\Roaming\Nubis\..\Nubis\hosts.json", data));
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        Assert.NotNull(McpHelpers.BlockedUploadReason(ssh, data));
        Assert.NotNull(McpHelpers.BlockedUploadReason(@"D:\work\deploy.pem", data));
        Assert.NotNull(McpHelpers.BlockedUploadReason(@"D:\work\id_ed25519", data));
        Assert.NotNull(McpHelpers.BlockedUploadReason(@"D:\work\server.PPK", data));
        // 비슷한 이름의 다른 폴더는 허용
        Assert.Null(McpHelpers.BlockedUploadReason(@"C:\Users\a\AppData\Roaming\NubisBackup\a.txt", data));
        Assert.Null(McpHelpers.BlockedUploadReason(@"D:\work\app.tar.gz", data));
    }

    [Fact]
    public void 포트_목록과_원격_파일_이름()
    {
        Assert.Equal(new[] { 22, 3389, 5985 }, McpHelpers.ParsePorts(new[] { "22,3389", " 5985", "22", "0", "70000", "abc" }));
        Assert.Equal(10, McpHelpers.ParsePorts(new[] { "1,2,3,4,5,6,7,8,9,10,11,12" }).Length);
        Assert.Equal("nginx.conf", McpHelpers.RemoteFileName("/etc/nginx/nginx.conf"));
        Assert.Equal("log", McpHelpers.RemoteFileName("/var/log/"));
        Assert.Equal("a.txt", McpHelpers.RemoteFileName("a.txt"));
    }
}
