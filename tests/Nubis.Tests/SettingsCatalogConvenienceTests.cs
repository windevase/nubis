using System;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>Claude 가 바꿀 수 있는 편의 설정 · 여러 줄 붙여넣기 확인(보안) · 자동 동기화 간격 검증</summary>
public class SettingsCatalogConvenienceTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("ShowRecent")]
    [InlineData("ConfirmCloseTab")]
    [InlineData("SmartCopyPaste")]
    [InlineData("CopyOnSelect")]
    [InlineData("SshAutoReconnect")]
    [InlineData("SftpOpenFolderAfterDownload")]
    [InlineData("CloudSyncOnStart")]
    public void 편의_설정은_바로_바꿀_수_있다(string key)
    {
        var def = SettingsCatalog.Find(key);
        Assert.NotNull(def);
        Assert.False(def!.Security);
        Assert.Equal("bool", def.Type);
        var s = new AppSettings();
        var before = (bool)def.Get(s);
        var plan = SettingsCatalog.Plan(s, J($$"""{"{{key}}": {{(!before).ToString().ToLowerInvariant()}}}"""));
        Assert.Equal(!before, plan.Single().New);
        def.Set(s, plan.Single().New);
        Assert.Equal(!before, def.Get(s));
    }

    [Fact]
    public void 여러_줄_붙여넣기_확인은_보안_설정이다()
    {
        var def = SettingsCatalog.Find("ConfirmMultilinePaste");
        Assert.NotNull(def);
        Assert.True(def!.Security);
        var plan = SettingsCatalog.Plan(new AppSettings { ConfirmMultilinePaste = true }, J("""{"ConfirmMultilinePaste": false}"""));
        Assert.True(plan.Single().Def.Security);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    [InlineData("1440", 1440)]
    [InlineData("\"30\"", 30)]
    public void 자동_동기화_간격은_0_또는_5_에서_1440(string json, int expected)
    {
        var plan = SettingsCatalog.Plan(new AppSettings { CloudSyncIntervalMinutes = 7 }, J($$"""{"CloudSyncIntervalMinutes": {{json}}}"""));
        Assert.Equal(expected, plan.Single().New);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("4")]
    [InlineData("1441")]
    [InlineData("-5")]
    [InlineData("true")]
    [InlineData("\"abc\"")]
    public void 자동_동기화_간격의_범위_밖_값은_거부한다(string json)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SettingsCatalog.Plan(new AppSettings(), J($$"""{"CloudSyncIntervalMinutes": {{json}}}""")));
        Assert.Contains("CloudSyncIntervalMinutes", ex.Message);
    }

    [Fact]
    public void 자동_동기화_키는_타이머를_다시_건다()
    {
        Assert.Contains("CloudSyncOnStart", SettingsCatalog.CloudSyncKeys);
        Assert.Contains("CloudSyncIntervalMinutes", SettingsCatalog.CloudSyncKeys);
        Assert.All(SettingsCatalog.CloudSyncKeys, k => Assert.NotNull(SettingsCatalog.Find(k)));
    }

    [Fact]
    public void 로그인_편집기_경로_기본_Vault_는_Claude_가_update_settings_로_바꿀_수_없다()
    {
        Assert.Null(SettingsCatalog.Find("SftpEditor"));
        Assert.Null(SettingsCatalog.Find("Login"));
        Assert.Null(SettingsCatalog.Find("DefaultSshVaultId"));
        Assert.Null(SettingsCatalog.Find("DefaultRdpVaultId"));
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Plan(new AppSettings(), J("""{"SftpEditor": "C:\\x.exe"}""")));
        // 자동 승인은 여전히 켤 수 없다
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Plan(new AppSettings(), J("""{"AutoApproveReadOnly": true}""")));
    }

    [Fact]
    public void 호스트키_저장값을_형식_지문_거부로_나눈다()
    {
        Assert.Equal(("ssh-ed25519", "SHA256:abc", false), MainWindow.SplitKnownHost("ssh-ed25519 SHA256:abc"));
        Assert.Equal(("ssh-rsa", "SHA256:x", true), MainWindow.SplitKnownHost(KnownHostStore.RejectedMark("ssh-rsa SHA256:x")));
        Assert.Equal(("", "x509thumb", false), MainWindow.SplitKnownHost("x509thumb"));
    }
    private static string Ed25519Blob()
    {
        var name = System.Text.Encoding.ASCII.GetBytes("ssh-ed25519");
        var blob = new byte[4 + name.Length + 4 + 32];
        blob[3] = (byte)name.Length;
        name.CopyTo(blob, 4);
        blob[4 + name.Length + 3] = 32;
        for (var i = 0; i < 32; i++) blob[^(i + 1)] = (byte)(i + 1);
        return Convert.ToBase64String(blob);
    }

    [Fact]
    public void 공개키는_형식과_blob_이_맞을_때만_돌려준다()
    {
        var b64 = Ed25519Blob();
        Assert.Equal($"ssh-ed25519 {b64}", MainWindow.SafePublicKeyLine($"ssh-ed25519 {b64}"));
        // 형식 이름이 blob 과 다름 · 개인키 · 여러 줄 · 주석 섞임 · 빈 값은 거부
        Assert.Null(MainWindow.SafePublicKeyLine($"ssh-rsa {b64}"));
        Assert.Null(MainWindow.SafePublicKeyLine("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----"));
        Assert.Null(MainWindow.SafePublicKeyLine($"ssh-ed25519 {b64}\nssh-ed25519 {b64}"));
        Assert.Null(MainWindow.SafePublicKeyLine($"ssh-ed25519 {b64} comment"));
        Assert.Null(MainWindow.SafePublicKeyLine("ssh-ed25519 not-base64!!!!!!!!!!"));
        Assert.Null(MainWindow.SafePublicKeyLine(""));
        Assert.Null(MainWindow.SafePublicKeyLine(null));
    }

    [Fact]
    public void 공개키_주석은_한_줄로_정리된다()
    {
        Assert.Equal("my_key", MainWindow.PublicKeyComment("my key"));
        Assert.Equal("a_b_c", MainWindow.PublicKeyComment("a\nb\u202Ec"));
        Assert.Equal("nubis", MainWindow.PublicKeyComment("   "));
    }
}
