using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>서버 OS 로고 판별 (os-release · 클라우드 OS 글 · 배너) · 저장 규칙 · 로고 그림</summary>
public class ServerOsKindTests
{
    // ----- os-release 샘플 (배포판이 실제로 내놓는 모양 그대로) -----
    private const string Ubuntu2204 = "PRETTY_NAME=\"Ubuntu 22.04.4 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"22.04\"\nVERSION=\"22.04.4 LTS (Jammy Jellyfish)\"\n" +
                                      "VERSION_CODENAME=jammy\nID=ubuntu\nID_LIKE=debian\nHOME_URL=\"https://www.ubuntu.com/\"\nUBUNTU_CODENAME=jammy\n";
    private const string Debian12 = "PRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\nNAME=\"Debian GNU/Linux\"\nVERSION_ID=\"12\"\nID=debian\n";
    private const string Rocky9 = "NAME=\"Rocky Linux\"\nVERSION=\"9.3 (Blue Onyx)\"\nID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\nVERSION_ID=\"9.3\"\nPLATFORM_ID=\"platform:el9\"\n";
    private const string Alma9 = "NAME=\"AlmaLinux\"\nID=\"almalinux\"\nID_LIKE=\"rhel centos fedora\"\nVERSION_ID=\"9.4\"\n";
    private const string CentOs7 = "NAME=\"CentOS Linux\"\nVERSION=\"7 (Core)\"\nID=\"centos\"\nID_LIKE=\"rhel fedora\"\nVERSION_ID=\"7\"\n";
    private const string Rhel9 = "NAME=\"Red Hat Enterprise Linux\"\nID=\"rhel\"\nID_LIKE=\"fedora\"\nVERSION_ID=\"9.4\"\n";
    private const string Fedora40 = "NAME=\"Fedora Linux\"\nVERSION=\"40 (Server Edition)\"\nID=fedora\nVERSION_ID=40\n";
    private const string Al2023 = "NAME=\"Amazon Linux\"\nVERSION=\"2023\"\nID=\"amzn\"\nID_LIKE=\"fedora\"\nVERSION_ID=\"2023\"\n";
    private const string Al2 = "NAME=\"Amazon Linux\"\nVERSION=\"2\"\nID=\"amzn\"\nID_LIKE=\"centos rhel fedora\"\nVERSION_ID=\"2\"\n";
    private const string Sles15 = "NAME=\"SLES\"\nVERSION=\"15-SP5\"\nID=\"sles\"\nID_LIKE=\"suse\"\n";
    private const string Leap = "NAME=\"openSUSE Leap\"\nID=\"opensuse-leap\"\nID_LIKE=\"suse opensuse\"\n";
    private const string Arch = "NAME=\"Arch Linux\"\nPRETTY_NAME=\"Arch Linux\"\nID=arch\nBUILD_ID=rolling\n";
    private const string Alpine = "NAME=\"Alpine Linux\"\nID=alpine\nVERSION_ID=3.20.0\n";
    private const string Oracle = "NAME=\"Oracle Linux Server\"\nID=\"ol\"\nID_LIKE=\"fedora\"\nVERSION_ID=\"9.4\"\n";
    private const string Mint = "NAME=\"Linux Mint\"\nID=linuxmint\nID_LIKE=\"ubuntu debian\"\n";
    private const string Raspbian = "PRETTY_NAME=\"Raspbian GNU/Linux 11 (bullseye)\"\nID=raspbian\nID_LIKE=debian\n";
    private const string Gentoo = "NAME=Gentoo\nID=gentoo\nPRETTY_NAME=\"Gentoo Linux\"\n";
    private const string FreeBsd = "NAME=FreeBSD\nVERSION=\"14.0-RELEASE\"\nID=freebsd\nANSI_COLOR=\"0;31\"\n";

