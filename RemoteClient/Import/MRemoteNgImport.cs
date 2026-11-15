using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Nubis;

/// <summary>
/// mRemoteNG 연결 파일(confCons.xml) 가져오기.
/// 폴더(Container) → 그룹 경로(" / "), RDP → RDP, SSH1/SSH2 → SSH. 그 밖의 프로토콜은 건너뛰고 개수만 알린다.
/// 비밀번호(암호화 값)는 절대 가져오지 않는다. 상속(InheritUsername/InheritPort/InheritProtocol)은 상위 폴더 값으로 푼다.
/// </summary>
public static class MRemoteNgImport
{
    /// <summary>mRemoteNG 기본 연결 파일 위치 (%APPDATA%\mRemoteNG\confCons.xml)</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG", "confCons.xml");

    /// <summary>XML 문자열을 읽는다. 잘못된 XML·암호화 파일이면 빈 결과 + 경고</summary>
    public static ImportResult Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            var r = ImportResult.Empty();
            r.Warnings.Add(L.T("mRemoteNG 연결 파일이 비어 있습니다."));
            return r;
        }
        using var reader = new StringReader(xml);
        return Load(() => XmlReader.Create(reader, SecureSettings()));
    }

    /// <summary>파일을 읽는다 (XML 선언의 인코딩을 따르도록 스트림으로 연다). 읽기 실패면 빈 결과 + 경고</summary>
    public static ImportResult ParseFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Load(() => XmlReader.Create(stream, SecureSettings()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var r = ImportResult.Empty();
            r.Warnings.Add(L.F("파일을 읽을 수 없습니다: {0}", ex.Message));
            return r;
        }
    }

    /// <summary>외부 엔터티·DTD 를 막은 설정 (XXE 방지)</summary>
    private static XmlReaderSettings SecureSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static ImportResult Load(Func<XmlReader> open)
    {
        var result = ImportResult.Empty();
        XDocument doc;
        try
        {
            using var xr = open();
            doc = XDocument.Load(xr, LoadOptions.None);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            result.Warnings.Add(L.F("mRemoteNG 연결 파일을 읽을 수 없습니다 (XML 형식 오류): {0}", ex.Message));
            return result;
        }

        var root = doc.Root;
        if (root == null || !root.Name.LocalName.Equals("Connections", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add(L.T("mRemoteNG 연결 파일(confCons.xml) 형식이 아닙니다."));
            return result;
        }

        // 파일 전체 암호화: 루트 안이 Node 가 아니라 암호화된 문자열 덩어리다
        var hasNodes = root.Elements().Any(IsNode);
        if (IsTrue(Attr(root, "FullFileEncryption")) || (!hasNodes && !string.IsNullOrWhiteSpace(root.Value)))
        {
            result.Warnings.Add(L.T("파일 전체가 암호화된 mRemoteNG 연결 파일은 읽을 수 없습니다. mRemoteNG 에서 파일 → 내보내기로 전체 암호화 없이 내보낸 뒤 다시 가져오세요."));
            return result;
        }

        var state = new WalkState();
        Walk(root, Context.Root, state, result.Hosts);

        if (state.Skipped.Count > 0)
        {
            var total = state.Skipped.Values.Sum();
            var detail = string.Join(", ", state.Skipped
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key + " " + kv.Value.ToString(CultureInfo.InvariantCulture)));
            result.Warnings.Add(L.F("지원하지 않는 프로토콜(SSH·RDP 외) 연결 {0}개를 건너뛰었습니다: {1}", total, detail));
        }
        if (state.NoAddress > 0)
            result.Warnings.Add(L.F("주소(Hostname)가 없는 연결 {0}개를 건너뛰었습니다.", state.NoAddress));
        if (state.PasswordSeen)
            result.Warnings.Add(L.T("mRemoteNG 에 저장된 비밀번호는 가져오지 않습니다. 등록한 뒤 Vault 계정을 지정하세요."));
        return result;
    }

    /// <summary>상위 폴더에서 물려받을 값 (상속 해석용). 최상위는 값이 없다</summary>
    private sealed record Context(string? Group, string? Username, string? Port, string? Protocol, string? Domain, bool IsRoot)
    {
        public static readonly Context Root = new(null, null, null, null, null, true);
    }

    private sealed class WalkState
    {
        public readonly Dictionary<string, int> Skipped = new(StringComparer.OrdinalIgnoreCase);
        public int NoAddress;
        public bool PasswordSeen;
    }

    private static void Walk(XElement parent, Context ctx, WalkState state, List<ImportedHost> hosts)
    {
        foreach (var node in parent.Elements().Where(IsNode))
        {
            if (!string.IsNullOrEmpty(Attr(node, "Password"))) state.PasswordSeen = true;

            var username = Resolve(node, "Username", ctx.Username, ctx.IsRoot);
            var port = Resolve(node, "Port", ctx.Port, ctx.IsRoot);
            var protocol = Resolve(node, "Protocol", ctx.Protocol, ctx.IsRoot);
            var domain = Resolve(node, "Domain", ctx.Domain, ctx.IsRoot);
            var name = (Attr(node, "Name") ?? "").Trim();
            var hostname = (Attr(node, "Hostname") ?? "").Trim();

            if (IsContainer(node, hostname))
            {
                var group = string.IsNullOrEmpty(name) ? ctx.Group
                    : string.IsNullOrEmpty(ctx.Group) ? name : ctx.Group + " / " + name;
                Walk(node, new Context(group, username, port, protocol, domain, false), state, hosts);
                continue;
            }

            var mapped = MapProtocol(protocol, out var skip);
            if (skip)
            {
                var key = protocol!.Trim();
                state.Skipped[key] = state.Skipped.TryGetValue(key, out var n) ? n + 1 : 1;
                continue;
            }
            if (hostname.Length == 0) { state.NoAddress++; continue; }

            // RDP 의 Domain 칸은 "DOMAIN\user" 로 합친다 (사용자명에 이미 도메인 · UPN 이 있으면 그대로)
            var user = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
            if (user != null && mapped == Nubis.Protocol.Rdp && !string.IsNullOrWhiteSpace(domain)
                && !user.Contains('\\') && !user.Contains('@'))
                user = domain.Trim() + "\\" + user;

            hosts.Add(new ImportedHost
            {
                Name = name.Length > 0 ? name : hostname,
                Address = hostname,
                Port = ParsePort(port) ?? DefaultPort(mapped),
                Protocol = mapped,
                Group = string.IsNullOrEmpty(ctx.Group) ? null : ctx.Group,
                Username = user,
                Note = string.IsNullOrWhiteSpace(Attr(node, "Descr")) ? null : Attr(node, "Descr")!.Trim(),
            });
            // 연결 아래에 Node 가 더 있는 이상한 파일도 놓치지 않는다 (그룹은 그대로)
            if (node.Elements().Any(IsNode)) Walk(node, ctx, state, hosts);
        }
    }

    /// <summary>Inherit&lt;속성&gt;="true" 이고 상위 폴더가 있으면 상위 값, 아니면 자기 값</summary>
    private static string? Resolve(XElement node, string attr, string? inherited, bool parentIsRoot)
    {
        if (!parentIsRoot && IsTrue(Attr(node, "Inherit" + attr))) return inherited;
        return Attr(node, attr);
    }

    /// <summary>Type 이 없으면(옛 형식) 주소가 없고 하위 Node 가 있을 때 폴더로 본다</summary>
    private static bool IsContainer(XElement node, string hostname)
    {
        var type = Attr(node, "Type");
        if (!string.IsNullOrWhiteSpace(type))
            return type.Trim().Equals("Container", StringComparison.OrdinalIgnoreCase)
                || type.Trim().Equals("Root", StringComparison.OrdinalIgnoreCase);
        return hostname.Length == 0 && node.Elements().Any(IsNode);
    }

    /// <summary>RDP → Rdp, SSH1/SSH2 → Ssh. 값이 없으면 null(미리보기에서 추정). 그 밖은 skip</summary>
    private static Protocol? MapProtocol(string? protocol, out bool skip)
    {
        skip = false;
        var p = protocol?.Trim() ?? "";
        if (p.Length == 0) return null;
        if (p.Equals("RDP", StringComparison.OrdinalIgnoreCase)) return Nubis.Protocol.Rdp;
        if (p.Equals("SSH1", StringComparison.OrdinalIgnoreCase) || p.Equals("SSH2", StringComparison.OrdinalIgnoreCase)
            || p.Equals("SSH", StringComparison.OrdinalIgnoreCase))
            return Nubis.Protocol.Ssh;
        skip = true;
        return null;
    }

    private static int? ParsePort(string? s) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : null;

    private static int? DefaultPort(Protocol? p) => p switch
    {
        Nubis.Protocol.Rdp => 3389,
        Nubis.Protocol.Ssh => 22,
        _ => null,
    };

    private static bool IsNode(XElement e) => e.Name.LocalName.Equals("Node", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(string? s) => bool.TryParse(s?.Trim(), out var b) && b;

    /// <summary>속성 이름 대소문자 무시 (옛 버전 호환)</summary>
    private static string? Attr(XElement e, string name)
    {
        foreach (var a in e.Attributes())
            if (!a.IsNamespaceDeclaration && a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return a.Value;
        return null;
    }
}
