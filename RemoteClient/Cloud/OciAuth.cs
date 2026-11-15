using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nubis;

// =====================================================================
// Oracle Cloud (OCI): ~/.oci/config 프로필 + HTTP Signature(draft-cavage-08, rsa-sha256) 직접 서명
// OCI SDK 없이 손으로 서명한다 (Tencent TC3 와 같은 방식).
// =====================================================================

/// <summary>~/.oci/config 의 한 프로필 (DEFAULT 상속 적용 후 값, 경로는 절대 경로로 펼친 상태)</summary>
public sealed record OciProfile(string Name, string Tenancy, string Region, string? User, string? Fingerprint,
    string KeyFile, string? PassPhrase, string? SecurityTokenFile)
{
    /// <summary>oci session authenticate 로 만든 세션 토큰 프로필인지</summary>
    public bool IsSession => !string.IsNullOrWhiteSpace(SecurityTokenFile);
}

/// <summary>OCI CLI 설정 파일(INI) 읽기</summary>
public static class OciConfig
{
    private const string DefaultSection = "DEFAULT";

    /// <summary>OCI_CLI_CONFIG_FILE 환경 변수가 있으면 그것, 없으면 %USERPROFILE%\.oci\config (매번 계산 — 실행 중 환경 변수가 바뀔 수 있음)</summary>
    public static string ConfigPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("OCI_CLI_CONFIG_FILE");
            if (!string.IsNullOrWhiteSpace(env))
                return ExpandPath(env, Environment.CurrentDirectory);
            return Path.Combine(Home, ".oci", "config");
        }
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>설정 파일의 모든 프로필 (DEFAULT 포함, 나온 순서). 파일이 없으면 빈 목록</summary>
    public static List<OciProfile> ReadProfiles(string? path = null)
    {
        path ??= ConfigPath;
        var result = new List<OciProfile>();
        if (!File.Exists(path)) return result;

        var configDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
        var sections = ParseIniOrdered(File.ReadAllLines(path));
        var defaults = sections.FirstOrDefault(kv => string.Equals(kv.Key, DefaultSection, StringComparison.Ordinal)).Value;

        foreach (var (name, own) in sections)
        {
            // OCI CLI(configparser) 처럼 DEFAULT 값을 깔고 프로필 값으로 덮는다
            var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (defaults != null) foreach (var kv in defaults) v[kv.Key] = kv.Value;
            foreach (var kv in own) v[kv.Key] = kv.Value;

            string? Val(string key) => v.TryGetValue(key, out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
            var tokenFile = Val("security_token_file");
            result.Add(new OciProfile(
                name,
                Val("tenancy") ?? "",
                Val("region") ?? "",
                Val("user"),
                Val("fingerprint"),
                ExpandPath(Val("key_file"), configDir),
                Val("pass_phrase"),
                tokenFile == null ? null : ExpandPath(tokenFile, configDir)));
        }
        return result;
    }

    /// <summary>이름으로 프로필 찾기 (대소문자 구분 — OCI CLI 와 같음)</summary>
    public static OciProfile? Get(string name, string? path = null) =>
        ReadProfiles(path).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>INI 파싱: [섹션] / key=value (= 이 없으면 :), # · ; 주석 줄 무시. 섹션 순서 유지</summary>
    internal static List<KeyValuePair<string, Dictionary<string, string>>> ParseIniOrdered(IEnumerable<string> lines)
    {
        var list = new List<KeyValuePair<string, Dictionary<string, string>>>();
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                var existing = list.FindIndex(kv => string.Equals(kv.Key, name, StringComparison.Ordinal));
                if (existing >= 0) current = list[existing].Value;   // 같은 섹션이 다시 나오면 합친다
                else
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    list.Add(new(name, current));
                }
                continue;
            }
            if (current == null) continue;   // 섹션 밖의 줄은 무시
            var idx = line.IndexOf('=');
            if (idx < 0) idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Length > 0) current[key] = value;
        }
        return list;
    }

    /// <summary>~ → 사용자 폴더, 환경 변수 펼침, 상대 경로는 설정 파일 폴더 기준. 빈 값은 ""</summary>
    internal static string ExpandPath(string? value, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var p = value.Trim();
        if (p.Length >= 2 && (p[0] == '"' && p[^1] == '"' || p[0] == '\'' && p[^1] == '\'')) p = p[1..^1];
        if (p == "~") p = Home;
        else if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Path.Combine(Home, p[2..].TrimStart('/', '\\'));
        p = Environment.ExpandEnvironmentVariables(p);
        if (!Path.IsPathRooted(p)) p = Path.Combine(baseDir, p);
        try { return Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }
}

