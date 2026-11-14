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
            using var ec2 = new AmazonEC2Client(creds, RegionEndpoint.USEast1);
            var resp = await ec2.DescribeRegionsAsync(new DescribeRegionsRequest(), ct);
            regions = (resp.Regions ?? new()).Select(r => r.RegionName).ToList();
        }
        catch (Exception ex) when (IsSsoProfile(chain, p.Name, creds) && LooksLikeSsoTokenError(ex))
        {
            // SSO 프로필일 때만 SSO 로그인 안내 (액세스 키 프로필의 "security token invalid/expired" 에 SSO 안내가 붙지 않게)
            throw new InvalidOperationException(L.F("SSO 로그인이 필요합니다 (aws sso login --profile {0}) — {1}", p.Name, CloudProviders.FirstLine(ex.Message)));
        }

        var (items, errors) = await CloudProviders.ParallelCollect(regions, 10, async region =>
        {
            using var client = new AmazonEC2Client(creds, RegionEndpoint.GetBySystemName(region));
            var result = new List<CloudInstance>();
            string? token = null;
            do
            {
                var page = await client.DescribeInstancesAsync(new DescribeInstancesRequest { NextToken = token }, ct);
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
        }, r => r);

        if (items.Count == 0 && errors.Count > 0) throw new InvalidOperationException(errors[0]);
        return new CloudFetchResult(items, errors);
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
}
