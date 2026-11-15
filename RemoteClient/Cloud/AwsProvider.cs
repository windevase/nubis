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
// AWS: ~/.aws/config 프로필 (SSO 포함). 먼저 aws sso login 필요
// =====================================================================
public sealed class AwsProvider : ICloudProvider
{
    public CloudProvider Kind => CloudProvider.Aws;
    public string AuthHint => L.T("~/.aws/config 프로필 · SSO 는 aws sso login 필요");
    public string LoginCommand(CloudProfile? p) => $"aws sso login --profile {p?.Name ?? "default"}";

    public Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct)
    {
        // config 의 [default] · [profile x] 와 credentials 의 [x] (액세스 키 프로필) 를 모두 후보로
        var config = CloudPaths.Effective(CloudProvider.Aws);
        var credentials = Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") is { Length: > 0 } env
            ? env
            : Path.Combine(Path.GetDirectoryName(config) ?? "", "credentials");
        var names = new List<string>();
        void Add(string n) { n = n.Trim(); if (n.Length > 0 && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n); }
        foreach (var (path, isConfig) in new[] { (config, true), (credentials, false) })
        {
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (!t.StartsWith('[') || !t.EndsWith(']')) continue;
                var section = t[1..^1].Trim();
                if (isConfig)
                {
                    if (section.Equals("default", StringComparison.OrdinalIgnoreCase)) Add("default");
                    else if (section.StartsWith("profile ", StringComparison.OrdinalIgnoreCase)) Add(section[8..]);
                    // [sso-session x] · [services x] 는 프로필이 아님
                }
                else Add(section);
            }
        }
        return Task.FromResult(names.Select(n => new CloudCandidate(n, n.ToUpperInvariant())).ToList());
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(p.Name, out var creds))
            throw new InvalidOperationException(L.F("AWS 프로필 '{0}' 을 찾을 수 없습니다.", p.Name));

        List<string> regions;
        try
        {
            regions = await ListRegionsAsync(creds, ConfiguredRegion(chain, p.Name), ct);
        }
        catch (Exception ex) when (IsSsoProfile(chain, p.Name, creds) && LooksLikeSsoTokenError(ex))
        {
            // SSO 프로필일 때만 SSO 로그인 안내 (액세스 키 프로필의 "security token invalid/expired" 에 SSO 안내가 붙지 않게)
            throw new InvalidOperationException(L.F("SSO 로그인이 필요합니다 (aws sso login --profile {0}) — {1}", p.Name, CloudProviders.FirstLine(ex.Message)));
        }
        // 리전이 0개면 "조회 성공 + 서버 없음" 으로 보여 이 프로필 호스트가 전부 지워지므로 실패로 처리
        if (regions.Count == 0)
            throw new InvalidOperationException(L.T("사용 가능한 리전 목록이 비어 있습니다 (DescribeRegions) — 이번 동기화는 건너뜁니다."));

        // SCP·IAM 으로 막힌 리전은 오류와 따로 모은다 — 막힌 리전 때문에 동기화가 매번 "부분 실패" 가 되어
        // 다른 리전의 삭제가 영영 반영되지 않는 것을 피한다 (CloudSync.ApplyResult 가 막힌 리전 호스트만 남김)
        var denied = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var (items, errors) = await CloudProviders.ParallelCollect(regions, 10, async region =>
        {
            using var client = new AmazonEC2Client(creds, RegionEndpoint.GetBySystemName(region));
            var result = new List<CloudInstance>();
            string? token = null;
            do
            {
                DescribeInstancesResponse page;
                try { page = await client.DescribeInstancesAsync(new DescribeInstancesRequest { NextToken = token }, ct); }
                catch (Amazon.Runtime.AmazonServiceException ex) when (token == null && IsAccessDenied(ex.ErrorCode))
                {
                    // 첫 페이지부터 거부 = 이 리전 전체가 정책으로 막힘. 중간 페이지 거부는 일반 오류로 둔다
                    denied[region] = CloudProviders.FirstLine(ex.Message);
                    return Enumerable.Empty<CloudInstance>();
                }
                foreach (var res in page.Reservations ?? new())
                foreach (var inst in res.Instances ?? new())
                {
                    var state = inst.State?.Name?.Value ?? "";
                    if (state == "terminated") continue;
                    var tags = (inst.Tags ?? new()).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First().Value);
                    var (name, purpose) = CloudInstance.SplitName(tags.GetValueOrDefault("Name") ?? "");
                    if (purpose.Length == 0) purpose = tags.GetValueOrDefault("ServerType") ?? tags.GetValueOrDefault("Purpose") ?? "";
                    var os = inst.PlatformDetails ?? "";
                    var win = inst.Platform?.Value == "windows" || os.Contains("Windows", StringComparison.OrdinalIgnoreCase);
                    result.Add(new CloudInstance(inst.InstanceId, name.Length > 0 ? name : inst.InstanceId, purpose, state, region,
                        inst.PrivateIpAddress ?? "", inst.PublicIpAddress ?? "", win, os));
                }
                token = page.NextToken;
            } while (!string.IsNullOrEmpty(token));
            return (IEnumerable<CloudInstance>)result;
        }, r => r, ct);

        // 모든 리전이 실패했거나(거부 포함), 서버가 하나도 없는데 실제 오류가 있으면 이번 동기화 실패
        if (items.Count == 0 && errors.Count > 0) throw new InvalidOperationException(errors[0]);
        if (items.Count == 0 && denied.Count == regions.Count)
            throw new InvalidOperationException(L.F("모든 리전에서 인스턴스 조회 권한이 없습니다 (ec2:DescribeInstances) — {0}", denied.Values.First()));
        // 거부 리전 사유는 실제 오류 뒤에 붙인다 (화면은 첫 오류를 보여 주므로 실제 오류가 먼저 보이게)
        var deniedRegions = denied.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList();
        foreach (var r in deniedRegions)
            errors.Add(L.F("{0}: 권한 없음 (SCP·IAM 정책으로 거부 — 이 리전 호스트는 유지) — {1}", r, denied[r]));
        return new CloudFetchResult(items, errors) { DeniedRegions = deniedRegions };
    }

    /// <summary>정책(SCP·IAM)으로 거부됐다는 EC2 오류 코드인지. 자격 증명 자체의 문제(AuthFailure 등)는 포함하지 않는다</summary>
    internal static bool IsAccessDenied(string? errorCode) =>
        errorCode is "UnauthorizedOperation" or "AccessDenied" or "AccessDeniedException";

    /// <summary>
    /// 리전 목록 조회. 프로필에 설정된 리전(config 의 region)으로 먼저 부르고, 실패하면 us-east-1 로 다시 시도한다
    /// (SCP 로 us-east-1 을 막은 조직에서도 동기화되게). 둘 다 실패하면 첫 번째(설정 리전) 오류를 던진다.
    /// </summary>
    private static async Task<List<string>> ListRegionsAsync(Amazon.Runtime.AWSCredentials creds, RegionEndpoint? home, CancellationToken ct)
    {
        async Task<List<string>> Call(RegionEndpoint endpoint)
        {
            using var ec2 = new AmazonEC2Client(creds, endpoint);
            var resp = await ec2.DescribeRegionsAsync(new DescribeRegionsRequest(), ct);
            return (resp.Regions ?? new()).Select(r => r.RegionName).ToList();
        }

        if (home == null || home.SystemName == RegionEndpoint.USEast1.SystemName) return await Call(RegionEndpoint.USEast1);
        try
        {
            return await Call(home);
        }
        catch (Exception first) when (first is not OperationCanceledException)
        {
            try { return await Call(RegionEndpoint.USEast1); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
                throw;   // 도달하지 않음
            }
        }
    }

    /// <summary>프로필(config)에 설정된 리전. 없거나 읽지 못하면 null</summary>
    private static RegionEndpoint? ConfiguredRegion(CredentialProfileStoreChain chain, string name)
    {
        try { return chain.TryGetProfile(name, out var prof) ? prof.Region : null; }
        catch { return null; }
    }

    /// <summary>SSO 자격 증명을 쓰는 프로필인지 (SSOAWSCredentials 이거나 config 에 sso_* 설정, source_profile 체인 포함)</summary>
    private static bool IsSsoProfile(CredentialProfileStoreChain chain, string name, Amazon.Runtime.AWSCredentials creds)
    {
        if (creds is Amazon.Runtime.SSOAWSCredentials) return true;
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var n = name; !string.IsNullOrEmpty(n) && seen.Add(n);)
            {
                if (!chain.TryGetProfile(n, out var prof)) break;
                if (HasSsoSettings(prof.Options.SsoStartUrl, prof.Options.SsoSession)) return true;
                n = prof.Options.SourceProfile;
            }
        }
        catch { }
        return false;
    }

    internal static bool HasSsoSettings(string? ssoStartUrl, string? ssoSession) =>
        !string.IsNullOrWhiteSpace(ssoStartUrl) || !string.IsNullOrWhiteSpace(ssoSession);

    /// <summary>SSO 토큰이 없거나 만료된 오류로 보이는지 (SSO 프로필에서만 쓴다)</summary>
    internal static bool LooksLikeSsoTokenError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var m = e.Message ?? "";
            if (m.Contains("SSO", StringComparison.OrdinalIgnoreCase) || m.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("expired", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------- EKS 클러스터 ----------------

    public async Task<ClusterFetchResult> FetchClustersAsync(CloudProfile p, CancellationToken ct)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(p.Name, out var creds))
            throw new InvalidOperationException(L.F("AWS 프로필 '{0}' 을 찾을 수 없습니다.", p.Name));
        List<string> regions;
        try { regions = await ListRegionsAsync(creds, ConfiguredRegion(chain, p.Name), ct); }
        catch (Exception ex) when (IsSsoProfile(chain, p.Name, creds) && LooksLikeSsoTokenError(ex))
        {
            throw new InvalidOperationException(L.F("SSO 로그인이 필요합니다 (aws sso login --profile {0}) — {1}", p.Name, CloudProviders.FirstLine(ex.Message)));
        }
        if (regions.Count == 0)
            throw new InvalidOperationException(L.T("사용 가능한 리전 목록이 비어 있습니다 (DescribeRegions) — 이번 동기화는 건너뜁니다."));

        var denied = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var (items, errors) = await CloudProviders.ParallelCollect(regions, 10, async region =>
        {
            using var eks = new Amazon.EKS.AmazonEKSClient(creds, RegionEndpoint.GetBySystemName(region));
            var names = new List<string>();
            string? token = null;
            do
            {
                Amazon.EKS.Model.ListClustersResponse page;
                try { page = await eks.ListClustersAsync(new Amazon.EKS.Model.ListClustersRequest { NextToken = token }, ct); }
                catch (Amazon.Runtime.AmazonServiceException ex) when (token == null && IsAccessDenied(ex.ErrorCode))
                {
                    denied[region] = CloudProviders.FirstLine(ex.Message);
                    return Enumerable.Empty<CloudCluster>();
                }
                names.AddRange(page.Clusters ?? new());
                token = page.NextToken;
            } while (!string.IsNullOrEmpty(token));
            var result = new List<CloudCluster>();
            foreach (var name in names)
            {
                var c = (await eks.DescribeClusterAsync(new Amazon.EKS.Model.DescribeClusterRequest { Name = name }, ct)).Cluster;
                if (c == null || string.IsNullOrEmpty(c.Endpoint)) continue;   // 만드는 중이라 아직 주소가 없다
                result.Add(EksCluster(c.Arn ?? $"{region}/{name}", name, region, c.Endpoint, c.CertificateAuthority?.Data ?? "",
                    c.Version ?? "", c.Status?.Value ?? "", p.Name));
            }
            return (IEnumerable<CloudCluster>)result;
        }, r => r, ct);

        if (items.Count == 0 && denied.Count == regions.Count)
            throw new InvalidOperationException(L.F("모든 리전에서 EKS 조회 권한이 없습니다 (eks:ListClusters) — {0}", denied.Values.First()));
        var deniedRegions = denied.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList();
        foreach (var r in deniedRegions)
            errors.Add(L.F("{0}: 권한 없음 (SCP·IAM 정책으로 거부 — 이 리전 클러스터는 유지) — {1}", r, denied[r]));
        return new ClusterFetchResult(items, errors) { DeniedRegions = deniedRegions };
    }

    /// <summary>
    /// EKS 클러스터 → 공통 형식. 인증은 aws eks get-token (AWS_PROFILE = 이 프로필 — SSO 프로필도 그대로) — kubeconfig 에 비밀값 없음
    /// </summary>
    internal static CloudCluster EksCluster(string arn, string name, string region, string endpoint, string caData, string version, string status, string profile)
    {
        var kubeconfig = Kubeconfig.BuildExec(arn, endpoint, caData, "aws",
            new[] { "--region", region, "eks", "get-token", "--cluster-name", name, "--output", "json" },
            new Dictionary<string, string> { ["AWS_PROFILE"] = profile });
        return new CloudCluster("eks:" + arn, name, region, endpoint, version, CloudCluster.NormalizeState(status), K8sAuthKind.Aws, kubeconfig, arn);
    }
}
