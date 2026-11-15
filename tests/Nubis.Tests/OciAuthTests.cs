using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>OCI 설정 파일 읽기 · HTTP Signature 서명</summary>
public class OciAuthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nubis-oci-" + Guid.NewGuid().ToString("N"));

    public OciAuthTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Jwt(long exp)
    {
        static string B64Url(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}") + "." + B64Url("{\"sub\":\"ocid1.user.oc1..x\",\"exp\":" + exp + "}") + ".c2ln";
    }

    // ---------------- 설정 파일 ----------------

    [Fact]
    public void 설정_파일이_없으면_빈_목록()
    {
        Assert.Empty(OciConfig.ReadProfiles(Path.Combine(_dir, "nope", "config")));
        Assert.Null(OciConfig.Get("DEFAULT", Path.Combine(_dir, "nope", "config")));
    }

    [Fact]
    public void DEFAULT_값을_상속하고_프로필_값이_우선한다()
    {
        var cfg = WriteFile("config", """
            # 주석
            ; 다른 주석
            [DEFAULT]
            tenancy=ocid1.tenancy.oc1..ten
            region=ap-seoul-1
            user=ocid1.user.oc1..u1
            fingerprint=aa:bb:cc
            key_file=keys/default.pem

            [PROD]
            region = ap-chuncheon-1
            user=ocid1.user.oc1..u2
            key_file=C:\oci\prod.pem
            pass_phrase=secret

            [SESSION]
            security_token_file=~/.oci/sessions/SESSION/token
            key_file=~/.oci/sessions/SESSION/oci_api_key.pem
            """);

        var all = OciConfig.ReadProfiles(cfg);
        Assert.Equal(new[] { "DEFAULT", "PROD", "SESSION" }, all.Select(p => p.Name).ToArray());

        var def = all[0];
        Assert.Equal("ocid1.tenancy.oc1..ten", def.Tenancy);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "keys", "default.pem")), def.KeyFile);   // 상대 경로 → 설정 파일 폴더 기준
        Assert.False(def.IsSession);

        var prod = OciConfig.Get("PROD", cfg)!;
        Assert.Equal("ocid1.tenancy.oc1..ten", prod.Tenancy);   // 상속
        Assert.Equal("ap-chuncheon-1", prod.Region);             // 덮어씀
        Assert.Equal("ocid1.user.oc1..u2", prod.User);
        Assert.Equal("aa:bb:cc", prod.Fingerprint);
        Assert.Equal(@"C:\oci\prod.pem", prod.KeyFile);
        Assert.Equal("secret", prod.PassPhrase);
        Assert.Null(prod.SecurityTokenFile);

        var ses = OciConfig.Get("SESSION", cfg)!;
        Assert.True(ses.IsSession);
        Assert.Equal(Path.GetFullPath(Path.Combine(Home, ".oci", "sessions", "SESSION", "token")), ses.SecurityTokenFile);
        Assert.Equal(Path.GetFullPath(Path.Combine(Home, ".oci", "sessions", "SESSION", "oci_api_key.pem")), ses.KeyFile);

        Assert.Null(OciConfig.Get("prod", cfg));   // 프로필 이름은 대소문자 구분
    }

    [Fact]
    public void DEFAULT_없이도_읽힌다()
    {
        var cfg = WriteFile("config2", "[only]\ntenancy=t\nregion=r\nkey_file=~\\k.pem\n");
        var p = Assert.Single(OciConfig.ReadProfiles(cfg));
        Assert.Equal("only", p.Name);
        Assert.Equal(Path.GetFullPath(Path.Combine(Home, "k.pem")), p.KeyFile);
        Assert.Null(p.User);
        Assert.Null(p.SecurityTokenFile);
    }

    // ---------------- 서명 문자열 ----------------

    [Fact]
    public void GET_서명_문자열_형식()
    {
        var s = OciSigner.BuildSigningString("GET", "/20160918/instances?compartmentId=ocid1.compartment.oc1..aaa&limit=100",
            "iaas.ap-seoul-1.oraclecloud.com", "Thu, 05 Jan 2014 21:31:40 GMT");
        Assert.Equal(
            "date: Thu, 05 Jan 2014 21:31:40 GMT\n" +
            "(request-target): get /20160918/instances?compartmentId=ocid1.compartment.oc1..aaa&limit=100\n" +
            "host: iaas.ap-seoul-1.oraclecloud.com", s);
        Assert.Equal("date (request-target) host", OciSigner.SignedHeaders(false));
    }

    [Fact]
    public void POST_서명_문자열_형식()
    {
        var s = OciSigner.BuildSigningString("POST", "/20160918/instances", "iaas.ap-seoul-1.oraclecloud.com",
            "Thu, 05 Jan 2014 21:31:40 GMT", (316, "application/json", "V9Z20UJTvkvpJ50flBzKE32+6m2zJjweHpDMX/U4Uy0="));
        Assert.Equal(
            "date: Thu, 05 Jan 2014 21:31:40 GMT\n" +
            "(request-target): post /20160918/instances\n" +
            "host: iaas.ap-seoul-1.oraclecloud.com\n" +
            "content-length: 316\n" +
            "content-type: application/json\n" +
            "x-content-sha256: V9Z20UJTvkvpJ50flBzKE32+6m2zJjweHpDMX/U4Uy0=", s);
        Assert.Equal("date (request-target) host content-length content-type x-content-sha256", OciSigner.SignedHeaders(true));
    }

    // ---------------- 실제 서명 · 검증 ----------------

    private sealed record ParsedAuth(string KeyId, string Algorithm, string Headers, byte[] Signature);

    private static ParsedAuth ParseAuth(HttpRequestMessage req)
    {
        var auth = req.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("Signature version=\"1\",", auth);
        string Part(string name) => Regex.Match(auth, name + "=\"([^\"]*)\"").Groups[1].Value;
        return new ParsedAuth(Part("keyId"), Part("algorithm"), Part("headers"), Convert.FromBase64String(Part("signature")));
    }

    /// <summary>요청에 실린 헤더 값으로 서명 문자열을 다시 만들어 공개 키로 검증</summary>
    private static void AssertVerifies(HttpRequestMessage req, RSA publicKey)
    {
        var a = ParseAuth(req);
        Assert.Equal("rsa-sha256", a.Algorithm);
        var lines = a.Headers.Split(' ').Select(h => h switch
        {
            "(request-target)" => "(request-target): " + req.Method.Method.ToLowerInvariant() + " " + req.RequestUri!.PathAndQuery,
            "host" => "host: " + req.Headers.Host,
            "date" => "date: " + req.Headers.GetValues("date").Single(),
            "x-content-sha256" => "x-content-sha256: " + req.Headers.GetValues("x-content-sha256").Single(),
            "content-length" => "content-length: " + req.Content!.Headers.ContentLength,
            "content-type" => "content-type: " + req.Content!.Headers.ContentType,
            _ => throw new InvalidOperationException(h),
        });
        var signingString = string.Join("\n", lines);
        Assert.True(publicKey.VerifyData(Encoding.UTF8.GetBytes(signingString), a.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void API_키_GET_서명이_공개_키로_검증된다()
    {
        using var rsa = RSA.Create(2048);
        var key = WriteFile("pkcs1.pem", rsa.ExportRSAPrivateKeyPem());
        var p = new OciProfile("DEFAULT", "ocid1.tenancy.oc1..t", "ap-seoul-1", "ocid1.user.oc1..u", "11:22:33", key, null, null);

        using var signer = OciSigner.Create(p);
        Assert.False(signer.IsSession);
        Assert.Null(signer.TokenExpiresUtc);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://iaas.ap-seoul-1.oraclecloud.com/20160918/instances?compartmentId=ocid1.compartment.oc1..a%3Ab&limit=100");
        signer.Sign(req, null);

        Assert.Equal("iaas.ap-seoul-1.oraclecloud.com", req.Headers.Host);
        var date = req.Headers.GetValues("date").Single();
        Assert.EndsWith(" GMT", date);
        Assert.False(req.Headers.Contains("x-content-sha256"));

        var a = ParseAuth(req);
        Assert.Equal("ocid1.tenancy.oc1..t/ocid1.user.oc1..u/11:22:33", a.KeyId);
        Assert.Equal("date (request-target) host", a.Headers);
        AssertVerifies(req, rsa);
    }

    [Fact]
    public void API_키_POST_서명에_본문_해시가_들어가고_검증된다()
    {
        using var rsa = RSA.Create(2048);
        var key = WriteFile("pkcs8.pem", rsa.ExportPkcs8PrivateKeyPem());
        var p = new OciProfile("DEFAULT", "t", "ap-seoul-1", "u", "f", key, null, null);
        using var signer = OciSigner.Create(p);

        var body = Encoding.UTF8.GetBytes("{\"action\":\"START\"}");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://iaas.ap-seoul-1.oraclecloud.com/20160918/instances/ocid1.instance.oc1..i");
        signer.Sign(req, body);

        Assert.Equal(Convert.ToBase64String(SHA256.HashData(body)), req.Headers.GetValues("x-content-sha256").Single());
        Assert.Equal(body.Length, req.Content!.Headers.ContentLength);
        Assert.Equal("application/json", req.Content.Headers.ContentType!.ToString());
        Assert.Equal("date (request-target) host content-length content-type x-content-sha256", ParseAuth(req).Headers);
        AssertVerifies(req, rsa);
    }

    [Fact]
    public void POST_본문이_없으면_빈_본문_해시()
    {
        using var rsa = RSA.Create(2048);
        var p = new OciProfile("DEFAULT", "t", "r", "u", "f", WriteFile("k.pem", rsa.ExportRSAPrivateKeyPem()), null, null);
        using var signer = OciSigner.Create(p);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.oraclecloud.com/x");
        signer.Sign(req, null);
        Assert.Equal("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=", req.Headers.GetValues("x-content-sha256").Single());
        Assert.Equal(0, req.Content!.Headers.ContentLength);
        AssertVerifies(req, rsa);
    }

    [Fact]
    public void 암호화된_PKCS8_키는_pass_phrase_로_읽는다()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportEncryptedPkcs8PrivateKeyPem("pw!",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10000));
        var key = WriteFile("enc.pem", pem);

        using (var signer = OciSigner.Create(new OciProfile("A", "t", "r", "u", "f", key, "pw!", null)))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.oraclecloud.com/a");
            signer.Sign(req, null);
            AssertVerifies(req, rsa);
        }

        Assert.Throws<InvalidOperationException>(() => OciSigner.Create(new OciProfile("A", "t", "r", "u", "f", key, null, null)));
        var wrong = Assert.Throws<InvalidOperationException>(() => OciSigner.Create(new OciProfile("A", "t", "r", "u", "f", key, "nope", null)));
        Assert.DoesNotContain("pw!", wrong.Message);
    }

    [Fact]
    public void 키_파일이_없거나_값이_빠지면_InvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OciSigner.Create(new OciProfile("A", "t", "r", "u", "f", Path.Combine(_dir, "missing.pem"), null, null)));

        using var rsa = RSA.Create(2048);
        var key = WriteFile("k2.pem", rsa.ExportRSAPrivateKeyPem());
        Assert.Throws<InvalidOperationException>(() => OciSigner.Create(new OciProfile("A", "t", "r", null, "f", key, null, null)));
        Assert.Throws<InvalidOperationException>(() => OciSigner.Create(new OciProfile("A", "t", "r", "u", "f", WriteFile("bad.pem", "not a key"), null, null)));
    }

    // ---------------- 세션 토큰 ----------------

    [Fact]
    public void 세션_토큰_keyId_는_ST_접두어와_토큰()
    {
        using var rsa = RSA.Create(2048);
        var key = WriteFile("session.pem", rsa.ExportPkcs8PrivateKeyPem());
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = Jwt(exp);
        var tokenFile = WriteFile("token", token + "\r\n");
        var p = new OciProfile("SES", "t", "r", null, null, key, null, tokenFile);

        using var signer = OciSigner.Create(p);
        Assert.True(signer.IsSession);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp), signer.TokenExpiresUtc);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.oraclecloud.com/a?b=c");
        signer.Sign(req, null);
        Assert.Equal("ST$" + token, ParseAuth(req).KeyId);
        AssertVerifies(req, rsa);
    }

    [Fact]
    public void 만료된_세션은_Create_에서_거부된다()
    {
        using var rsa = RSA.Create(2048);
        var key = WriteFile("s2.pem", rsa.ExportPkcs8PrivateKeyPem());
        var token = Jwt(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        var tokenFile = WriteFile("token2", token);
        var ex = Assert.Throws<InvalidOperationException>(() => OciSigner.Create(new OciProfile("MYSES", "t", "r", null, null, key, null, tokenFile)));
        Assert.Contains("MYSES", ex.Message);
        Assert.DoesNotContain(token, ex.Message);

        Assert.Throws<InvalidOperationException>(() =>
            OciSigner.Create(new OciProfile("MYSES", "t", "r", null, null, key, null, Path.Combine(_dir, "no-token"))));
    }

    [Fact]
    public void JWT_exp_파싱()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), OciSigner.ReadTokenExpiry(Jwt(1700000000)));
        Assert.Null(OciSigner.ReadTokenExpiry("not-a-jwt"));
        Assert.Null(OciSigner.ReadTokenExpiry("a.!!!.c"));
        Assert.Null(OciSigner.ReadTokenExpiry(""));
    }

    [Fact]
    public void IsExpired_는_1분_여유를_둔다()
    {
        var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.False(OciSigner.IsExpired(null, now));
        Assert.True(OciSigner.IsExpired(now.AddMinutes(-5), now));
        Assert.True(OciSigner.IsExpired(now.AddSeconds(30), now));
        Assert.False(OciSigner.IsExpired(now.AddMinutes(10), now));
    }
}
