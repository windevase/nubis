using Xunit;

namespace Nubis.Tests;

public class Review7RdpTests
{
    // RDP 컨트롤이 받지 않는 색 깊이는 32 로 — 그대로 넘기면 ColorDepth 설정이 예외를 내 연결이 실패한다
    [Theory]
    [InlineData("session bpp:i:24", 24)]
    [InlineData("session bpp:i:16", 16)]
    [InlineData("session bpp:i:15", 15)]
    [InlineData("session bpp:i:8", 8)]
    [InlineData("session bpp:i:0", 32)]
    [InlineData("session bpp:i:12", 32)]
    [InlineData("session bpp:i:-1", 32)]
    [InlineData("", 32)]
    public void ColorDepth_InvalidFallsBackTo32(string rdp, int expected) =>
        Assert.Equal(expected, RdpDefaults.Parse(rdp).ColorDepth);
}
