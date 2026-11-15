using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime.CredentialManagement;
using Azure.Core;
using Azure.Identity;
using Google.Apis.Auth.OAuth2;

namespace Nubis;

// =====================================================================
// 저장 + 동기화 병합
// =====================================================================
public static class CloudStore
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "cloud_profiles.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static List<CloudProfile> Load() =>
        SafeFile.LoadProtected(FilePath, s => JsonSerializer.Deserialize<List<CloudProfile>>(s, Options), () => new List<CloudProfile>());

    public static void Save(IEnumerable<CloudProfile> profiles) =>
        SafeFile.WriteProtected(FilePath, JsonSerializer.Serialize(profiles, Options));
}

public static class CloudSync
{
    /// <summary>
    /// 조회 결과(CloudFetchResult)를 그대로 반영. 실제 조회 실패가 있으면 아무것도 지우지 않고,
    /// 권한(SCP·IAM)으로 거부된 리전만 있으면 그 리전(과 리전을 모르는) 호스트는 남기고 나머지 리전에서만 사라진 서버를 지운다.
    /// removedHosts 를 주면 지운 호스트를 담는다 (점프 호스트로 쓰이던 호스트가 지워졌는지 화면에서 알릴 때).
    /// (Apply 오버로드로 두면 호출부의 target-typed new() 가 모호해지므로 이름을 따로 둔다)
    /// </summary>
    public static (int added, int updated, int removed) ApplyResult(CloudProfile p, CloudFetchResult result, List<HostEntry> hosts,
        ICollection<HostEntry>? removedHosts = null) =>
        Apply(p, result.Items, hosts, removeMissing: !result.HasUnexpectedErrors, removedHosts: removedHosts,
            protectedRegions: result.DeniedRegions, missGrace: SyncMissGrace);

    /// <summary>실제 동기화(ApplyResult)에서 호스트·숨긴 인스턴스를 지우기 전에 필요한 "빠짐없는 조회에서 연속으로 안 보인" 횟수</summary>
    public const int SyncMissGrace = 2;

