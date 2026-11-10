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

/// <summary>
/// Claude 패널 MCP 도구 (2차): Vault 목록, 호스트 추가·수정·삭제, 접속 진단, 출력 기다리기, SFTP, 클라우드 동기화.
/// 보안 원칙:
/// - 호스트 추가·수정·삭제는 항상 승인 (주소를 바꾸고 Vault 계정을 붙여 open_session 하면 계정이 다른 서버로 갈 수 있음)
/// - Claude 는 비밀번호를 넣거나 읽지 못한다 (Vault 계정 연결만)
/// - 원격 파일 읽기는 run_command 와 같은 규칙(승인, 설정 시 읽기 전용 자동 승인), 파일 전송은 항상 승인
/// </summary>
public partial class MainWindow
{
    private IEnumerable<McpTool> ExtraTools()
    {
        // ---------- Vault ----------
        yield return new McpTool("list_vault",
            "Vault 계정 목록 (이름 · 사용자명 · 로그인 방식 · 사용 중인 호스트 수). 비밀번호·키 내용은 절대 포함하지 않는다. " +
            "add_host / update_host 의 vault 인자에 이름을 넘겨 호스트에 연결한다.",
            Schema(new { }),
            _ => OnUi(() =>
            {
                var all = VaultStore.All();
                if (all.Count == 0) return "Vault 에 저장된 계정이 없습니다. 사용자에게 Nubis 의 Vault 메뉴에서 계정을 추가해 달라고 안내하세요.";
                var sb = new StringBuilder("id | 이름 | 사용자명 | 로그인 방식 | 사용 호스트\n");
                foreach (var v in all)
                    sb.AppendLine($"{v.Id.ToString()[..8]} | {v.Name} | {(v.Username.Length > 0 ? v.Username : "-")} | {v.MethodsText}{(v.KeyType.Length > 0 ? $" ({v.KeyType})" : "")} | {_hosts.Count(h => h.VaultId == v.Id)}");
                return sb.ToString();
            }));

        // ---------- 호스트 관리 ----------
        yield return new McpTool("add_host",
            "호스트(SSH/RDP 서버)를 새로 등록한다. 비밀번호는 넣을 수 없다 — 계정은 vault 로 Vault 계정을 연결하거나, 비워 두면 접속할 때 앱이 묻는다. " +
            "등록 전에 앱이 사용자 승인을 받는다.",
            Schema(new
            {
                address = new { type = "string", description = "IP 또는 호스트명" },
                protocol = new { type = "string", @enum = new[] { "ssh", "rdp" }, description = "기본 ssh" },
                label = new { type = "string" },
                group = new { type = "string" },
                port = new { type = "integer", description = "생략하면 설정의 기본 포트" },
                username = new { type = "string" },
                vault = new { type = "string", description = "Vault 계정 이름 또는 id" },
                public_ip = new { type = "string" },
                use_public_ip = new { type = "boolean" },
            }, "address"),
            a => OnUi(() => AddHostFromClaude(a)));

        yield return new McpTool("update_host",
            "등록된 호스트 1개의 설정을 바꾼다 (넘긴 항목만). host 는 이름·IP·id. 클라우드 동기화 호스트는 주소를 바꿀 수 없다. " +
            "비밀번호는 바꿀 수 없다. 변경 전에 앱이 사용자 승인을 받는다.",
            Schema(new
            {
                host = new { type = "string", description = "대상 호스트 (이름·IP·id, 정확히 1개여야 함)" },
                address = new { type = "string", description = "새 주소 (직접 등록한 호스트만)" },
                protocol = new { type = "string", @enum = new[] { "ssh", "rdp" } },
                label = new { type = "string" },
                group = new { type = "string", description = "그룹 (직접 등록한 호스트만)" },
                port = new { type = "integer" },
                username = new { type = "string", description = "빈 문자열 = 지우기 (Vault 계정의 사용자명을 따름)" },
                vault = new { type = "string", description = "Vault 계정 이름 또는 id (list_vault). \"none\" = 연결 해제" },
                public_ip = new { type = "string", description = "빈 문자열 = 지우기" },
                use_public_ip = new { type = "boolean" },
            }, "host"),
            a => OnUi(() => UpdateHostFromClaude(a)));

        yield return new McpTool("delete_host",
            "호스트를 목록에서 지운다 (여러 개 가능, 'group:이름' 은 허용하지 않음). 클라우드 호스트는 클라우드 서버는 그대로 두고 다음 동기화에서도 숨긴다. " +
            "삭제 전에 앱이 사용자 승인을 받는다.",
            Schema(new { hosts = new { type = "array", items = new { type = "string" }, description = "이름·IP·id 목록" } }, "hosts"),
            a => OnUi(() => DeleteHostsFromClaude(a)));

        // ---------- 접속 진단 ----------
        yield return new McpTool("test_connection",
            "등록된 호스트에 접속이 안 될 때 원인을 찾는다: 주소별(사설 → 공인) DNS 조회, TCP 포트 연결(SSH 는 서버 배너까지), RDP 호스트는 WinRM(5986/5985)도 확인. " +
            "로그인은 하지 않으며 서버를 바꾸지 않는다. ports 로 확인할 포트를 직접 지정할 수 있다(최대 10개).",
            Schema(new
            {
                host = new { type = "string", description = "호스트 이름·IP·id" },
                ports = new { type = "array", items = new { type = "integer" }, description = "확인할 포트 (생략 시 프로토콜 기본)" },
                timeout_sec = new { type = "integer", description = "포트당 제한 시간 (기본 4초)" },
            }, "host"),
            TestConnectionAsync);

        // ---------- 출력 기다리기 ----------
        yield return new McpTool("wait_for_output",
            "SSH/로컬 터미널에서 출력이 나올 때까지 기다린다. pattern 을 주면 그 문자열(또는 regex=true 면 정규식)이 새로 나타날 때까지, " +
            "생략하면 출력이 idle_sec 초 동안 멈출 때까지(명령이 끝날 때까지) 기다린 뒤 화면 마지막 lines 줄을 돌려준다. send_to_terminal 다음에 쓰면 좋다.",
            Schema(new
            {
                session_id = new { type = "integer", description = "생략 시 현재 탭" },
                pattern = new { type = "string", description = "기다릴 문자열 (예: '$ ', 'Complete!', 'error')" },
                regex = new { type = "boolean" },
                timeout_sec = new { type = "integer", description = "최대 대기 (기본 60, 최대 600)" },
                idle_sec = new { type = "number", description = "pattern 없을 때 출력이 멈춘 것으로 볼 시간 (기본 2초)" },
                lines = new { type = "integer", description = "돌려줄 줄 수 (기본 60)" },
            }),
            WaitForOutputAsync);

        // ---------- SFTP ----------
        yield return new McpTool("sftp_list",
            "SSH 호스트의 원격 폴더 목록 (이름·종류·크기·수정 시각·권한). 승인 규칙은 run_command 와 같다(설정에 따라 읽기 전용 자동 승인).",
            Schema(new
            {
                host = new { type = "string" },
                path = new { type = "string", description = "원격 경로 (생략 시 홈)" },
            }, "host"),
            a => SftpListAsync(a));

        yield return new McpTool("sftp_read",
            "SSH 호스트의 원격 텍스트 파일을 읽는다 (로그·설정 확인용, 기본 64KB, 최대 1MB). tail=true 면 끝부분. 바이너리 파일은 거부. " +
            "승인 규칙은 run_command 와 같다.",
            Schema(new
            {
                host = new { type = "string" },
                path = new { type = "string" },
                max_bytes = new { type = "integer", description = "읽을 최대 바이트 (기본 65536, 최대 1048576)" },
                tail = new { type = "boolean", description = "파일 끝부분을 읽기 (로그)" },
            }, "host", "path"),
            a => SftpReadAsync(a));

        yield return new McpTool("sftp_download",
            "SSH 호스트의 원격 파일·폴더를 이 PC 의 다운로드\\Nubis\\<호스트> 폴더로 받는다 (경로는 앱이 정함, 기존 파일은 덮어쓰지 않음). 항상 사용자 승인.",
            Schema(new
            {
                host = new { type = "string" },
                path = new { type = "string", description = "원격 파일 또는 폴더" },
            }, "host", "path"),
            a => SftpDownloadAsync(a));

        yield return new McpTool("sftp_upload",
            "이 PC 의 파일·폴더를 SSH 호스트의 원격 폴더로 올린다. 같은 이름이 있으면 overwrite=true 일 때만 덮어쓴다. 항상 사용자 승인. " +
            "Nubis 데이터 폴더와 ~/.ssh, 개인키 파일은 올릴 수 없다.",
            Schema(new
            {
                host = new { type = "string" },
                local_path = new { type = "string", description = "이 PC 의 파일 또는 폴더 (절대 경로)" },
                remote_dir = new { type = "string", description = "올릴 원격 폴더" },
                overwrite = new { type = "boolean" },
            }, "host", "local_path", "remote_dir"),
            a => SftpUploadAsync(a));

        // ---------- 클라우드 ----------
        yield return new McpTool("cloud_sync",
            "클라우드 프로필의 서버 목록을 다시 조회해 호스트 목록에 반영한다 (조회 API 만 사용, 클라우드 리소스는 바꾸지 않음). " +
            "profile 은 그룹 이름·프로필 이름 또는 'all'. 생략하면 프로필 목록만 보여준다.",
            Schema(new { profile = new { type = "string" } }),
            CloudSyncAsync);
    }

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
        if (!ApprovalDialog.Ask(this, "Claude 가 호스트 등록을 요청합니다",
                "아래 호스트를 등록합니다. Vault 계정을 연결하면 이 주소로 접속할 때 그 계정의 비밀번호·키가 쓰입니다. 주소가 맞는지 확인하세요." +
                (dup != null ? $"\n⚠ 같은 주소·포트의 호스트 '{dup.DisplayName}' 가 이미 있습니다." : ""),
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
        if (!ApprovalDialog.Ask(this, "Claude 가 호스트 설정 변경을 요청합니다",
                $"'{h.DisplayName}' 의 설정을 바꿉니다." + (risky ? "\n주소·포트·Vault 계정이 바뀌면 저장된 계정이 쓰이는 서버가 달라집니다. 확인하세요." : ""),
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
        if (!ApprovalDialog.Ask(this, $"Claude 가 호스트 {targets.Count}개 삭제를 요청합니다",
                "Nubis 목록에서 지웁니다. 서버 자체에는 영향이 없습니다.", labels, "delete_host"))
        {
            AuditLog.Write("delete_host", labels, "delete_host", "거부");
            throw new McpToolException("사용자가 삭제를 거부했습니다.");
        }
        RemoveHosts(targets);
        AuditLog.Write("delete_host", labels, "delete_host", "승인·삭제");
        return $"{targets.Count}개를 지웠습니다:\n" + string.Join("\n", labels);
    }

    // =================== 접속 진단 ===================

    private async Task<string> TestConnectionAsync(JsonElement a)
    {
        var (h, conn) = await OnUi(() => { var x = ResolveOneHost(Arg(a, "host")); return (x, WithProfileDefaults(x)); });
        var timeout = TimeSpan.FromSeconds(Math.Clamp(IntArg(a, "timeout_sec") ?? 4, 1, 20));
        var custom = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("ports", out var pe) && pe.ValueKind == JsonValueKind.Array
            ? McpHelpers.ParsePorts(pe.EnumerateArray().Select(x => x.ToString())) : Array.Empty<int>();
        var ports = custom.Length > 0 ? custom
            : h.Protocol == Protocol.Ssh ? new[] { h.Port }
            : new[] { h.Port, WinRm.HttpsPort, WinRm.HttpPort }.Distinct().ToArray();

        var sb = new StringBuilder($"[{h.DisplayName}] {h.ProtocolText}, 시도 순서: {string.Join(" → ", h.AddressCandidates())}" +
                                   (h.IsStopped ? $"  ⚠ 클라우드 상태: {h.CloudState}" : "") + "\n");
        foreach (var addr in h.AddressCandidates())
        {
            sb.AppendLine($"\n## {addr}");
            IPAddress[] ips;
            try
            {
                ips = IPAddress.TryParse(addr, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(addr).WaitAsync(timeout);
                if (!IPAddress.TryParse(addr, out _)) sb.AppendLine($"DNS: {string.Join(", ", ips.Select(i => i.ToString()))}");
            }
            catch (Exception ex) { sb.AppendLine($"DNS 실패: {ex.Message}"); continue; }

            foreach (var port in ports)
            {
                var sw = Stopwatch.StartNew();
                using var tcp = new TcpClient();
                try
                {
                    using var cts = new CancellationTokenSource(timeout);
                    await tcp.ConnectAsync(ips, port, cts.Token);
                    var line = $"  {port}/tcp 열림 ({sw.ElapsedMilliseconds}ms)";
                    if (h.Protocol == Protocol.Ssh && port == h.Port)
                    {
                        // SSH 서버는 접속하자마자 배너(SSH-2.0-...)를 보낸다 — 방화벽·다른 서비스와 구분
                        try
                        {
                            tcp.ReceiveTimeout = 3000;
                            var buf = new byte[256];
                            using var bcts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            var n = await tcp.GetStream().ReadAsync(buf, bcts.Token);
                            var banner = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0].Trim();
                            line += banner.StartsWith("SSH-") ? $" — 배너 {banner}" : $" — SSH 배너가 아님: '{banner}'";
                        }
                        catch { line += " — 배너 없음 (SSH 가 아니거나 응답 지연)"; }
                    }
                    sb.AppendLine(line);
                }
                catch (OperationCanceledException) { sb.AppendLine($"  {port}/tcp 응답 없음 ({timeout.TotalSeconds:0}초 초과 — 방화벽·보안 그룹에서 막혔거나 서버 꺼짐)"); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { sb.AppendLine($"  {port}/tcp 거부됨 (서버는 살아 있으나 이 포트에서 서비스가 안 떠 있음)"); }
                catch (Exception ex) { sb.AppendLine($"  {port}/tcp 실패: {ex.Message}"); }
            }
        }
        if (string.IsNullOrWhiteSpace(conn.Username) || (string.IsNullOrEmpty(conn.Password) && !conn.HasKey))
            sb.AppendLine("\n참고: 이 호스트에는 저장된 계정이 없어 run_command 등 비대화형 접속은 실패합니다 (호스트·Vault 에 계정 저장 필요).");
        var result = sb.ToString();
        AuditLog.Write("test_connection", new[] { h.DisplayName }, string.Join(",", ports), "조회");
        return result;
    }

    // =================== 출력 기다리기 ===================

    private async Task<string> WaitForOutputAsync(JsonElement a)
    {
        var tab = await OnUi(() => FindSession(a)) ?? throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
        if (tab.Protocol == Protocol.Rdp) throw new McpToolException("RDP 세션은 글자로 읽을 수 없습니다. capture_screen 을 쓰세요.");
        var pattern = Arg(a, "pattern");
        var regex = BoolArg(a, "regex") == true;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(IntArg(a, "timeout_sec") ?? 60, 1, 600));
        var idle = TimeSpan.FromSeconds(a.ValueKind == JsonValueKind.Object && a.TryGetProperty("idle_sec", out var ie) && ie.TryGetDouble(out var d) ? Math.Clamp(d, 0.5, 60) : 2);
        var lines = Math.Clamp(IntArg(a, "lines") ?? 60, 1, 2000);
        const int scan = 1000;   // 패턴은 마지막 1000줄에서 센다

        async Task<string> Read(int n) => await OnUiAsync(async () => await tab.Session.ReadTerminalAsync(n) ?? "");
        try { McpHelpers.CountMatches("", pattern, regex); } catch (ArgumentException ex) { throw new McpToolException(ex.Message); }

        var start = await Read(scan);
        var baseline = McpHelpers.CountMatches(start, pattern, regex);
        var last = start;
        var lastChange = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        string outcome;
        while (true)
        {
            await Task.Delay(400);
            if (!await OnUi(() => _tabs.Contains(tab))) throw new McpToolException("기다리는 동안 탭이 닫혔습니다.");
            var now = await Read(scan);
            if (now != last) { last = now; lastChange = DateTime.UtcNow; }
            if (pattern.Length > 0)
            {
                if (McpHelpers.CountMatches(now, pattern, regex) > baseline) { outcome = $"'{pattern}' 이(가) 나타났습니다"; break; }
            }
            else if (now != start && DateTime.UtcNow - lastChange >= idle) { outcome = $"출력이 {idle.TotalSeconds:0.#}초 동안 멈췄습니다"; break; }
            if (sw.Elapsed >= timeout)
            {
                outcome = pattern.Length > 0 ? $"{timeout.TotalSeconds:0}초 안에 '{pattern}' 이(가) 나타나지 않았습니다 (시간 초과)"
                    : now == start ? $"{timeout.TotalSeconds:0}초 동안 새 출력이 없었습니다 (시간 초과)" : $"{timeout.TotalSeconds:0}초 동안 출력이 계속됐습니다 (시간 초과)";
                break;
            }
        }
        var tail = string.Join("\n", last.Split('\n').TakeLast(lines));
        AuditLog.Write("wait_for_output", new[] { $"session {tab.Id}: {tab.Title}" }, pattern.Length > 0 ? pattern : "(idle)", "조회");
        return $"[session {tab.Id}: {tab.Title}] {outcome} ({sw.Elapsed.TotalSeconds:0.0}초)\n{tail}";
    }

    // =================== SFTP ===================

    /// <summary>SFTP 대상 호스트 (SSH 만, 중지 서버 제외) — 접속용 사본</summary>
    private async Task<(HostEntry Saved, HostEntry Conn)> SftpTargetAsync(JsonElement a) => await OnUi(() =>
    {
        var h = ResolveOneHost(Arg(a, "host"));
        if (h.Protocol != Protocol.Ssh) throw new McpToolException($"'{h.DisplayName}' 는 RDP(Windows) 호스트입니다. SFTP 는 SSH 호스트만 됩니다.");
        if (h.IsStopped) throw new McpToolException($"'{h.DisplayName}' 는 중지 상태입니다 ({h.CloudState}).");
        return (h, WithProfileDefaults(h));
    });

    /// <summary>원격 읽기: run_command 의 읽기 전용 자동 승인 설정을 따른다</summary>
    private async Task ApproveRemoteRead(string tool, HostEntry h, string what)
    {
        var target = $"[SFTP]  {h.DisplayName}  ({string.Join(" → ", h.AddressCandidates())})";
        if (SettingsStore.Current.AutoApproveReadOnly) { AuditLog.Write(tool, new[] { target }, what, "자동 승인(읽기 전용)"); return; }
        var ok = await OnUi(() => ApprovalDialog.Ask(this, "Claude 가 원격 파일 읽기를 요청합니다",
            "승인하면 서버에 SFTP 로 접속해 아래 내용을 읽어 Claude 에게 돌려줍니다 (서버는 바꾸지 않음).", new[] { target }, what));
        AuditLog.Write(tool, new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");
    }

    private static async Task<T> WithSftp<T>(HostEntry conn, Func<Renci.SshNet.SftpClient, T> f) => await Task.Run(() =>
    {
        Renci.SshNet.SftpClient c;
        try { c = SshExec.OpenSftp(conn); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
        using (c)
        {
            try { return f(c); }
            catch (Renci.SshNet.Common.SftpPathNotFoundException) { throw new McpToolException("원격 경로가 없습니다."); }
            catch (Renci.SshNet.Common.SftpPermissionDeniedException) { throw new McpToolException("원격 경로에 권한이 없습니다."); }
        }
    });

    private async Task<string> SftpListAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var path = Arg(a, "path");
        await ApproveRemoteRead("sftp_list", h, $"폴더 목록: {(path.Length > 0 ? path : "(홈)")}");
        return await WithSftp(conn, c =>
        {
            var dir = path.Length > 0 ? path : c.WorkingDirectory;
            var items = c.ListDirectory(dir).Where(f => f.Name is not "." and not "..")
                .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder($"[{h.DisplayName}] {dir} — {items.Count}개{(items.Count > 500 ? " (앞 500개)" : "")}\n종류 | 권한 | 크기 | 수정 시각 | 이름\n");
            foreach (var f in items.Take(500))
                sb.AppendLine($"{(f.IsDirectory ? "dir" : f.IsSymbolicLink ? "link" : "file")} | {Perms(f)} | {(f.IsDirectory ? "-" : SftpPanel.HumanSize(f.Length))} | {f.LastWriteTime:yyyy-MM-dd HH:mm} | {f.Name}");
            return sb.ToString();
        });
    }

    private static string Perms(Renci.SshNet.Sftp.ISftpFile f) =>
        (f.OwnerCanRead ? "r" : "-") + (f.OwnerCanWrite ? "w" : "-") + (f.OwnerCanExecute ? "x" : "-") +
        (f.GroupCanRead ? "r" : "-") + (f.GroupCanWrite ? "w" : "-") + (f.GroupCanExecute ? "x" : "-") +
        (f.OthersCanRead ? "r" : "-") + (f.OthersCanWrite ? "w" : "-") + (f.OthersCanExecute ? "x" : "-");

    private async Task<string> SftpReadAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var path = Arg(a, "path");
        var max = Math.Clamp(IntArg(a, "max_bytes") ?? 65536, 1024, 1048576);
        var tail = BoolArg(a, "tail") == true;
        await ApproveRemoteRead("sftp_read", h, $"파일 읽기: {path} ({(tail ? "끝 " : "앞 ")}{SftpPanel.HumanSize(max)})");
        return await WithSftp(conn, c =>
        {
            var attr = c.GetAttributes(path);
            if (attr.IsDirectory) throw new McpToolException("폴더입니다. sftp_list 를 쓰세요.");
            using var s = c.OpenRead(path);
            var size = attr.Size;
            if (tail && size > max) s.Seek(size - max, SeekOrigin.Begin);
            var buf = new byte[Math.Min(max, size > 0 ? size : max)];
            var read = 0;
            while (read < buf.Length) { var n = s.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            var data = buf.AsSpan(0, read);
            if (McpHelpers.LooksBinary(data)) throw new McpToolException($"바이너리 파일로 보입니다 ({SftpPanel.HumanSize(size)}). 필요하면 sftp_download 로 받으세요.");
            var text = Encoding.UTF8.GetString(data);
            var cut = size > read ? (tail ? $" (끝 {SftpPanel.HumanSize(read)}만)" : $" (앞 {SftpPanel.HumanSize(read)}만)") : "";
            return $"[{h.DisplayName}] {path} — {SftpPanel.HumanSize(size)}{cut}\n{text}";
        });
    }

    private async Task<string> SftpDownloadAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var path = Arg(a, "path").Trim();
        if (path.Length == 0) throw new McpToolException("path 가 필요합니다.");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Nubis", SftpPanel.SafeLocalName(h.DisplayName));
        var target = $"[SFTP]  {h.DisplayName}  ({string.Join(" → ", h.AddressCandidates())})";
        var what = $"받기: {path}\n저장 위치: {root}";
        var ok = await OnUi(() => ApprovalDialog.Ask(this, "Claude 가 파일 다운로드를 요청합니다",
            "서버의 파일을 이 PC 로 받습니다. 기존 파일은 덮어쓰지 않습니다(이름 뒤에 번호).", new[] { target }, what));
        AuditLog.Write("sftp_download", new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");

        return await WithSftp(conn, c =>
        {
            Directory.CreateDirectory(root);
            var attr = c.GetAttributes(path);
            var top = SftpPanel.UniquePath(Path.Combine(root, SftpPanel.SafeLocalName(McpHelpers.RemoteFileName(path))), attr.IsDirectory);
            var count = 0;
            long bytes = 0;
            var full = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            void Get(string remote, bool isDir, string local)
            {
                if (!Path.GetFullPath(local).StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new McpToolException("허용되지 않는 파일 이름이 있어 중단했습니다.");
                if (isDir)
                {
                    Directory.CreateDirectory(local);
                    foreach (var f in c.ListDirectory(remote).Where(f => f.Name is not "." and not ".."))
                        Get(f.FullName, f.IsDirectory && !f.IsSymbolicLink, Path.Combine(local, SftpPanel.SafeLocalName(f.Name)));
                    return;
                }
                if (++count > 5000) throw new McpToolException("파일이 5000개를 넘어 중단했습니다.");
                using var fs = File.Create(local);
                c.DownloadFile(remote, fs);
                bytes += fs.Length;
            }
            Get(path, attr.IsDirectory, top);
            AuditLog.Write("sftp_download", new[] { target }, path, $"완료 {count}개 {SftpPanel.HumanSize(bytes)}");
            return $"받았습니다: {count}개 ({SftpPanel.HumanSize(bytes)}) → {top}";
        });
    }

    private async Task<string> SftpUploadAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var local = Arg(a, "local_path").Trim().Trim('"');
        var remoteDir = Arg(a, "remote_dir").Trim();
        var overwrite = BoolArg(a, "overwrite") == true;
        if (local.Length == 0 || remoteDir.Length == 0) throw new McpToolException("local_path 와 remote_dir 가 필요합니다.");
        if (!Path.IsPathFullyQualified(local)) throw new McpToolException("local_path 는 절대 경로여야 합니다.");
        if (McpHelpers.BlockedUploadReason(local, AppPaths.DataDir) is { } why) throw new McpToolException(why);
        var isDir = Directory.Exists(local);
        if (!isDir && !File.Exists(local)) throw new McpToolException($"로컬 경로가 없습니다: {local}");

        var files = isDir ? Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).ToList() : new List<string> { local };
        if (files.Count > 5000) throw new McpToolException("파일이 5000개를 넘습니다.");
        var blocked = files.Select(f => McpHelpers.BlockedUploadReason(f, AppPaths.DataDir)).FirstOrDefault(r => r != null);
        if (blocked != null) throw new McpToolException(blocked);
        var total = files.Sum(f => new FileInfo(f).Length);

        var target = $"[SFTP]  {h.DisplayName}  ({string.Join(" → ", h.AddressCandidates())})";
        var what = $"올리기: {local}{(isDir ? $" (폴더, 파일 {files.Count}개)" : "")} — {SftpPanel.HumanSize(total)}\n원격 폴더: {remoteDir}" + (overwrite ? "\n같은 이름이 있으면 덮어씀" : "");
        var ok = await OnUi(() => ApprovalDialog.Ask(this, "Claude 가 파일 업로드를 요청합니다",
            "이 PC 의 파일을 서버로 보냅니다. 보내도 되는 파일인지 확인하세요.", new[] { target }, what));
        AuditLog.Write("sftp_upload", new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");

        return await WithSftp(conn, c =>
        {
            var name = Path.GetFileName(local.TrimEnd('\\'));
            var root = SftpPanel.Combine(remoteDir, name);
            if (c.Exists(root) && !overwrite) throw new McpToolException($"원격에 이미 있습니다: {root} (덮어쓰려면 overwrite=true)");
            if (isDir)
            {
                if (!c.Exists(root)) c.CreateDirectory(root);
                foreach (var d in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                {
                    var rd = SftpPanel.Combine(root, Path.GetRelativePath(local, d).Replace('\\', '/'));
                    if (!c.Exists(rd)) c.CreateDirectory(rd);
                }
            }
            foreach (var f in files)
            {
                var remote = isDir ? SftpPanel.Combine(root, Path.GetRelativePath(local, f).Replace('\\', '/')) : root;
                using var fs = File.OpenRead(f);
                c.UploadFile(fs, remote, true);
            }
            AuditLog.Write("sftp_upload", new[] { target }, local, $"완료 {files.Count}개 {SftpPanel.HumanSize(total)}");
            return $"올렸습니다: {files.Count}개 ({SftpPanel.HumanSize(total)}) → {root}";
        });
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
