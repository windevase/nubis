using System;
using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>호스트 사용자 태그(키=값) — add_host / update_host 의 tags</summary>
[Collection("settings")]
public class HostTagsTests
{
    private static KeyValuePair<string, string?> T(string k, string? v) => new(k, v);

    [Fact]
    public void 넘긴_키만_추가_변경_삭제한다()
    {
        var cur = new Dictionary<string, string> { ["env"] = "prod", ["용도"] = "웹" };
        var changes = new List<HostTags.Change>();
        var r = HostTags.Apply(cur, new[] { T("용도", "Rocky Linux 9.3"), T("env", ""), T("owner", "infra") }, false, changes);

        Assert.NotNull(r);
        Assert.Equal("Rocky Linux 9.3", r!["용도"]);
        Assert.Equal("infra", r["owner"]);
        Assert.False(r.ContainsKey("env"));
        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.Key == "env" && c.From == "prod" && c.To == null);
        // 원본은 그대로
        Assert.Equal("prod", cur["env"]);
    }

    [Fact]
    public void 키는_대소문자를_구분하지_않고_원래_표기를_유지한다()
    {
        var cur = new Dictionary<string, string> { ["Env"] = "dev" };
        var r = HostTags.Apply(cur, new[] { T("env", "prod") }, false, new());
        Assert.Equal("prod", Assert.Single(r!).Value);
        Assert.Equal("Env", Assert.Single(r!).Key);
        Assert.Throws<ArgumentException>(() => HostTags.Apply(null, new[] { T("a", "1"), T("A", "2") }, false, new()));
    }

    [Fact]
    public void replace_면_넘기지_않은_태그를_지우고_비면_null()
    {
        var cur = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        var r = HostTags.Apply(cur, new[] { T("b", "2") }, true, new());
        Assert.Equal("b", Assert.Single(r!).Key);
        var changes = new List<HostTags.Change>();
        Assert.Null(HostTags.Apply(cur, Array.Empty<KeyValuePair<string, string?>>(), true, changes));
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void 같은_값이면_변경이_없다()
    {
        var cur = new Dictionary<string, string> { ["a"] = "1" };
        var changes = new List<HostTags.Change>();
        HostTags.Apply(cur, new[] { T("a", " 1 "), T("zzz", "") }, false, changes);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData("", "v")]
    [InlineData("a=b", "v")]
    [InlineData("a,b", "v")]
    [InlineData("a|b", "v")]
    [InlineData("ad‮min", "v")]
    [InlineData("k", "x​y")]
    [InlineData("k", "a\u0007b")]
    public void 잘못된_키_값은_거부한다(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => HostTags.Apply(null, new[] { T(key, value) }, false, new()));
    }

    [Fact]
    public void 길이와_개수를_제한하고_줄바꿈은_공백으로()
    {
        Assert.Throws<ArgumentException>(() => HostTags.Apply(null, new[] { T(new string('k', HostTags.MaxKeyLength + 1), "v") }, false, new()));
        Assert.Throws<ArgumentException>(() => HostTags.Apply(null, new[] { T("k", new string('v', HostTags.MaxValueLength + 1)) }, false, new()));
        var many = new List<KeyValuePair<string, string?>>();
        for (var i = 0; i <= HostTags.MaxTags; i++) many.Add(T("k" + i, "v"));
        Assert.Throws<ArgumentException>(() => HostTags.Apply(null, many, false, new()));

        var r = HostTags.Apply(null, new[] { T("note", "첫 줄\r\n둘째 줄") }, false, new());
        Assert.Equal("첫 줄 둘째 줄", r!["note"]);
    }

    [Fact]
    public void 편집_칸_글자를_키값으로_읽고_쓴다()
    {
        var parsed = HostTags.ParseText("용도 = 웹 서버\r\n\r\n  env=prod\nurl=http://a/?x=1\nold=");
        Assert.Equal(4, parsed.Count);
        Assert.Equal(T("용도", "웹 서버"), parsed[0]);
        Assert.Equal(T("url", "http://a/?x=1"), parsed[2]);   // 값 안의 = 는 그대로
        Assert.Equal(T("old", null), parsed[3]);
        Assert.Throws<FormatException>(() => HostTags.ParseText("값만 있는 줄"));
        Assert.Throws<FormatException>(() => HostTags.ParseText("=값"));

        var tags = HostTags.Apply(null, parsed, true, new());
        Assert.Equal(3, tags!.Count);
        Assert.Equal(tags, HostTags.Apply(null, HostTags.ParseText(HostTags.ToText(tags)), true, new()));
        Assert.Equal("", HostTags.ToText(null));
    }

    [Fact]
    public void 일괄_편집은_적은_키만_바꾸고_나머지는_호스트마다_유지한다()
    {
        var a = new HostEntry { Address = "a", Tags = new() { ["env"] = "dev", ["owner"] = "kim" } };
        var b = new HostEntry { Address = "b", CloudProfileId = Guid.NewGuid() };
        var c = new HostEntry { Address = "c", Tags = new() { ["env"] = "prod" } };
        var edit = new BulkEdit { Tags = new() { T("env", "prod"), T("owner", null) } };
        Assert.False(edit.IsEmpty);

        Assert.Equal(2, edit.Apply(new[] { a, b, c }));   // c 는 이미 같은 값 → 안 바뀜, b(클라우드)도 적용
        Assert.Equal("prod", Assert.Single(a.Tags!).Value);
        Assert.Equal("prod", b.Tags!["env"]);
        Assert.Equal("prod", c.Tags!["env"]);
    }

    [Fact]
    public void 일괄_편집_태그_개수_제한은_미리_알린다()
    {
        var full = new HostEntry { Label = "full", Address = "f", Tags = new() };
        for (var i = 0; i < HostTags.MaxTags; i++) full.Tags["k" + i] = "v";
        var edit = new BulkEdit { Tags = new() { T("new", "v") } };
        Assert.NotNull(edit.TagLimitError(new[] { new HostEntry { Address = "x" }, full }));
        Assert.Null(new BulkEdit { Tags = new() { T("k0", "w") } }.TagLimitError(new[] { full }));
        Assert.Throws<ArgumentException>(() => BulkEdit.ValidateTags(new[] { T("a=b", "v") }));
    }

    [Fact]
    public void 바뀐_키_계산()
    {
        var before = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        var after = new Dictionary<string, string> { ["A"] = "1", ["c"] = "3" };
        var keys = McpHostRules.ChangedTagKeys(before, after);
        Assert.Equal(2, keys.Count);
        Assert.Contains("b", keys);
        Assert.Contains("c", keys);
    }

    [Fact]
    public void 복제본의_태그는_원본과_따로()
    {
        var h = new HostEntry { Label = "web", Address = "10.0.0.1", Protocol = Protocol.Ssh, Port = 22, Tags = new() { ["용도"] = "웹" } };
        var c = h.Clone();
        c.Tags!["용도"] = "DB";
        Assert.Equal("웹", h.Tags["용도"]);
    }

    [Fact]
    public void 목록_부제목에_태그_값이_보이고_동기화는_태그를_건드리지_않는다()
    {
        var h = new HostEntry { Label = "db", Address = "10.0.0.9", Protocol = Protocol.Ssh, Port = 22, Tags = new() { ["용도"] = "백업" } };
        Assert.EndsWith(" · 백업", h.Subtitle);
        Assert.Equal("용도=백업", h.TagsText);

        var p = new CloudProfile();
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { new("i-1", "web", "api", "running", "ap-northeast-2", "10.0.0.1", "", false, "Linux") }, hosts);
        hosts[0].Tags = new() { ["용도"] = "Rocky Linux 9.3" };
        CloudSync.Apply(p, new() { new("i-1", "web", "api-v2", "running", "ap-northeast-2", "10.0.0.1", "", false, "Linux") }, hosts);
        Assert.Equal("Rocky Linux 9.3", hosts[0].Tags!["용도"]);
        Assert.Contains("Rocky Linux 9.3", hosts[0].Subtitle);
    }
}