    /// <summary>
    /// 조회 결과를 호스트 목록에 반영한다. 이름·주소·상태 등 클라우드 정보는 갱신하고,
    /// 사용자가 바꾼 프로토콜·포트·계정은 유지한다. 클라우드에서 사라진 인스턴스는 제거한다 (removeMissing 일 때, protectedRegions 의 호스트 제외).
    /// 조회 결과의 중복 InstanceId 는 첫 번째만 반영하고, 같은 인스턴스를 가리키는 기존 호스트가 여럿이면 첫 번째만 남긴다(삭제 허용 시).
    /// missGrace: 사라진 서버를 지우기 전에 필요한 연속 미확인 횟수 (1 = 바로 지움). 횟수는 p.MissCounts 에 남는다.
    /// </summary>
    public static (int added, int updated, int removed) Apply(CloudProfile p, List<CloudInstance> instances, List<HostEntry> hosts,
        bool removeMissing = true, ICollection<HostEntry>? removedHosts = null, IReadOnlyCollection<string>? protectedRegions = null,
        int missGrace = 1)
    {
        // 이번 조회에 보인 인스턴스는 (부분 조회여도) 존재하는 것이므로 연속 미확인 횟수를 지운다
        var present = new HashSet<string>(instances.Select(i => i.InstanceId).Where(id => id.Length > 0), StringComparer.Ordinal);
        foreach (var id in present) p.MissCounts.Remove(id);

        // 빠짐없는 조회에서 안 보인 인스턴스: 연속 미확인 횟수를 올리고, missGrace 에 이르렀을 때만 지워도 된다고 답한다
        bool MissedEnough(string id)
        {
            if (missGrace <= 1) { p.MissCounts.Remove(id); return true; }
            var n = p.MissCounts.GetValueOrDefault(id) + 1;
            if (n >= missGrace) { p.MissCounts.Remove(id); return true; }
            p.MissCounts[id] = n;
            return false;
        }

        var groups = hosts.Where(h => h.CloudProfileId == p.Id && h.CloudInstanceId != null)
            .GroupBy(h => h.CloudInstanceId!).ToList();
        // 같은 프로필·인스턴스로 호스트가 여럿이면 첫 번째만 갱신하고, 나머지는 중복으로 보고 제거 대상 (삭제가 허용될 때만)
        var existing = groups.ToDictionary(g => g.Key, g => g.First());
        var duplicates = new HashSet<HostEntry>(groups.SelectMany(g => g.Skip(1)), ReferenceEqualityComparer.Instance);
        var seen = new HashSet<string>();
        // 조회 결과에 같은 InstanceId 가 두 번 오면(페이지 경계 밀림 등) 첫 번째만 반영
        var processed = new HashSet<string>();
        int added = 0, updated = 0;
        // 숨긴 인스턴스는 인스턴스마다 목록을 훑지 않게 집합으로 (숨긴 수 × 인스턴스 수 → 인스턴스 수)
        var hidden = new HashSet<string>(p.HiddenInstances, StringComparer.Ordinal);

        foreach (var inst in instances)
        {
            if (inst.InstanceId.Length == 0) continue;
            if (hidden.Contains(inst.InstanceId)) continue;
            if (!processed.Add(inst.InstanceId)) continue;
            // "중지된 서버 제외" 여도 이미 있는 호스트는 지우지 않는다 (호스트별 설정 유지, 목록에서만 숨김). 새로 추가하지는 않는다
            if (!p.IncludeStopped && !inst.IsRunning)
            {
                if (existing.TryGetValue(inst.InstanceId, out var stopped))
                {
                    seen.Add(inst.InstanceId);
                    stopped.CloudState = inst.State;
                }
                continue;
            }
            // 기본 접속 주소는 사설 IP (없으면 공인). 공인 IP 는 따로 저장해 호스트별로 골라 쓴다
            var address = inst.PrivateIp.Length > 0 ? inst.PrivateIp : inst.PublicIp;
            if (address.Length == 0)
            {
                // 주소가 잠깐 없는 서버(할당 해제·IP 교체 중 등): 이미 있는 호스트는 지우지 않고 상태만 갱신 (계정·포트·기억한 답 유지)
                if (existing.TryGetValue(inst.InstanceId, out var noAddr))
                {
                    seen.Add(inst.InstanceId);
                    noAddr.CloudState = inst.State;
                }
                continue;
            }
            seen.Add(inst.InstanceId);

            if (!existing.TryGetValue(inst.InstanceId, out var h))
            {
                h = new HostEntry
                {
                    CloudProfileId = p.Id,
                    CloudInstanceId = inst.InstanceId,
                    Protocol = inst.IsWindows ? Protocol.Rdp : Protocol.Ssh,
                    Port = inst.IsWindows ? p.EffectiveRdpPort : p.EffectiveSshPort,
                };
                h.UsePublicIp = p.UsePublicIp && inst.PublicIp.Length > 0;   // 새 호스트는 프로필 기본값을 따름
                hosts.Add(h);
                existing[inst.InstanceId] = h;
                added++;
            }
            else
            {
                // 사설/공인 IP 를 처음 저장하는 경우(이전 버전에서 동기화된 호스트)엔 프로필 기본값으로
                if (h.PrivateIp == null && h.PublicIp == null) h.UsePublicIp = p.UsePublicIp && inst.PublicIp.Length > 0;
                updated++;
            }

            // 사용자가 이름을 바꾼 호스트는 유지 (마지막으로 받은 클라우드 이름과 다르면 사용자가 바꾼 것)
            if (string.IsNullOrWhiteSpace(h.Label) || h.CloudName == null || h.Label == h.CloudName) h.Label = inst.Name;
            h.CloudName = inst.Name;
            // 새 호스트: 프로필 기본 Vault 는 접속 때 적용되므로 여기서 따로 넣지 않는다
            if (h.Address != address) h.SshBannerOs = null;   // 주소가 바뀌면 다음 접속 때 다시 판별
            h.Address = address;
            h.PrivateIp = inst.PrivateIp.Length > 0 ? inst.PrivateIp : null;
            // EIP 를 떼면 공인 IP 는 사라지지만 사용자가 고른 "공인 IP 로 접속" 은 유지 — 다시 붙이면 그대로 공인으로 (공인 IP 가 없는 동안은 EffectiveAddress 가 사설로)
            h.PublicIp = inst.PublicIp.Length > 0 ? inst.PublicIp : null;
            h.Purpose = inst.Purpose;
            h.CloudState = inst.State;
            h.CloudRegion = inst.Region;
            h.Os = inst.Os;
        }

        // 일부 리전/영역 조회가 실패한 경우(removeMissing=false)엔 "안 보인 서버" 를 지우지 않는다 — 일시 장애로 호스트·저장 계정이 사라지는 것 방지
        // 권한 거부 리전(protectedRegions)의 호스트와 리전을 모르는 호스트는 이번에 보지 못한 것이므로 남긴다
        var denied = protectedRegions is { Count: > 0 }
            ? new HashSet<string>(protectedRegions, StringComparer.OrdinalIgnoreCase)
            : null;
        // 호스트 위치가 가용 영역인 클라우드(Tencent: 리전 ap-seoul → 영역 ap-seoul-1)는 "리전-" 접두어로도 맞춘다.
        // 넓게 맞으면 지우지 않는 쪽이라 안전하다
        bool IsProtected(HostEntry h) => denied != null && (string.IsNullOrEmpty(h.CloudRegion) || denied.Contains(h.CloudRegion) ||
                                                            denied.Any(r => h.CloudRegion.StartsWith(r + "-", StringComparison.OrdinalIgnoreCase)));
        var removed = removeMissing
            ? hosts.RemoveAll(h =>
            {
                if (h.CloudProfileId != p.Id || IsProtected(h)) return false;
                if (h.CloudInstanceId != null && seen.Contains(h.CloudInstanceId) && !duplicates.Contains(h)) return false;
                // 조회 결과에 아예 없는 서버는 연속으로 안 보였을 때만 지운다 — 목록 API 가 잠깐 빠뜨린 경우 보호.
                // 중복 호스트·ID 없는 옛 호스트·결과에는 있지만 숨긴 인스턴스의 호스트는 사라진 것이 아니므로 바로 지운다
                if (h.CloudInstanceId != null && !duplicates.Contains(h) && !present.Contains(h.CloudInstanceId) &&
                    !MissedEnough(h.CloudInstanceId)) return false;
                removedHosts?.Add(h);
                return true;
            })
            : 0;

        // 숨긴 인스턴스 목록 정리: 빠짐없이 조회된 경우에만, 클라우드에서 없어진(종료된) 인스턴스 ID 를 뺀다 (목록이 계속 커지지 않게).
        // 인스턴스 ID 는 다시 쓰이지 않으므로 지워도 숨긴 서버가 다시 나타나지 않는다.
        // 잠깐 빠진 것을 종료로 오인해 숨김이 풀리면 다음 동기화에 지운 서버가 다시 생기므로 호스트 삭제와 같은 연속 미확인 기준을 쓴다
        if (removeMissing && denied == null && p.HiddenInstances.Count > 0)
        {
            p.HiddenInstances.RemoveAll(id => !present.Contains(id) && MissedEnough(id));
        }

        // 남은 호스트·숨긴 인스턴스에 해당하지 않는 횟수는 버린다 (지워졌거나 사용자가 지운 호스트)
        var tracked = new HashSet<string>(p.HiddenInstances);
        foreach (var h in hosts)
            if (h.CloudProfileId == p.Id && h.CloudInstanceId != null) tracked.Add(h.CloudInstanceId);
        foreach (var id in p.MissCounts.Keys.Where(id => !tracked.Contains(id)).ToList()) p.MissCounts.Remove(id);
        p.PendingRemoval = hosts.Count(h => h.CloudProfileId == p.Id && h.CloudInstanceId != null && p.MissCounts.ContainsKey(h.CloudInstanceId));

        p.LastSync = DateTime.Now;
        return (added, updated, removed);
    }
}
