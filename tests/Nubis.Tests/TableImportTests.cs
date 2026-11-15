using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class TableImportTests
{
    [Fact]
    public void 빈_입력은_빈_결과()
    {
        var r = TableImport.Parse("  \n\t\n");
        Assert.Empty(r.Hosts);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void 엑셀_탭_영어_머리글()
    {
        var text = "Name\tIP Address\tPort\tOS\tGroup\tUser\r\n" +
                   "web01\t10.0.0.1\t22\tUbuntu 22.04\tprod/web\tubuntu\r\n" +
                   "win01\t10.0.0.2\t\tWindows Server 2019\tprod\t\r\n";
        var r = TableImport.Parse(text);
        Assert.Empty(r.Warnings);
        Assert.Equal(2, r.Hosts.Count);

        var web = r.Hosts[0];
        Assert.Equal("web01", web.Name);
        Assert.Equal("10.0.0.1", web.Address);
        Assert.Equal(22, web.Port);
        Assert.Equal(Protocol.Ssh, web.Protocol);
        Assert.Equal("prod / web", web.Group);
        Assert.Equal("ubuntu", web.Username);
        Assert.Equal("Ubuntu 22.04", web.Os);

        var win = r.Hosts[1];
        Assert.Equal("win01", win.Name);
        Assert.Null(win.Port);
        Assert.Equal(Protocol.Rdp, win.Protocol);
        Assert.Equal("prod", win.Group);
        Assert.Null(win.Username);
    }

    [Fact]
    public void CSV_한국어_머리글과_따옴표_칸()
    {
        var text = "서버명,IP 주소,포트,비고,분류\n" +
                   "\"웹, 서버\",192.168.0.10,2222,\"설명 \"\"따옴표\"\"\n두 줄\",운영\n";
        var r = TableImport.Parse(text);
        Assert.Empty(r.Warnings);
        var h = Assert.Single(r.Hosts);
        Assert.Equal("웹, 서버", h.Name);
        Assert.Equal("192.168.0.10", h.Address);
        Assert.Equal(2222, h.Port);
        Assert.Null(h.Protocol);   // 2222 · OS 없음 → 추정 불가
        Assert.Equal("설명 \"따옴표\" 두 줄", h.Note);
        Assert.Equal("운영", h.Group);
    }

    [Fact]
    public void 세미콜론_구분()
    {
        var r = TableImport.Parse("hostname;address;username;용도\nweb;10.1.1.1;admin;a, b\n");
        var h = Assert.Single(r.Hosts);
        Assert.Equal("web", h.Name);
        Assert.Equal("10.1.1.1", h.Address);
        Assert.Equal("admin", h.Username);
        Assert.Equal("a, b", h.Note);
    }

    [Fact]
    public void 탭이_있으면_쉼표_세미콜론보다_우선()
    {
        var r = TableImport.Parse("name\tip\tnote\na\t10.0.0.1\tx, y; z\n");
        var h = Assert.Single(r.Hosts);
        Assert.Equal("x, y; z", h.Note);
    }

    [Fact]
    public void 머리글_없는_행은_값_모양으로_추정()
    {
        var r = TableImport.Parse("web01\t10.0.0.5\t3389\ndb01\t10.0.0.6\tssh\ndb.example.com\t22\n");
        Assert.Empty(r.Warnings);
        Assert.Equal(3, r.Hosts.Count);

        Assert.Equal("web01", r.Hosts[0].Name);
        Assert.Equal("10.0.0.5", r.Hosts[0].Address);
        Assert.Equal(3389, r.Hosts[0].Port);
        Assert.Equal(Protocol.Rdp, r.Hosts[0].Protocol);

        Assert.Equal("db01", r.Hosts[1].Name);
        Assert.Equal(Protocol.Ssh, r.Hosts[1].Protocol);
        Assert.Null(r.Hosts[1].Port);

        Assert.Equal("db.example.com", r.Hosts[2].Address);
        Assert.Equal("db.example.com", r.Hosts[2].Name);   // 이름이 없으면 주소
        Assert.Equal(22, r.Hosts[2].Port);
    }

    [Fact]
    public void 머리글_없는_행_IP가_호스트명보다_주소로_우선()
    {
        var r = TableImport.Parse("web01.corp.local,10.0.0.9,Windows Server 2022\n");
        var h = Assert.Single(r.Hosts);
        Assert.Equal("10.0.0.9", h.Address);
        Assert.Equal("web01.corp.local", h.Name);
        Assert.Equal(Protocol.Rdp, h.Protocol);
    }

    [Fact]
    public void 주소칸의_사용자_포트_분리()
    {
        var text = "name,host,port\n" +
                   "a,root@10.0.0.7:2200,\n" +
                   "b,[::1]:22,\n" +
                   "c,fe80::1,\n" +
                   "d,admin@10.0.0.8:2200,2022\n" +
                   "e,rdp://win.example.com,\n";
        var r = TableImport.Parse(text);
        Assert.Equal(5, r.Hosts.Count);

        Assert.Equal("10.0.0.7", r.Hosts[0].Address);
        Assert.Equal("root", r.Hosts[0].Username);
        Assert.Equal(2200, r.Hosts[0].Port);

        Assert.Equal("::1", r.Hosts[1].Address);
        Assert.Equal(22, r.Hosts[1].Port);
        Assert.Equal(Protocol.Ssh, r.Hosts[1].Protocol);

        Assert.Equal("fe80::1", r.Hosts[2].Address);
        Assert.Null(r.Hosts[2].Port);

        Assert.Equal(2022, r.Hosts[3].Port);   // 포트 열이 주소 안 포트보다 우선
        Assert.Equal("admin", r.Hosts[3].Username);

        Assert.Equal("win.example.com", r.Hosts[4].Address);
        Assert.Equal(Protocol.Rdp, r.Hosts[4].Protocol);
    }

    [Fact]
    public void 사용자_열이_주소안_사용자보다_우선()
    {
        var r = TableImport.Parse("name,ip,계정\na,root@10.0.0.7,deploy\n");
        Assert.Equal("deploy", Assert.Single(r.Hosts).Username);
    }

    [Fact]
    public void 비밀번호_열은_가져오지_않고_경고_한번()
    {
        var text = "name,ip,password,root 비밀번호,PW\n" +
                   "a,10.0.0.1,secret1,secret2,secret3\n" +
                   "b,10.0.0.2,secret4,secret5,secret6\n";
        var r = TableImport.Parse(text);
        Assert.Equal(2, r.Hosts.Count);
        Assert.Single(r.Warnings);
        foreach (var h in r.Hosts)
        {
            var all = string.Join("|", h.Name, h.Address, h.Username, h.Note, h.Group, h.Os, h.JumpHost);
            Assert.DoesNotContain("secret", all);
        }
    }

    [Fact]
    public void 주소_없는_줄은_건너뛰고_행번호를_묶어서_경고()
    {
        // 1: 머리글, 2: a, 3: b(주소 없음), 4: c(주소 없음), 5: 빈 줄, 6: d, 7: e(주소 없음)
        var text = "name,ip\na,10.0.0.1\nb,\nc,\n\nd,10.0.0.4\ne,\n";
        var r = TableImport.Parse(text);
        Assert.Equal(new[] { "a", "d" }, r.Hosts.Select(h => h.Name).ToArray());
        var w = Assert.Single(r.Warnings);
        Assert.Contains("3-4, 7", w);
    }

    [Fact]
    public void 머리글_없는_줄에_주소가_없으면_경고()
    {
        var r = TableImport.Parse("10.0.0.1\tweb\n그냥 메모\n");
        Assert.Single(r.Hosts);
        Assert.Contains(r.Warnings, w => w.Contains(": 2"));
    }

    [Fact]
    public void 프로토콜_추정()
    {
        var text = "name\tip\tport\tos\ttype\n" +
                   "a\t10.0.0.1\t33890\t\t\n" +          // 33890 → RDP
                   "b\t10.0.0.2\t\tWindows 11\t\n" +      // Windows → RDP
                   "c\t10.0.0.3\t\tRocky Linux 9\t\n" +   // 리눅스 → SSH
                   "d\t10.0.0.4\t\t\t\n" +                // 정보 없음 → null
                   "e\t10.0.0.5\t3389\t\tSSH\n" +         // 명시한 프로토콜이 우선
                   "f\t10.0.0.6\t\t\twindows\n";          // 종류 열의 windows → RDP
        var r = TableImport.Parse(text);
        Assert.Equal(6, r.Hosts.Count);
        Assert.Equal(Protocol.Rdp, r.Hosts[0].Protocol);
        Assert.Equal(Protocol.Rdp, r.Hosts[1].Protocol);
        Assert.Equal(Protocol.Ssh, r.Hosts[2].Protocol);
        Assert.Null(r.Hosts[3].Protocol);
        Assert.Equal(Protocol.Ssh, r.Hosts[4].Protocol);
        Assert.Equal(Protocol.Rdp, r.Hosts[5].Protocol);
    }

    [Fact]
    public void 잘못된_포트는_무시하고_경고()
    {
        var r = TableImport.Parse("name,ip,port\na,10.0.0.1,abc\nb,10.0.0.2,70000\n");
        Assert.Equal(2, r.Hosts.Count);
        Assert.All(r.Hosts, h => Assert.Null(h.Port));
        Assert.Contains(r.Warnings, w => w.Contains("2-3"));
    }

    [Fact]
    public void 주소_열이_없으면_주소_모양의_이름을_주소로()
    {
        var r = TableImport.Parse("hostname,os,점프\nweb01.example.com,CentOS 7,bastion\nweb02,CentOS 7,\n");
        var h = Assert.Single(r.Hosts);
        Assert.Equal("web01.example.com", h.Address);
        Assert.Equal(Protocol.Ssh, h.Protocol);
        Assert.Equal("bastion", h.JumpHost);
        Assert.Equal(2, r.Warnings.Count);   // 주소 열 없음 + 건너뛴 줄 3
        Assert.Contains(r.Warnings, w => w.Contains(": 3"));
    }

    [Fact]
    public void 알수없는_열은_무시하고_같은_종류_열은_처음_값()
    {
        var r = TableImport.Parse("서버명,자산번호,사설 IP,공인 IP\na,A-001,,1.2.3.4\nb,A-002,10.0.0.2,1.2.3.5\n");
        Assert.Equal("1.2.3.4", r.Hosts[0].Address);
        Assert.Equal("10.0.0.2", r.Hosts[1].Address);
        Assert.DoesNotContain(r.Hosts, h => (h.Note ?? "").Contains("A-00"));
    }

    [Fact]
    public void 닫히지_않은_따옴표_경고()
    {
        var r = TableImport.Parse("name,ip,note\na,10.0.0.1,\"열린 따옴표\n");
        Assert.Single(r.Hosts);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void 행번호_압축()
    {
        Assert.Equal("1-3, 5, 7-8", TableImport.FormatRows(new[] { 3, 1, 2, 5, 7, 8 }));
    }
}
