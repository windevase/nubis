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
    /// 조회 결과를 호스트 목록에 반영한다. 이름·주소·상태 등 클라우드 정보는 갱신하고,
    /// 사용자가 바꾼 프로토콜·포트·계정은 유지한다. 클라우드에서 사라진 인스턴스는 제거한다.
    /// </summary>
    public static (int added, int updated, int removed) Apply(CloudProfile p, List<CloudInstance> instances, List<HostEntry> hosts,
        bool removeMissing = true)
    {
        var existing = hosts.Where(h => h.CloudProfileId == p.Id && h.CloudInstanceId != null)
            .GroupBy(h => h.CloudInstanceId!).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string>();
        int added = 0, updated = 0;

        foreach (var inst in instances)
        {
            if (p.HiddenInstances.Contains(inst.InstanceId)) continue;
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
            if (address.Length == 0 || inst.InstanceId.Length == 0) continue;
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
            h.Address = address;
            h.PrivateIp = inst.PrivateIp.Length > 0 ? inst.PrivateIp : null;
            h.PublicIp = inst.PublicIp.Length > 0 ? inst.PublicIp : null;   // EIP 를 떼면 사라짐
            if (h.PublicIp == null) h.UsePublicIp = false;
            h.Purpose = inst.Purpose;
            h.CloudState = inst.State;
            h.CloudRegion = inst.Region;
            h.Os = inst.Os;
        }

        // 일부 리전/영역 조회가 실패한 경우(removeMissing=false)엔 "안 보인 서버" 를 지우지 않는다 — 일시 장애로 호스트·저장 계정이 사라지는 것 방지
        var removed = removeMissing
            ? hosts.RemoveAll(h => h.CloudProfileId == p.Id && (h.CloudInstanceId == null || !seen.Contains(h.CloudInstanceId)))
            : 0;
        p.LastSync = DateTime.Now;
        return (added, updated, removed);
    }
}
