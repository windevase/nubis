using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 리뷰 3차: 보이지 않는 문자 판별(이모지 ZWJ 오탐·빠진 서식 문자), 감사 로그용 계정 표시(번역하지 않음),
/// sftp_read 비밀 경로 보강(/proc/*/environ·셸 기록), docker -H/--host 자동 승인 제외
/// </summary>
public class Review3McpExecTests
{
    private static bool Sh(string c) => ReadOnlyCommands.IsReadOnly(c, CommandShell.Sh);

    // ---------------------------------------------------------------- 보이지 않는 문자

    [Theory]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467 팀 서버")]   // 가족 이모지 (ZWJ 시퀀스)
    [InlineData("\u2764\uFE0F\u200D\U0001F525 hot")]                     // 불타는 하트
    [InlineData("\U0001F3F3\uFE0F\u200D\U0001F308")]                     // 무지개 깃발
    [InlineData("\u26F9\uFE0F\u200D\u2642\uFE0F")]                       // 기호 블록 이모지 + ZWJ
    [InlineData("한글 서버 😀")]
    public void 이모지_시퀀스의_ZWJ_는_정상_이름이다(string s)
    {
        Assert.False(McpHelpers.HasInvisibleChars(s));
        Assert.Equal(s, McpHelpers.VisibleControlChars(s));
    }

    [Theory]
    [InlineData("pro\u200Dd")]           // 글자 사이 ZWJ
    [InlineData("\u200D\U0001F469")]     // 맨 앞 ZWJ
    [InlineData("\U0001F469\u200D")]     // 맨 끝 ZWJ
    [InlineData("pro\u200Cd")]           // ZWNJ
    [InlineData("we\u00ADb")]            // 소프트 하이픈
    [InlineData("a\u061Cb")]             // 아랍 문자 표시 (양방향)
    [InlineData("a\u180Eb")]             // 몽골 모음 구분자
    [InlineData("a\u2060b")]             // 단어 결합자
    [InlineData("a\u2064b")]
    [InlineData("a\u2029b")]
    public void 빠졌던_서식_문자와_글자_사이_ZWJ_를_알아본다(string s)
    {
        Assert.True(McpHelpers.HasInvisibleChars(s));
        Assert.Contains("\\u{", McpHelpers.VisibleControlChars(s));
    }

    [Fact]
    public void 줄_구분자는_승인_창에서_드러난다()
    {
        Assert.Equal("a\\u{2028}b", McpHelpers.VisibleControlChars("a\u2028b"));
        Assert.Equal("a\\u{00AD}b", McpHelpers.VisibleControlChars("a\u00ADb"));
    }

    // ---------------------------------------------------------------- 감사 로그용 계정 표시

    [Fact]
    public void 감사_로그용_계정_표시는_번역하지_않은_한국어다()
    {
        Assert.Equal("계정 없음", McpHelpers.AccountLabel("", null, translate: false));
        Assert.Equal("계정 root", McpHelpers.AccountLabel("root", null, translate: false));
        Assert.Equal("계정 root · 이 호스트에 저장된 계정", McpHelpers.AccountLabel("root", "이 호스트에 저장된 계정", translate: false));
        Assert.Contains("\\u{202E}", McpHelpers.AccountLabel("ro\u202Eot", null, translate: false));
    }

    [Fact]
    public void 감사_로그용_계정_출처는_화면용과_같은_분기를_따른다()
    {
        var own = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.1", ProtectedPassword = "x" };
        Assert.Equal("이 호스트에 저장된 계정", McpHelpers.CredentialSourceForAudit(own, null));

        var profile = new CloudProfile { DefaultUsername = "ec2-user" };
        var cloud = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.2" };
        Assert.Equal("클라우드 프로필 기본 계정", McpHelpers.CredentialSourceForAudit(cloud, profile));

        // 한국어 화면에서는 화면용 문구와 같다 (테스트는 기본 언어 = 한국어)
        if (!L.IsEnglish)
        {
            Assert.Equal(own.CredentialSourceText(null), McpHelpers.CredentialSourceForAudit(own, null));
            Assert.Equal(cloud.CredentialSourceText(profile), McpHelpers.CredentialSourceForAudit(cloud, profile));
        }
    }

    // ---------------------------------------------------------------- sftp_read 비밀 경로 보강

    [Theory]
    [InlineData("/proc/self/environ")]
    [InlineData("/proc/1234/environ")]
    [InlineData("/proc/1/task/1/environ")]
    [InlineData("/root/.bash_history")]
    [InlineData("/home/u/.mysql_history")]
    [InlineData("/home/u/.psql_history")]
    [InlineData("/etc/sudoers.d/90-cloud-init-users")]
    public void 환경_변수와_명령_기록은_비밀_경로다(string path) => Assert.True(McpHelpers.IsSensitiveRemotePath(path));

    [Theory]
    [InlineData("/proc/cpuinfo")]
    [InlineData("/proc/self/status")]
    [InlineData("/proc/meminfo")]
    [InlineData("/var/log/history.log")]
    public void 일반_proc_파일은_비밀_경로가_아니다(string path) => Assert.False(McpHelpers.IsSensitiveRemotePath(path));

    // ---------------------------------------------------------------- docker 데몬 주소 바꾸기

    [Theory]
    [InlineData("docker -H ssh://user@evil.example ps")]
    [InlineData("docker -Hssh://user@evil.example ps")]
    [InlineData("docker -H tcp://10.0.0.9:2375 ps")]
    [InlineData("docker --host ssh://user@evil.example ps")]
    [InlineData("docker --host=tcp://10.0.0.9:2375 ps")]
    [InlineData("docker --ho tcp://10.0.0.9:2375 ps")]
    [InlineData("docker ps -H tcp://10.0.0.9:2375")]
    [InlineData("DOCKER_HOST=ssh://user@evil.example docker ps")]
    [InlineData("env DOCKER_HOST=ssh://user@evil.example docker ps")]
    [InlineData("export DOCKER_HOST=ssh://user@evil.example; docker ps")]
    public void docker_의_데몬_주소_바꾸기는_자동_승인하지_않는다(string c) => Assert.False(Sh(c));

    [Theory]
    [InlineData("docker ps -a")]
    [InlineData("docker logs --tail 50 web")]
    [InlineData("docker --context prod ps")]
    public void docker_조회는_계속_자동_승인된다(string c) => Assert.True(Sh(c));
}
