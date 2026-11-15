using System.IO;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class MRemoteNgImportTests
{
    private const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

    private static string Wrap(string nodes, string rootAttrs = "FullFileEncryption=\"false\" ConfVersion=\"2.6\"") =>
        Head + "<mrng:Connections xmlns:mrng=\"http://mremoteng.org\" Name=\"Connections\" Export=\"false\" EncryptionEngine=\"AES\" BlockCipherMode=\"GCM\" KdfIterations=\"1000\" "
        + rootAttrs + " Protected=\"abc\">\n" + nodes + "\n</mrng:Connections>";

    [Fact]
    public void 중첩_폴더는_그룹_경로가_되고_RDP_SSH2_를_변환한다()
    {
        var xml = Wrap("""
            <Node Name="운영" Type="Container" Expanded="true" Descr="" Icon="mRemoteNG" Username="" Protocol="RDP" Port="3389">
              <Node Name="웹" Type="Container" Username="" Protocol="RDP" Port="3389">
                <Node Name="web01" Type="Connection" Descr="웹 서버" Hostname="10.0.0.1" Protocol="SSH2" Port="2222" Username="ubuntu" Password="" />
                <Node Name="win01" Type="Connection" Descr="" Hostname="win01.example.com" Protocol="RDP" Port="3389" Username="admin" Password="" />
              </Node>
            </Node>
            <Node Name="top" Type="Connection" Hostname="192.168.0.5" Protocol="SSH1" Port="" Username="" Password="" />
            """);
        var r = MRemoteNgImport.Parse(xml);
        Assert.Empty(r.Warnings);
        Assert.Equal(3, r.Hosts.Count);

        var web = r.Hosts.Single(h => h.Name == "web01");
        Assert.Equal("10.0.0.1", web.Address);
        Assert.Equal(Protocol.Ssh, web.Protocol);
        Assert.Equal(2222, web.Port);
        Assert.Equal("운영 / 웹", web.Group);
        Assert.Equal("ubuntu", web.Username);
        Assert.Equal("웹 서버", web.Note);

        var win = r.Hosts.Single(h => h.Name == "win01");
        Assert.Equal(Protocol.Rdp, win.Protocol);
        Assert.Equal(3389, win.Port);
        Assert.Equal("운영 / 웹", win.Group);
        Assert.Null(win.Note);

        var top = r.Hosts.Single(h => h.Name == "top");
        Assert.Equal(Protocol.Ssh, top.Protocol);
        Assert.Equal(22, top.Port);          // 빈 포트 → 기본 포트
        Assert.Null(top.Group);
        Assert.Null(top.Username);
    }

    [Fact]
    public void 지원하지_않는_프로토콜은_건너뛰고_한_번만_알린다()
    {
        var xml = Wrap("""
            <Node Name="vnc1" Type="Connection" Hostname="10.0.0.1" Protocol="VNC" Port="5900" />
            <Node Name="vnc2" Type="Connection" Hostname="10.0.0.2" Protocol="VNC" Port="5900" />
            <Node Name="tel" Type="Connection" Hostname="10.0.0.3" Protocol="Telnet" Port="23" />
            <Node Name="ssh" Type="Connection" Hostname="10.0.0.4" Protocol="SSH2" Port="22" />
            """);
        var r = MRemoteNgImport.Parse(xml);
        Assert.Single(r.Hosts);
        var w = Assert.Single(r.Warnings);
        Assert.Equal(L.F("지원하지 않는 프로토콜(SSH·RDP 외) 연결 {0}개를 건너뛰었습니다: {1}", 3, "VNC 2, Telnet 1"), w);
    }

    [Fact]
    public void 비밀번호는_가져오지_않고_한_번만_알린다()
    {
        var xml = Wrap("""
            <Node Name="a" Type="Connection" Hostname="10.0.0.1" Protocol="SSH2" Port="22" Username="root" Password="c2VjcmV0MQ==" />
            <Node Name="b" Type="Connection" Hostname="10.0.0.2" Protocol="RDP" Port="3389" Username="admin" Password="c2VjcmV0Mg==" />
            """);
        var r = MRemoteNgImport.Parse(xml);
        Assert.Equal(2, r.Hosts.Count);
        var w = Assert.Single(r.Warnings);
        Assert.Equal(L.T("mRemoteNG 에 저장된 비밀번호는 가져오지 않습니다. 등록한 뒤 Vault 계정을 지정하세요."), w);
        Assert.DoesNotContain(r.Hosts, h => (h.Note ?? "").Contains("c2VjcmV0") || (h.Username ?? "").Contains("c2VjcmV0"));
    }

    [Fact]
    public void 상속_속성은_가장_가까운_상위_폴더_값을_쓴다()
    {
        var xml = Wrap("""
            <Node Name="dc" Type="Container" Username="ops" Protocol="SSH2" Port="2200">
              <Node Name="sub" Type="Container" Username="sub-user" Protocol="RDP" Port="3390" InheritUsername="false">
                <Node Name="x" Type="Connection" Hostname="10.0.0.1" Protocol="VNC" Port="5900" Username="own"
                      InheritUsername="true" InheritPort="true" InheritProtocol="true" />
              </Node>
              <Node Name="sub2" Type="Container" Username="ignored" Protocol="RDP" Port="1" InheritUsername="true" InheritPort="True" InheritProtocol="true">
                <Node Name="y" Type="Connection" Hostname="10.0.0.2" Protocol="RDP" Port="3389" Username="own"
                      InheritUsername="true" InheritPort="true" InheritProtocol="true" />
              </Node>
            </Node>
            <Node Name="root-level" Type="Connection" Hostname="10.0.0.3" Protocol="SSH2" Port="22" Username="me" InheritUsername="true" />
            """);
        var r = MRemoteNgImport.Parse(xml);
        Assert.Empty(r.Warnings);

        var x = r.Hosts.Single(h => h.Name == "x");
        Assert.Equal("sub-user", x.Username);
        Assert.Equal(Protocol.Rdp, x.Protocol);
        Assert.Equal(3390, x.Port);

        // sub2 자체가 상속 → dc 값까지 거슬러 올라간다
        var y = r.Hosts.Single(h => h.Name == "y");
        Assert.Equal("ops", y.Username);
        Assert.Equal(Protocol.Ssh, y.Protocol);
        Assert.Equal(2200, y.Port);
        Assert.Equal("dc / sub2", y.Group);

        // 최상위는 물려받을 폴더가 없으니 자기 값
        Assert.Equal("me", r.Hosts.Single(h => h.Name == "root-level").Username);
    }

    [Fact]
    public void 옛_형식_속성_대소문자와_Type_없음을_허용한다()
    {
        var xml = Head + """
            <Connections Name="Connections" ConfVersion="2.2">
              <Node name="folder">
                <Node name="srv" hostname="10.1.1.1" protocol="ssh2" port="22" username="u" />
              </Node>
            </Connections>
            """;
        var r = MRemoteNgImport.Parse(xml);
        var h = Assert.Single(r.Hosts);
        Assert.Equal("srv", h.Name);
        Assert.Equal("folder", h.Group);
        Assert.Equal(Protocol.Ssh, h.Protocol);
        Assert.Equal("u", h.Username);
    }

    [Fact]
    public void 전체_암호화_파일은_읽지_않고_알린다()
    {
        var xml = Head + "<mrng:Connections xmlns:mrng=\"http://mremoteng.org\" Name=\"Connections\" FullFileEncryption=\"true\" ConfVersion=\"2.6\">"
            + "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=</mrng:Connections>";
        var r = MRemoteNgImport.Parse(xml);
        Assert.Empty(r.Hosts);
        var w = Assert.Single(r.Warnings);
        Assert.Equal(L.T("파일 전체가 암호화된 mRemoteNG 연결 파일은 읽을 수 없습니다. mRemoteNG 에서 파일 → 내보내기로 전체 암호화 없이 내보낸 뒤 다시 가져오세요."), w);
    }

    [Fact]
    public void 잘못된_XML_은_빈_결과와_경고()
    {
        var r = MRemoteNgImport.Parse("<mrng:Connections xmlns:mrng=\"http://mremoteng.org\"><Node Name=\"a\"");
        Assert.Empty(r.Hosts);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void DTD_는_거부한다()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]>"
            + "<Connections><Node Name=\"a\" Type=\"Connection\" Hostname=\"&e;\" Protocol=\"SSH2\" /></Connections>";
        var r = MRemoteNgImport.Parse(xml);
        Assert.Empty(r.Hosts);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void 주소_없는_연결은_건너뛰고_알린다()
    {
        var xml = Wrap("""<Node Name="empty" Type="Connection" Hostname="" Protocol="SSH2" Port="22" />""");
        var r = MRemoteNgImport.Parse(xml);
        Assert.Empty(r.Hosts);
        Assert.Equal(L.F("주소(Hostname)가 없는 연결 {0}개를 건너뛰었습니다.", 1), Assert.Single(r.Warnings));
    }

    [Fact]
    public void 파일에서_읽는다_없는_파일은_경고()
    {
        var path = Path.Combine(Path.GetTempPath(), "nubis-mrng-" + System.Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, Wrap("""<Node Name="a" Type="Connection" Hostname="10.0.0.9" Protocol="RDP" Port="3389" />"""));
        try
        {
            var r = MRemoteNgImport.ParseFile(path);
            Assert.Equal("10.0.0.9", Assert.Single(r.Hosts).Address);
        }
        finally { File.Delete(path); }

        var missing = MRemoteNgImport.ParseFile(path);
        Assert.Empty(missing.Hosts);
        Assert.Single(missing.Warnings);
        Assert.EndsWith("confCons.xml", MRemoteNgImport.DefaultPath);
    }
}
