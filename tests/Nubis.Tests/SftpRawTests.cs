using Nubis;
using Renci.SshNet;
using Xunit;

namespace Nubis.Tests;

public class SftpRawTests
{
    // SSH.NET 을 올렸을 때 internal SftpSession · ISftpSession.RequestRemove/RequestRename 이 사라지면
    // 링크 삭제·이름 바꾸기가 모두 거부된다 (원본이 지워지는 DeleteFile 로는 절대 대신하지 않음) — 업그레이드 때 여기서 알아챈다
    [Fact]
    public void 링크_자체_삭제_이름변경용_리플렉션_대상이_있다() =>
        Assert.True(SftpRaw.Supported);

    [Fact]
    public void 상대_경로는_거부한다()
    {
        using var c = new SftpClient("127.0.0.1", "u", "p");
        Assert.Throws<System.ArgumentException>(() => SftpRaw.Remove(c, "link"));
        Assert.Throws<System.ArgumentException>(() => SftpRaw.Rename(c, "/a/link", "b"));
    }

    [Fact]
    public void 연결_전에는_예외()
    {
        using var c = new SftpClient("127.0.0.1", "u", "p");
        Assert.Throws<Renci.SshNet.Common.SshConnectionException>(() => SftpRaw.Remove(c, "/a/link"));
    }
}
