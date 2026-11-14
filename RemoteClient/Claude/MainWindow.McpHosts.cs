using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

public partial class MainWindow
{
    // =================== 호스트 관리 ===================

    private VaultEntry? ResolveVault(string spec)
    {
        spec = spec.Trim();
        var all = VaultStore.All();
        var hit = all.Where(v => v.Name.Equals(spec, StringComparison.OrdinalIgnoreCase) ||
                                 (spec.Length >= 8 && v.Id.ToString().StartsWith(spec, StringComparison.OrdinalIgnoreCase))).ToList();
        if (hit.Count == 0) hit = all.Where(v => v.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        return hit.Count switch
        {
            1 => hit[0],
            0 => throw new McpToolException($"Vault 계정 '{spec}' 을 찾을 수 없습니다. list_vault 로 확인하세요."),
            _ => throw new McpToolException($"Vault 계정 '{spec}' 이 {hit.Count}개와 일치합니다: {string.Join(", ", hit.Select(v => v.Name))}"),
        };
    }

    private HostEntry ResolveOneHost(string spec)
    {
        if (spec.StartsWith("group:", StringComparison.OrdinalIgnoreCase)) throw new McpToolException("이 도구는 호스트를 하나씩 지정해야 합니다 (group: 불가).");
        var found = ResolveHosts(spec);
        return found.Count switch
        {
            1 => found[0],
            0 => throw new McpToolException($"'{spec}' 와 일치하는 호스트가 없습니다. list_hosts 로 확인하세요."),
            _ => throw new McpToolException($"'{spec}' 가 {found.Count}개와 일치합니다. id 로 지정하세요:\n" + string.Join("\n", found.Take(10).Select(HostLine))),
        };
    }

    private static bool Has(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    /// <summary>add/update 공통: 인자를 사본에 적용하고 바뀐 내용 목록을 돌려준다</summary>
    private List<string> ApplyHostArgs(HostEntry h, JsonElement a, bool isNew)
    {
        var changes = new List<string>();
        void Note(string label, object? from, object? to)
        {
            if (!Equals(from, to)) changes.Add(isNew ? $"{label}: {to}" : $"{label}: {from} → {to}");
        }

        if (Has(a, "protocol"))
        {
            var p = Arg(a, "protocol").ToLowerInvariant() switch
            {
                "ssh" => Protocol.Ssh,
                "rdp" => Protocol.Rdp,
                _ => throw new McpToolException("protocol 은 ssh 또는 rdp 입니다."),
            };
            if (h.IsCloud && p != h.Protocol) throw new McpToolException("클라우드 동기화 호스트의 프로토콜은 바꿀 수 없습니다 (OS 에서 정해짐).");
            Note("프로토콜", h.ProtocolText, p == Protocol.Ssh ? "SSH" : "RDP");
            if (isNew || p != h.Protocol) { var oldDefault = HostEntry.PreferredPort(h.Protocol); h.Protocol = p; if (!Has(a, "port") && (isNew || h.Port == oldDefault)) h.Port = HostEntry.PreferredPort(p); }
        }
        if (Has(a, "address"))
        {
            var addr = Arg(a, "address").Trim();
            if (addr.Length == 0 || addr.Any(char.IsWhiteSpace)) throw new McpToolException("address 가 올바르지 않습니다.");
            if (h.IsCloud) throw new McpToolException("클라우드 동기화 호스트의 주소는 동기화로만 바뀝니다.");
            Note("주소", h.Address, addr);
            h.Address = addr;
        }
        if (Has(a, "port"))
        {
            var port = IntArg(a, "port") ?? 0;
            if (port is < 1 or > 65535) throw new McpToolException("port 는 1~65535 입니다.");
            Note("포트", h.Port, port);
            h.Port = port;
        }
        if (Has(a, "label")) { var v = Arg(a, "label").Trim(); Note("이름", h.Label, v); h.Label = v; }
        if (Has(a, "group"))
        {
            if (h.IsCloud) throw new McpToolException("클라우드 호스트의 그룹은 클라우드 프로필입니다 (group 을 바꿀 수 없음).");
            var v = Arg(a, "group").Trim(); Note("그룹", h.Group, v); h.Group = v;
        }
        if (Has(a, "username")) { var v = Arg(a, "username").Trim(); Note("사용자명", h.Username, v); h.Username = v; }
        if (Has(a, "public_ip"))
        {
            var v = Arg(a, "public_ip").Trim();
            if (h.IsCloud) throw new McpToolException("클라우드 호스트의 공인 IP 는 동기화로만 바뀝니다.");
            Note("공인 IP", h.PublicIp, v.Length > 0 ? v : null);
            h.PublicIp = v.Length > 0 ? v : null;
        }
        if (Has(a, "use_public_ip"))
        {
            var v = BoolArg(a, "use_public_ip") == true;
            if (v && !h.HasPublicIp) throw new McpToolException("공인 IP 가 없는 호스트입니다.");
            Note("공인 IP 로 접속", h.UsePublicIp, v);
            h.UsePublicIp = v;
        }
        if (Has(a, "vault"))
        {
            var spec = Arg(a, "vault").Trim();
            var v = spec.Length == 0 || spec.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ResolveVault(spec);
            Note("Vault 계정", VaultStore.Get(h.VaultId)?.Name ?? (h.VaultId != null ? "(삭제됨)" : "없음"), v?.Name ?? "없음");
            h.VaultId = v?.Id;
        }
        return changes;
    }

    private string HostSummary(HostEntry h) =>
        $"{h.ProtocolText} {h.Address}:{h.Port}" + (h.HasPublicIp ? $" (공인 {h.PublicIp}{(h.UsePublicIp ? ", 공인으로 접속" : "")})" : "") +
        $" · 이름 {(h.Label.Length > 0 ? h.Label : "-")} · 그룹 {GroupLabel(h)} · 사용자 {(h.Username.Length > 0 ? h.Username : "-")}" +
        $" · Vault {VaultStore.Get(h.VaultId)?.Name ?? "없음"}";

    private string AddHostFromClaude(JsonElement a)
    {
        var h = new HostEntry { Protocol = Protocol.Ssh };
        h.Port = HostEntry.PreferredPort(Protocol.Ssh);
        if (!Has(a, "address")) throw new McpToolException("address 가 필요합니다.");
        ApplyHostArgs(h, a, isNew: true);
        var dup = _hosts.FirstOrDefault(x => x.Address.Equals(h.Address, StringComparison.OrdinalIgnoreCase) && x.Port == h.Port && x.Protocol == h.Protocol);
        var summary = HostSummary(h);
        if (!AskApproval("add_host", L.T("Claude 가 호스트 등록을 요청합니다"),
                L.T("아래 호스트를 등록합니다. Vault 계정을 연결하면 이 주소로 접속할 때 그 계정의 비밀번호·키가 쓰입니다. 주소가 맞는지 확인하세요.") +
                (dup != null ? L.F("\n⚠ 같은 주소·포트의 호스트 '{0}' 가 이미 있습니다.", dup.DisplayName) : ""),
                new[] { h.DisplayName }, summary))
        {
            AuditLog.Write("add_host", new[] { h.DisplayName }, summary, "거부");
            throw new McpToolException("사용자가 호스트 등록을 거부했습니다.");
        }
        _hosts.Add(h);
        SaveHosts();
        RefreshHosts();
        AuditLog.Write("add_host", new[] { h.DisplayName }, summary, "승인·등록");
        return $"등록했습니다: {h.Id.ToString()[..8]} | {summary}";
    }

    private string UpdateHostFromClaude(JsonElement a)
    {
        var h = ResolveOneHost(Arg(a, "host"));
        var copy = h.Clone();
        var changes = ApplyHostArgs(copy, a, isNew: false);
        if (changes.Count == 0) return "바뀐 항목이 없습니다.";
        var text = string.Join("\n", changes);
        var risky = changes.Any(c => c.StartsWith("주소") || c.StartsWith("공인") || c.StartsWith("Vault") || c.StartsWith("포트"));
        if (!AskApproval("update_host", L.T("Claude 가 호스트 설정 변경을 요청합니다"),
                L.F("'{0}' 의 설정을 바꿉니다.", h.DisplayName) + (risky ? L.T("\n주소·포트·Vault 계정이 바뀌면 저장된 계정이 쓰이는 서버가 달라집니다. 확인하세요.") : ""),
                new[] { h.DisplayName }, text))
        {
            AuditLog.Write("update_host", new[] { h.DisplayName }, text, "거부");
            throw new McpToolException("사용자가 변경을 거부했습니다.");
        }
        // 확인한 값만 원본에 반영 (비밀번호·키 파일·클라우드 정보는 건드리지 않음)
        h.Protocol = copy.Protocol; h.Address = copy.Address; h.Port = copy.Port; h.Label = copy.Label; h.Group = copy.Group;
        h.Username = copy.Username; h.PublicIp = copy.PublicIp; h.UsePublicIp = copy.UsePublicIp; h.VaultId = copy.VaultId;
        if (h.PrivateIp != null && !h.IsCloud) h.PrivateIp = h.Address;
        SaveHosts();
        RefreshHosts();
        AuditLog.Write("update_host", new[] { h.DisplayName }, text, "승인·변경");
        return $"바꿨습니다: {h.DisplayName}\n{text}";
    }

    private string DeleteHostsFromClaude(JsonElement a)
    {
        var specs = ListArg(a, "hosts");
        if (specs.Count == 0) throw new McpToolException("hosts 가 필요합니다.");
        var targets = specs.Select(ResolveOneHost).DistinctBy(h => h.Id).ToList();
        var labels = targets.Select(h => $"{h.DisplayName}  ({h.ProtocolText} {h.Address}:{h.Port}{(h.IsCloud ? ", 클라우드 — 다음 동기화에서도 숨김" : "")})").ToList();
        if (!AskApproval("delete_host", L.F("Claude 가 호스트 {0}개 삭제를 요청합니다", targets.Count),
                L.T("Nubis 목록에서 지웁니다. 서버 자체에는 영향이 없습니다."), labels, "delete_host"))
        {
            AuditLog.Write("delete_host", labels, "delete_host", "거부");
            throw new McpToolException("사용자가 삭제를 거부했습니다.");
        }
        RemoveHosts(targets);
        AuditLog.Write("delete_host", labels, "delete_host", "승인·삭제");
        return $"{targets.Count}개를 지웠습니다:\n" + string.Join("\n", labels);
    }

    // =================== 클라우드 동기화 ===================

    private async Task<string> CloudSyncAsync(JsonElement a)
    {
        var spec = Arg(a, "profile").Trim();
        var targets = await OnUi(() =>
        {
            if (spec.Length == 0) return new List<CloudProfile>();
            if (spec.Equals("all", StringComparison.OrdinalIgnoreCase)) return _profiles.ToList();
            bool Match(CloudProfile p, bool exact) => new[] { p.GroupName, p.Name, $"[{p.ProviderText}] {p.GroupName}" }
                .Any(n => exact ? n.Equals(spec, StringComparison.OrdinalIgnoreCase) : n.Contains(spec, StringComparison.OrdinalIgnoreCase));
            var hit = _profiles.Where(p => Match(p, true)).ToList();
            return hit.Count > 0 ? hit : _profiles.Where(p => Match(p, false)).ToList();
        });

        string Line(CloudProfile p) =>
            $"[{p.ProviderText}] {p.GroupName} ({p.Name}) — 서버 {_hosts.Count(h => h.CloudProfileId == p.Id)}대 (실행 {_hosts.Count(h => h.CloudProfileId == p.Id && !h.IsStopped)})";

        if (targets.Count == 0)
        {
            var list = await OnUi(() => string.Join("\n", _profiles.OrderBy(p => p.Provider).ThenBy(p => p.GroupName).Select(Line)));
            if (list.Length == 0) return "클라우드 프로필이 없습니다. 사용자에게 Nubis 의 Cloud 메뉴에서 프로필을 추가해 달라고 안내하세요.";
            return (spec.Length > 0 ? $"'{spec}' 와 일치하는 프로필이 없습니다.\n" : "") + "프로필 목록 (profile 에 이름 또는 'all'):\n" + list;
        }

        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(targets.Select(async p =>
        {
            await gate.WaitAsync();
            try { await await OnUi(() => SyncProfileAsync(p)); } finally { gate.Release(); }
        }));
        var result = await OnUi(() => string.Join("\n", targets.Select(p =>
            Line(p) + $" · {(p.StatusText.Length > 0 ? p.StatusText : p.LastResult)}")));
        AuditLog.Write("cloud_sync", targets.Select(p => $"[{p.ProviderText}] {p.GroupName}"), "sync", "조회·반영");
        return "동기화 결과:\n" + result;
    }
}
