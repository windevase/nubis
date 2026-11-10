using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Nubis.Tests;

/// <summary>
/// 테스트용 SSH 개인키를 실행할 때마다 새로 만든다 (저장소에 키를 넣지 않는다).
/// RSA 는 .NET 으로 만들고, 기대하는 공개키("ssh-rsa AAAA...")도 직접 계산한다.
/// Ed25519 는 ssh-keygen 이 있으면 임시 폴더에 만들고 바로 지운다.
/// </summary>
internal static class TestKeys
{
    public const string Passphrase = "test-passphrase";

    private static readonly RSA Key = RSA.Create(2048);

    /// <summary>암호 없는 개인키 (PKCS#1 PEM)</summary>
    public static readonly string Rsa = Key.ExportRSAPrivateKeyPem();

    /// <summary>암호가 걸린 개인키 (PKCS#8 PEM, AES-256)</summary>
    public static readonly string RsaEncrypted = Key.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000));

    /// <summary>ssh-keygen 이 만드는 것과 같은 공개키 한 줄 (설명 없음)</summary>
    public static readonly string RsaPublic = SshRsaPublic(Key.ExportParameters(false));

    private static string SshRsaPublic(RSAParameters p)
    {
        using var ms = new MemoryStream();
        void Bytes(byte[] b)
        {
            var len = BitConverter.GetBytes(b.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(len);
            ms.Write(len);
            ms.Write(b);
        }
        // mpint: 부호 비트가 켜져 있으면 앞에 0 을 붙인다
        static byte[] MpInt(byte[] v)
        {
            var t = v.SkipWhile(x => x == 0).ToArray();
            return t.Length > 0 && (t[0] & 0x80) != 0 ? new byte[] { 0 }.Concat(t).ToArray() : t;
        }
        Bytes(Encoding.ASCII.GetBytes("ssh-rsa"));
        Bytes(MpInt(p.Exponent!));
        Bytes(MpInt(p.Modulus!));
        return "ssh-rsa " + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    /// ssh-keygen 으로 Ed25519 키를 만들어 (개인키, 공개키 한 줄) 을 돌려준다. ssh-keygen 이 없으면 null.
    /// 파일은 임시 폴더에 만들고 읽은 뒤 바로 지운다.
    /// </summary>
    public static (string Private, string Public)? Ed25519(string passphrase = "")
    {
        var exe = new[] { Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe"), "ssh-keygen" }
            .FirstOrDefault(p => !p.Contains(Path.DirectorySeparatorChar) || File.Exists(p));
        if (exe == null) return null;
        var dir = Path.Combine(Path.GetTempPath(), "Nubis.Tests.keys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "k");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-q", "-t", "ed25519", "-N", passphrase, "-C", "", "-f", file }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null || !proc.WaitForExit(15_000) || proc.ExitCode != 0 || !File.Exists(file)) return null;
            var pub = File.ReadAllText(file + ".pub").Trim().Split(' ');
            return (File.ReadAllText(file), $"{pub[0]} {pub[1]}");
        }
        catch { return null; }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
