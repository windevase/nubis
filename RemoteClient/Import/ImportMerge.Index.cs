using System;
using System.Collections.Generic;

namespace Nubis;

/// <summary>ImportMerge 의 색인들 (점프 서버 찾기 · 기존 목록 대조) — 수천 대 가져오기에서 줄마다 목록 전체를 훑지 않게</summary>
public static partial class ImportMerge
{
    /// <summary>점프 서버 주소(·포트) 집합 — 대소문자 무시</summary>
    private sealed class JumpAddressSet
    {
        private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hostPorts = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string address, int port)
        {
            _hosts.Add(address);
            _hostPorts.Add(address + "|" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public bool Contains(string host, int? port) =>
            port == null ? _hosts.Contains(host) : _hostPorts.Contains(host + "|" + port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 기존 목록 색인: (종류, 포트, 주소 소문자) → 목록에서 처음 나오는 호스트. FindExisting 과 같은 결과를 O(1) 로.
    /// 주소·사설 IP·공인 IP 모두 키로 넣는다
    /// </summary>
    private sealed class ExistingIndex
    {
        private readonly Dictionary<string, HostEntry> _map = new(StringComparer.OrdinalIgnoreCase);

        public ExistingIndex(IEnumerable<HostEntry> hosts)
        {
            foreach (var h in hosts) Add(h);
        }

        /// <summary>뒤에 추가한 호스트는 앞의 호스트를 덮지 않는다 (FirstOrDefault 와 같은 우선순위)</summary>
        public void Add(HostEntry h)
        {
            foreach (var a in Addresses(h)) _map.TryAdd(IndexKey(h.Protocol, a, h.Port), h);
        }

        public HostEntry? Find(Protocol protocol, string address, int port) =>
            _map.TryGetValue(IndexKey(protocol, address.Trim(), port), out var h) ? h : null;

        private static string IndexKey(Protocol p, string address, int port) =>
            ((int)p).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + address;
    }

    /// <summary>점프 서버 찾기: 가져온 이름 → 목록의 이름 → 주소(·포트). SSH 호스트만</summary>
    private static HostEntry? ResolveJump(string spec, Dictionary<string, HostEntry> byName, SshLookup ssh)
    {
        if (byName.TryGetValue(spec, out var named) && named.Protocol == Protocol.Ssh) return named;
        if (ssh.ByLabel(spec) is { } byLabel) return byLabel;
        var (host, port) = ParseJumpSpec(spec);
        if (host.Length == 0) return null;
        if (byName.TryGetValue(host, out var named2) && named2.Protocol == Protocol.Ssh) return named2;
        if (ssh.ByLabel(host) is { } byLabel2) return byLabel2;
        return ssh.ByAddress(host, port);
    }

    /// <summary>
    /// 목록의 SSH 호스트 색인 — 이름(앞뒤 공백 무시) · 주소 · 주소+포트 → 목록에서 처음 나오는 호스트 (FirstOrDefault 와 같은 우선순위)
    /// </summary>
    private sealed class SshLookup
    {
        private readonly Dictionary<string, HostEntry> _label = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HostEntry> _addr = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HostEntry> _addrPort = new(StringComparer.OrdinalIgnoreCase);

        public SshLookup(IEnumerable<HostEntry> hosts)
        {
            foreach (var e in hosts)
            {
                if (e.Protocol != Protocol.Ssh) continue;
                _label.TryAdd(e.Label.Trim(), e);
                foreach (var a in Addresses(e))
                {
                    _addr.TryAdd(a, e);
                    _addrPort.TryAdd(a + "|" + e.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), e);
                }
            }
        }

        public HostEntry? ByLabel(string label) => _label.TryGetValue(label.Trim(), out var h) ? h : null;

        public HostEntry? ByAddress(string host, int? port) =>
            (port == null
                ? _addr.TryGetValue(host, out var h)
                : _addrPort.TryGetValue(host + "|" + port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out h)) ? h : null;
    }
}
