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
        var protocolChanged = false;
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
            // 클라우드 호스트도 편집 창처럼 바꿀 수 있다 — 동기화는 새 호스트에만 OS 로 프로토콜을 정하고 기존 호스트의 값은 유지한다 (CloudSync.Apply)
            Note("프로토콜", h.ProtocolText, p == Protocol.Ssh ? "SSH" : "RDP");
            if (isNew || p != h.Protocol)
            {
                protocolChanged = !isNew;
                var oldDefault = HostEntry.PreferredPort(h.Protocol);
                var oldPort = h.Port;
                h.Protocol = p;
                if (!Has(a, "port") && (isNew || h.Port == oldDefault)) h.Port = HostEntry.PreferredPort(p);
                // 프로토콜을 바꾸며 기본 포트로 따라 바뀐 포트도 승인 창·결과에 보인다 (조용히 바뀌지 않게)
                if (!isNew && h.Port != oldPort) Note("포트", oldPort, h.Port);
            }
        }
        if (Has(a, "address"))
        {
            var addr = Arg(a, "address").Trim();
            if (addr.Length == 0 || addr.Any(char.IsWhiteSpace) || McpHelpers.HasInvisibleChars(addr)) throw new McpToolException("address 가 올바르지 않습니다.");
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
        // 이름·그룹·사용자명에 보이지 않는 제어·양방향 문자(U+202E 등)를 넣으면 목록·승인 창에서 다른 서버처럼 보이게 꾸밀 수 있다 → 거부
        string Visible(string field)
        {
            var v = Arg(a, field).Trim();
            if (McpHelpers.HasInvisibleChars(v)) throw new McpToolException($"{field} 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(v)}");
            return v;
        }
        if (Has(a, "label")) { var v = Visible("label"); Note("이름", h.Label, v); h.Label = v; }
        if (Has(a, "tags") || BoolArg(a, "replace_tags") == true)
        {
            var updates = new List<KeyValuePair<string, string?>>();
            if (Has(a, "tags"))
            {
                var t = a.GetProperty("tags");
                if (t.ValueKind != JsonValueKind.Object) throw new McpToolException("tags 는 {\"키\": \"값\"} 객체입니다 (값이 빈 문자열·null 이면 그 태그를 지움).");
                foreach (var p in t.EnumerateObject())
                    updates.Add(new(p.Name, p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Null => null,
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                        _ => throw new McpToolException($"태그 '{p.Name}' 의 값은 문자열이어야 합니다."),
                    }));
            }
            var tagChanges = new List<HostTags.Change>();
            try { h.Tags = HostTags.Apply(h.Tags, updates, BoolArg(a, "replace_tags") == true, tagChanges); }
            catch (ArgumentException ex) { throw new McpToolException(ex.Message); }
            foreach (var c in tagChanges) Note($"{McpHostRules.TagLabel} {c.Key}", c.From ?? "없음", c.To ?? "없음");
        }
        if (Has(a, "group"))
        {
            if (h.IsCloud) throw new McpToolException("클라우드 호스트의 그룹은 클라우드 프로필입니다 (group 을 바꿀 수 없음).");
            var v = Visible("group"); Note("그룹", h.Group, v); h.Group = v;
        }
        if (Has(a, "username")) { var v = Visible("username"); Note("사용자명", h.Username, v); h.Username = v; }
        if (Has(a, "public_ip"))
        {
            var v = Arg(a, "public_ip").Trim();
            if (v.Any(char.IsWhiteSpace) || McpHelpers.HasInvisibleChars(v)) throw new McpToolException("public_ip 가 올바르지 않습니다.");
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
        if (Has(a, "favorite"))
        {
            var v = BoolArg(a, "favorite") ?? throw new McpToolException("favorite 는 true 또는 false 입니다.");
            Note(McpHostRules.FavoriteLabel, h.Favorite ? "예" : "아니오", v ? "예" : "아니오");
            h.Favorite = v;
        }
        // 편집 창과 같게: 프로토콜을 바꾸면 다른 프로토콜 전용 값(점프 서버 = SSH, RD 게이트웨이 = RDP)은 지운다
        if (h.Protocol != Protocol.Ssh && h.JumpHostId != null && !Has(a, "jump_host"))
        {
            Note(McpHostRules.JumpLabel, JumpName(h.JumpHostId), "없음");
            h.JumpHostId = null;
        }
        if (h.Protocol != Protocol.Rdp && h.RdpGateway != null && !Has(a, "rdp_gateway"))
        {
            Note(McpHostRules.GatewayLabel, h.RdpGateway, "없음");
            h.RdpGateway = null;
        }
        if (h.Protocol != Protocol.Rdp && h.RdpDisableNla && !Has(a, "rdp_disable_nla"))
        {
            Note(McpHostRules.NlaLabel, "끔", "켬");
            h.RdpDisableNla = false;
        }
        if (Has(a, "rdp_disable_nla"))
        {
            var v = BoolArg(a, "rdp_disable_nla") ?? throw new McpToolException("rdp_disable_nla 는 true 또는 false 입니다.");
            if (v && h.Protocol != Protocol.Rdp) throw new McpToolException("rdp_disable_nla 는 RDP 호스트에만 지정할 수 있습니다.");
            Note(McpHostRules.NlaLabel, h.RdpDisableNla ? "끔" : "켬", v ? "끔" : "켬");
            h.RdpDisableNla = v;
        }
        if (Has(a, "jump_host"))
        {
            var spec = Arg(a, "jump_host").Trim();
            if (McpHelpers.HasInvisibleChars(spec)) throw new McpToolException($"jump_host 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(spec)}");
            HostEntry? jump = null;
            if (spec.Length > 0 && !spec.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                jump = ResolveOneHost(spec);
                if (McpHostRules.JumpError(h, jump) is { } err) throw new McpToolException(err);
            }
            Note(McpHostRules.JumpLabel, JumpName(h.JumpHostId), jump == null ? "없음" : JumpName(jump.Id));
            h.JumpHostId = jump?.Id;
        }
        if (Has(a, "rdp_gateway"))
        {
            var raw = Arg(a, "rdp_gateway");
            if (McpHelpers.HasInvisibleChars(raw.Trim())) throw new McpToolException($"rdp_gateway 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(raw)}");
            var gw = McpHostRules.NormalizeGateway(raw, out var err);
            if (err != null) throw new McpToolException(err);
            if (gw != null && h.Protocol != Protocol.Rdp) throw new McpToolException("rdp_gateway 는 RDP 호스트에만 지정할 수 있습니다.");
            Note(McpHostRules.GatewayLabel, h.RdpGateway ?? "없음", gw ?? "없음");
            h.RdpGateway = gw;
        }
        // 저장된 비밀번호 · 키 파일 경로는 지우기만 (넣기는 불가). 값·경로는 승인 창·결과·감사 로그에 넣지 않는다.
        // 편집 창과 같게 SSH 가 아닌 프로토콜로 바꾸면 키 파일 경로도 지운다 (남아 있으면 기본 Vault 계정이 쓰이지 않음 — ResolveVault)
        if (Has(a, "clear_password") && BoolArg(a, "clear_password") == null) throw new McpToolException("clear_password 는 true/false 입니다.");
        if (Has(a, "clear_key_path") && BoolArg(a, "clear_key_path") == null) throw new McpToolException("clear_key_path 는 true/false 입니다.");
        if (BoolArg(a, "clear_password") == true && !string.IsNullOrEmpty(h.ProtectedPassword))
        {
            Note(McpHostParity.PasswordLabel, "있음", "지움");
            h.ProtectedPassword = null;
        }
        if ((BoolArg(a, "clear_key_path") == true || protocolChanged && h.Protocol != Protocol.Ssh) && !string.IsNullOrWhiteSpace(h.KeyPath))
        {
            Note(McpHostParity.KeyPathLabel, "있음", "지움");
            h.KeyPath = null;
        }
        if (BoolArg(a, "clear_prompt_answers") == true && h.PromptAnswers is { Count: > 0 } answers)
        {
            // 답 내용(OTP 방식 선택 등)은 승인 창·결과·감사 로그에 넣지 않고 개수만
            Note(McpHostRules.AnswersLabel, $"{answers.Count}개", "지움");
            h.PromptAnswers = null;
        }
        return changes;
    }

    /// <summary>점프 서버 표시 이름 (승인 창·결과용). 목록에 없으면 "(삭제된 호스트)"</summary>
    private string JumpName(Guid? id) =>
        id is not { } j ? "없음"
        : _hosts.FirstOrDefault(x => x.Id == j) is { } x ? $"{McpHelpers.VisibleControlChars(x.DisplayName)} ({x.Address})"
        : "(삭제된 호스트)";

    private string HostSummary(HostEntry h) =>
        $"{h.ProtocolText} {h.Address}:{h.Port}" + (h.HasPublicIp ? $" (공인 {h.PublicIp}{(h.UsePublicIp ? ", 공인으로 접속" : "")})" : "") +
        $" · 이름 {(h.Label.Length > 0 ? h.Label : "-")} · 그룹 {GroupLabel(h)} · 사용자 {(h.Username.Length > 0 ? h.Username : "-")}" +
        $" · Vault {VaultStore.Get(h.VaultId)?.Name ?? "없음"}" +
        (h.TagsText.Length > 0 ? $" · 태그 {h.TagsText}" : "") +
        (h.JumpHostId != null ? $" · 점프 서버 {JumpName(h.JumpHostId)}" : "") +
        (h.RdpGateway != null ? $" · RD 게이트웨이 {h.RdpGateway}" : "") +
        (h.RdpDisableNla ? " · NLA 끔" : "") +
        (h.Favorite ? " · 즐겨찾기" : "");

    /// <summary>
    /// 승인을 기다리는 동안 점프 서버가 지워졌거나 더는 쓸 수 없게 됐으면(SSH 가 아님 · 자기도 점프를 거침) 오류 문구. 괜찮으면 null.
    /// 바꾸지 않은 점프 서버(before 와 같음)는 보지 않는다
    /// </summary>
    private string? JumpStillValid(HostEntry target, Guid? jumpId, Guid? before)
    {
        if (jumpId is not { } id || id == before) return null;
        var jump = _hosts.FirstOrDefault(x => x.Id == id);
        if (jump == null) return "승인을 기다리는 동안 점프 서버로 고른 호스트가 목록에서 지워져 반영하지 않았습니다.";
        return McpHostRules.JumpError(target, jump) is { } err ? "승인을 기다리는 동안 점프 서버 설정이 바뀌었습니다: " + err : null;
    }

    private async Task<string> AddHostFromClaude(JsonElement a)
    {
        var h = new HostEntry { Protocol = Protocol.Ssh };
        h.Port = HostEntry.PreferredPort(Protocol.Ssh);
        if (!Has(a, "address")) throw new McpToolException("address 가 필요합니다.");
        ApplyHostArgs(h, a, isNew: true);
        var dup = _hosts.FirstOrDefault(x => x.Address.Equals(h.Address, StringComparison.OrdinalIgnoreCase) && x.Port == h.Port && x.Protocol == h.Protocol);
        var summary = HostSummary(h);
        if (!await AskApprovalAsync("add_host", L.T("Claude 가 호스트 등록을 요청합니다"),
                L.T("아래 호스트를 등록합니다. Vault 계정을 연결하면 이 주소로 접속할 때 그 계정의 비밀번호·키가 쓰입니다. 주소가 맞는지 확인하세요.") +
                (dup != null ? L.F("\n⚠ 같은 주소·포트의 호스트 '{0}' 가 이미 있습니다.", dup.DisplayName) : "") +
                (h.JumpHostId != null || h.RdpGateway != null ? L.T("\n점프 서버·RD 게이트웨이를 거쳐 접속하면 로그인이 그 서버를 지나갑니다. 믿을 수 있는 서버인지 확인하세요.") : ""),
                new[] { h.DisplayName }, summary))
        {
            AuditLog.Write("add_host", new[] { h.DisplayName }, summary, "거부");
            throw new McpToolException("사용자가 호스트 등록을 거부했습니다.");
        }
        // 승인 창을 기다리는 동안(다른 승인 창 대기 포함) 같은 주소·포트의 호스트가 새로 생겼으면 — 사용자가 본 경고와 달라지므로 등록하지 않는다
        var dupNow = _hosts.FirstOrDefault(x => x.Address.Equals(h.Address, StringComparison.OrdinalIgnoreCase) && x.Port == h.Port && x.Protocol == h.Protocol);
        if (dupNow != null && dupNow != dup)
        {
            AuditLog.Write("add_host", new[] { h.DisplayName }, summary, $"승인 후 취소 (대기 중 같은 주소·포트의 '{dupNow.DisplayName}' 가 생김)");
            throw new McpToolException($"승인을 기다리는 동안 같은 주소·포트의 호스트 '{dupNow.DisplayName}' 가 생겨 등록하지 않았습니다. 확인한 뒤 다시 요청하세요.");
        }
        if (JumpStillValid(h, h.JumpHostId, null) is { } jumpErr)
        {
            AuditLog.Write("add_host", new[] { h.DisplayName }, summary, "승인 후 취소 (점프 서버 변경)");
            throw new McpToolException(jumpErr);
        }
        _hosts.Add(h);
        SaveHosts();
        RefreshHosts();
        AuditLog.Write("add_host", new[] { h.DisplayName }, summary, "승인·등록");
        return $"등록했습니다: {h.Id.ToString()[..8]} | {summary}";
    }

    private async Task<string> UpdateHostFromClaude(JsonElement a)
    {
        var h = ResolveOneHost(Arg(a, "host"));
        var copy = h.Clone();
        var tagsBefore = copy.Tags == null ? null : new Dictionary<string, string>(copy.Tags, StringComparer.OrdinalIgnoreCase);
        var (pwBefore, keyBefore) = (h.ProtectedPassword, h.KeyPath);
        var kept = new List<string>();
        var changes = ApplyHostArgs(copy, a, isNew: false);
        if (changes.Count == 0) return "바뀐 항목이 없습니다.";
        var text = string.Join("\n", changes);
        // 즐겨찾기만 바꾸는 요청은 접속 대상·계정에 영향이 없어 승인 없이 반영한다 (감사 로그는 남김)
        var favoriteOnly = McpHostRules.IsFavoriteOnly(changes);
        if (!favoriteOnly)
        {
            var risky = changes.Any(McpHostRules.IsRisky);
            var desc = L.F("'{0}' 의 설정을 바꿉니다.", McpHelpers.VisibleControlChars(h.DisplayName)) +
                       (risky ? L.T("\n주소·포트·Vault 계정·점프 서버·RD 게이트웨이가 바뀌면 저장된 계정이 쓰이는 서버나 거쳐 가는 경로가 달라집니다. 확인하세요.") : "");
            // 이 호스트를 점프 서버로 쓰는 호스트가 있는데 이 호스트에 점프를 붙이면 — 점프는 한 단계만이라 그 호스트들은 새 점프를 거치지 않는다
            if (copy.JumpHostId != null && copy.JumpHostId != h.JumpHostId && MainWindow.JumpUsers(_hosts, h) is > 0 and var users)
                desc += "\n" + L.F("이 호스트를 점프 서버로 쓰는 호스트 {0}개는 새 점프 서버를 거치지 않고 이 호스트에 바로 연결합니다 (점프는 한 단계만 지원).", users);
            // 비밀번호 · 키 파일 경로 지우기: 되돌릴 수 없고, 남은 값이 없으면 기본 Vault 계정(있으면)이 쓰이게 된다 (ResolveVault)
            if (changes.Any(c => c.StartsWith(McpHostParity.PasswordLabel + ":", StringComparison.Ordinal) || c.StartsWith(McpHostParity.KeyPathLabel + ":", StringComparison.Ordinal)))
            {
                desc += "\n" + L.T("⚠ 호스트에 저장된 비밀번호·키 파일 경로를 지웁니다. 지운 비밀번호는 되돌릴 수 없습니다.");
                var after = McpHelpers.CredentialSourceForAudit(copy, _profiles.FirstOrDefault(p => p.Id == copy.CloudProfileId));
                var afterUi = copy.CredentialSourceText(_profiles.FirstOrDefault(p => p.Id == copy.CloudProfileId));
                desc += "\n" + (afterUi != null ? L.F("지운 뒤 접속에 쓰일 계정: {0}", afterUi) : L.T("지운 뒤에는 저장된 계정이 없어 접속할 때 묻습니다."));
                text += "\n(지운 뒤 접속 계정: " + (after ?? "없음 — 접속할 때 묻는다") + ")";
            }
            if (!await AskApprovalAsync("update_host", L.T("Claude 가 호스트 설정 변경을 요청합니다"),
                    desc, new[] { McpHelpers.VisibleControlChars(h.DisplayName) }, text))
            {
                AuditLog.Write("update_host", new[] { h.DisplayName }, text, "거부");
                throw new McpToolException("사용자가 변경을 거부했습니다.");
            }
        }
        // 승인 창을 기다리는 동안(다른 승인 창 대기 포함) 호스트가 지워졌으면 반영하지 않는다
        if (!_hosts.Contains(h))
        {
            AuditLog.Write("update_host", new[] { h.DisplayName }, text, "승인 후 취소 (대기 중 호스트가 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 호스트가 목록에서 지워져 바꾸지 않았습니다.");
        }
        // 새로 고른 점프 서버가 그사이 지워졌거나 쓸 수 없게 됐으면 반영하지 않는다 (지워진 점프 → 연결 불가, 중첩 점프 → 점프 없이 직접 연결)
        if (JumpStillValid(copy, copy.JumpHostId, h.JumpHostId) is { } jumpErr)
        {
            AuditLog.Write("update_host", new[] { h.DisplayName }, text, "승인 후 취소 (점프 서버 변경)");
            throw new McpToolException(jumpErr);
        }
        // 확인한 값만 원본에 반영 (비밀번호·키 파일은 승인한 지우기만, 클라우드 정보는 건드리지 않음)
        if (favoriteOnly)
            h.Favorite = copy.Favorite;
        else
        {
            if (h.Address != copy.Address) h.SshBannerOs = null;   // 다른 서버일 수 있어 다음 접속 때 다시 판별
            h.Protocol = copy.Protocol; h.Address = copy.Address; h.Port = copy.Port; h.Label = copy.Label; h.Group = copy.Group;
            h.Username = copy.Username; h.PublicIp = copy.PublicIp; h.UsePublicIp = copy.UsePublicIp; h.VaultId = copy.VaultId;
            // 새 항목은 요청에서 실제로 바뀐 것만 — 승인을 기다리는 동안 사용자가 화면에서 바꾼 즐겨찾기·점프·게이트웨이를 되돌리지 않게
            bool Changed(string label) => changes.Any(c => c.StartsWith(label + ":", StringComparison.Ordinal));
            if (Changed(McpHostRules.FavoriteLabel)) h.Favorite = copy.Favorite;
            if (Changed(McpHostRules.JumpLabel)) h.JumpHostId = copy.JumpHostId;
            if (Changed(McpHostRules.GatewayLabel)) h.RdpGateway = copy.RdpGateway;
            if (Changed(McpHostRules.NlaLabel)) h.RdpDisableNla = copy.RdpDisableNla;
            // 태그는 요청에서 바뀐 키만 원본에 반영 (대기 중 다른 경로로 바뀐 다른 키는 유지)
            var tagKeys = McpHostRules.ChangedTagKeys(tagsBefore, copy.Tags);
            if (tagKeys.Count > 0)
            {
                var merged = h.Tags == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(h.Tags, StringComparer.OrdinalIgnoreCase);
                foreach (var key in tagKeys)
                {
                    if (copy.Tags != null && copy.Tags.TryGetValue(key, out var v)) merged[key] = v;
                    else merged.Remove(key);
                }
                h.Tags = merged.Count > 0 ? merged : null;
            }
            // 기억한 답 지우기는 승인 시점의 답 전체를 지운다 (대기 중 새로 기억한 답 포함 — 사용자가 지우기를 승인했으므로)
            if (copy.PromptAnswers == null && changes.Any(c => c.StartsWith(McpHostRules.AnswersLabel, StringComparison.Ordinal))) h.PromptAnswers = null;
            // 비밀번호 · 키 파일 경로는 승인한 지우기만. 승인을 기다리는 동안 사용자가 새로 저장한 값(접속 때 입력·편집 창)은
            // 승인 창에 보인 값이 아니므로 지우지 않는다
            if (Changed(McpHostParity.PasswordLabel))
            {
                if (h.ProtectedPassword == pwBefore) h.ProtectedPassword = null;
                else kept.Add("대기 중 새로 저장된 비밀번호는 지우지 않았습니다.");
            }
            if (Changed(McpHostParity.KeyPathLabel))
            {
                if (h.KeyPath == keyBefore) h.KeyPath = null;
                else kept.Add("대기 중 새로 저장된 키 파일 경로는 지우지 않았습니다.");
            }
            if (h.PrivateIp != null && !h.IsCloud) h.PrivateIp = h.Address;
        }
        SaveHosts();
        RefreshHosts();
        AuditLog.Write("update_host", new[] { h.DisplayName }, text,
            (favoriteOnly ? "변경 (즐겨찾기만 — 승인 생략)" : "승인·변경") + (kept.Count > 0 ? $" ({string.Join(" ", kept)})" : ""));
        return $"바꿨습니다: {h.DisplayName}\n{text}" + string.Concat(kept.Select(k => "\n" + k));
    }

    private async Task<string> DeleteHostsFromClaude(JsonElement a)
    {
        var specs = ListArg(a, "hosts");
        if (specs.Count == 0) throw new McpToolException("hosts 가 필요합니다.");
        var targets = specs.Select(ResolveOneHost).DistinctBy(h => h.Id).ToList();
        // 감사 로그·결과용(한국어 원문)과 승인 창용(번역)을 따로 만든다
        string Label(HostEntry h, bool ui) => $"{McpHelpers.VisibleControlChars(h.DisplayName)}  ({h.ProtocolText} {h.Address}:{h.Port}" +
            (h.IsCloud ? ui ? L.T(", 클라우드 — 다음 동기화에서도 숨김") : ", 클라우드 — 다음 동기화에서도 숨김" : "") + ")";
        var labels = targets.Select(h => Label(h, ui: false)).ToList();
        if (!await AskApprovalAsync("delete_host", L.F("Claude 가 호스트 {0}개 삭제를 요청합니다", targets.Count),
                L.T("Nubis 목록에서 지웁니다. 서버 자체에는 영향이 없습니다."), targets.Select(h => Label(h, ui: true)).ToList(), "delete_host"))
        {
            AuditLog.Write("delete_host", labels, "delete_host", "거부");
            throw new McpToolException("사용자가 삭제를 거부했습니다.");
        }
        // 승인 창을 기다리는 동안 이미 지워진 호스트는 빼고 실제로 지운 것만 알린다
        var (removed, gone) = McpHelpers.SplitStillPresent(targets.Zip(labels, (h, l) => (h, l)).ToList(), t => _hosts.Contains(t.h));
        if (removed.Count > 0) RemoveHosts(removed.Select(t => t.h));
        var removedLabels = removed.Select(t => t.l).ToList();
        var goneLabels = gone.Select(t => t.l).ToList();
        AuditLog.Write("delete_host", labels, "delete_host",
            goneLabels.Count == 0 ? "승인·삭제" : $"승인·삭제 {removedLabels.Count}개 (대기 중 이미 지워짐 {goneLabels.Count}개)");
        var msg = $"{removedLabels.Count}개를 지웠습니다" + (removedLabels.Count > 0 ? ":\n" + string.Join("\n", removedLabels) : ".");
        if (goneLabels.Count > 0) msg += $"\n승인을 기다리는 동안 이미 목록에서 지워져 있던 {goneLabels.Count}개:\n" + string.Join("\n", goneLabels);
        return msg;
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

        // Claude 가 요청을 취소하면: 이 호출이 시작한 동기화는 취소하고(SyncProfileAsync 의 cancel),
        // 사용자·자동 동기화가 이미 진행 중이던 것은 그대로 두고 기다리기만 그만둔다 (WaitAsync)
        var ct = McpRequest.Token;
        using var gate = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(targets.Select(async p =>
            {
                await gate.WaitAsync(ct);
                // 이미 동기화 중인 프로필은 그 동기화가 끝날 때까지 기다린다 (SyncProfileAsync 가 진행 중인 작업을 돌려줌)
                try { await (await OnUi(() => SyncProfileAsync(p, ct))).WaitAsync(ct); } finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AuditLog.Write("cloud_sync", targets.Select(p => $"[{p.ProviderText}] {p.GroupName}"), "sync", "취소");
            throw new McpToolException("Claude 쪽에서 요청이 취소되었습니다.");
        }
        // 변경 요약(LastResult)과 경고·실패(StatusText: 실패한 리전 · 제거된 호스트 · 점프 호스트 의존성)를 함께 보고.
        // 동기화 중에 사용자가 제거한 프로필은 따로 표시 (예전 결과처럼 보이지 않게)
        var result = await OnUi(() => string.Join("\n", targets.Select(p => !_profiles.Contains(p)
            ? $"[{p.ProviderText}] {p.GroupName} ({p.Name}) — 동기화 중 사용자가 프로필을 제거함"
            : Line(p) + string.Concat(new[] { p.LastResult, p.StatusText }.Where(s => s.Length > 0).Select(s => " · " + s)))));
        AuditLog.Write("cloud_sync", targets.Select(p => $"[{p.ProviderText}] {p.GroupName}"), "sync", "조회·반영");
        return "동기화 결과:\n" + result;
    }
}
