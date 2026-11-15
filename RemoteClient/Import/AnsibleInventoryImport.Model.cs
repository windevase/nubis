using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nubis;

/// <summary>Ansible 인벤토리: 그룹 · 호스트 · 변수 모델과 결과 만들기 (AnsibleInventoryImport 의 일부)</summary>
public static partial class AnsibleInventoryImport
{
    // ───────────────────────── 인벤토리 모델 ─────────────────────────

    private readonly record struct VarValue(string Value, bool Unresolved);

    private sealed class GroupInfo
    {
        public required string Name { get; init; }
        public required int Order { get; init; }
        public Dictionary<string, VarValue> Vars { get; } = new(StringComparer.Ordinal);
        public List<string> Parents { get; } = new();
        public HashSet<YamlNode> YamlVisited { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class HostInfo
    {
        public required string Name { get; init; }
        public Dictionary<string, VarValue> Vars { get; } = new(StringComparer.Ordinal);
        /// <summary>직접 속한 그룹 (선언 순서)</summary>
        public List<string> Groups { get; } = new();
    }

    private sealed class Inventory
    {
        private readonly Dictionary<string, GroupInfo> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HostInfo> _hosts = new(StringComparer.Ordinal);
        private readonly List<HostInfo> _hostOrder = new();
        private readonly List<string> _warnings = new();
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
        private bool _secretSeen, _limitHit;

        public void Warn(string message)
        {
            if (_warned.Add(message)) _warnings.Add(message);
        }

        public GroupInfo GetGroup(string name)
        {
            if (!_groups.TryGetValue(name, out var g))
                _groups[name] = g = new GroupInfo { Name = name, Order = _groups.Count };
            return g;
        }

        /// <summary>지금까지 나온 그룹 이름 (사본)</summary>
        public List<string> GroupNames() => _groups.Keys.ToList();

        /// <summary>호스트 이름과 그 호스트 변수 사전 (host_vars 를 덮어쓸 곳)</summary>
        public List<(string Name, Dictionary<string, VarValue> Vars)> HostVarTargets() =>
            _hostOrder.Select(h => (h.Name, h.Vars)).ToList();

        public void AddChild(string parent, string child)
        {
            GetGroup(parent);
            var c = GetGroup(child);
            if (child == AllGroup || child == parent) return;
            if (!c.Parents.Contains(parent)) c.Parents.Add(parent);
        }

        /// <summary>비밀번호는 버리고, 우리가 쓰는 변수만 저장. 템플릿 · 암호화 값은 "해석 안 됨"으로 표시해 둔다</summary>
        public void SetVar(Dictionary<string, VarValue> target, string key, string value, bool tagged)
        {
            if (IsSecret(key)) { _secretSeen = true; return; }
            if (!RelevantVars.Contains(key)) return;
            bool unresolved = tagged || value.Contains("{{", StringComparison.Ordinal) || value.Contains("{%", StringComparison.Ordinal);
            target[key] = new VarValue(value, unresolved);
        }

        public void AddHostPattern(string group, string pattern, List<KeyValuePair<string, string>> vars)
        {
            var dict = new Dictionary<string, VarValue>(StringComparer.Ordinal);
            foreach (var kv in vars) SetVar(dict, kv.Key, kv.Value, tagged: false); // 따옴표는 ShellSplit 가 이미 벗겼다
            AddHostPattern(group, pattern, dict);
        }

        public void AddHostPattern(string group, string pattern, Dictionary<string, VarValue> vars)
        {
            GetGroup(group);
            // 한 패턴은 최대 MaxHosts 개까지만 펼치고, 새 호스트는 전체 한도 안에서만 받는다 (이미 있는 호스트는 다시 세지 않음)
            var (names, port, truncated) = ExpandHostPattern(pattern, MaxHosts);
            if (truncated) _limitHit = true;
            foreach (var name in names)
            {
                if (!_hosts.TryGetValue(name, out var h))
                {
                    if (_hostOrder.Count >= MaxHosts) { _limitHit = true; continue; }
                    _hosts[name] = h = new HostInfo { Name = name };
                    _hostOrder.Add(h);
                }
                if (!h.Groups.Contains(group)) h.Groups.Add(group);
                // name:port 축약은 호스트 변수 ansible_port 로 (같은 줄의 ansible_port 가 있으면 그것이 이김)
                if (port is int p) h.Vars["ansible_port"] = new VarValue(p.ToString(CultureInfo.InvariantCulture), false);
                foreach (var kv in vars) h.Vars[kv.Key] = kv.Value;
            }
        }

        private static bool IsSecret(string key) =>
            key.StartsWith("ansible_", StringComparison.Ordinal) && key.Contains("pass", StringComparison.OrdinalIgnoreCase);

        // ── 결과 만들기 ──

        private int Depth(string name, HashSet<string> visiting)
        {
            if (name == AllGroup) return 0;
            if (!_groups.TryGetValue(name, out var g) || !visiting.Add(name)) return 1;
            int d = 1;
            foreach (var p in g.Parents) d = Math.Max(d, Depth(p, visiting) + 1);
            visiting.Remove(name);
            return d;
        }

        private void Ancestors(string name, HashSet<string> acc)
        {
            if (!acc.Add(name) || !_groups.TryGetValue(name, out var g)) return;
            foreach (var p in g.Parents) Ancestors(p, acc);
        }

        /// <summary>"prod / web" 처럼 첫 번째 상위 그룹을 따라 올라간 경로 (all 제외)</summary>
        private string GroupPath(string name)
        {
            var parts = new List<string> { name };
            var seen = new HashSet<string>(StringComparer.Ordinal) { name };
            var cur = name;
            while (_groups.TryGetValue(cur, out var g))
            {
                var parent = g.Parents.FirstOrDefault(p => p != AllGroup && p != UngroupedGroup && !seen.Contains(p));
                if (parent == null) break;
                parts.Add(parent);
                seen.Add(parent);
                cur = parent;
            }
            parts.Reverse();
            return string.Join(" / ", parts);
        }

        public ImportResult Build()
        {
            var result = ImportResult.Empty();
            var unresolvedKeys = new SortedSet<string>(StringComparer.Ordinal);
            int skippedConn = 0;
            bool multiHop = false;
            var depthCache = new Dictionary<string, int>(StringComparer.Ordinal);
            int DepthOf(string g) => depthCache.TryGetValue(g, out var d) ? d : depthCache[g] = Depth(g, new HashSet<string>(StringComparer.Ordinal));

            foreach (var h in _hostOrder)
            {
                // 변수 합치기: all → 그룹(얕은 것부터, 같은 깊이는 이름순 — Ansible 과 같음) → 호스트
                var groups = new HashSet<string>(StringComparer.Ordinal);
                foreach (var g in h.Groups) Ancestors(g, groups);
                groups.Remove(AllGroup);
                var merged = new Dictionary<string, VarValue>(StringComparer.Ordinal);
                if (_groups.TryGetValue(AllGroup, out var all)) foreach (var kv in all.Vars) merged[kv.Key] = kv.Value;
                foreach (var g in groups.OrderBy(DepthOf).ThenBy(x => x, StringComparer.Ordinal))
                    foreach (var kv in _groups[g].Vars) merged[kv.Key] = kv.Value;
                foreach (var kv in h.Vars) merged[kv.Key] = kv.Value;

                string? Get(string key)
                {
                    if (!merged.TryGetValue(key, out var v)) return null;
                    if (v.Unresolved) { unresolvedKeys.Add(key); return null; }
                    var s = v.Value.Trim();
                    return s.Length == 0 ? null : s;
                }

                var conn = Get("ansible_connection");
                var connName = conn == null ? "ssh" : conn.Substring(conn.LastIndexOf('.') + 1);
                if (NonRemoteConnections.Contains(connName)) { skippedConn++; continue; }
                bool winrm = connName.Equals("winrm", StringComparison.OrdinalIgnoreCase) || connName.Equals("psrp", StringComparison.OrdinalIgnoreCase);

                int? port = null;
                var portText = Get("ansible_port") ?? Get("ansible_ssh_port");
                if (portText != null)
                {
                    if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535) port = p;
                    else Warn(L.F("포트 값이 올바르지 않아 무시했습니다: {0}", h.Name + " = " + portText));
                }

                // 그룹: 직접 속한 그룹 중 가장 깊은 것 (같으면 먼저 선언된 것). ungrouped 는 다른 그룹이 없을 때만
                var direct = h.Groups.Where(g => g != AllGroup).ToList();
                if (direct.Count > 1) direct.RemoveAll(g => g == UngroupedGroup);
                string chosen = UngroupedGroup;
                int best = -1;
                foreach (var g in direct)
                {
                    int d = DepthOf(g);
                    if (d > best) { best = d; chosen = g; }
                }
                var chosenAncestors = new HashSet<string>(StringComparer.Ordinal);
                Ancestors(chosen, chosenAncestors);
                var others = direct.Where(g => !chosenAncestors.Contains(g)).ToList();

                var notes = new List<string>();
                if (winrm) notes.Add(port is int wp ? "WinRM :" + wp.ToString(CultureInfo.InvariantCulture) : "WinRM");
                if (others.Count > 0) notes.Add(L.F("다른 그룹: {0}", string.Join(", ", others)));

                string? jump = null;
                if (!winrm)
                {
                    var (j, multi) = FindJumpHost(Get("ansible_ssh_common_args"));
                    if (j == null) (j, multi) = FindJumpHost(Get("ansible_ssh_extra_args"));
                    jump = j;
                    multiHop |= multi;
                }

                result.Hosts.Add(new ImportedHost
                {
                    Name = h.Name,
                    Address = Get("ansible_host") ?? Get("ansible_ssh_host") ?? h.Name,
                    // WinRM 포트(5985/5986)는 RDP 포트가 아니므로 넣지 않는다
                    Port = winrm ? null : port,
                    Protocol = winrm ? Protocol.Rdp : Protocol.Ssh,
                    Group = GroupPath(chosen),
                    Username = Get("ansible_user") ?? Get("ansible_ssh_user"),
                    JumpHost = jump,
                    Os = winrm ? "Windows" : null,
                    Note = notes.Count > 0 ? string.Join(" · ", notes) : null,
                });
            }

            // 점프 서버가 인벤토리의 다른 호스트(이름 · 주소)를 가리키면 그 항목 이름으로 바꾼다 (사용자 · 포트 지정이 없을 때만)
            // 이름 · 주소 → 첫 항목 (호스트 수천 대에서 줄마다 목록을 훑지 않게)
            var byName = new Dictionary<string, ImportedHost>(StringComparer.Ordinal);
            var byAddress = new Dictionary<string, ImportedHost>(StringComparer.Ordinal);
            foreach (var x in result.Hosts)
            {
                byName.TryAdd(x.Name, x);
                byAddress.TryAdd(x.Address, x);
            }
            for (int i = 0; i < result.Hosts.Count; i++)
            {
                var jh = result.Hosts[i].JumpHost;
                if (jh == null || jh.Contains('@') || jh.Contains(':')) continue;
                var target = byName.TryGetValue(jh, out var n) ? n : byAddress.TryGetValue(jh, out var ad) ? ad : null;
                if (target != null && !ReferenceEquals(target, result.Hosts[i])) result.Hosts[i] = result.Hosts[i] with { JumpHost = target.Name };
            }

            var warnings = new List<string>();
            if (_secretSeen) warnings.Add(L.T("인벤토리의 비밀번호(ansible_password 등)는 가져오지 않았습니다. 계정은 Vault 로 지정하세요."));
            if (unresolvedKeys.Count > 0) warnings.Add(L.F("Jinja 템플릿이나 암호화된 값은 해석하지 않고 건너뛰었습니다: {0}", string.Join(", ", unresolvedKeys)));
            if (skippedConn > 0) warnings.Add(L.F("SSH · WinRM 이 아닌 연결 방식(local · docker 등)의 호스트 {0}개는 건너뛰었습니다.", skippedConn));
            if (multiHop) warnings.Add(L.T("다단계 ProxyJump 는 첫 번째 점프 서버만 가져왔습니다."));
            if (_limitHit) warnings.Add(L.F("호스트가 {0}개를 넘어 나머지는 가져오지 않았습니다.", MaxHosts));
            warnings.AddRange(_warnings);
            result.Warnings.AddRange(warnings);
            return result;
        }
    }
}