/// <summary>
/// OCI 요청 서명기 (API 키 또는 세션 토큰). 키는 Create 때 한 번 읽고, Sign 마다 서명 헤더를 붙인다.
/// 키·토큰 값은 예외 메시지·로그에 절대 넣지 않는다.
/// </summary>
public sealed class OciSigner : IDisposable
{
    /// <summary>만료 판정 여유 (시계 오차·요청 소요 시간)</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    private const string HeadersNoBody = "date (request-target) host";
    private const string HeadersWithBody = "date (request-target) host content-length content-type x-content-sha256";

    private readonly RSA _rsa;
    private readonly object _lock = new();

    /// <summary>Authorization 의 keyId (세션이면 ST$토큰 — 비밀값이므로 외부에 노출하지 않는다)</summary>
    internal string KeyId { get; }

    public bool IsSession { get; }

    /// <summary>세션 토큰(JWT)의 exp. API 키면 null</summary>
    public DateTimeOffset? TokenExpiresUtc { get; }

    private OciSigner(RSA rsa, string keyId, bool isSession, DateTimeOffset? expires)
    {
        _rsa = rsa;
        KeyId = keyId;
        IsSession = isSession;
        TokenExpiresUtc = expires;
    }

    /// <summary>프로필로 서명기 만들기. 파일이 없거나 읽을 수 없거나 세션이 만료됐으면 InvalidOperationException</summary>
    public static OciSigner Create(OciProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (string.IsNullOrWhiteSpace(p.KeyFile))
            throw new InvalidOperationException(L.F("OCI 프로필 {0} 에 {1} 값이 없습니다.", p.Name, "key_file"));

        string keyId;
        DateTimeOffset? expires = null;
        if (p.IsSession)
        {
            var tokenFile = p.SecurityTokenFile!;
            if (!File.Exists(tokenFile))
                throw new InvalidOperationException(L.F("OCI 세션 토큰 파일이 없습니다: {0}", tokenFile));
            string token;
            try { token = File.ReadAllText(tokenFile).Trim(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(L.F("OCI 세션 토큰 파일을 읽을 수 없습니다: {0}", tokenFile), ex);
            }
            if (token.Length == 0)
                throw new InvalidOperationException(L.F("OCI 세션 토큰 파일이 비어 있습니다: {0}", tokenFile));
            expires = ReadTokenExpiry(token);
            if (IsExpired(expires, DateTimeOffset.UtcNow))
                throw new InvalidOperationException(L.F("OCI 세션이 만료되었습니다 — 로그인 버튼(oci session authenticate --profile-name {0})으로 다시 로그인하세요.", p.Name));
            keyId = "ST$" + token;
        }
        else
        {
            foreach (var (key, value) in new[] { ("tenancy", p.Tenancy), ("user", p.User), ("fingerprint", p.Fingerprint) })
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException(L.F("OCI 프로필 {0} 에 {1} 값이 없습니다.", p.Name, key));
            keyId = $"{p.Tenancy.Trim()}/{p.User!.Trim()}/{p.Fingerprint!.Trim()}";
        }

        var rsa = LoadKey(p.KeyFile, p.PassPhrase);
        return new OciSigner(rsa, keyId, p.IsSession, expires);
    }

    /// <summary>PEM(PKCS#1 / PKCS#8 / 암호화 PKCS#8) RSA 개인 키 읽기</summary>
    private static RSA LoadKey(string keyFile, string? passPhrase)
    {
        if (!File.Exists(keyFile))
            throw new InvalidOperationException(L.F("OCI 키 파일이 없습니다: {0}", keyFile));
        string pem;
        try { pem = File.ReadAllText(keyFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(L.F("OCI 키 파일을 읽을 수 없습니다 ({0}) — PEM 형식과 pass_phrase 를 확인하세요.", keyFile), ex);
        }

        // 구형 OpenSSL 암호화 PKCS#1 (Proc-Type: 4,ENCRYPTED) 은 .NET 이 읽지 못한다
        if (pem.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.F("OCI 키 파일이 지원하지 않는 암호화 형식입니다 ({0}) — openssl pkcs8 -topk8 으로 PKCS#8 형식으로 변환하세요.", keyFile));

        var encrypted = pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);
        if (encrypted && string.IsNullOrEmpty(passPhrase))
            throw new InvalidOperationException(L.F("OCI 키 파일이 암호화되어 있는데 pass_phrase 가 없습니다 ({0}).", keyFile));

        var rsa = RSA.Create();
        try
        {
            if (encrypted) rsa.ImportFromEncryptedPem(pem.AsSpan(), passPhrase!.AsSpan());
            else rsa.ImportFromPem(pem);
            return rsa;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            // 내부 예외 메시지에는 키 값이 들어가지 않지만, 혹시 몰라 원인 예외는 붙이지 않는다
            throw new InvalidOperationException(L.F("OCI 키 파일을 읽을 수 없습니다 ({0}) — PEM 형식과 pass_phrase 를 확인하세요.", keyFile));
        }
    }

    /// <summary>JWT payload 의 exp(초) → UTC 시각. 형식이 다르거나 exp 가 없으면 null</summary>
    public static DateTimeOffset? ReadTokenExpiry(string jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        var parts = jwt.Trim().Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(payload));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("exp", out var exp)) return null;
            long seconds;
            if (exp.ValueKind == JsonValueKind.Number)
            {
                if (!exp.TryGetInt64(out seconds))
                {
                    if (!exp.TryGetDouble(out var d)) return null;
                    seconds = (long)d;
                }
            }
            else if (exp.ValueKind == JsonValueKind.String && long.TryParse(exp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) seconds = s;
            else return null;
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4)
        {
            case 2: b += "=="; break;
            case 3: b += "="; break;
            case 1: throw new FormatException("invalid base64url");
        }
        return Convert.FromBase64String(b);
    }

    /// <summary>만료됐는지 (만료 1분 전부터 만료로 본다). exp 를 모르면 false</summary>
    public static bool IsExpired(DateTimeOffset? exp, DateTimeOffset now) => exp is { } e && e <= now + ExpirySkew;

    /// <summary>본문이 서명 대상인 메서드 (OCI: POST/PUT/PATCH)</summary>
    internal static bool HasBody(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch;

    /// <summary>headers="…" 에 들어갈 서명 헤더 목록</summary>
    public static string SignedHeaders(bool withBody) => withBody ? HeadersWithBody : HeadersNoBody;

    /// <summary>
    /// 서명 문자열 (draft-cavage-08). 줄은 \n 으로 잇고 마지막 줄바꿈은 없다.
    /// body 가 있으면 content-length / content-type / x-content-sha256 줄이 붙는다.
    /// </summary>
    public static string BuildSigningString(string method, string pathAndQuery, string host, string date,
        (long ContentLength, string ContentType, string ContentSha256)? body = null)
    {
        var sb = new StringBuilder();
        sb.Append("date: ").Append(date).Append('\n');
        sb.Append("(request-target): ").Append(method.ToLowerInvariant()).Append(' ').Append(pathAndQuery).Append('\n');
        sb.Append("host: ").Append(host);
        if (body is { } b)
        {
            sb.Append('\n').Append("content-length: ").Append(b.ContentLength.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n').Append("content-type: ").Append(b.ContentType);
            sb.Append('\n').Append("x-content-sha256: ").Append(b.ContentSha256);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 요청에 date · host · (POST/PUT/PATCH 면 x-content-sha256 · content-type · content-length) · Authorization 을 붙인다.
    /// POST/PUT/PATCH 는 req.Content 를 body 바이트로 교체한다 (Content-Type 은 기존 값, 없으면 application/json).
    /// req.RequestUri 는 절대 URI 여야 한다.
    /// </summary>
    public void Sign(HttpRequestMessage req, byte[]? body)
    {
        ArgumentNullException.ThrowIfNull(req);
        var uri = req.RequestUri;
        if (uri == null || !uri.IsAbsoluteUri) throw new ArgumentException("RequestUri must be an absolute URI.", nameof(req));

        var host = uri.IsDefaultPort ? uri.Host : uri.Authority;
        var pathAndQuery = uri.PathAndQuery;   // 실제로 보내는 이스케이프된 형태
        var date = DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture);

        req.Headers.Remove("date");
        req.Headers.TryAddWithoutValidation("date", date);
        req.Headers.Host = host;

        (long, string, string)? bodyHeaders = null;
        var withBody = HasBody(req.Method);
        if (withBody)
        {
            body ??= Array.Empty<byte>();
            var contentType = req.Content?.Headers.ContentType ?? new MediaTypeHeaderValue("application/json");
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = contentType;
            content.Headers.ContentLength = body.Length;
            req.Content = content;

            var sha = Convert.ToBase64String(SHA256.HashData(body));
            req.Headers.Remove("x-content-sha256");
            req.Headers.TryAddWithoutValidation("x-content-sha256", sha);
            bodyHeaders = (body.Length, contentType.ToString(), sha);
        }

        var signingString = BuildSigningString(req.Method.Method, pathAndQuery, host, date, bodyHeaders);
        byte[] sig;
        lock (_lock) sig = _rsa.SignData(Encoding.UTF8.GetBytes(signingString), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var auth = $"Signature version=\"1\",keyId=\"{KeyId}\",algorithm=\"rsa-sha256\",headers=\"{SignedHeaders(withBody)}\",signature=\"{Convert.ToBase64String(sig)}\"";
        req.Headers.Remove("Authorization");
        req.Headers.TryAddWithoutValidation("Authorization", auth);
    }

    public void Dispose() => _rsa.Dispose();
}
