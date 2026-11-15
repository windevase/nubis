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
public sealed class VaultEntry : System.ComponentModel.INotifyPropertyChanged
{
    // Vault 화면·선택 창 목록이 이 개체(앱 전체에서 공유하는 VaultStore 목록의 항목)에 바인딩한다.
    // INotifyPropertyChanged 가 없으면 WPF 가 PropertyDescriptor.AddValueChanged 로 듣고 그 등록이 전역 표에 남으므로,
    // 약한 참조 경로를 쓰도록 구현만 한다 (값은 목록을 다시 만들어 반영하므로 알림은 보내지 않는다 — 구독도 저장하지 않음)
    event System.ComponentModel.PropertyChangedEventHandler? System.ComponentModel.INotifyPropertyChanged.PropertyChanged { add { } remove { } }

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
        (true, true) => L.T("비밀번호 + SSH 키"),
        (false, true) => L.T("SSH 키"),
        (true, false) => L.T("비밀번호"),
        _ => L.T("인증 정보 없음"),
    };
    [JsonIgnore]
    public string Detail
    {
        get
        {
            var parts = new List<string> { string.IsNullOrWhiteSpace(Username) ? L.T("사용자명 없음") : L.F("사용자 {0}", Username) };
            if (HasPassword) parts.Add(L.T("비밀번호"));
            if (HasKey) parts.Add(string.IsNullOrEmpty(ProtectedPassphrase)
                ? L.F("{0} 키", KeyType.Length > 0 ? KeyType : "SSH")
                : L.F("{0} 키 (키 암호 저장)", KeyType.Length > 0 ? KeyType : "SSH"));
            if (!string.IsNullOrWhiteSpace(Note)) parts.Add(Note);
            return string.Join(" · ", parts);
        }
    }
    /// <summary>이 항목을 쓰는 호스트 수 (목록 표시 때 채움)</summary>
    [JsonIgnore] public int UsedBy { get; set; }
    [JsonIgnore] public string UsedByText => UsedBy == 0 ? L.T("쓰는 호스트 없음") : L.F("호스트 {0}개", UsedBy);
    /// <summary>설정의 기본 계정이면 "SSH 기본" 등 (목록 표시 때 채움)</summary>
    [JsonIgnore] public string DefaultBadge { get; set; } = "";
    [JsonIgnore] public Visibility DefaultBadgeVisibility => DefaultBadge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility CopyVisibility => HasKey && !string.IsNullOrEmpty(PublicKey) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 수정용 사본 (모든 필드가 문자열·값 형식이라 얕은 복사로 충분). 수정 창은 사본을 고친 뒤 <see cref="VaultStore.Upsert"/> 로
    /// 바꿔 넣는다 — 저장된 개체를 직접 고치면 비대화형 실행 스레드가 반쯤 바뀐 값을 읽거나, 저장 실패 때 되돌릴 수 없다.
    /// </summary>
    public VaultEntry Clone() => (VaultEntry)MemberwiseClone();
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

    /// <summary>vault.json 을 읽지 못해(.bak 복구도 실패) 빈 목록으로 시작했는지</summary>
    public static bool LoadFailed { get; private set; }

    private static List<VaultEntry> Entries
    {
        get
        {
            lock (Gate)
            {
                if (_entries != null) return _entries;
                _entries = SafeFile.LoadProtected(FilePath, s => JsonSerializer.Deserialize<List<VaultEntry>>(s, Options), () => new List<VaultEntry>(), out var failed);
                LoadFailed = failed;
                var migrated = false;
                foreach (var e in _entries) migrated |= e.MigrateLegacy();
                if (migrated) { try { Save(); } catch { } }
                return _entries;
            }
        }
    }

    /// <summary>
    /// 미리 읽어 둔다 (앱 시작 때 데이터 폴더를 정한 뒤). 처음 쓸 때 읽으면 시작 경고 창이 이미 지나가
    /// 읽기 실패 경고가 보이지 않는다.
    /// </summary>
    public static void Preload()
    {
        lock (Gate) _ = Entries;
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

    /// <summary>
    /// 추가하거나, 같은 Id 의 항목을 이 개체로 바꾼다 (수정 창의 사본). 저장에 실패하면 메모리 목록도 되돌리고 예외를 다시 던진다
    /// (같은 개체를 직접 고친 경우는 필드까지 되돌리지는 못한다 — 사본을 고쳐 넘기는 것이 원칙).
    /// </summary>
    public static void Upsert(VaultEntry entry)
    {
        lock (Gate)
        {
            var list = Entries;
            var index = list.FindIndex(e => e.Id == entry.Id);
            var old = index >= 0 ? list[index] : null;
            var oldUpdated = entry.Updated;
            entry.Updated = DateTime.Now;
            if (index >= 0) list[index] = entry;
            else list.Add(entry);
            try { Save(); }
            catch
            {
                if (index >= 0) list[index] = old!;
                else list.Remove(entry);
                entry.Updated = oldUpdated;
                throw;
            }
        }
    }

    /// <summary>지운다. 저장에 실패하면 메모리 목록도 되돌리고 예외를 다시 던진다</summary>
    public static void Remove(Guid id)
    {
        lock (Gate)
        {
            var list = Entries;
            var removed = list.Select((e, i) => (e, i)).Where(x => x.e.Id == id).ToList();
            if (removed.Count == 0) return;
            list.RemoveAll(e => e.Id == id);
            try { Save(); }
            catch
            {
                foreach (var (e, i) in removed) list.Insert(Math.Min(i, list.Count), e);
                throw;
            }
        }
    }

    /// <summary>같은 이름(대소문자 무시)의 다른 계정이 있는지</summary>
    public static bool NameTaken(string name, Guid exceptId)
    {
        lock (Gate)
            return Entries.Any(e => e.Id != exceptId && string.Equals(e.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    private static void Save()
    {
        // 읽지 못한 vault.json 의 원본 사본도 만들지 못했으면 덮어쓰지 않는다 (새 계정 하나짜리 목록으로 계정들이 사라지지 않게)
        // (사본을 다시 만들어 보고, 파일이 없어졌으면 저장한다 — 잠깐의 잠금으로 이번 실행 내내 저장이 막히지 않게)
        if (!SafeFile.EnsureOriginalPreserved(FilePath))
            throw new IOException(L.T("vault.json 을 읽지 못했고 원본 사본도 만들지 못해, 덮어쓰지 않도록 저장하지 않았습니다. 파일을 복구하거나 다른 곳으로 옮긴 뒤 Nubis 를 다시 시작하세요."));
        SafeFile.WriteProtected(FilePath, JsonSerializer.Serialize(Entries, Options));
        LoadFailed = false;   // 저장했으면 지금 목록이 원본이다 (읽지 못한 원본은 .corrupt 사본에 있다)
    }

    /// <summary>
    /// 접속용 사본에 Vault 계정 값을 채운다 (사본을 직접 바꾼다). 호스트에 직접 적은 값이 우선한다:
    /// - 사용자명: 호스트에 없으면 계정의 사용자명
    /// - 비밀번호: 호스트에 없으면 계정의 비밀번호
    /// - SSH 키: 호스트에 키 파일이 없으면 계정의 키(+키 암호). RDP 는 키를 쓰지 않는다
    /// 항목이 지워졌으면 무시한다.
    /// </summary>
    /// <param name="preferVault">
    /// true = 호스트에 직접 고른 Vault 계정: 그 계정이 우선이라 호스트에 따로 적힌 사용자명·비밀번호·키는 쓰지 않는다
    /// (사용자명은 Vault 에 없을 때만 호스트 값). false = 기본 계정(설정·프로필): 호스트에 비어 있는 값만 채운다.
    /// </param>
    public static HostEntry Apply(HostEntry copy, Guid? id = null, bool preferVault = false)
    {
        if (Get(id ?? copy.VaultId) is not { } v) return copy;
        if (preferVault)
        {
            if (!string.IsNullOrWhiteSpace(v.Username)) copy.Username = v.Username;
            copy.ProtectedPassword = v.ProtectedPassword;
            copy.KeyPath = null;
            var useKey = copy.Protocol == Protocol.Ssh && v.HasKey;
            copy.KeyMaterial = useKey ? v.Key : null;
            copy.KeyPassphrase = useKey ? v.Passphrase : null;
            return copy;
        }
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

    /// <summary>
    /// 암호가 걸리지 않은 키면 공개키 한 줄, 암호가 걸렸거나 읽을 수 없으면 null.
    /// 키 암호를 넣었지만 키에 암호가 없는 경우를 가려 쓰이지 않는 키 암호를 저장하지 않는 데 쓴다.
    /// </summary>
    public static string? TryReadUnencryptedPublicKey(string privateKey)
    {
        try { return ReadPublicKey(privateKey, null); }
        catch { return null; }
    }

    /// <summary>SSH 공개키 blob 의 첫 필드(키 형식 이름: uint32 길이 + 문자열)</summary>
    public static string BlobType(byte[] blob)
    {
        if (blob.Length < 4) return "ssh-key";
        var len = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
        return len > 0 && len <= blob.Length - 4 && len < 64 ? Encoding.ASCII.GetString(blob, 4, len) : "ssh-key";
    }
}
