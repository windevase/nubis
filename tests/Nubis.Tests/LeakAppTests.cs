using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 앱 전역 누수 점검: 목록 행 · Vault 항목 바인딩 경로(INotifyPropertyChanged), 클라우드 동기화 상태(MissCounts · 숨긴 인스턴스)가
/// 동기화를 오래 반복해도 쌓이지 않는지
/// </summary>
public class LeakAppTests
{
    // ----- 바인딩 원본: INotifyPropertyChanged 가 없으면 WPF 가 PropertyDescriptor.AddValueChanged 로 원본을 전역 표에 붙잡는다 -----

    [Fact]
    public void 카드_줄과_Vault_항목은_INotifyPropertyChanged_를_구현한다()
    {
        Assert.IsAssignableFrom<INotifyPropertyChanged>(new CardRow { Hosts = new List<HostEntry>() });
        Assert.IsAssignableFrom<INotifyPropertyChanged>(new VaultEntry());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SubscribeCapturing(INotifyPropertyChanged source)
    {
        var listener = new byte[1024];
        source.PropertyChanged += (_, _) => GC.KeepAlive(listener);
        return new WeakReference(listener);
    }

    [Fact]
    public void 알림이_없는_행은_구독자를_붙잡지_않는다()
    {
        var row = new CardRow { Hosts = new List<HostEntry> { new() } };
        var vault = new VaultEntry { Name = "v" };
        var w1 = SubscribeCapturing(row);
        var w2 = SubscribeCapturing(vault);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(w1.IsAlive);
        Assert.False(w2.IsAlive);
        GC.KeepAlive(row);
        GC.KeepAlive(vault);
    }

    [Fact]
    public void Vault_항목_저장_형식은_그대로다()
    {
        var e = new VaultEntry { Name = "web", Username = "ec2-user" };
        var json = JsonSerializer.Serialize(e);
        Assert.DoesNotContain("PropertyChanged", json);
        var back = JsonSerializer.Deserialize<VaultEntry>(json)!;
        Assert.Equal("web", back.Name);
        Assert.Equal("ec2-user", back.Username);
        // 수정용 사본도 그대로
        Assert.Equal("web", e.Clone().Name);
    }

    // ----- 클라우드 동기화 상태가 반복 동기화로 쌓이지 않는지 -----

    private static CloudProfile Profile() => new() { Provider = CloudProvider.Aws, Name = "leak", DisplayName = "Leak" };

    private static CloudInstance Inst(string id) => new(id, "web-" + id, "", "running", "ap-northeast-2", "10.0.0.1", "", false, "Linux");

    private static CloudFetchResult Ok(params CloudInstance[] items) => new(items.ToList(), new());

    [Fact]
    public void 인스턴스가_계속_바뀌어도_미확인_횟수와_호스트가_쌓이지_않는다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        for (var k = 0; k < 200; k++)
        {
            // 매번 새 인스턴스 하나가 생기고 이전 것은 종료됨 (오토스케일링)
            CloudSync.ApplyResult(p, Ok(Inst("i-keep"), Inst("i-" + k)), hosts);
            Assert.True(p.MissCounts.Count <= 2, $"MissCounts {p.MissCounts.Count}");
            Assert.True(hosts.Count <= 4, $"hosts {hosts.Count}");
        }
    }

    [Fact]
    public void 사용자가_지운_호스트의_미확인_횟수는_다음_동기화에서_버린다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        CloudSync.ApplyResult(p, Ok(Inst("i-a"), Inst("i-b")), hosts);
        CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);
        Assert.Equal(1, p.MissCounts["i-b"]);
        hosts.RemoveAll(h => h.CloudInstanceId == "i-b");   // 숨기지 않고 목록에서만 지움
        CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);
        Assert.Empty(p.MissCounts);
    }

    [Fact]
    public void 숨긴_인스턴스가_많아도_다시_추가하지_않고_종료된_것은_정리한다()
    {
        var p = Profile();
        for (var i = 0; i < 500; i++) p.HiddenInstances.Add("i-gone-" + i);
        p.HiddenInstances.Add("i-hidden");
        var hosts = new List<HostEntry>();
        var all = Enumerable.Range(0, 300).Select(i => Inst("i-" + i)).Append(Inst("i-hidden")).ToArray();

        CloudSync.ApplyResult(p, Ok(all), hosts);
        Assert.Equal(300, hosts.Count);
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "i-hidden");

        // 두 번 연속 안 보인 숨긴 인스턴스는 목록에서 빠진다 (계속 커지지 않게)
        CloudSync.ApplyResult(p, Ok(all), hosts);
        Assert.Equal(new[] { "i-hidden" }, p.HiddenInstances);
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "i-hidden");
    }
}
