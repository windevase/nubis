using System;
using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>호스트·Vault·클라우드 동기화 버그 수정 회귀 테스트 (2026-11-14 전체 점검)</summary>
[Collection("settings")]
public class HostFixTests
{
    private static CloudInstance Inst(string id, string priv = "10.0.0.1", string pub = "", string state = "running") =>
        new(id, "web", "", state, "ap-northeast-2", priv, pub, false, "Linux");

    [Fact]
    public void 주소가_잠깐_없는_서버는_동기화에서_지우지_않는다()
    {
        var p = new CloudProfile { IncludeStopped = true };
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Inst("i-1") }, hosts);
        var h = Assert.Single(hosts);
        h.PromptAnswers = new() { ["Choose:"] = "1" };

        var (_, _, removed) = CloudSync.Apply(p, new() { Inst("i-1", priv: "", state: "stopped") }, hosts);
        Assert.Equal(0, removed);
        Assert.Same(h, Assert.Single(hosts));
        Assert.Equal("stopped", h.CloudState);
        Assert.Equal("1", h.PromptAnswers!["Choose:"]);
    }

    [Fact]
    public void EIP_를_잠깐_떼도_공인_IP_로_접속_설정은_유지한다()
    {
        var p = new CloudProfile();
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Inst("i-1", pub: "203.0.113.5") }, hosts);
        var h = hosts[0];
        h.UsePublicIp = true;

        CloudSync.Apply(p, new() { Inst("i-1", pub: "") }, hosts);
        Assert.True(h.UsePublicIp);
        Assert.Equal("10.0.0.1", h.EffectiveAddress);   // 공인 IP 가 없는 동안은 사설로
        CloudSync.Apply(p, new() { Inst("i-1", pub: "203.0.113.9") }, hosts);
        Assert.Equal("203.0.113.9", h.EffectiveAddress);
    }

    [Fact]
    public void 복제본은_기억한_답을_원본과_공유하지_않는다()
    {
        var h = new HostEntry { PromptAnswers = new() { ["Q:"] = "1" } };
        var c = h.Clone();
        c.PromptAnswers!["Q:"] = "2";
        c.PromptAnswers["New:"] = "x";
        Assert.Equal("1", h.PromptAnswers["Q:"]);
        Assert.False(h.PromptAnswers.ContainsKey("New:"));
    }

    [Fact]
    public void 직접_고른_Vault_에_비밀번호가_없으면_프로필_기본_비밀번호를_채우지_않는다()
    {
        var v = new VaultEntry { Name = "t-keyonly", Username = "deploy", Key = "" };
        VaultStore.Upsert(v);
        try
        {
            var p = new CloudProfile { DefaultUsername = "ec2-user" };
            p.DefaultPassword = "profile-pw";
            var h = new HostEntry { Protocol = Protocol.Ssh, CloudProfileId = p.Id, VaultId = v.Id };
            var c = h.WithDefaults(p);
            Assert.Equal("deploy", c.Username);
            Assert.True(string.IsNullOrEmpty(c.Password));
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 비밀번호_없는_Vault_계정도_같은_사용자명이면_입력한_비밀번호를_저장한다()
    {
        var v = new VaultEntry { Name = "t-nopw", Username = "deploy" };
        VaultStore.Upsert(v);
        try
        {
            var h = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.7", VaultId = v.Id };
            Assert.Equal(v.Id, MainWindow.CredentialSaveTarget(h, null, "deploy")?.Id);
            Assert.Null(MainWindow.CredentialSaveTarget(h, null, "other"));
        }
        finally { VaultStore.Remove(v.Id); }
    }
}
