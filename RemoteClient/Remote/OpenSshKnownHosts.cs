using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>
/// OpenSSH 의 ~/.ssh/known_hosts 를 읽기 전용으로 참고한다 — ssh 로 이미 신뢰한 서버를 Nubis 가 처음 본다고 묻지 않게,
/// 그리고 ssh 가 알던 키와 다르면 바뀐 키처럼 경고하게. 파일은 절대 고치지 않는다.
/// 형식: "호스트[,호스트...] 키형식 base64 [설명]", 호스트는 이름 · [이름]:포트 · 해시(|1|salt|hash) · 와일드카드(* ?) · 부정(!).
/// @revoked 표시가 붙은 키는 폐기된 키, @cert-authority 줄은 쓰지 않는다.
/// </summary>
public static class OpenSshKnownHosts
{
    public enum Verdict
    {
        /// <summary>이 호스트의 같은 형식 키가 없음</summary>
        Unknown,
        /// <summary>같은 키가 있음 (신뢰)</summary>
        Match,
        /// <summary>같은 형식의 다른 키만 있음 (키가 바뀜)</summary>
        Mismatch,
        /// <summary>@revoked 로 폐기된 키</summary>
        Revoked,
    }

    /// <summary>known_hosts 위치 (null 이면 %USERPROFILE%\.ssh\known_hosts). 테스트가 바꾼다</summary>
    public static string? FilePath { get; set; }

    public static Verdict Check(string host, int port, byte[] keyBlob)
    {
        var path = FilePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
        try
        {
            if (!File.Exists(path)) return Verdict.Unknown;
            return Check(File.ReadLines(path), host, port, keyBlob);
        }
        catch { return Verdict.Unknown; }   // 읽을 수 없으면 참고하지 않는다
    }

    public static Verdict Check(IEnumerable<string> lines, string host, int port, byte[] keyBlob)
    {
        var token = port == 22 ? host : $"[{host}]:{port}";
        var type = BlobType(keyBlob);
        var sawOther = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = Regex.Split(line, @"\s+");
            var marker = parts[0].StartsWith('@') ? parts[0] : null;
            var f = marker == null ? parts : parts.Skip(1).ToArray();
            if (f.Length < 3 || marker == "@cert-authority") continue;
            if (!HostsMatch(f[0], token)) continue;
            byte[] blob;
            try { blob = Convert.FromBase64String(f[2]); }
            catch (FormatException) { continue; }
            if (BlobType(blob) != type) continue;   // 다른 형식의 키 (ed25519 와 rsa 등) — 판단하지 않음
            var same = blob.AsSpan().SequenceEqual(keyBlob);
            if (marker == "@revoked") { if (same) return Verdict.Revoked; continue; }
            if (same) return Verdict.Match;
            sawOther = true;
        }
        return sawOther ? Verdict.Mismatch : Verdict.Unknown;
    }

    /// <summary>호스트 목록(쉼표 구분)이 token 과 맞는가. 부정(!) 패턴이 맞으면 그 줄은 해당하지 않는다</summary>
    public static bool HostsMatch(string list, string token)
    {
        var matched = false;
        foreach (var p in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p.StartsWith('!')) { if (PatternMatch(p[1..], token)) return false; continue; }
            if (PatternMatch(p, token)) matched = true;
        }
        return matched;
    }

    private static bool PatternMatch(string pattern, string token)
    {
        if (pattern.StartsWith("|1|", StringComparison.Ordinal))
        {
            // 해시된 호스트: |1|base64(salt)|base64(HMAC-SHA1(salt, 호스트))
            var s = pattern.Split('|');
            if (s.Length != 4) return false;
            try
            {
                var mac = HMACSHA1.HashData(Convert.FromBase64String(s[2]), Encoding.UTF8.GetBytes(token));
                return mac.AsSpan().SequenceEqual(Convert.FromBase64String(s[3]));
            }
            catch (FormatException) { return false; }
        }
        if (pattern.IndexOfAny(['*', '?']) < 0) return string.Equals(pattern, token, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(token, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>공개키 blob 의 첫 필드(키 형식 이름)</summary>
    private static string BlobType(byte[] blob)
    {
        try { return VaultStore.BlobType(blob); }
        catch { return ""; }
    }
}
