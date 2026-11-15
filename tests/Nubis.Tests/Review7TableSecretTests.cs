using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>머리글 없는 표: 비밀번호로 보이는 칸이 호스트 이름이 되지 않는다 (비밀번호는 가져오지 않는다는 원칙)</summary>
public class Review7TableSecretTests
{
    [Theory]
    [InlineData("10.0.0.1\tP@ssw0rd!")]
    [InlineData("10.0.0.1\tPassw0rd")]
    [InlineData("10.0.0.1\t22\tQwer1234")]
    public void 주소_뒤_비밀번호_칸은_이름으로_쓰지_않는다(string line)
    {
        var h = Assert.Single(TableImport.Parse(line + "\n").Hosts);
        Assert.Equal("10.0.0.1", h.Name);
    }

    [Theory]
    [InlineData("web01\t10.0.0.1\tP@ssw0rd!", "web01")]   // 주소 앞 칸이 이름
    [InlineData("10.0.0.1\tweb01", "web01")]
    [InlineData("10.0.0.1\tWEB-01", "WEB-01")]
    [InlineData("10.0.0.1\t웹 서버 1", "웹 서버 1")]
    public void 이름_칸은_그대로(string line, string name)
    {
        var h = Assert.Single(TableImport.Parse(line + "\n").Hosts);
        Assert.Equal(name, h.Name);
    }
}
