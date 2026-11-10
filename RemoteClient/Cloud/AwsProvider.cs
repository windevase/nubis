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
    public string AuthHint => "~/.aws/config 프로필 · SSO 는 aws sso login 필요";
    public string LoginCommand(CloudProfile? p) => $"aws sso login --profile {p?.Name ?? "default"}";

    public Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var path = CloudPaths.Effective(CloudProvider.Aws);
        var list = new List<CloudCandidate>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (!t.StartsWith("[profile ") || !t.EndsWith("]")) continue;
                var name = t[9..^1].Trim();
                list.Add(new CloudCandidate(name, name.ToUpperInvariant()));
            }
        }
        return Task.FromResult(list);
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(p.Name, out var creds))
            throw new InvalidOperationException($"AWS 프로필 '{p.Name}' 을 찾을 수 없습니다.");

        List<string> regions;
        try
        {
            using var ec2 = new AmazonEC2Client(creds, RegionEndpoint.USEast1);
            var resp = await ec2.DescribeRegionsAsync(new DescribeRegionsRequest(), ct);
            regions = (resp.Regions ?? new()).Select(r => r.RegionName).ToList();
        }
        catch (Exception ex) when (ex.Message.Contains("SSO", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SSO 로그인이 필요합니다 (aws sso login --profile {p.Name}) — {CloudProviders.FirstLine(ex.Message)}");
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
}
