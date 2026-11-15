using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>import_hosts MCP 도구의 순수 부분 (인자 검사 · 줄 고르기 · 결과 표 · 경로 차단)</summary>
public class McpImportToolTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly ImportDefaults D = new() { SshPort = 22, RdpPort = 3389 };

    private static ImportedHost H(string name, string address, int? port = null, Protocol? proto = null) =>
        new() { Name = name, Address = address, Port = port, Protocol = proto };

    // ───────────── 인자 검사 ─────────────

    [Fact]
    public void 기본값은_미리보기와_현재_사용자()
    {
        var a = McpImport.ParseArgs(J("""{"source":"ad"}"""));
        Assert.Equal(McpImportSource.Ad, a.Source);
        Assert.False(a.Apply);
        Assert.False(a.UpdateExisting);
        Assert.True(a.ServersOnly);
        Assert.True(a.SkipDisabled);
        Assert.True(a.UseCurrentUser);
        Assert.False(a.ResolveIp);
        Assert.Empty(a.Select);

        var s = McpImport.ParseArgs(J("""{"source":"ssh_config"}"""));
        Assert.Equal(McpImportSource.SshConfig, s.Source);
        Assert.Null(s.Path);
        Assert.Null(s.Text);
    }

    [Theory]
    [InlineData("""{}""")]                                                   // source 없음
    [InlineData("""{"source":"excel"}""")]                                   // 모르는 출처
    [InlineData("""{"source":"table"}""")]                                   // text/path 없음
    [InlineData("""{"source":"ansible"}""")]
    [InlineData("""{"source":"table","text":"   "}""")]                      // 빈 text
    [InlineData("""{"source":"table","text":"a","path":"b.csv"}""")]         // 둘 다
    [InlineData("""{"source":"table","path":"C:\\x\\hosts.xlsx"}""")]        // 엑셀 파일
    [InlineData("""{"source":"ad","text":"a"}""")]                           // AD 에 text
    [InlineData("""{"source":"ssh_config","domain":"corp.local"}""")]        // AD 전용 인자
    [InlineData("""{"source":"ad","use_current_user":false}""")]             // 계정 없음
    [InlineData("""{"source":"ad","ad_vault":"svc","use_current_user":true}""")]
    [InlineData("""{"source":"ad","active_within_days":0}""")]
    [InlineData("""{"source":"ad","active_within_days":5000}""")]
    [InlineData("""{"source":"ad","active_within_days":1.5}""")]
    [InlineData("""{"source":"table","text":"a","group":"x","group_prefix":"y"}""")]
    [InlineData("""{"source":"table","text":"a","apply":"maybe"}""")]       // bool 형식 오류
    [InlineData("""{"source":"table","text":"a","select":[1,2]}""")]        // 문자열 배열 아님
    [InlineData("""{"source":"table","text":5}""")]                          // 문자열 아님
    [InlineData("""{"source":"table","text":"a","foo":1}""")]                // 모르는 인자
    public void 잘못된_인자는_거부(string json) =>
        Assert.Throws<McpToolException>(() => McpImport.ParseArgs(J(json)));

    [Theory]
    [InlineData("password")]
    [InlineData("ad_password")]
    [InlineData("client_secret")]
    public void 비밀번호_인자는_받지_않는다(string key)
    {
        var ex = Assert.Throws<McpToolException>(() => McpImport.ParseArgs(J($$"""{"source":"ad","{{key}}":"x"}""")));
        Assert.Contains("ad_vault", ex.Message);
    }

    [Fact]
    public void ad_vault_를_주면_현재_사용자를_쓰지_않는다()
    {
        var a = McpImport.ParseArgs(J("""{"source":"ad","ad_vault":"svc-ldap","domain":" corp.local ","active_within_days":"30","resolve_ip":true}"""));
        Assert.False(a.UseCurrentUser);
        Assert.Equal("svc-ldap", a.AdVault);
        Assert.Equal("corp.local", a.Domain);
        Assert.Equal(30, a.ActiveWithinDays);
        Assert.True(a.ResolveIp);
    }

    [Fact]
    public void 등록_옵션을_읽는다()
    {
        var a = McpImport.ParseArgs(J("""{"source":"table","text":"x","group":"","ssh_vault":"ops","update_existing":true,"select":["web01"," 10.0.0.2 ",""],"apply":true}"""));
        Assert.Equal("", a.Group);   // "" = 그룹 없음 (null 과 구분)
        Assert.Null(a.GroupPrefix);
        Assert.Equal("ops", a.SshVault);
        Assert.Null(a.RdpVault);
        Assert.True(a.UpdateExisting);
        Assert.True(a.Apply);
        Assert.Equal(new[] { "web01", "10.0.0.2" }, a.Select);

        // select 는 문자열 하나도 받는다
        Assert.Equal(new[] { "db" }, McpImport.ParseArgs(J("""{"source":"table","text":"x","select":"db"}""")).Select);
    }

    // ───────────── 줄 고르기 ─────────────

    private static ImportPlan SamplePlan(out HostEntry existing, out HostEntry cloud)
    {
        existing = new HostEntry { Label = "old", Address = "10.0.0.9", Port = 22, Protocol = Protocol.Ssh };
        cloud = new HostEntry { Label = "c", Address = "10.0.0.8", Port = 22, Protocol = Protocol.Ssh, CloudProfileId = Guid.NewGuid() };
        var hosts = new List<ImportedHost>
        {
            H("web01", "10.0.0.1"),
            H("web02", "10.0.0.2", 2222),
            H("dup", "10.0.0.1"),             // 앞 줄과 같음
            H("bad", "not valid!"),            // 오류
            H("old-renamed", "10.0.0.9"),      // 기존 호스트
            H("cloud-x", "10.0.0.8"),          // 클라우드 호스트
            H("win", "10.0.0.3", proto: Protocol.Rdp),
        };
        return ImportMerge.Plan(hosts, new List<HostEntry> { existing, cloud }, D);
    }

    [Fact]
    public void select_가_없으면_새_호스트_전체()
    {
        var plan = SamplePlan(out _, out _);
        var (sel, unmatched) = McpImport.SelectRows(plan.Rows, Array.Empty<string>(), updateExisting: false);
        Assert.Empty(unmatched);
        Assert.Equal(new[] { "web01", "web02", "win" }, sel.Select(r => r.NameText));

        // update_existing 이면 클라우드가 아닌 기존 호스트도
        var (sel2, _) = McpImport.SelectRows(plan.Rows, Array.Empty<string>(), updateExisting: true);
        Assert.Equal(new[] { "web01", "web02", "old-renamed", "win" }, sel2.Select(r => r.NameText));
    }

    [Fact]
    public void select_는_이름_주소_주소포트로_고른다()
    {
        var plan = SamplePlan(out _, out _);
        var (sel, unmatched) = McpImport.SelectRows(plan.Rows, new[] { "WIN", "10.0.0.2:2222", "10.0.0.1" }, updateExisting: false);
        Assert.Empty(unmatched);
        Assert.Equal(new[] { "web01", "web02", "win" }, sel.Select(r => r.NameText));   // 원래 순서
    }

    [Fact]
    public void 등록할_수_없는_줄을_고르면_unmatched()
    {
        var plan = SamplePlan(out _, out _);
        var (sel, unmatched) = McpImport.SelectRows(plan.Rows, new[] { "web01", "old-renamed", "cloud-x", "bad", "nope" }, updateExisting: false);
        Assert.Equal(new[] { "web01" }, sel.Select(r => r.NameText));
        Assert.Equal(new[] { "old-renamed", "cloud-x", "bad", "nope" }, unmatched);

        var msg = McpImport.UnmatchedMessage(plan.Rows, unmatched, updateExisting: false);
        Assert.Contains("update_existing=true", msg);
        Assert.Contains("가져온 목록에 없음", msg);

        // update_existing 이어도 클라우드 호스트는 고를 수 없다
        var (_, unmatched2) = McpImport.SelectRows(plan.Rows, new[] { "old-renamed", "cloud-x" }, updateExisting: true);
        Assert.Equal(new[] { "cloud-x" }, unmatched2);
    }

    // ───────────── 결과 표 ─────────────

    [Fact]
    public void 미리보기는_상태별_개수와_표를_보여_준다()
    {
        var plan = SamplePlan(out _, out _);
        var (sel, _) = McpImport.SelectRows(plan.Rows, Array.Empty<string>(), false);
        var text = McpImport.FormatPreview("table (붙여 넣은 내용)", plan.Rows, sel, new List<string> { "경고 하나" }, false);
        Assert.Contains("New 3 · Existing 2 · Duplicate 1 · Invalid 1", text);
        Assert.Contains("추가 3개 · 갱신 0개", text);
        Assert.Contains("경고 하나", text);
        Assert.Contains("* | web01 | 10.0.0.1:22 | SSH", text);
        Assert.Contains("  | dup | 10.0.0.1:22 | SSH", text);
        Assert.Contains("apply=true", text);
    }

    [Fact]
    public void 미리보기는_줄_수를_자르고_표_구분자를_바꾼다()
    {
        var hosts = Enumerable.Range(1, 250).Select(i => H($"h|{i}", $"10.1.{i / 250}.{i % 250}")).ToList();
        var plan = ImportMerge.Plan(hosts, new List<HostEntry>(), D);
        var text = McpImport.FormatPreview("x", plan.Rows, plan.Rows, new List<string>(), false, maxRows: 200);
        Assert.Contains("… 외 50줄", text);
        Assert.DoesNotContain("h|1 ", text);
        Assert.Contains("h/1 ", text);
    }

    [Fact]
    public void 승인_대상은_앞_30개와_나머지_개수()
    {
        var hosts = Enumerable.Range(1, 40).Select(i => H($"s{i}", $"10.2.0.{i}")).ToList();
        var plan = ImportMerge.Plan(hosts, new List<HostEntry>(), D);
        var lines = McpImport.ApprovalTargets(plan.Rows);
        Assert.Equal(31, lines.Count);
        Assert.StartsWith("+ s1  (SSH 10.2.0.1:22)", lines[0]);
        Assert.Contains("10", lines[^1]);
    }

    [Fact]
    public void 등록_결과에_추가_갱신_건너뜀과_대기_중_변경을_적는다()
    {
        var r = new ImportApplyResult(3, 1, 2, new List<string> { "점프 경고" });
        var text = McpImport.FormatApplied(r, new List<string> { "파서 경고" }, changedWhileWaiting: 1);
        Assert.Contains("추가 3개 · 갱신 1개 · 건너뜀 2개", text);
        Assert.Contains("1개는 등록·갱신하지 않았습니다", text);
        Assert.Contains("점프 경고", text);
        Assert.Contains("파서 경고", text);
    }

    // ───────────── 경로 검사 ─────────────

    private const string DataDir = @"C:\Users\a\AppData\Roaming\Nubis";
    private const string SshDir = @"C:\Users\a\.ssh";

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Roaming\Nubis\claude\mcp-config.json")]
    [InlineData(@"C:\Users\a\AppData\Roaming\Nubis\users\x\hosts.json")]
    [InlineData(@"C:\work\id_rsa")]
    [InlineData(@"C:\work\server.pem")]
    [InlineData(@"C:\work\putty.ppk")]
    [InlineData(@"\\?\C:\work\hosts.csv")]
    [InlineData(@"C:\work\hosts.csv:stream")]
    public void 데이터_폴더_개인키_장치경로는_읽지_않는다(string path) =>
        Assert.NotNull(McpImport.BlockedPathReason(path, DataDir, DataDir, SshDir, allowSshDir: true));

    [Fact]
    public void ssh_폴더는_ssh_config_출처만_허용()
    {
        Assert.Null(McpImport.BlockedPathReason(@"C:\Users\a\.ssh\config", DataDir, DataDir, SshDir, allowSshDir: true));
        Assert.NotNull(McpImport.BlockedPathReason(@"C:\Users\a\.ssh\config", DataDir, DataDir, SshDir, allowSshDir: false));
        // ssh_config 출처라도 개인키 이름은 거부
        Assert.NotNull(McpImport.BlockedPathReason(@"C:\Users\a\.ssh\id_ed25519", DataDir, DataDir, SshDir, allowSshDir: true));
        // 공개키는 이름으로 막지 않는다
        Assert.Null(McpImport.BlockedPathReason(@"C:\work\id_ed25519.pub", DataDir, DataDir, SshDir, allowSshDir: false));
        Assert.Null(McpImport.BlockedPathReason(@"C:\work\inventory.ini", DataDir, DataDir, SshDir, allowSshDir: false));
    }

    [Fact]
    public void 개인키_내용을_알아본다()
    {
        Assert.True(McpImport.LooksLikePrivateKey("-----BEGIN OPENSSH PRIVATE KEY-----\nabc"));
        Assert.True(McpImport.LooksLikePrivateKey("PuTTY-User-Key-File-3: ssh-ed25519"));
        Assert.False(McpImport.LooksLikePrivateKey("Host web\n  HostName 10.0.0.1"));
    }
}
