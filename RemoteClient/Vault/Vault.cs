using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Renci.SshNet;

namespace Nubis;

/// <summary>예전(분리형) Vault 항목 종류. 새 항목은 쓰지 않고, 예전 파일을 읽을 때 변환에만 쓴다</summary>
public enum VaultKind { Password, SshKey }

/// <summary>
/// Vault 계정: 여러 호스트가 함께 쓰는 로그인 정보 (사용자명 + 비밀번호 및/또는 SSH 개인키).
/// 비밀 값(비밀번호·키 내용·키 암호)은 DPAPI(CurrentUser, <see cref="Nubis.Secret"/>)로 암호화해 저장한다.
/// </summary>
public sealed class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>로그인 사용자명 (호스트에 사용자명을 따로 적지 않으면 이 값)</summary>
    public string Username { get; set; } = "";
    /// <summary>비밀번호 — DPAPI 암호문</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>SSH 개인키 파일 내용 — DPAPI 암호문</summary>
    public string? ProtectedKey { get; set; }
    /// <summary>SSH 키 암호 — DPAPI 암호문</summary>
    public string? ProtectedPassphrase { get; set; }
    /// <summary>공개키 한 줄 ("ssh-ed25519 AAAA...", 비밀 아님 — 표시·authorized_keys 복사용)</summary>
    public string? PublicKey { get; set; }
    public string Note { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;

    // ----- 예전 형식 (암호/키 분리) 읽기 전용: 불러올 때 위 필드로 옮기고 저장하지 않는다 -----
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public VaultKind? Kind { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProtectedSecret { get; set; }

    /// <summary>예전 형식이면 새 필드로 옮긴다. 바뀌었으면 true</summary>
    public bool MigrateLegacy()
    {
        if (Kind == null && ProtectedSecret == null) return false;
        if (ProtectedSecret != null)
        {
            if (Kind == VaultKind.SshKey) ProtectedPassphrase ??= ProtectedSecret;
            else ProtectedPassword ??= ProtectedSecret;
        }
        Kind = null;
        ProtectedSecret = null;
        return true;
    }

    [JsonIgnore]
    public string Password
    {
        get => Secret.Unprotect(ProtectedPassword);
        set => ProtectedPassword = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    [JsonIgnore]
    public string Key
    {
        get => Secret.Unprotect(ProtectedKey);
        set => ProtectedKey = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    [JsonIgnore]
    public string Passphrase
    {
        get => Secret.Unprotect(ProtectedPassphrase);
        set => ProtectedPassphrase = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(ProtectedPassword);
    [JsonIgnore] public bool HasKey => !string.IsNullOrEmpty(ProtectedKey);

    // ----- 화면 표시용 -----
    /// <summary>키가 있으면 열쇠, 비밀번호만 있으면 자물쇠</summary>
    [JsonIgnore] public string Glyph => HasKey ? "" : "";
    [JsonIgnore] public string KeyType => PublicKey?.Split(' ')[0] ?? "";
    /// <summary>"비밀번호 + SSH 키" 처럼 이 계정으로 로그인하는 수단</summary>
    [JsonIgnore]
    public string MethodsText => (HasPassword, HasKey) switch
    {
        (true, true) => "비밀번호 + SSH 키",
        (false, true) => "SSH 키",
        (true, false) => "비밀번호",
        _ => "인증 정보 없음",
    };
    [JsonIgnore]
    public string Detail
    {
        get
        {
            var parts = new List<string> { string.IsNullOrWhiteSpace(Username) ? "사용자명 없음" : $"사용자 {Username}" };
            if (HasPassword) parts.Add("비밀번호");
            if (HasKey) parts.Add((KeyType.Length > 0 ? KeyType : "SSH") + " 키" + (string.IsNullOrEmpty(ProtectedPassphrase) ? "" : " (키 암호 저장)"));
            if (!string.IsNullOrWhiteSpace(Note)) parts.Add(Note);
            return string.Join(" · ", parts);
        }
    }
    /// <summary>이 항목을 쓰는 호스트 수 (목록 표시 때 채움)</summary>
    [JsonIgnore] public int UsedBy { get; set; }
    [JsonIgnore] public string UsedByText => UsedBy == 0 ? "사용 안 함" : $"호스트 {UsedBy}개";
    [JsonIgnore] public Visibility CopyVisibility => HasKey && !string.IsNullOrEmpty(PublicKey) ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>vault.json 저장소. 앱 전체에서 하나의 목록을 공유한다 (비대화형 실행 스레드에서도 읽으므로 잠금)</summary>
public static class VaultStore
{
    private static readonly object Gate = new();
    private static List<VaultEntry>? _entries;

    public const string FileName = "vault.json";
    public static string FilePath => Path.Combine(AppPaths.DataDir, FileName);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static List<VaultEntry> Entries
    {
        get
        {
            lock (Gate)
            {
                if (_entries != null) return _entries;
                _entries = SafeFile.LoadProtected(FilePath, s => JsonSerializer.Deserialize<List<VaultEntry>>(s, Options), () => new List<VaultEntry>());
                var migrated = false;
                foreach (var e in _entries) migrated |= e.MigrateLegacy();
                if (migrated) { try { Save(); } catch { } }
                return _entries;
            }
        }
    }

    public static List<VaultEntry> All()
    {
        lock (Gate) return Entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static VaultEntry? Get(Guid? id)
    {
        if (id == null) return null;
        lock (Gate) return Entries.FirstOrDefault(e => e.Id == id);
    }

    public static void Upsert(VaultEntry entry)
    {
        lock (Gate)
        {
            entry.Updated = DateTime.Now;
            if (!Entries.Contains(entry)) Entries.Add(entry);
            Save();
        }
    }

    public static void Remove(Guid id)
    {
        lock (Gate)
        {
            Entries.RemoveAll(e => e.Id == id);
            Save();
        }
    }

    private static void Save() => SafeFile.WriteProtected(FilePath, JsonSerializer.Serialize(Entries, Options));

    /// <summary>
    /// 접속용 사본에 Vault 계정 값을 채운다 (사본을 직접 바꾼다). 호스트에 직접 적은 값이 우선한다:
    /// - 사용자명: 호스트에 없으면 계정의 사용자명
    /// - 비밀번호: 호스트에 없으면 계정의 비밀번호
    /// - SSH 키: 호스트에 키 파일이 없으면 계정의 키(+키 암호). RDP 는 키를 쓰지 않는다
    /// 항목이 지워졌으면 무시한다.
    /// </summary>
    public static HostEntry Apply(HostEntry copy)
    {
        if (Get(copy.VaultId) is not { } v) return copy;
        if (string.IsNullOrWhiteSpace(copy.Username)) copy.Username = v.Username;
        if (string.IsNullOrEmpty(copy.ProtectedPassword)) copy.ProtectedPassword = v.ProtectedPassword;
        if (copy.Protocol == Protocol.Ssh && string.IsNullOrWhiteSpace(copy.KeyPath) && v.HasKey)
        {
            copy.KeyMaterial = v.Key;
            copy.KeyPassphrase = v.Passphrase;
        }
        return copy;
    }

    /// <summary>
    /// 개인키 내용을 검사하고 공개키 한 줄을 만든다. 키에 암호가 걸려 있는데 passphrase 가 없거나 틀리면 예외.
    /// </summary>
    public static string ReadPublicKey(string privateKey, string? passphrase)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
        using var pk = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, passphrase);
        var blob = pk.HostKeyAlgorithms.First().Data;
        return $"{BlobType(blob)} {Convert.ToBase64String(blob)}";
    }

    /// <summary>SSH 공개키 blob 의 첫 필드(키 형식 이름: uint32 길이 + 문자열)</summary>
    public static string BlobType(byte[] blob)
    {
        if (blob.Length < 4) return "ssh-key";
        var len = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
        return len > 0 && len <= blob.Length - 4 && len < 64 ? Encoding.ASCII.GetString(blob, 4, len) : "ssh-key";
    }
}
