using System;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class SettingsCatalogTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public SettingsCatalogTests() => SettingsCatalog.FontInstalled = n => n is "Cascadia Mono" or "D2Coding";

    [Fact]
    public void 바뀌는_값만_계획에_넣는다()
    {
        var s = new AppSettings { FontSize = 14, TerminalTheme = TerminalThemes.All[0].Id };
        var plan = SettingsCatalog.Plan(s, J($$"""{"FontSize": 16, "terminaltheme": "{{TerminalThemes.All[0].Id}}", "HighlightIp": "false"}"""));
        Assert.Equal(new[] { "FontSize", "HighlightIp" }, plan.Select(p => p.Def.Key));
        Assert.Equal(16, plan[0].New);
        Assert.Equal(false, plan[1].New);
        Assert.Equal(14, s.FontSize);   // 계획만 세우고 바꾸지는 않는다
    }

    [Fact]
    public void 잘못된_값이_하나라도_있으면_전부_거부한다()
    {
        var s = new AppSettings();
        var ex = Assert.Throws<ArgumentException>(() => SettingsCatalog.Plan(s, J("""{"FontSize": 40, "FontFamily": "Comic Sans", "Nope": 1, "TerminalTheme": "x"}""")));
        Assert.Contains("FontSize", ex.Message);
        Assert.Contains("FontFamily", ex.Message);
        Assert.Contains("Nope", ex.Message);
        Assert.Contains("TerminalTheme", ex.Message);
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Plan(s, J("{}")));
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Plan(s, J("""{"DefaultSshPort": 70000}""")));
    }

    [Fact]
    public void Claude_는_자동_승인을_켤_수_없고_끄기만_할_수_있다()
    {
        var off = new AppSettings { AutoApproveReadOnly = false };
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Plan(off, J("""{"AutoApproveReadOnly": true}""")));
        var on = new AppSettings { AutoApproveReadOnly = true };
        var plan = SettingsCatalog.Plan(on, J("""{"AutoApproveReadOnly": false}"""));
        Assert.True(plan.Single().Def.Security);
    }

    [Fact]
    public void 보안_설정은_승인_대상으로_표시하고_클라우드_경로는_다루지_않는다()
    {
        Assert.True(SettingsCatalog.Find("RdpCertWarning")!.Security);
        Assert.True(SettingsCatalog.Find("SshHostKeyPrompt")!.Security);
        Assert.False(SettingsCatalog.Find("FontSize")!.Security);
        Assert.Null(SettingsCatalog.Find("CloudPaths"));
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Plan(new AppSettings(), J("""{"CloudPaths": {}}""")));
    }

    [Fact]
    public void 설정_설명에_모든_키와_현재_값이_나온다()
    {
        var text = SettingsCatalog.Describe(new AppSettings { FontSize = 13 });
        foreach (var d in SettingsCatalog.All) Assert.Contains(d.Key + " |", text);
        Assert.Contains("FontSize | 13 |", text);
    }

    [Fact]
    public void 설정_파일_직접_수정과_자동_승인_우회를_막는다()
    {
        var rules = ClaudeGuard.DenyRules(@"C:\Users\a\AppData\Roaming\Nubis");
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/Nubis/settings.json*)", rules);
        Assert.Contains("PowerShell(*AutoApproveReadOnly*)", rules);
        Assert.Contains("Bash(*AutoApproveReadOnly*)", rules);
    }
}
