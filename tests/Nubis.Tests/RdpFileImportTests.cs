using System;
using System.IO;
using System.Linq;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class RdpFileImportTests
{
    [Fact]
    public void EntraID_사용자와_NLA_끄기를_옮긴다()
    {
        var text = "screen mode id:i:2\r\nfull address:s:pc01.example.com\r\nusername:s:AzureAD\\user@example.com\r\nenablecredsspsupport:i:0\r\n";
        var r = RdpFileImport.Parse(text, "pc01");
        Assert.Empty(r.Warnings);
        var h = Assert.Single(r.Hosts);
        Assert.Equal("pc01", h.Name);
        Assert.Equal("pc01.example.com", h.Address);
        Assert.Null(h.Port);
        Assert.Equal(Protocol.Rdp, h.Protocol);
        Assert.Equal(@"AzureAD\user@example.com", h.Username);
        Assert.True(h.RdpDisableNla);
        Assert.Null(h.RdpGateway);
    }

    [Fact]
    public void 도메인_사용자는_그대로_두고_NLA_는_기본으로_켠다()
    {
        var r = RdpFileImport.Parse("full address:s:10.0.0.5\nusername:s:EXAMPLE\\admin\nenablecredsspsupport:i:1\n", "srv");
        var h = Assert.Single(r.Hosts);
        Assert.Equal(@"EXAMPLE\admin", h.Username);
        Assert.False(h.RdpDisableNla);
    }

    [Fact]
    public void 주소에_붙은_포트가_server_port_보다_우선한다()
    {
        var a = Assert.Single(RdpFileImport.Parse("full address:s:host.example.com:3390\nserver port:i:4000\n", "a").Hosts);
        Assert.Equal("host.example.com", a.Address);
        Assert.Equal(3390, a.Port);

        var b = Assert.Single(RdpFileImport.Parse("full address:s:host.example.com\nserver port:i:4000\n", "b").Hosts);
        Assert.Equal(4000, b.Port);

        var v6 = Assert.Single(RdpFileImport.Parse("full address:s:[fd00::5]:3391\n", "v6").Hosts);
        Assert.Equal("fd00::5", v6.Address);
        Assert.Equal(3391, v6.Port);
    }

    [Fact]
    public void 잘못된_server_port_는_경고하고_무시한다()
    {
        var r = RdpFileImport.Parse("full address:s:host.example.com\nserver port:i:99999\n", "x");
        Assert.Null(Assert.Single(r.Hosts).Port);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void 게이트웨이는_사용_방식이_0_이_아닐_때만_넣는다()
    {
        var on = Assert.Single(RdpFileImport.Parse("full address:s:pc\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:1\n", "on").Hosts);
        Assert.Equal("gw.example.com", on.RdpGateway);
        Assert.Contains("gw.example.com", on.Note);

        var off = Assert.Single(RdpFileImport.Parse("full address:s:pc\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:0\n", "off").Hosts);
        Assert.Null(off.RdpGateway);

        var empty = Assert.Single(RdpFileImport.Parse("full address:s:pc\ngatewayhostname:s:\ngatewayusagemethod:i:4\n", "empty").Hosts);
        Assert.Null(empty.RdpGateway);
    }

    [Fact]
    public void 이상한_게이트웨이와_사용자명은_비우고_경고한다()
    {
        var r = RdpFileImport.Parse("full address:s:pc\ngatewayhostname:s:gw example/com\ngatewayusagemethod:i:1\nusername:s:a\u0007b\n", "pc");
        var h = Assert.Single(r.Hosts);
        Assert.Null(h.RdpGateway);
        Assert.Null(h.Username);
        Assert.Equal(2, r.Warnings.Count);
    }

    [Fact]
    public void 비밀번호_줄은_값을_담지_않고_경고만_한다()
    {
        var text = "full address:s:pc.example.com\npassword 51:b:01000000D08C9DDF0115D1118C7A00C04FC297EB\nusername:s:user\n";
        var r = RdpFileImport.Parse(text, "pc");
        var h = Assert.Single(r.Hosts);
        Assert.Single(r.Warnings);
        // 결과 어디에도 비밀번호 암호문이 남지 않는다
        Assert.DoesNotContain("01000000D08C", string.Join("|", h.Name, h.Address, h.Username, h.Note, h.RdpGateway, string.Join("|", r.Warnings)));
    }

    [Fact]
    public void 주소가_없으면_호스트_없이_경고한다()
    {
        var r = RdpFileImport.Parse("username:s:user\n", "empty");
        Assert.Empty(r.Hosts);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void UTF16_파일과_UTF8_파일을_모두_읽고_이름은_파일_이름이다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-rdp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string body = "full address:s:pc1.example.com:3390\r\nusername:s:AzureAD\\user@example.com\r\nenablecredsspsupport:i:0\r\n";
            var utf16 = Path.Combine(dir, "운영 PC.rdp");
            File.WriteAllText(utf16, body, new UnicodeEncoding(false, true));   // mstsc 와 같은 UTF-16LE + BOM
            var utf8 = Path.Combine(dir, "web.rdp");
            File.WriteAllText(utf8, body.Replace("pc1", "pc2"), new UTF8Encoding(false));

            var r = RdpFileImport.ParseFiles(new[] { utf16, utf8 });
            Assert.Empty(r.Warnings);
            Assert.Equal(2, r.Hosts.Count);
            var a = r.Hosts.Single(h => h.Name == "운영 PC");
            Assert.Equal("pc1.example.com", a.Address);
            Assert.Equal(3390, a.Port);
            Assert.True(a.RdpDisableNla);
            Assert.Equal("pc2.example.com", r.Hosts.Single(h => h.Name == "web").Address);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 큰_파일과_텍스트가_아닌_파일은_건너뛰고_경고한다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-rdp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var big = Path.Combine(dir, "big.rdp");
            File.WriteAllBytes(big, new byte[RdpFileImport.MaxFileBytes + 1]);
            var bin = Path.Combine(dir, "bin.rdp");
            File.WriteAllBytes(bin, new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x00, 0x01 });
            var r = RdpFileImport.ParseFiles(new[] { big, bin });
            Assert.Empty(r.Hosts);
            Assert.Equal(2, r.Warnings.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 가져오기_계획이_RDP_전용_값을_호스트에_싣는다()
    {
        var src = RdpFileImport.Parse("full address:s:pc.example.com:3390\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:2\nenablecredsspsupport:i:0\nusername:s:AzureAD\\user@example.com\n", "pc").Hosts;
        var plan = ImportMerge.Plan(src, Array.Empty<HostEntry>(), new ImportDefaults());
        var row = Assert.Single(plan.Rows);
        Assert.Equal(ImportStatus.New, row.Status);
        var h = row.Host!;
        Assert.Equal(Protocol.Rdp, h.Protocol);
        Assert.Equal(3390, h.Port);
        Assert.Equal("gw.example.com", h.RdpGateway);
        Assert.True(h.RdpDisableNla);
        Assert.Null(h.ProtectedPassword);

        // 같은 주소·포트가 이미 있으면 기존 규칙대로 "이미 있음"
        var existing = new[] { new HostEntry { Label = "old", Address = "pc.example.com", Port = 3390, Protocol = Protocol.Rdp } };
        Assert.Equal(ImportStatus.Existing, Assert.Single(ImportMerge.Plan(src, existing, new ImportDefaults()).Rows).Status);
    }

    [Theory]
    [InlineData("""{"source":"rdp"}""")]
    [InlineData("""{"source":"rdp","path":"C:\\tmp\\hosts.csv"}""")]
    [InlineData("""{"source":"rdp","path":"C:\\tmp\\a.rdp","domain":"x"}""")]
    public void MCP_rdp_출처는_rdp_파일_경로나_내용이_필요하다(string json) =>
        Assert.Throws<McpToolException>(() => McpImport.ParseArgs(System.Text.Json.JsonDocument.Parse(json).RootElement.Clone()));

    [Fact]
    public void MCP_rdp_출처_인자를_읽는다()
    {
        var a = McpImport.ParseArgs(System.Text.Json.JsonDocument.Parse("""{"source":"RDP","path":"C:\\tmp\\pc.RDP","group":"Windows"}""").RootElement.Clone());
        Assert.Equal(McpImportSource.Rdp, a.Source);
        Assert.Equal("rdp", McpImport.SourceName(a.Source));
        Assert.Equal(@"C:\tmp\pc.RDP", a.Path);
    }

    // ───────── 검수 회귀 테스트 ─────────

    [Fact]
    public void 게이트웨이_사용_방식_4_는_쓰지_않음으로_본다()
    {
        // Microsoft 문서: 0 과 4 는 모두 게이트웨이를 쓰지 않음 (4 = 로컬 주소 우회)
        var h = Assert.Single(RdpFileImport.Parse("full address:s:pc\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:4\n", "pc").Hosts);
        Assert.Null(h.RdpGateway);
        // 3(기본 설정 사용) · 값 없음은 쓴다
        Assert.Equal("gw.example.com", Assert.Single(RdpFileImport.Parse("full address:s:pc\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:3\n", "pc").Hosts).RdpGateway);
        Assert.Equal("gw.example.com", Assert.Single(RdpFileImport.Parse("full address:s:pc\ngatewayhostname:s:gw.example.com\n", "pc").Hosts).RdpGateway);
    }

    [Theory]
    [InlineData("gw.ex\u0430mple.com")]      // 키릴 'а' — 다른 게이트웨이처럼 보이게
    [InlineData("gw.example.com\u202E")]      // 양방향 재정의
    [InlineData("gw%25.example.com")]
    [InlineData("[fd00::1")]
    [InlineData("gw.example.com:0")]
    public void 위장한_게이트웨이는_비우고_경고에는_숨은_문자를_보이게_한다(string gw)
    {
        var r = RdpFileImport.Parse($"full address:s:pc\ngatewayhostname:s:{gw}\ngatewayusagemethod:i:1\n", "pc");
        Assert.Null(Assert.Single(r.Hosts).RdpGateway);
        var w = Assert.Single(r.Warnings);
        Assert.DoesNotContain('\u202E', w);
    }

    [Fact]
    public void IPv6_게이트웨이와_포트는_받는다()
    {
        var h = Assert.Single(RdpFileImport.Parse("full address:s:pc\ngatewayhostname:s:[fd00::1]:8443\ngatewayusagemethod:i:1\n", "pc").Hosts);
        Assert.Equal("[fd00::1]:8443", h.RdpGateway);
    }

    [Theory]
    [InlineData("admin\u202Enimda")]
    [InlineData("\u200Badmin")]
    [InlineData("ad\u0085min")]
    public void 양방향_서식_문자가_있는_사용자명은_비운다(string user)
    {
        var r = RdpFileImport.Parse($"full address:s:pc\nusername:s:{user}\n", "pc");
        Assert.Null(Assert.Single(r.Hosts).Username);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void 따로_적힌_domain_은_사용자명_앞에_붙인다()
    {
        Assert.Equal(@"EXAMPLE\admin", Assert.Single(RdpFileImport.Parse("full address:s:pc\nusername:s:admin\ndomain:s:EXAMPLE\n", "pc").Hosts).Username);
        // 이미 DOMAIN\ · UPN 이면 그대로
        Assert.Equal(@"OTHER\admin", Assert.Single(RdpFileImport.Parse("full address:s:pc\nusername:s:OTHER\\admin\ndomain:s:EXAMPLE\n", "pc").Hosts).Username);
        Assert.Equal("admin@example.com", Assert.Single(RdpFileImport.Parse("full address:s:pc\nusername:s:admin@example.com\ndomain:s:EXAMPLE\n", "pc").Hosts).Username);
    }

    [Fact]
    public void 파일_이름의_양방향_문자는_호스트_이름에서_뺀다()
    {
        Assert.Equal("prodexe", RdpFileImport.NameFromPath(@"C:\tmp\prod" + "\u202E" + "exe.rdp"));
        Assert.Equal("pc", Assert.Single(RdpFileImport.Parse("full address:s:pc.example.com\n", "\u2066pc\u2069").Hosts).Name);
    }

    [Theory]
    [InlineData("full address:s:[fd00::5]:0\n")]
    [InlineData("full address:s:[fd00::5]:99999\n")]
    [InlineData("full address:s:[fd00::5]x\n")]
    [InlineData("full address:s:[fd00::5]:0\nserver port:i:4000\n")]
    [InlineData("full address:s:host.example.com:99999\nserver port:i:4000\n")]
    public void 괄호_주소의_잘못된_포트는_오류로_보인다(string text)
    {
        var src = RdpFileImport.Parse(text, "pc").Hosts;
        Assert.Equal(ImportStatus.Invalid, Assert.Single(ImportMerge.Plan(src, Array.Empty<HostEntry>(), new ImportDefaults()).Rows).Status);
    }

    [Fact]
    public void 같은_키가_여러_번이면_마지막_값과_대소문자_무시()
    {
        var h = Assert.Single(RdpFileImport.Parse("Full Address:s:first.example.com\r\nFULL ADDRESS:s:  second.example.com  \r\n", "pc").Hosts);
        Assert.Equal("second.example.com", h.Address);
    }

    [Fact]
    public void UTF16BE_파일과_rdp_이름의_폴더()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-rdp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var be = Path.Combine(dir, "be.rdp");
            File.WriteAllText(be, "full address:s:be.example.com\r\n", new UnicodeEncoding(true, true));
            var folder = Path.Combine(dir, "folder.rdp");
            Directory.CreateDirectory(folder);
            var r = RdpFileImport.ParseFiles(new[] { be, folder });
            Assert.Empty(r.Warnings);
            Assert.Equal("be.example.com", Assert.Single(r.Hosts).Address);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MCP_승인_목록과_감사에_게이트웨이와_NLA_끄기가_보인다()
    {
        var src = RdpFileImport.Parse("full address:s:pc.example.com\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:1\nenablecredsspsupport:i:0\n", "pc").Hosts;
        var plan = ImportMerge.Plan(src, Array.Empty<HostEntry>(), new ImportDefaults());
        var line = Assert.Single(McpImport.ApprovalTargets(plan.Rows));
        Assert.Contains("gw.example.com", line);
        Assert.Contains("NLA", line);
        Assert.Contains("RD 게이트웨이 gw.example.com · NLA 끔", McpImport.RdpExtras(plan.Rows[0], translate: false));
        Assert.True(McpImport.AnyGateway(plan.Rows));

        // 게이트웨이 없는 호스트 · 기존 호스트 갱신 줄에는 붙이지 않는다
        var plain = ImportMerge.Plan(RdpFileImport.Parse("full address:s:pc.example.com\n", "pc").Hosts, Array.Empty<HostEntry>(), new ImportDefaults());
        Assert.False(McpImport.AnyGateway(plain.Rows));
        Assert.Equal("", McpImport.RdpExtras(plain.Rows[0], translate: false));
        var existing = new[] { new HostEntry { Label = "old", Address = "pc.example.com", Port = 3389, Protocol = Protocol.Rdp } };
        var upd = ImportMerge.Plan(src, existing, new ImportDefaults());
        Assert.Equal(ImportStatus.Existing, upd.Rows[0].Status);
        Assert.Equal("", McpImport.RdpExtras(upd.Rows[0], translate: false));
    }

    [Fact]
    public void 끌어다_놓기는_rdp_이름만_파일_시스템을_보지_않고_고른다()
    {
        var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop,
            new[] { @"Z:\nope\a.rdp", @"Z:\nope\b.txt", @"Z:\nope\C.RDP", "" });
        Assert.Equal(new[] { @"Z:\nope\a.rdp", @"Z:\nope\C.RDP" }, ImportHostsDialog.RdpFilesIn(data));
        Assert.Empty(ImportHostsDialog.RdpFilesIn(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, "x.rdp")));

        var many = Enumerable.Range(0, RdpFileImport.MaxFiles + 50).Select(i => $@"Z:\nope\{i}.rdp").ToArray();
        Assert.Equal(RdpFileImport.MaxFiles + 1, ImportHostsDialog.RdpFilesIn(new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, many)).Count);
    }

    [Fact]
    public void SSH_로_추정된_가져오기에는_RDP_전용_값을_넣지_않는다()
    {
        var h = ImportMerge.ToHostEntry(new ImportedHost { Address = "10.0.0.1", Protocol = Protocol.Ssh, RdpGateway = "gw", RdpDisableNla = true }, new ImportDefaults());
        Assert.Null(h.RdpGateway);
        Assert.False(h.RdpDisableNla);
    }
}