    public static IEnumerable<object?[]> OsReleaseSamples() => new[]
    {
        new object?[] { Ubuntu2204, "ubuntu debian", ServerOsKind.Ubuntu },
        new object?[] { Debian12, "debian", ServerOsKind.Debian },
        new object?[] { Rocky9, "rocky rhel centos fedora", ServerOsKind.Rocky },
        new object?[] { Alma9, "almalinux rhel centos fedora", ServerOsKind.Alma },
        new object?[] { CentOs7, "centos rhel fedora", ServerOsKind.CentOs },
        new object?[] { Rhel9, "rhel fedora", ServerOsKind.RedHat },
        new object?[] { Fedora40, "fedora", ServerOsKind.Fedora },
        new object?[] { Al2023, "amzn fedora", ServerOsKind.Amazon },
        new object?[] { Al2, "amzn centos rhel fedora", ServerOsKind.Amazon },
        new object?[] { Sles15, "sles suse", ServerOsKind.Suse },
        new object?[] { Leap, "opensuse-leap suse opensuse", ServerOsKind.OpenSuse },
        new object?[] { Arch, "arch", ServerOsKind.Arch },
        new object?[] { Alpine, "alpine", ServerOsKind.Alpine },
        // RHEL 호환이지만 다른 배포판 — ID_LIKE(fedora) 로 Fedora 로고를 붙이지 않는다
        new object?[] { Oracle, "ol fedora", ServerOsKind.Linux },
        // ID 를 모르면 ID_LIKE 폴백
        new object?[] { Mint, "linuxmint ubuntu debian", ServerOsKind.Ubuntu },
        new object?[] { Raspbian, "raspbian debian", ServerOsKind.Debian },
        // 표에 없는 Linux → 일반 Linux
        new object?[] { Gentoo, "gentoo", ServerOsKind.Linux },
        // os-release 가 있어도 Linux 가 아님 → 로고 없음 (기존 아이콘)
        new object?[] { FreeBsd, "freebsd", null },
    };

    [Theory]
    [MemberData(nameof(OsReleaseSamples))]
    public void os_release_샘플을_배포판과_로고로_판별한다(string text, string distro, string? kind)
    {
        Assert.Equal(distro, ServerOsKind.ParseOsRelease(text));
        Assert.Equal(kind, ServerOsKind.FromDistro(distro));
        Assert.Equal(kind, ServerOsKind.Resolve(distro, null, null));
    }

