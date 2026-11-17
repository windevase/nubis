using System;
using System.Collections.Generic;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// MCP 호스트 도구의 UI 동작 맞추기 (get_host · list_hosts selected_only/vault · list_groups 접힘 · 빠른 연결 · 기본 포트만 · duplicate_host 추가 항목)
/// — UI 와 무관한 판단 (McpHostParity, MainWindow.MakeQuickHost/QuickTargetError)
/// </summary>
[Collection("settings")]
public class McpParityHostsTests
{
    private static VaultEntry NewVault(string name, string user = "u")
    {
        var v = new VaultEntry { Name = name + "-" + Guid.NewGuid().ToString("N")[..6], Username = user, Password = "vault-secret-pw" };
        VaultStore.Upsert(v);
        return v;
    }

    [Fact]
    public void Vault_출처_표시()
    {
        Assert.Equal("직접", McpHostParity.VaultSourceText("host"));
        Assert.Equal("프로필 기본", McpHostParity.VaultSourceText("profile"));
        Assert.Equal("설정 기본", McpHostParity.VaultSourceText("default"));
        Assert.Equal("", McpHostParity.VaultSourceText(null));
    }

    [Fact]
    public void 고른_카드는_다중_선택_우선_없으면_키보드로_고른_카드()
    {
        var a = new HostEntry { Label = "a" };
        var b = new HostEntry { Label = "b" };
        var gone = new HostEntry { Label = "gone" };
        var all = new List<HostEntry> { a, b };

        var (multi, kind) = McpHostParity.Picked(new[] { b, gone, a }, a, all);
        Assert.Equal("multi", kind);
        Assert.Equal(new[] { b, a }, multi);   // 화면 순서 유지, 목록에 없는 호스트 제외

        var (one, kind2) = McpHostParity.Picked(Array.Empty<HostEntry>(), b, all);
        Assert.Equal("highlighted", kind2);
        Assert.Equal(new[] { b }, one);

        var (none, kind3) = McpHostParity.Picked(new[] { gone }, gone, all);
        Assert.Equal("none", kind3);
        Assert.Empty(none);
        Assert.StartsWith("선택 0개", McpHostParity.PickedHeader(0, kind3));
        Assert.StartsWith("선택 2개", McpHostParity.PickedHeader(2, "multi"));
    }

    [Fact]
    public void 그룹_접힘_상태_키와_기본값()
    {
        var canon = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase) { ["web"] = "Web", [""] = "" };
        var local = new HostEntry { Group = " web " };
        var none = new HostEntry { Group = "" };
        var pid = Guid.NewGuid();
        var cloud = new HostEntry { CloudProfileId = pid };

        Assert.Equal("L:Web", McpHostParity.GroupStateKey(local, false, canon));
        Assert.Equal("L:", McpHostParity.GroupStateKey(none, false, canon));
        Assert.Equal("C:" + pid, McpHostParity.GroupStateKey(cloud, true, canon));
        // 프로필이 지워진 클라우드 호스트는 직접 등록 그룹 규칙으로
        Assert.StartsWith("L:", McpHostParity.GroupStateKey(cloud, false, canon));

