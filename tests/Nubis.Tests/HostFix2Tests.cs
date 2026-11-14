using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>저장(.bak 복구 · 디스크 반영) · known_hosts 읽기 실패 · 비밀번호 비교 · 그룹/선택 대상 계산 · 이전 평문 정리</summary>
public class HostFix2Tests
{
    private static string TempFile(string name) =>
        Path.Combine(AppPaths.DataDir, $"{name}-{Guid.NewGuid():N}.json");

    private static string[]? ReadList(string path) =>
        SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>());

    [Fact]
    public void 저장하면_직전_원본을_bak_으로_남긴다()
    {
        var path = TempFile("bak");
        SafeFile.WriteProtected(path, "[\"v1\"]");
        Assert.False(File.Exists(SafeFile.BackupPath(path)));   // 처음 저장은 바꿀 원본이 없다
        SafeFile.WriteProtected(path, "[\"v2\"]");
        Assert.True(File.Exists(SafeFile.BackupPath(path)));
        Assert.Equal("[\"v1\"]", SafeFile.UnprotectBytes(File.ReadAllBytes(SafeFile.BackupPath(path))));
        Assert.Equal("v2", ReadList(path)!.Single());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void 원본이_0바이트가_되면_bak_으로_복구한다()
    {
        var path = TempFile("zero");
        SafeFile.WriteProtected(path, "[\"old\"]");
        SafeFile.WriteProtected(path, "[\"new\"]");
        File.WriteAllBytes(path, Array.Empty<byte>());   // 전원이 꺼져 0바이트가 된 경우

        var list = SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>(), out var failed);
        Assert.False(failed);
        Assert.Equal("old", list.Single());
        // 원본 자리에 되돌려 써서 다음 실행에서도 읽힌다, 읽지 못한 원본은 .corrupt 사본으로 보관
        Assert.Equal("old", ReadList(path)!.Single());
        Assert.NotEmpty(Directory.GetFiles(AppPaths.DataDir, Path.GetFileName(path) + ".corrupt-*"));
    }

    [Fact]
    public void bak_도_없으면_빈_값과_실패를_알린다()
    {
        var path = TempFile("nobak");
        File.WriteAllText(path, "{ not json");
        var list = SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>(), out var failed);
        Assert.True(failed);
        Assert.Empty(list);
    }

    [Fact]
    public void 평문_설정_파일도_bak_으로_복구한다()
    {
        var path = TempFile("plain");
        SafeFile.WriteAtomic(path, "[\"a\"]");
        SafeFile.WriteAtomic(path, "[\"b\"]");
        File.WriteAllBytes(path, Array.Empty<byte>());
        var list = SafeFile.Load(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>());
        Assert.Equal("a", list.Single());
    }

    [Fact]
    public void 평문을_암호화로_바꿀_때는_평문_bak_을_남기지_않는다()
    {
        var path = TempFile("legacy");
        File.WriteAllText(path, "[\"10.1.2.3\"]");
        Assert.Equal("10.1.2.3", ReadList(path)!.Single());
        Assert.True(SafeFile.IsProtected(File.ReadAllBytes(path)));
        Assert.False(File.Exists(SafeFile.BackupPath(path)));
    }

    [Fact]
    public void known_hosts_를_읽지_못하면_빈_목록이_아니라_확인_불가_표시를_돌려준다()
    {
        var path = AppPaths.KnownHostsFile;
        var bak = SafeFile.BackupPath(path);
        try
        {
            File.Delete(bak);
            File.WriteAllText(path, "{ broken");
            // LoadWarnings 는 병렬로 도는 다른 테스트도 쓰므로 known_hosts 경고만 센다
            int Mine() { lock (SafeFile.LoadWarnings) return SafeFile.LoadWarnings.Count(w => w.Contains("known_hosts") || w.Contains("호스트키")); }
            var warningsBefore = Mine();

            Assert.Equal(KnownHostStore.UnavailableMark, KnownHostStore.Get("10.0.0.1:22"));
            Assert.True(KnownHostStore.LoadFailed);
            var warningsAfterFirst = Mine();
            Assert.True(warningsAfterFirst > warningsBefore);
            // 같은 파일이면 다시 읽지 않는다 (.corrupt 사본·경고가 Get 마다 늘지 않음)
            var corruptCount = Directory.GetFiles(AppPaths.DataDir, "known_hosts.json.corrupt-*").Length;
            Assert.Equal(KnownHostStore.UnavailableMark, KnownHostStore.Get("10.0.0.2:22"));
            Assert.Equal(warningsAfterFirst, Mine());
            Assert.Equal(corruptCount, Directory.GetFiles(AppPaths.DataDir, "known_hosts.json.corrupt-*").Length);

            // 확인 창으로 신뢰한 키는 이번 실행에서만 기억하고, 손상된 원본 위에 쓰지 않는다
            KnownHostStore.Set("10.0.0.1:22", "ssh-ed25519 SHA256:abc");
            Assert.Equal("ssh-ed25519 SHA256:abc", KnownHostStore.Get("10.0.0.1:22"));
            Assert.Equal("{ broken", File.ReadAllText(path));
        }
        finally
        {
            // 파일을 지우면 다시 정상 (빈 목록)
            File.Delete(path);
            File.Delete(bak);
        }
        Assert.Null(KnownHostStore.Get("10.0.0.1:22"));
        Assert.False(KnownHostStore.LoadFailed);
    }

    [Fact]
    public void 같은_비밀번호를_다시_암호화해도_계정이_바뀐_것으로_보지_않는다()
    {
        var a = Secret.Protect("pw-1");
        var b = Secret.Protect("pw-1");
        Assert.NotEqual(a, b);   // DPAPI 암호문은 매번 다르다
        Assert.True(HostEntry.SamePassword(a, b));
        Assert.False(HostEntry.SamePassword(a, Secret.Protect("pw-2")));
        Assert.True(HostEntry.SamePassword(null, null));

        var tab = new HostEntry { Username = "u", Password = "pw-1" };
        var fresh = new HostEntry { Username = "u", Password = "pw-1" };
        Assert.False(tab.RefreshCredentialsFrom(fresh));
        fresh.Password = "pw-2";
        Assert.True(tab.RefreshCredentialsFrom(fresh));
        Assert.Equal("pw-2", tab.Password);
    }

    [Fact]
    public void 편집_패널은_바뀐_칸만_찾는다()
    {
        var before = new[] { "a", "b", "c" };
        Assert.Equal(new[] { false, true, false }, MainWindow.ChangedFields(before, new[] { "a", "x", "c" }));
        // 스냅숏이 없으면(새 호스트·다시 추가) 전부
        Assert.All(MainWindow.ChangedFields(Array.Empty<string>(), new[] { "a", "b" }), b => Assert.True(b));
    }

    [Fact]
    public void 그룹_일괄_작업은_검색으로_숨겨진_호스트까지_대상으로_한다()
    {
        var profile = new CloudProfile();
        var web1 = new HostEntry { Label = "web1", Group = "Web" };
        var web2 = new HostEntry { Label = "web2", Group = " web " };   // 공백·대소문자만 다른 이름은 같은 그룹
        var db = new HostEntry { Label = "db", Group = "DB" };
        var c1 = new HostEntry { Label = "c1", CloudProfileId = profile.Id, CloudState = "stopped" };
        var c2 = new HostEntry { Label = "c2", CloudProfileId = profile.Id };
        var all = new List<HostEntry> { web1, web2, db, c1, c2 };

        // 화면에는 web1 만 보이는 상태 (검색 중)
        var local = new HostGroup { Key = "L:Web", Name = "Web", GroupName = "Web", Hosts = new List<HostEntry> { web1 } };
        Assert.Equal(new[] { web1, web2 }, MainWindow.GroupMembers(all, local));
        var cloud = new HostGroup { Key = "C:" + profile.Id, Name = "p", Profile = profile, Hosts = new List<HostEntry> { c2 } };
        Assert.Equal(new[] { c1, c2 }, MainWindow.GroupMembers(all, cloud));
        var fav = new HostGroup { Key = "F:", Name = "fav", Hosts = new List<HostEntry> { db } };
        Assert.Equal(new[] { db }, MainWindow.GroupMembers(all, fav));
    }

    [Fact]
    public void 점프_서버_의존_호스트는_같이_지우는_호스트를_빼고_센다()
    {
        var jump = new HostEntry { Label = "jump" };
        var a = new HostEntry { Label = "a", JumpHostId = jump.Id };
        var b = new HostEntry { Label = "b", JumpHostId = jump.Id };
        var all = new List<HostEntry> { jump, a, b };
        Assert.Equal(new[] { a, b }, MainWindow.JumpDependents(all, new[] { jump }));
        Assert.Equal(new[] { b }, MainWindow.JumpDependents(all, new[] { jump, a }));
        Assert.Empty(MainWindow.JumpDependents(all, new[] { a }));
    }

    [Fact]
    public void 선택한_호스트는_화면_순서로_정렬하고_안_보이는_호스트는_뒤로()
    {
        var x = new HostEntry { Label = "x" };
        var y = new HostEntry { Label = "y" };
        var z = new HostEntry { Label = "z" };
        var hidden = new HostEntry { Label = "hidden" };
        var order = new List<HostEntry> { z, x, y, x };   // 즐겨찾기 등으로 두 번 보이면 처음 위치
        Assert.Equal(new[] { z, x, y, hidden }, MainWindow.OrderByScreen(new[] { hidden, y, x, z }, order));
    }

    [Fact]
    public void 예전_폴더의_평문_데이터_파일은_암호화_확인_후에만_지운다()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nubis.Tests", "migrate-" + Guid.NewGuid().ToString("N")[..8]);
        var legacy = Path.Combine(root, "legacy");
        var target = Path.Combine(root, "new");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        try
        {
            // 1) 복사만 된 평문 → 새 폴더에서 암호화하고 예전 평문 삭제
            File.WriteAllText(Path.Combine(legacy, "hosts.json"), "[\"10.9.9.9\"]");
            File.Copy(Path.Combine(legacy, "hosts.json"), Path.Combine(target, "hosts.json"));
            AppPaths.RemoveMigratedPlaintext(Path.Combine(legacy, "hosts.json"), Path.Combine(target, "hosts.json"));
            Assert.False(File.Exists(Path.Combine(legacy, "hosts.json")));
            var bytes = File.ReadAllBytes(Path.Combine(target, "hosts.json"));
            Assert.True(SafeFile.IsProtected(bytes));
            Assert.Equal("[\"10.9.9.9\"]", SafeFile.UnprotectBytes(bytes));

            // 2) 새 폴더 파일이 그 뒤 바뀌었으면 예전 파일을 지우지 않는다
            File.WriteAllText(Path.Combine(legacy, "vault.json"), "[1]");
            SafeFile.WriteProtected(Path.Combine(target, "vault.json"), "[2]");
            AppPaths.RemoveMigratedPlaintext(Path.Combine(legacy, "vault.json"), Path.Combine(target, "vault.json"));
            Assert.True(File.Exists(Path.Combine(legacy, "vault.json")));

            // 3) 계정 파일 목록에 없는 파일은 건드리지 않는다
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
            File.Copy(Path.Combine(legacy, "settings.json"), Path.Combine(target, "settings.json"));
            AppPaths.RemoveMigratedPlaintext(Path.Combine(legacy, "settings.json"), Path.Combine(target, "settings.json"));
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "settings.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 단일_인스턴스_이름은_데이터_폴더마다_다르다()
    {
        Assert.Equal(SingleInstance.Id(@"C:\Data\Nubis"), SingleInstance.Id(@"c:\data\nubis\"));
        Assert.NotEqual(SingleInstance.Id(@"C:\Data\Nubis"), SingleInstance.Id(@"C:\Data\Nubis-test"));
    }
}