    [Fact]
    public void CRLF_주석_작은따옴표_BOM_이스케이프를_견딘다()
    {
        Assert.Equal("ubuntu debian", ServerOsKind.ParseOsRelease("﻿# comment\r\nNAME='Ubuntu'\r\nID='ubuntu'\r\nID_LIKE=\"debian\"\r\n"));
        Assert.Equal("ubuntu", ServerOsKind.ParseOsRelease("ID=\"ub\\untu\"\n"));
        // 같은 키가 여러 번이면 마지막 값 (셸과 같게), 대문자는 소문자로
        Assert.Equal("debian", ServerOsKind.ParseOsRelease("ID=ubuntu\nID=Debian\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\n   \n")]
    [InlineData("NAME=\"Something\"\nVERSION=1\n")]          // ID 없음
    [InlineData("ID=\nID_LIKE=\n")]                         // 빈 값
    [InlineData("ID=\"ubuntu\nID_LIKE=\"")]                  // 짝이 안 맞는 따옴표
    [InlineData("ID=ubu ntu;rm -rf /\n")]                    // 규격에 맞지 않는 글자
    [InlineData("ID=$(reboot)\n")]
    [InlineData("\0\u0001\u0002binaryÿþ")]          // 깨진 이진 데이터
    [InlineData("=ubuntu\nID\n")]
    public void 깨진_입력은_null(string? text) => Assert.Null(ServerOsKind.ParseOsRelease(text));

    [Fact]
    public void 이상한_값은_버리고_맞는_낱말만_남기며_개수와_길이를_제한한다()
    {
        // ID 가 규격 밖이면 ID_LIKE 만
        Assert.Equal("debian", ServerOsKind.ParseOsRelease("ID=\"bad id!\"\nID_LIKE=\"debian <script>\"\n"));
        Assert.Equal("a b c d", ServerOsKind.ParseOsRelease("ID=a\nID_LIKE=\"b c d e f a\"\n"));
        Assert.Null(ServerOsKind.ParseOsRelease("ID=" + new string('x', 33) + "\n"));
        // 크기 상한 뒤의 내용은 보지 않는다
        Assert.Null(ServerOsKind.ParseOsRelease(new string('#', ServerOsKind.MaxOsReleaseBytes) + "\nID=ubuntu\n"));
    }

    [Theory]
    // AWS PlatformDetails
    [InlineData("Linux/UNIX", ServerOsKind.Linux)]
    [InlineData("Red Hat Enterprise Linux", ServerOsKind.RedHat)]
    [InlineData("SUSE Linux", ServerOsKind.Suse)]
    [InlineData("Ubuntu Pro", ServerOsKind.Ubuntu)]
    [InlineData("Windows with SQL Server Standard", ServerOsKind.Windows)]
    // Tencent OsName
    [InlineData("CentOS 7.9 64位", ServerOsKind.CentOs)]
    [InlineData("TencentOS Server 3.1 (TK4)", ServerOsKind.Linux)]   // "centos" 를 품고 있지만 CentOS 가 아님
    [InlineData("OpenCloudOS 8.6", ServerOsKind.Linux)]
    [InlineData("Ubuntu Server 22.04 LTS 64位", ServerOsKind.Ubuntu)]
    [InlineData("Rocky Linux 9.2 64位", ServerOsKind.Rocky)]
    // GCP 라이선스 · Azure 이미지 · OCI
    [InlineData("https://www.googleapis.com/compute/v1/projects/debian-cloud/global/licenses/debian-12-bookworm", ServerOsKind.Debian)]
    [InlineData("https://www.googleapis.com/compute/v1/projects/rhel-cloud/global/licenses/rhel-9-server", ServerOsKind.RedHat)]
    [InlineData("windows-server-2022-dc", ServerOsKind.Windows)]
    [InlineData("0001-com-ubuntu-server-jammy 22_04-lts-gen2", ServerOsKind.Ubuntu)]
    [InlineData("almalinux 9-gen2", ServerOsKind.Alma)]
    [InlineData("Canonical Ubuntu", ServerOsKind.Ubuntu)]
    [InlineData("Oracle Linux", ServerOsKind.Linux)]
    [InlineData("Linux", ServerOsKind.Linux)]
    // 가져오기 (AD operatingSystem 등)
    [InlineData("Windows Server 2019 Datacenter 10.0 (17763)", ServerOsKind.Windows)]
    [InlineData("Amazon Linux 2023", ServerOsKind.Amazon)]
    [InlineData("openSUSE Leap 15.5", ServerOsKind.OpenSuse)]
    [InlineData("Arch Linux", ServerOsKind.Arch)]
    [InlineData("Alpine 3.19", ServerOsKind.Alpine)]
    [InlineData("Fedora CoreOS", ServerOsKind.Fedora)]
    [InlineData("FreeBSD 14", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 클라우드_가져오기_OS_글을_판별한다(string? os, string? kind) => Assert.Equal(kind, ServerOsKind.FromCloudOs(os));

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.6", "ubuntu")]
    [InlineData("SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3", "debian")]
    [InlineData("SSH-2.0-OpenSSH_9.6 FreeBSD-20240104", "freebsd")]
    [InlineData("SSH-2.0-OpenSSH_8.7", null)]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", null)]
    [InlineData("SSH-2.0-RebexSSH_5.0.8372.0", null)]
    [InlineData(null, null)]
    public void 배너의_배포판_이름을_짐작한다(string? banner, string? distro) => Assert.Equal(distro, ServerOsKind.FromBanner(banner));

    [Fact]
    public void 우선순위_접속한_배포판_그다음_클라우드_그다음_배너()
    {
        // AWS 는 Linux/UNIX 만 — 접속해 알아낸 Ubuntu 가 이긴다
        Assert.Equal(ServerOsKind.Ubuntu, ServerOsKind.Resolve("ubuntu debian", "Linux/UNIX", SshServerOs.Unix));
        // 배포판을 모르면(일반 Linux) 클라우드의 구체적인 값
        Assert.Equal(ServerOsKind.RedHat, ServerOsKind.Resolve("gentoo", "Red Hat Enterprise Linux", null));
        Assert.Equal(ServerOsKind.Linux, ServerOsKind.Resolve(null, "Linux/UNIX", null));
        // 접속해 보니 FreeBSD — 클라우드 "Linux/UNIX" 로 Tux 를 붙이지 않는다
        Assert.Null(ServerOsKind.Resolve("freebsd", "Linux/UNIX", SshServerOs.Unix));
        // Windows 는 배너로 충분
        Assert.Equal(ServerOsKind.Windows, ServerOsKind.Resolve(null, null, SshServerOs.Windows));
        // 배너가 unix 라는 것만으로는 Linux 인지 모른다 (BSD · 네트워크 장비) → 기존 아이콘
        Assert.Null(ServerOsKind.Resolve(null, null, SshServerOs.Unix));
        Assert.Null(ServerOsKind.Resolve(null, null, null));
    }

    [Fact]
    public void 배너_짐작은_비어_있을_때만_os_release_는_다르면_저장()
    {
        Assert.True(ServerOsKind.ShouldStore(null, "ubuntu", fromOsRelease: false));
        Assert.False(ServerOsKind.ShouldStore("linuxmint ubuntu debian", "ubuntu", fromOsRelease: false));
        Assert.True(ServerOsKind.ShouldStore("ubuntu", "linuxmint ubuntu debian", fromOsRelease: true));
        Assert.False(ServerOsKind.ShouldStore("ubuntu debian", "ubuntu debian", fromOsRelease: true));
        Assert.False(ServerOsKind.ShouldStore(null, null, fromOsRelease: true));
    }

    [Fact]
    public void 모든_로고_종류에_고정된_그림과_이름이_있다()
    {
        foreach (var kind in ServerOsKind.All)
        {
            var img = BrandIcons.OsLogo(kind);
            Assert.NotNull(img);
            Assert.True(img!.IsFrozen, kind);
            // 같은 그림을 다시 쓴다 (항목마다 새로 만들지 않음)
            Assert.Same(img, BrandIcons.OsLogo(kind));
            Assert.Equal(24, img.Width, 3);
            Assert.Equal(24, img.Height, 3);
            Assert.False(string.IsNullOrEmpty(ServerOsKind.DisplayName(kind)));
        }
        Assert.Null(BrandIcons.OsLogo(null));
        Assert.Null(BrandIcons.OsLogo("beos"));
        Assert.Null(new OsLogoConverter().Convert(null, typeof(ImageSource), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 호스트의_OsKind_와_변경_알림()
    {
        var h = new HostEntry { Address = "10.0.0.5" };
        Assert.Null(h.OsKind);
        Assert.False(h.HasOsLogo);
        var changed = new List<string?>();
        h.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        // 값을 바꾸는 쪽이 따로 알리지 않아도 로고가 바뀐다 (편집기 · MCP · 동기화 · 접속 판별 모두)
        h.OsDistro = "rocky rhel centos fedora";
        Assert.Equal(ServerOsKind.Rocky, h.OsKind);
        Assert.True(h.HasOsLogo);
        Assert.Contains(nameof(HostEntry.OsKind), changed);
        Assert.Contains(nameof(HostEntry.HasOsLogo), changed);
        // 복제(Clone)는 값을 그대로 — 같은 서버
        Assert.Equal(h.OsDistro, h.Clone().OsDistro);
        // 툴팁에 OS 이름
        Assert.Contains("OS: Rocky Linux", h.ToolTipText);
    }

    [Fact]
    public void 클라우드_동기화는_접속해_알아낸_배포판을_덮어쓰지_않는다()
    {
        var p = new CloudProfile { Provider = CloudProvider.Aws, Name = "os", DisplayName = "OS" };
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { new("i-1", "web", "", "running", "ap-northeast-2", "10.0.0.1", "", false, "Linux/UNIX") }, hosts);
        var h = hosts.Single();
        Assert.Equal(ServerOsKind.Linux, h.OsKind);
        h.OsDistro = "ubuntu debian";
        Assert.Equal(ServerOsKind.Ubuntu, h.OsKind);

        // 다시 동기화 (주소가 바뀌어도 같은 인스턴스) — OsDistro 유지, Os 만 갱신
        CloudSync.Apply(p, new() { new("i-1", "web", "", "running", "ap-northeast-2", "10.0.0.2", "", false, "Linux/UNIX") }, hosts);
        Assert.Equal("ubuntu debian", hosts.Single().OsDistro);
        Assert.Equal(ServerOsKind.Ubuntu, hosts.Single().OsKind);
    }

    [Theory]
    [InlineData(nameof(HostEntry.Os), "Windows Server 2022")]
    [InlineData(nameof(HostEntry.SshBannerOs), SshServerOs.Windows)]
    [InlineData(nameof(HostEntry.OsDistro), "ubuntu debian")]
    public void OS_값을_바꾸거나_지우면_로고_변경을_알린다(string property, string value)
    {
        var h = new HostEntry { Address = "10.0.0.5" };
        var changed = new List<string?>();
        h.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var prop = typeof(HostEntry).GetProperty(property)!;
        prop.SetValue(h, value);
        Assert.True(h.HasOsLogo);
        Assert.Contains(nameof(HostEntry.OsKind), changed);
        Assert.Contains(nameof(HostEntry.HasOsLogo), changed);
        Assert.Contains(nameof(HostEntry.ToolTipText), changed);   // 카드 툴팁의 OS 줄 (OneWay 바인딩)
        // 같은 값이면 알리지 않는다
        changed.Clear();
        prop.SetValue(h, value);
        Assert.Empty(changed);
        // 주소를 바꿔 지우면(편집기 · update_host) 화면의 옛 로고도 사라져야 한다
        prop.SetValue(h, null);
        Assert.False(h.HasOsLogo);
        Assert.Contains(nameof(HostEntry.HasOsLogo), changed);
    }

    [Fact]
    public void 클라우드에서_OS_가_다른_종류로_바뀌면_배포판을_지운다()
    {
        var p = new CloudProfile { Provider = CloudProvider.Tencent, Name = "os", DisplayName = "OS" };
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { new("ins-1", "web", "", "RUNNING", "ap-seoul-1", "10.0.0.1", "", false, "CentOS 7.9 64位") }, hosts);
        var h = hosts.Single();
        h.OsDistro = "centos rhel fedora";
        // 같은 종류 안의 글 변화(버전)는 유지
        CloudSync.Apply(p, new() { new("ins-1", "web", "", "RUNNING", "ap-seoul-1", "10.0.0.1", "", false, "CentOS 7.6 64位") }, hosts);
        Assert.Equal("centos rhel fedora", h.OsDistro);
        // 시스템 재설치로 Ubuntu 가 됨 — 옛 CentOS 배포판을 지우고 클라우드 값으로 (다음 접속 때 다시 읽음)
        CloudSync.Apply(p, new() { new("ins-1", "web", "", "RUNNING", "ap-seoul-1", "10.0.0.1", "", false, "Ubuntu Server 22.04 LTS 64位") }, hosts);
        Assert.Null(h.OsDistro);
        Assert.Equal(ServerOsKind.Ubuntu, h.OsKind);
        // 처음 받는 OS 글 · 빈 글로는 지우지 않는다
        Assert.False(ServerOsKind.CloudOsReplaced(null, "Linux/UNIX"));
        Assert.False(ServerOsKind.CloudOsReplaced("Linux/UNIX", ""));
        Assert.False(ServerOsKind.CloudOsReplaced("Linux/UNIX", "Linux/UNIX"));
    }

    [Fact]
    public void 재설치로_배포판을_지우면_같은_주소_포트도_다시_읽는다()
    {
        var id = Guid.NewGuid();
        Assert.True(SshServerOs.MarkProbed(id, "10.0.0.1", 22));
        Assert.False(SshServerOs.MarkProbed(id, "10.0.0.1", 22));
        Assert.True(SshServerOs.MarkProbed(id, "10.0.0.1", 2222));   // 포트가 다르면 다른 서버일 수 있다
        var other = Guid.NewGuid();
        Assert.True(SshServerOs.MarkProbed(other, "10.0.0.1", 22));

        // 클라우드 동기화가 OS 재설치를 알아채 배포판을 지우면 읽은 기록도 지운다 (그 호스트만)
        var p = new CloudProfile { Provider = CloudProvider.Tencent, Name = "os", DisplayName = "OS" };
        var hosts = new List<HostEntry> { new() { Id = id, CloudProfileId = p.Id, CloudInstanceId = "ins-9", Address = "10.0.0.1", Os = "CentOS 7.9", OsDistro = "centos rhel fedora" } };
        CloudSync.Apply(p, new() { new("ins-9", "web", "", "RUNNING", "ap-seoul-1", "10.0.0.1", "", false, "Ubuntu Server 22.04") }, hosts);
        Assert.Null(hosts[0].OsDistro);
        Assert.True(SshServerOs.MarkProbed(id, "10.0.0.1", 22));
        Assert.False(SshServerOs.MarkProbed(other, "10.0.0.1", 22));
    }

    [Fact]
    public void 재설치_전에_시작한_판별이_뒤늦게_끝나도_저장하지_않는다()
    {
        // CodeRabbit (PR #50): CentOS 를 읽는 중에 클라우드 동기화가 재설치(→ Ubuntu)를 알아채 배포판을 지움 → 옛 CentOS 결과가 새 값을 덮어쓰던 경로
        var p = new CloudProfile { Provider = CloudProvider.Tencent, Name = "os", DisplayName = "OS" };
        var h = new HostEntry { CloudProfileId = p.Id, CloudInstanceId = "ins-7", Address = "10.0.0.7", Port = 22, Os = "CentOS 7.9" };
        var before = SshServerOs.ProbeGeneration(h.Id);   // 재설치 전에 시작한 판별이 기록한 세대
        Assert.True(SshServerOs.ShouldApplyDistro(h, "10.0.0.7", 22, "centos rhel fedora", true, before));

        CloudSync.Apply(p, new() { new("ins-7", "web", "", "RUNNING", "ap-seoul-1", "10.0.0.7", "", false, "Ubuntu Server 22.04") }, new List<HostEntry> { h });
        Assert.Null(h.OsDistro);
        // 같은 주소·포트지만 초기화 전에 시작한 결과는 버린다 (os-release · 배너 짐작 모두)
        Assert.False(SshServerOs.ShouldApplyDistro(h, "10.0.0.7", 22, "centos rhel fedora", true, before));
        Assert.False(SshServerOs.ShouldApplyDistro(h, "10.0.0.7", 22, "centos", false, before));
        // 초기화 뒤에 시작한 판별은 새 세대라 그대로 저장된다
        var after = SshServerOs.ProbeGeneration(h.Id);
        Assert.NotEqual(before, after);
        Assert.True(SshServerOs.ShouldApplyDistro(h, "10.0.0.7", 22, "ubuntu debian", true, after));
        // 다른 호스트의 세대는 그대로
        var other = new HostEntry { Address = "10.0.0.8", Port = 22 };
        Assert.True(SshServerOs.ShouldApplyDistro(other, "10.0.0.8", 22, "ubuntu debian", true, SshServerOs.ProbeGeneration(other.Id)));
        // 호스트가 없거나 주소가 바뀌었으면 여전히 버린다
        Assert.False(SshServerOs.ShouldApplyDistro(null, "10.0.0.7", 22, "ubuntu debian", true, after));
        Assert.False(SshServerOs.ShouldApplyDistro(h, "10.0.0.9", 22, "ubuntu debian", true, after));
    }

    [Fact]
    public void 읽는_사이에_주소나_포트를_바꿨으면_결과를_버린다()
    {
        var h = new HostEntry { Address = "10.0.0.1", PublicIp = "3.3.3.3", Port = 22 };
        Assert.True(SshServerOs.SameEndpoint(h, "10.0.0.1", 22));
        Assert.True(SshServerOs.SameEndpoint(h, "3.3.3.3", 22));     // 공인 IP 로 접속한 결과
        Assert.False(SshServerOs.SameEndpoint(h, "10.0.0.1", 2222)); // 포트를 바꾼 뒤 도착한 옛 포트의 결과
        Assert.False(SshServerOs.SameEndpoint(h, "10.0.0.9", 22));   // 주소를 바꾼 뒤
    }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.6", true)]
    [InlineData("SSH-2.0-OpenSSH_8.7", true)]
    [InlineData("SSH-2.0-dropbear_2022.83", true)]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", false)]   // Windows — os-release 없음
    [InlineData("SSH-2.0-Cisco-1.25", false)]                // 네트워크 장비에는 두 번째 채널을 열지 않는다
    [InlineData("SSH-2.0-HUAWEI-1.5", false)]
    [InlineData("SSH-2.0-RebexSSH_5.0.8372.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 배포판은_OpenSSH_Dropbear_Unix_서버에서만_읽는다(string? banner, bool probe) =>
        Assert.Equal(probe, SshServerOs.ShouldProbe(banner));

    [Fact]
    public void get_host_결과에_배포판과_로고_이름()
    {
        var h = new HostEntry { Address = "10.0.0.7", OsDistro = "almalinux rhel centos fedora" };
        var d = McpHostParity.Detail(h, null, "-", null, 0, new List<int>());
        Assert.Contains("Linux 배포판(접속 때 /etc/os-release 또는 SSH 배너로 판별): almalinux rhel centos fedora", d);
        Assert.Contains("OS 로고: AlmaLinux", d);
        Assert.DoesNotContain("Linux 배포판", McpHostParity.Detail(new HostEntry { Address = "10.0.0.8" }, null, "-", null, 0, new List<int>()));
    }
}
