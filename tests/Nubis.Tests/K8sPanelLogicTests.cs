using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리소스 패널의 화면 무관 판단: 파드 상태 · 출력 잘림 · 같은 행 비교 · 목록 맞추기 · 새로고침 간격 · 오류 문구</summary>
[Collection("settings")]
public class K8sPanelLogicTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static K8sItem Pod(string json) => K8sResources.Parse(K8sKind.Pods, "{\"items\":[" + json + "]}", Now).Single();

    [Fact]
    public void 실행_중인_컨테이너가_있으면_끝난_컨테이너가_있어도_Running()
    {
        var p = Pod("""
            {"metadata":{"name":"job-with-sidecar","namespace":"a"},
             "spec":{"containers":[{"name":"init-like"},{"name":"app"}]},
             "status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}],"containerStatuses":[
               {"name":"init-like","ready":false,"state":{"terminated":{"reason":"Completed","exitCode":0}}},
               {"name":"app","ready":true,"state":{"running":{}}}]}}
            """);
        Assert.Equal("Running", p.Status);
        Assert.Equal("1/2", p.Ready);
        Assert.False(p.Healthy);   // 2개 중 1개만 Ready
    }

    [Fact]
    public void Ready_조건이_없으면_NotReady_로_확인_필요()
    {
        var p = Pod("""
            {"metadata":{"name":"x","namespace":"a"},"spec":{"containers":[{"name":"a"},{"name":"b"}]},
             "status":{"phase":"Running","conditions":[{"type":"Ready","status":"False"}],"containerStatuses":[
               {"name":"a","ready":false,"state":{"terminated":{"reason":"Completed","exitCode":0}}},
               {"name":"b","ready":true,"state":{"running":{}}}]}}
            """);
        Assert.Equal("NotReady", p.Status);
        Assert.False(p.Healthy);
    }

    [Fact]
    public void 모두_끝난_파드는_Completed_정상()
    {
        var p = Pod("""
            {"metadata":{"name":"job-1","namespace":"a"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Succeeded","containerStatuses":[
               {"name":"a","ready":false,"state":{"terminated":{"reason":"Completed","exitCode":0}}}]}}
            """);
        Assert.Equal("Completed", p.Status);
        Assert.True(p.Healthy);
    }

    [Fact]
    public void 오류로_끝난_컨테이너는_사유_또는_종료_코드()
    {
        Assert.Equal("Error", Pod("""
            {"metadata":{"name":"x","namespace":"a"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Failed","containerStatuses":[{"name":"a","state":{"terminated":{"reason":"Error","exitCode":1}}}]}}
            """).Status);
        var p = Pod("""
            {"metadata":{"name":"x","namespace":"a"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Failed","containerStatuses":[{"name":"a","state":{"terminated":{"exitCode":137}}}]}}
            """);
        Assert.Equal("ExitCode:137", p.Status);
        Assert.False(p.Healthy);
    }

    [Fact]
    public void 초기화_컨테이너_상태()
    {
        Assert.Equal("Init:0/2", Pod("""
            {"metadata":{"name":"x","namespace":"a"},"spec":{"initContainers":[{"name":"i1"},{"name":"i2"}],"containers":[{"name":"a"}]},
             "status":{"phase":"Pending","initContainerStatuses":[{"name":"i1","state":{"running":{}}},{"name":"i2","state":{"waiting":{"reason":"PodInitializing"}}}],
               "containerStatuses":[{"name":"a","state":{"waiting":{"reason":"PodInitializing"}}}]}}
            """).Status);
        Assert.Equal("Init:CrashLoopBackOff", Pod("""
            {"metadata":{"name":"x","namespace":"a"},"spec":{"initContainers":[{"name":"i1"}],"containers":[{"name":"a"}]},
             "status":{"phase":"Pending","initContainerStatuses":[{"name":"i1","state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}
            """).Status);
        // 끝난 초기화 컨테이너 · 시작한 사이드카는 건너뛰고 본 컨테이너로
        Assert.Equal("Running", Pod("""
            {"metadata":{"name":"x","namespace":"a"},
             "spec":{"initContainers":[{"name":"i1"},{"name":"proxy","restartPolicy":"Always"}],"containers":[{"name":"a"}]},
             "status":{"phase":"Running","initContainerStatuses":[
               {"name":"i1","state":{"terminated":{"reason":"Completed","exitCode":0}}},
               {"name":"proxy","started":true,"state":{"running":{}}}],
               "containerStatuses":[{"name":"a","ready":true,"state":{"running":{}}}]}}
            """).Status);
    }

    [Fact]
    public void 삭제_중이면_Terminating_노드_유실이면_Unknown()
    {
        Assert.Equal("Terminating", Pod("""
            {"metadata":{"name":"x","namespace":"a","deletionTimestamp":"2026-11-15T12:00:00Z"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Running","containerStatuses":[{"name":"a","ready":true,"state":{"running":{}}}]}}
            """).Status);
        Assert.Equal("Unknown", Pod("""
            {"metadata":{"name":"x","namespace":"a","deletionTimestamp":"2026-11-15T12:00:00Z"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Running","reason":"NodeLost"}}
            """).Status);
    }

    [Fact]
    public async Task 출력이_한도를_넘으면_잘림을_알린다()
    {
        var (small, cut) = await Kubectl.ReadCappedAsync(new StringReader(new string('a', 1000)), 4000);
        Assert.False(cut);
        Assert.Equal(1000, small.Length);
        var json = "{\"items\":[" + string.Join(",", Enumerable.Repeat("{\"metadata\":{\"name\":\"p\"}}", 2000)) + "]}";
        var (text, truncated) = await Kubectl.ReadCappedAsync(new StringReader(json), 4000);
        Assert.True(truncated);
        Assert.True(text.Length < json.Length);
    }

    [Fact]
    public void 같은_행_비교는_컨테이너_목록까지()
    {
        var a = new K8sItem(K8sKind.Pods, "web", "shop", "1/1", "Running", "0", "1d", "node-a", new[] { "app" }, true);
        Assert.True(K8sResources.SameItem(a, a with { Containers = new List<string> { "app" } }));   // 다른 목록 객체라도 내용이 같으면 같다
        Assert.False(K8sResources.SameItem(a, a with { Age = "2d" }));
        Assert.False(K8sResources.SameItem(a, a with { Containers = new[] { "app", "sidecar" } }));
        Assert.False(K8sResources.SameItem(a, a with { Healthy = false }));
    }

    private sealed class Row
    {
        public required string Key;
        public int Value;
    }

    [Fact]
    public void 목록_맞추기는_같은_키의_행을_다시_쓴다()
    {
        var a = new Row { Key = "a", Value = 1 };
        var b = new Row { Key = "b", Value = 1 };
        var c = new Row { Key = "c", Value = 1 };
        var rows = new ObservableCollection<Row> { a, b, c };
        var created = 0;
        var wanted = new[] { ("a", 2), ("c", 1), ("d", 1) };   // b 삭제 · a 값 변경 · d 추가
        var structural = K8sRowSync.Apply(rows, wanted, r => r.Key, w => w.Item1,
            w => { created++; return new Row { Key = w.Item1, Value = w.Item2 }; }, (r, w) => r.Value = w.Item2);
        Assert.True(structural);
        Assert.Equal(new[] { "a", "c", "d" }, rows.Select(r => r.Key));
        Assert.Same(a, rows[0]);
        Assert.Same(c, rows[1]);
        Assert.Equal(2, a.Value);
        Assert.Equal(1, created);

        // 내용만 바뀌면 구성 변경 없음
        Assert.False(K8sRowSync.Apply(rows, new[] { ("a", 3), ("c", 3), ("d", 3) }, r => r.Key, w => w.Item1,
            w => new Row { Key = w.Item1 }, (r, w) => r.Value = w.Item2));
        Assert.All(rows, r => Assert.Equal(3, r.Value));

        // 순서 바뀜: 행 객체는 그대로 옮긴다
        K8sRowSync.Apply(rows, new[] { ("d", 0), ("a", 0) }, r => r.Key, w => w.Item1, w => new Row { Key = w.Item1 }, (r, w) => { });
        Assert.Equal(new[] { "d", "a" }, rows.Select(r => r.Key));
        Assert.Same(a, rows[1]);
    }

    [Fact]
    public void 추가_삭제가_많으면_새로_만든다()
    {
        var current = Enumerable.Range(0, 300).Select(i => "p" + i).ToList();
        Assert.False(K8sRowSync.ShouldRebuild(current, current.Skip(1).Append("new")));
        Assert.True(K8sRowSync.ShouldRebuild(current, current.Take(100)));   // 200개 삭제 (검색어 입력 등)
        Assert.True(K8sRowSync.ShouldRebuild(current, current.Select(k => k + "-v2")));   // 롤아웃으로 이름이 모두 바뀜
    }

    private static Dictionary<NotifyCollectionChangedAction, int> CountChanges(ObservableCollection<Row> rows)
    {
        var counts = new Dictionary<NotifyCollectionChangedAction, int>();
        rows.CollectionChanged += (_, e) => counts[e.Action] = counts.GetValueOrDefault(e.Action) + 1;
        return counts;
    }

    private static void SyncRows(ObservableCollection<Row> rows, IReadOnlyList<string> keys) =>
        K8sRowSync.Apply(rows, keys, r => r.Key, k => k, k => new Row { Key = k }, (r, k) => { });

    [Fact]
    public void 목록_맞추기는_삭제_삽입에_아래_행을_옮기지_않는다()
    {
        var keys = Enumerable.Range(0, 5000).Select(i => $"p{i:D5}").ToList();
        var rows = new ObservableCollection<Row>(keys.Select(k => new Row { Key = k }));
        var originals = rows.ToList();
        var counts = CountChanges(rows);

        // 맨 앞 행 하나 삭제 → RemoveAt 한 번, Move 0
        SyncRows(rows, keys.Skip(1).ToList());
        Assert.Equal(1, counts.GetValueOrDefault(NotifyCollectionChangedAction.Remove));
        Assert.Equal(0, counts.GetValueOrDefault(NotifyCollectionChangedAction.Move));
        Assert.Equal(keys.Skip(1), rows.Select(r => r.Key));

        // 대량 삭제 (짝수 행 모두) → Move 0, 남은 행 객체는 그대로
        counts.Clear();
        var odd = keys.Where((_, i) => i % 2 == 1).ToList();
        SyncRows(rows, odd);
        Assert.Equal(0, counts.GetValueOrDefault(NotifyCollectionChangedAction.Move));
        Assert.Equal(odd, rows.Select(r => r.Key));
        Assert.Same(originals[1], rows[0]);

        // 중간 삽입 → Add 한 번, Move 0
        counts.Clear();
        var inserted = odd.Take(1000).Append("p01999x").Concat(odd.Skip(1000)).ToList();
        SyncRows(rows, inserted);
        Assert.Equal(1, counts.GetValueOrDefault(NotifyCollectionChangedAction.Add));
        Assert.Equal(0, counts.GetValueOrDefault(NotifyCollectionChangedAction.Move));
        Assert.Equal(inserted, rows.Select(r => r.Key));

        // 삭제와 삽입이 섞여도 Move 0
        counts.Clear();
        var mixed = inserted.Where((_, i) => i % 7 != 0).ToList();
        mixed.Insert(10, "new-a");
        mixed.Insert(2000, "new-b");
        SyncRows(rows, mixed);
        Assert.Equal(0, counts.GetValueOrDefault(NotifyCollectionChangedAction.Move));
        Assert.Equal(mixed, rows.Select(r => r.Key));
    }

    [Fact]
    public void 목록_맞추기는_중복_키_행을_하나만_남긴다()
    {
        var a1 = new Row { Key = "a" };
        var rows = new ObservableCollection<Row> { a1, new Row { Key = "a" }, new Row { Key = "b" } };
        SyncRows(rows, new[] { "a", "b" });
        Assert.Equal(new[] { "a", "b" }, rows.Select(r => r.Key));
        Assert.Same(a1, rows[0]);
    }

    [Fact]
    public void 삭제_중이어도_끝난_파드는_원래_상태()
    {
        var done = Pod("""
            {"metadata":{"name":"job","namespace":"a","deletionTimestamp":"2026-11-15T11:59:00Z"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Succeeded","containerStatuses":[{"name":"a","ready":false,"state":{"terminated":{"reason":"Completed","exitCode":0}}}]}}
            """);
        Assert.Equal("Completed", done.Status);
        var failed = Pod("""
            {"metadata":{"name":"job","namespace":"a","deletionTimestamp":"2026-11-15T11:59:00Z"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Failed","containerStatuses":[{"name":"a","ready":false,"state":{"terminated":{"reason":"Error","exitCode":1}}}]}}
            """);
        Assert.Equal("Error", failed.Status);
        var running = Pod("""
            {"metadata":{"name":"web","namespace":"a","deletionTimestamp":"2026-11-15T11:59:00Z"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Running","containerStatuses":[{"name":"a","ready":true,"state":{"running":{}}}]}}
            """);
        Assert.Equal("Terminating", running.Status);
        var lost = Pod("""
            {"metadata":{"name":"web","namespace":"a","deletionTimestamp":"2026-11-15T11:59:00Z"},"spec":{"containers":[{"name":"a"}]},
             "status":{"phase":"Running","reason":"NodeLost","containerStatuses":[{"name":"a","ready":true,"state":{"running":{}}}]}}
            """);
        Assert.Equal("Unknown", lost.Status);
    }

    [Fact]
    public void 네이티브_사이드카는_Ready_분모와_재시작_수에_들어간다()
    {
        var p = Pod("""
            {"metadata":{"name":"web","namespace":"a"},
             "spec":{"initContainers":[{"name":"migrate"},{"name":"proxy","restartPolicy":"Always"}],"containers":[{"name":"app"}]},
             "status":{"phase":"Running","conditions":[{"type":"Initialized","status":"True"},{"type":"Ready","status":"True"}],
               "initContainerStatuses":[
                 {"name":"migrate","ready":false,"restartCount":4,"state":{"terminated":{"reason":"Completed","exitCode":0}}},
                 {"name":"proxy","ready":true,"started":true,"restartCount":1,"state":{"running":{}}}],
               "containerStatuses":[{"name":"app","ready":true,"restartCount":2,"state":{"running":{}}}]}}
            """);
        Assert.Equal("Running", p.Status);
        Assert.Equal("2/2", p.Ready);
        Assert.Equal("3", p.Restarts);   // 사이드카 1 + app 2 (끝난 일반 초기화 컨테이너의 재시작은 빼는 kubectl 과 같게)
        Assert.True(p.Healthy);

        // 사이드카가 시작했지만 Ready 가 아니면 분모에만
        var notReady = Pod("""
            {"metadata":{"name":"web","namespace":"a"},
             "spec":{"initContainers":[{"name":"proxy","restartPolicy":"Always"}],"containers":[{"name":"app"}]},
             "status":{"phase":"Running","conditions":[{"type":"Initialized","status":"True"}],
               "initContainerStatuses":[{"name":"proxy","ready":false,"started":true,"state":{"running":{}}}],
               "containerStatuses":[{"name":"app","ready":true,"state":{"running":{}}}]}}
            """);
        Assert.Equal("1/2", notReady.Ready);
        Assert.False(notReady.Healthy);
    }

    [Fact]
    public void 초기화가_끝났으면_사이드카가_재시작_중이어도_컨테이너_사유를_보인다()
    {
        var p = Pod("""
            {"metadata":{"name":"web","namespace":"a"},
             "spec":{"initContainers":[{"name":"proxy","restartPolicy":"Always"}],"containers":[{"name":"app"}]},
             "status":{"phase":"Running","conditions":[{"type":"Initialized","status":"True"}],
               "initContainerStatuses":[{"name":"proxy","ready":false,"started":false,"restartCount":3,
                 "state":{"waiting":{"reason":"CrashLoopBackOff"}}}],
               "containerStatuses":[{"name":"app","ready":false,"restartCount":2,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}
            """);
        Assert.Equal("CrashLoopBackOff", p.Status);
        Assert.Equal("0/2", p.Ready);
        Assert.Equal("5", p.Restarts);

        // Initialized 가 아니면 초기화 단계 사유 그대로 (재시작 수는 초기화 컨테이너 것)
        var init = Pod("""
            {"metadata":{"name":"web","namespace":"a"},
             "spec":{"initContainers":[{"name":"proxy","restartPolicy":"Always"}],"containers":[{"name":"app"}]},
             "status":{"phase":"Pending","conditions":[{"type":"Initialized","status":"False"}],
               "initContainerStatuses":[{"name":"proxy","ready":false,"started":false,"restartCount":3,
                 "state":{"waiting":{"reason":"CrashLoopBackOff"}}}],
               "containerStatuses":[{"name":"app","ready":false,"restartCount":0,"state":{"waiting":{"reason":"PodInitializing"}}}]}}
            """);
        Assert.Equal("Init:CrashLoopBackOff", init.Status);
        Assert.Equal("3", init.Restarts);
    }

    [Fact]
    public void 스케줄_게이트에_걸린_파드는_SchedulingGated()
    {
        var p = Pod("""
            {"metadata":{"name":"gated","namespace":"a"},"spec":{"schedulingGates":[{"name":"x"}],"containers":[{"name":"a"}]},
             "status":{"phase":"Pending","conditions":[{"type":"PodScheduled","status":"False","reason":"SchedulingGated"}]}}
            """);
        Assert.Equal("SchedulingGated", p.Status);
        Assert.False(p.Healthy);
    }

    [Fact]
    public void 오래_걸린_목록은_새로고침_간격을_늘린다()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), K8sResources.NextRefreshInterval(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(TimeSpan.FromSeconds(12), K8sResources.NextRefreshInterval(TimeSpan.FromSeconds(4)));
        Assert.Equal(TimeSpan.FromSeconds(60), K8sResources.NextRefreshInterval(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void 오류_문구는_비지_않는다()
    {
        Assert.Equal(L.T("응답이 없습니다 (시간 초과)."), K8sResources.ErrorText(new Kubectl.Result(-1, "", "", true)));
        Assert.Equal(L.F("kubectl 이 실패했습니다 (종료 코드 {0}).", 1), K8sResources.ErrorText(new Kubectl.Result(1, "", "  \n", false)));
        Assert.Equal("error: forbidden", K8sResources.ErrorText(new Kubectl.Result(1, "", "\nerror: forbidden\nmore", false)));
    }
}