        var collapsed = new HashSet<string> { "L:Web" };
        var expanded = new HashSet<string>();
        Assert.True(McpHostParity.IsCollapsedState("L:Web", collapsed, expanded));
        Assert.False(McpHostParity.IsCollapsedState("L:", collapsed, expanded));
        // 클라우드 그룹은 기본 접힘, 펼친 것만 기억
        Assert.True(McpHostParity.IsCollapsedState("C:" + pid, collapsed, expanded));
        expanded.Add("C:" + pid);
        Assert.False(McpHostParity.IsCollapsedState("C:" + pid, collapsed, expanded));
    }

    [Fact]
    public void 기본_포트만_바꾸면_클라우드_호스트_포트는_그대로()
    {
        Assert.Equal(3, McpHostParity.PortMoves(true, 3));
        Assert.Equal(0, McpHostParity.PortMoves(false, 3));
    }

    [Fact]
    public void Without_은_지정한_속성만_뺀다()
    {
        var a = JsonDocument.Parse("{\"host\":\"x\",\"label\":\"y\",\"group\":\"\",\"port\":2222,\"tags\":{\"k\":\"v\"}}").RootElement;
        var b = McpHostParity.Without(a, "host", "label", "group");
        Assert.False(b.TryGetProperty("host", out _));
        Assert.False(b.TryGetProperty("label", out _));
        Assert.False(b.TryGetProperty("group", out _));
        Assert.Equal(2222, b.GetProperty("port").GetInt32());
        Assert.Equal("v", b.GetProperty("tags").GetProperty("k").GetString());
    }

    [Fact]
    public void 빠른_연결_대상_검사는_화면_빠른_접속_형식과_같다()
    {
        Assert.Null(MainWindow.QuickTargetError("10.0.0.5", "", null));
        Assert.Null(MainWindow.QuickTargetError("web-01.example.com", "ec2-user", 2222));
        Assert.NotNull(MainWindow.QuickTargetError("", "", null));
        Assert.NotNull(MainWindow.QuickTargetError("10.0.0.5:22", "", null));      // 포트는 port 인자로
        Assert.NotNull(MainWindow.QuickTargetError("root@10.0.0.5", "", null));    // 사용자명은 username 인자로
        Assert.NotNull(MainWindow.QuickTargetError("10.0.0.5 x", "", null));
        Assert.NotNull(MainWindow.QuickTargetError("10.0.0.5/evil", "", null));
        Assert.NotNull(MainWindow.QuickTargetError("10.0.0.5", "a b", null));
        Assert.NotNull(MainWindow.QuickTargetError("10.0.0.5", "a@b", null));
        Assert.NotNull(MainWindow.QuickTargetError("10.0.0.5", "ro\u202Eot", null)); // 보이지 않는 양방향 문자
        Assert.NotNull(MainWindow.QuickTargetError("10.0.0.5", "", 0));
        Assert.NotNull(MainWindow.QuickTargetError("10.0.0.5", "", 70000));

        var h = MainWindow.MakeQuickHost(Protocol.Rdp, "10.0.0.9", "admin", null);
        Assert.Equal(HostEntry.PreferredPort(Protocol.Rdp), h.Port);
        Assert.Equal("admin", h.Username);
        Assert.Equal(Protocol.Rdp, h.Protocol);
        Assert.Equal(2222, MainWindow.MakeQuickHost(Protocol.Ssh, "x", null, 2222).Port);
        Assert.Equal(HostEntry.PreferredPort(Protocol.Ssh), MainWindow.MakeQuickHost(Protocol.Ssh, "x", null, 99999).Port);
        Assert.Equal("SSH admin@x:22", McpHostParity.QuickTargetLabel(new HostEntry { Address = "x", Username = "admin", Port = 22 }));
    }

    [Fact]
    public void 빠른_연결은_설정의_기본_Vault_계정이_쓰일_수_있다()
    {
        var v = NewVault("t-quick-default", "ec2-user");
        var s = SettingsStore.Current;
        var old = s.DefaultSshVaultId;
        s.DefaultSshVaultId = v.Id;
        try
        {
            var h = MainWindow.MakeQuickHost(Protocol.Ssh, "10.9.9.9", "", null);
            // 승인 창에 보여줄 계정 출처 (항상 승인하는 이유)
            Assert.Contains(v.Name, McpHelpers.CredentialSourceForAudit(h, null));
            var (id, source) = h.ResolveVault(null);
            Assert.Equal(v.Id, id);
            Assert.Equal("default", source);
        }
        finally { s.DefaultSshVaultId = old; VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 계정_표시는_실제로_쓰이는_Vault_와_출처()
    {
        var mine = NewVault("t-acc-mine");
        var def = NewVault("t-acc-def");
        var s = SettingsStore.Current;
        var old = s.DefaultSshVaultId;
        s.DefaultSshVaultId = def.Id;
        try
        {
            Assert.EndsWith($" | 계정 {mine.Name}(직접)", McpHostParity.AccountSuffix(new HostEntry { VaultId = mine.Id }, null));
            Assert.EndsWith($" | 계정 {def.Name}(설정 기본)", McpHostParity.AccountSuffix(new HostEntry(), null));
            // 호스트에 비밀번호를 직접 저장했으면 기본 계정은 쓰이지 않는다 → 표시 없음
            Assert.Equal("", McpHostParity.AccountSuffix(new HostEntry { ProtectedPassword = "x" }, null));
            var profileVault = NewVault("t-acc-prof");
            try
            {
                var p = new CloudProfile { SshVaultId = profileVault.Id };
                Assert.EndsWith($"(프로필 기본)", McpHostParity.AccountSuffix(new HostEntry { CloudProfileId = p.Id }, p));
            }
            finally { VaultStore.Remove(profileVault.Id); }
        }
        finally { s.DefaultSshVaultId = old; VaultStore.Remove(mine.Id); VaultStore.Remove(def.Id); }
    }

    [Fact]
    public void 상세_조회는_비밀값을_넣지_않는다()
    {
        var v = NewVault("t-detail");
        try
        {
            var h = new HostEntry
            {
                Label = "db-01", Address = "10.1.1.1", Port = 22, Username = "root",
                VaultId = v.Id,
                KeyPath = @"C:\keys\very-secret-key.pem",
                PromptAnswers = new() { ["Choose [1] Push"] = "secret-answer-1", ["Q2"] = "secret-answer-2" },
                LastConnected = new DateTime(2026, 11, 1, 9, 30, 0),
            };
            h.Password = "plain-secret-pw";
            var text = McpHostParity.Detail(h, null, "Hosts", null, 2, new[] { 3, 5 });

            Assert.DoesNotContain("plain-secret-pw", text);
            Assert.DoesNotContain(h.ProtectedPassword!, text);
            Assert.DoesNotContain("vault-secret-pw", text);
            Assert.DoesNotContain("very-secret-key", text);
            Assert.DoesNotContain("secret-answer", text);
            Assert.DoesNotContain("Choose [1]", text);

            Assert.Contains($"Vault 계정: {v.Name} (직접)", text);
            Assert.Contains("저장된 비밀번호: 있음 · 키 파일 경로: 있음", text);
            Assert.Contains("직접 고른 Vault 계정이 우선이라 지금은 쓰이지 않음", text);
            Assert.Contains("기억한 인증 답: 2개", text);
            Assert.Contains("마지막 접속: 2026-11-01 09:30", text);
            Assert.Contains("점프 서버: 없음", text);
            Assert.Contains("이 호스트를 점프 서버로 쓰는 호스트: 2개", text);
            Assert.Contains("열린 탭: session 3, session 5", text);
            Assert.Contains(h.Id.ToString(), text);
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 상세_조회_RDP_는_NLA_와_게이트웨이()
    {
        var h = new HostEntry { Protocol = Protocol.Rdp, Address = "10.2.2.2", Port = 3389, RdpDisableNla = true, RdpGateway = "gw.example.com:443" };
        var text = McpHostParity.Detail(h, null, "Hosts", null, 0, Array.Empty<int>());
        Assert.Contains("NLA(CredSSP): 끔", text);
        Assert.Contains("RD 게이트웨이: gw.example.com:443", text);
        Assert.Contains("저장된 비밀번호: 없음 · 키 파일 경로: 없음", text);
        Assert.Contains("열린 탭: 없음", text);
        Assert.DoesNotContain("점프 서버:", text);
    }
}
