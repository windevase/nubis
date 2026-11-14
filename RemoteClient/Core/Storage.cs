using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nubis;

public static class AppPaths
{
    public const string AppName = "Nubis";
    private const string LegacyName = "RemoteClient";   // 이름 변경 전 데이터 폴더

    /// <summary>NUBIS_DATA_DIR 환경변수로 데이터 폴더를 바꿀 수 있다 (포터블 실행·테스트용)</summary>
    private static readonly string? OverrideDir = Environment.GetEnvironmentVariable("NUBIS_DATA_DIR") is { Length: > 0 } d
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(d)) : null;

    /// <summary>공통 데이터 폴더 (%APPDATA%\Nubis): 언어·로그인 설정(settings.json), 마지막 로그인 기록, 계정별 프로필(users\)</summary>
    public static string RootDir { get; } = Ensure(OverrideDir ?? Migrate(Environment.SpecialFolder.ApplicationData));

    /// <summary>
    /// 지금 쓰는 데이터 폴더 (호스트·Vault·클라우드 프로필·known_hosts·Claude 작업 폴더·감사 로그).
    /// 로그인을 쓰지 않으면 RootDir, 로그인하면 그 계정의 프로필 폴더 (앱 시작 때 한 번 정한다).
    /// </summary>
    public static string DataDir { get; private set; } = RootDir;

    /// <summary>계정별 프로필 폴더들의 위치</summary>
    public static string UsersDir => Path.Combine(RootDir, "users");

    public static bool UsingProfile => !string.Equals(DataDir, RootDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>앱 시작 때 로그인한 계정의 프로필 폴더로 바꾼다 (메인 창을 열기 전에)</summary>
    public static void UseDataDir(string dir) => DataDir = Ensure(Path.GetFullPath(dir));

    public static string WebViewDir { get; } = OverrideDir != null
        ? Path.Combine(OverrideDir, "WebView2")
        : Path.Combine(Migrate(Environment.SpecialFolder.LocalApplicationData), "WebView2");

    public static string HostsFile => Path.Combine(DataDir, "hosts.json");
    public static string KnownHostsFile => Path.Combine(DataDir, "known_hosts.json");

    private static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

    /// <summary>
    /// %APPDATA%\RemoteClient → %APPDATA%\Nubis 이전. 폴더째 옮기고, 옮기지 못했으면(사용 중 등)
    /// 새 폴더에 없는 파일만 복사한다 (다음 실행에서도 빠진 파일이 있으면 다시 시도).
    /// </summary>
    private static string Migrate(Environment.SpecialFolder root)
    {
        var baseDir = Environment.GetFolderPath(root);
        var dir = Path.Combine(baseDir, AppName);
        var legacy = Path.Combine(baseDir, LegacyName);
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(legacy)) Directory.Move(legacy, dir);
        }
        catch { }
        try
        {
            if (Directory.Exists(legacy))
            {
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(legacy))
                {
                    var target = Path.Combine(dir, Path.GetFileName(f));
                    try
                    {
                        if (!File.Exists(target)) File.Copy(f, target);
                        RemoveMigratedPlaintext(f, target);
                    }
                    catch { /* 이 파일은 다음 실행에서 다시 시도 */ }
                }
            }
        }
        catch { }
        return dir;
    }

    /// <summary>예전 폴더에서 복사해 온 계정·서버 목록 파일 (평문이면 새 폴더에서 암호화한 뒤 예전 평문을 지운다)</summary>
    internal static readonly string[] MigratedSecretFiles = { "hosts.json", "cloud_profiles.json", "known_hosts.json", "vault.json", "login.json" };

    /// <summary>
    /// 복사가 끝난 예전 폴더의 평문 데이터 파일을 지운다 (복사 대체 경로가 평문 hosts.json 등을 남기던 문제).
    /// 새 폴더의 파일을 암호화하고, 다시 풀어 예전 파일과 내용이 똑같은 것을 확인한 경우에만 지운다 —
    /// 그 사이 새 폴더 파일이 바뀌었거나 이미 암호화된 예전 파일, 목록에 없는 파일은 건드리지 않는다.
    /// </summary>
    internal static void RemoveMigratedPlaintext(string legacyFile, string target)
    {
        if (Array.IndexOf(MigratedSecretFiles, Path.GetFileName(legacyFile).ToLowerInvariant()) < 0) return;
        if (!File.Exists(legacyFile) || !File.Exists(target)) return;
        var legacyBytes = File.ReadAllBytes(legacyFile);
        if (SafeFile.IsProtected(legacyBytes)) return;
        var legacyText = Encoding.UTF8.GetString(legacyBytes).TrimStart('﻿');
        var targetBytes = File.ReadAllBytes(target);
        if (!SafeFile.IsProtected(targetBytes))
        {
            var targetText = Encoding.UTF8.GetString(targetBytes).TrimStart('﻿');
            if (targetText != legacyText) return;
            SafeFile.WriteProtected(target, targetText, keepBackup: false);
            targetBytes = File.ReadAllBytes(target);
        }
        if (!SafeFile.IsProtected(targetBytes) || SafeFile.UnprotectBytes(targetBytes) != legacyText) return;
        File.Delete(legacyFile);
    }
}

/// <summary>설정 파일 안전 저장/읽기: 임시 파일에 쓴 뒤 교체, 읽기 실패 시 원본 보관 후 경고</summary>
public static class SafeFile
{
    /// <summary>시작할 때 사용자에게 보여줄 읽기 실패 경고</summary>
    public static List<string> LoadWarnings { get; } = new();

    public static void WriteAtomic(string path, string content) =>
        WriteDurable(path, new UTF8Encoding(false).GetBytes(content), keepBackup: true);

    /// <summary>직전 저장본 (읽지 못하면 이 파일로 복구)</summary>
    public static string BackupPath(string path) => path + ".bak";

    /// <summary>
    /// 임시 파일에 쓰고 디스크까지 내린(Flush) 뒤 원본과 바꾼다. 쓰는 도중 전원이 꺼져도 0바이트 원본이 남지 않게.
    /// keepBackup 이면 바뀌기 전 원본을 .bak 으로 남긴다 (File.Replace). 원본이 없거나 Replace 를 못 쓰는 환경이면 Move
    /// </summary>
    internal static void WriteDurable(string path, byte[] bytes, bool keepBackup)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        if (keepBackup && File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, BackupPath(path), ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) { /* 아래 Move 로 (Replace 가 실패해도 tmp 는 남아 있다) */ }
            catch (UnauthorizedAccessException) { }
            catch (PlatformNotSupportedException) { }
        }
        File.Move(tmp, path, overwrite: true);
    }

    // ----- 파일 전체 암호화 (DPAPI CurrentUser) -----
    // 서버 목록·IP·계정 이름까지 숨긴다. 파일 앞에 표식을 두고, 표식이 없으면 예전 평문 파일로 읽는다(다음 저장 때 암호화).

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NUBIS-DPAPI1\n");
    private static readonly byte[] FileEntropy = Encoding.UTF8.GetBytes("Nubis.DataFile.v1");

    public static bool IsProtected(byte[] bytes) => bytes.AsSpan().StartsWith(Magic);

    public static byte[] ProtectBytes(string content) =>
        [.. Magic, .. ProtectedData.Protect(Encoding.UTF8.GetBytes(content), FileEntropy, DataProtectionScope.CurrentUser)];

    /// <summary>암호화된 파일이면 풀고, 예전 평문이면 그대로. 다른 PC·계정에서 만든 파일이면 CryptographicException</summary>
    public static string UnprotectBytes(byte[] bytes) =>
        IsProtected(bytes)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes[Magic.Length..], FileEntropy, DataProtectionScope.CurrentUser))
            : Encoding.UTF8.GetString(bytes).TrimStart('﻿');

    /// <param name="keepBackup">false = 직전 원본을 .bak 으로 남기지 않는다 (평문 → 암호화 변환처럼 예전 내용을 남기면 안 될 때)</param>
    public static void WriteProtected(string path, string content, bool keepBackup = true) =>
        WriteDurable(path, ProtectBytes(content), keepBackup);

    /// <summary>
    /// 암호화 파일 읽기. 예전 평문 파일이면 읽은 뒤 바로 암호화해 다시 쓴다.
    /// 읽지 못하면 원본을 .corrupt 사본으로 보관하고, 직전 저장본(.bak)이 읽히면 그것으로 복구, 아니면 빈 값으로 시작한다.
    /// </summary>
    public static T LoadProtected<T>(string path, Func<string, T?> parse, Func<T> fallback) where T : class =>
        LoadProtected(path, parse, fallback, out _);

    /// <param name="failed">원본도 .bak 도 읽지 못해 fallback 을 돌려줬으면 true (파일이 없을 때는 false)</param>
    public static T LoadProtected<T>(string path, Func<string, T?> parse, Func<T> fallback, out bool failed) where T : class
    {
        failed = false;
        if (!File.Exists(path)) return fallback();
        try
        {
            var bytes = File.ReadAllBytes(path);
            var value = parse(UnprotectBytes(bytes)) ?? fallback();
            if (!IsProtected(bytes))
            {
                // 평문을 .bak 으로 남기지 않는다
                try { WriteProtected(path, UnprotectBytes(bytes), keepBackup: false); } catch { }
            }
            return value;
        }
        catch (Exception ex)
        {
            var why = ex is CryptographicException ? L.T("이 PC·이 Windows 계정에서 만든 파일이 아니라 풀 수 없습니다") : ex.Message.Split('\n')[0];
            return Recover(path, bytes => parse(UnprotectBytes(bytes)), fallback, why, out failed);
        }
    }

    /// <param name="restoreBackup">
    /// false = 읽지 못해도 .bak 으로 되돌리지 않고 fallback(기본값)으로 시작한다. settings.json 용 — 로그인을 잘못 설정해 들어갈 수 없을 때
    /// 사용자가 settings.json 을 직접 고치는 것이 안내된 해결 방법인데, 고치다 JSON 을 틀리면 .bak(로그인이 켜진 예전 설정)이 되살아나
    /// 다시 갇히게 된다. 예전처럼 기본값(로그인 꺼짐)으로 시작하는 편이 안전하다.
    /// </param>
    public static T Load<T>(string path, Func<string, T?> parse, Func<T> fallback, bool restoreBackup = true) where T : class
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            return parse(File.ReadAllText(path)) ?? fallback();
        }
        catch (Exception ex)
        {
            return Recover(path, bytes => parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿')), fallback, ex.Message.Split('\n')[0], out _, restoreBackup);
        }
    }

    // 이미 .corrupt 사본을 만들고 경고한 손상 파일 (경로 → 수정 시각·크기). .bak 을 원본 자리로 되돌려 쓰지 못한 경우 등
    // 같은 손상 파일을 다시 읽을 때마다 .corrupt 사본과 경고가 쌓이지 않게 한 번만 처리한다
    private static readonly Dictionary<string, (DateTime stamp, long length)> ReportedCorrupt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>이 손상 파일을 처음 보는지 (처음이면 기록하고 true)</summary>
    private static bool FirstReport(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var key = Path.GetFullPath(path);
            var stamp = (info.LastWriteTimeUtc, info.Length);
            lock (ReportedCorrupt)
            {
                if (ReportedCorrupt.TryGetValue(key, out var seen) && seen == stamp) return false;
                ReportedCorrupt[key] = stamp;
                return true;
            }
        }
        catch { return true; }
    }

    /// <summary>
    /// 읽기 실패 처리: 손상된 원본을 빈 값으로 덮어쓰기 전에 .corrupt 사본으로 보관하고,
    /// 직전 저장본(.bak)이 읽히면 그 내용을 원본 자리로 되돌려 쓴다 (전원이 꺼져 0바이트가 된 경우 등).
    /// 같은 손상 파일(수정 시각·크기)을 다시 읽으면 사본·경고는 다시 만들지 않는다.
    /// </summary>
    private static T Recover<T>(string path, Func<byte[], T?> parseBytes, Func<T> fallback, string why, out bool failed, bool restoreBackup = true) where T : class
    {
        var first = FirstReport(path);
        var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        if (first) { try { File.Copy(path, backup, overwrite: true); } catch { } }
        var bak = BackupPath(path);
        if (restoreBackup)
        {
            try
            {
                if (File.Exists(bak) && parseBytes(File.ReadAllBytes(bak)) is { } recovered)
                {
                    try { File.Copy(bak, path, overwrite: true); } catch { /* 되돌려 쓰지 못함 — 다음 읽기도 여기로 오지만 FirstReport 로 사본·경고는 한 번만 */ }
                    if (first)
                        lock (LoadWarnings)
                            LoadWarnings.Add(L.F("{0} 을(를) 읽지 못해 직전 저장본({1})으로 복구했습니다.\n읽지 못한 원본은 {2} 에 보관했습니다.\n({3})",
                                Path.GetFileName(path), bak, backup, why));
                    failed = false;
                    return recovered;
                }
            }
            catch { /* .bak 도 읽지 못함 — 빈 값으로 */ }
        }
        if (first)
            lock (LoadWarnings)
                LoadWarnings.Add(restoreBackup
                    ? L.F("{0} 을(를) 읽지 못해 빈 상태로 시작했습니다.\n원본은 {1} 에 보관했습니다.\n({2})", Path.GetFileName(path), backup, why)
                    : L.F("{0} 을(를) 읽지 못해 기본 설정으로 시작했습니다.\n원본은 {1} 에 보관했습니다.\n({2})", Path.GetFileName(path), backup, why));
        failed = true;
        return fallback();
    }
}

/// <summary>Windows DPAPI(현재 사용자 범위)로 비밀번호를 암호화한다. 다른 계정/PC 에서는 복호화되지 않는다.</summary>
public static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RemoteClient.v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string? cipher) => TryUnprotect(cipher, out var plain) ? plain : "";

    /// <summary>
    /// 비어 있으면 "" 로 성공, 풀리지 않으면(다른 PC·계정의 암호문, 손상) false.
    /// Unprotect 는 실패를 "" 로 돌려줘 빈 비밀번호와 구분되지 않으므로, 구분이 필요하면 이것을 쓴다.
    /// </summary>
    public static bool TryUnprotect(string? cipher, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(cipher)) return true;
        try
        {
            plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch { return false; }
    }
}

public static class HostStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static List<HostEntry> Load()
    {
        var hosts = SafeFile.LoadProtected(AppPaths.HostsFile, s => JsonSerializer.Deserialize<List<HostEntry>>(s, Options), () => new List<HostEntry>());
        var migrated = false;
        foreach (var h in hosts) migrated |= h.MigrateLegacyVault();
        if (migrated) { try { Save(hosts); } catch { } }
        return hosts;
    }

    public static void Save(IEnumerable<HostEntry> hosts) =>
        SafeFile.WriteProtected(AppPaths.HostsFile, JsonSerializer.Serialize(hosts, Options));
}

public sealed class AppSettings
{
    public string TerminalTheme { get; set; } = TerminalThemes.DefaultId;
    public string FontFamily { get; set; } = "Cascadia Mono";
    public int FontSize { get; set; } = 14;
    /// <summary>배경과 대비가 낮은 글자색을 자동 보정 (xterm minimumContrastRatio)</summary>
    public bool AutoContrast { get; set; } = true;
    // 구문 강조 (출력에 색 덧입히기)
    public bool HighlightIp { get; set; } = true;
    public bool HighlightNumbers { get; set; } = true;
    public bool HighlightKeywords { get; set; } = true;
    /// <summary>터미널 화면 GPU 가속 (끄면 WebView2 메모리 약 70MB 절약, 앱 재시작 시 적용)</summary>
    public bool TerminalGpu { get; set; }
    /// <summary>RDP 서버 인증서를 확인할 수 없을 때 경고 표시 (기본 켜짐. 자체 서명 인증서 환경이면 꺼서 바로 접속)</summary>
    public bool RdpCertWarning { get; set; } = true;
    /// <summary>SSH 처음 접속하는 서버의 호스트키 확인 창 (기본 꺼짐: 자동 저장. 키가 바뀐 경우는 항상 경고)</summary>
    public bool SshHostKeyPrompt { get; set; }
    /// <summary>Claude 의 run_command 중 읽기 전용으로 판별된 명령은 승인 창 없이 실행 (기본 꺼짐, 감사 로그는 남김)</summary>
    public bool AutoApproveReadOnly { get; set; }
    /// <summary>새 호스트·새 클라우드 프로필에 넣을 기본 포트 (서버들이 비표준 포트를 쓰는 환경이면 바꿔 둔다)</summary>
    public int DefaultSshPort { get; set; } = 22;
    public int DefaultRdpPort { get; set; } = 3389;
    /// <summary>SSH 로그인에 성공하면 SFTP 패널을 자동으로 연다</summary>
    public bool SftpAutoOpen { get; set; } = true;
    /// <summary>터미널에서 cd 하면 SFTP 패널도 그 폴더로 (셸이 OSC 7 로 현재 경로를 알려줄 때)</summary>
    public bool SftpFollowTerminal { get; set; } = true;
    /// <summary>SFTP 파일 편집에 쓸 프로그램 (비우면 Windows 기본 프로그램). Claude 는 바꿀 수 없다</summary>
    public string SftpEditor { get; set; } = "";
    /// <summary>시작하기 안내를 이미 보여줬는지 (계정별 프로필마다 처음 한 번)</summary>
    public bool OnboardingShown { get; set; }
    /// <summary>앱 시작 화면 로그인 (GitHub · Google · Microsoft). Claude 는 바꿀 수 없다</summary>
    public LoginConfig Login { get; set; } = new();
    /// <summary>화면 언어: "ko"(기본) · "en". null 이면 처음 실행 — 언어 선택 화면을 띄운다</summary>
    public string? Language { get; set; }
    /// <summary>클라우드 인증 파일 경로 (비우면 기본 위치). 키: Aws, Tencent, Gcp, Azure</summary>
    public Dictionary<string, string> CloudPaths { get; set; } = new();

    /// <summary>기본 Vault 계정: 계정을 따로 지정하지 않은 모든 호스트가 쓴다 (SSH · RDP 따로)</summary>
    public Guid? DefaultSshVaultId { get; set; }
    public Guid? DefaultRdpVaultId { get; set; }

    // ----- 화면 상태 기억 -----
    /// <summary>창 위치·크기 (Normal 상태 기준)와 최대화 여부</summary>
    public double[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }
    /// <summary>Claude 패널 폭</summary>
    public double ClaudePanelWidth { get; set; } = 520;
    /// <summary>접어 둔 직접 등록 그룹 / 펼쳐 둔 클라우드 그룹 키</summary>
    public List<string> CollapsedGroups { get; set; } = new();
    public List<string> ExpandedCloudGroups { get; set; } = new();

    // ----- 편의 -----
    /// <summary>호스트 목록 맨 위에 최근 접속 그룹 표시</summary>
    public bool ShowRecent { get; set; } = true;
    /// <summary>연결 중인 탭을 닫을 때 확인</summary>
    public bool ConfirmCloseTab { get; set; } = true;
    /// <summary>여러 줄 붙여넣기 전에 확인</summary>
    public bool ConfirmMultilinePaste { get; set; } = true;
    /// <summary>
    /// 터미널: 선택이 있으면 Ctrl+C 는 복사, Ctrl+V 는 붙여넣기 (Windows Terminal 방식).
    /// vim·htop 같은 전체 화면 프로그램 안에서는 Ctrl+V 를 프로그램에 그대로 보낸다
    /// </summary>
    public bool SmartCopyPaste { get; set; } = true;
    /// <summary>SSH 연결이 끊기면 자동으로 다시 연결 (0 = 끔)</summary>
    public bool SshAutoReconnect { get; set; }
    /// <summary>클라우드 자동 동기화: 앱 시작 시 · N분마다 (0 = 끔)</summary>
    public bool CloudSyncOnStart { get; set; }
    public int CloudSyncIntervalMinutes { get; set; }
    /// <summary>SFTP 다운로드가 끝나면 탐색기 열기</summary>
    public bool SftpOpenFolderAfterDownload { get; set; } = true;
}

/// <summary>
/// 앱 설정 (settings.json). 변경 시 Changed 로 열린 터미널에 즉시 반영한다.
/// - Global: 공통 폴더의 settings.json — 언어(Language)·로그인(Login) 은 항상 여기서 읽고 쓴다
/// - Current: 지금 계정의 설정 (테마·글꼴·SFTP 등). 로그인을 쓰지 않으면 Global 과 같은 개체
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static string GlobalPath => Path.Combine(AppPaths.RootDir, "settings.json");
    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Global { get; private set; } = Load(GlobalPath);
    public static AppSettings Current { get; private set; } = Global;

    public static event Action? Changed;

    private static AppSettings Load(string path) =>
        // .bak 자동 복구 안 함: 사용자가 settings.json 을 직접 고치다 틀려도 예전(로그인이 켜진) 설정으로 되돌아가지 않고 기본값으로
        SafeFile.Load(path, s => JsonSerializer.Deserialize<AppSettings>(s), () => new AppSettings(), restoreBackup: false);

    /// <summary>
    /// AppPaths.UseDataDir 다음에 부른다: 그 계정의 settings.json 을 읽는다.
    /// 처음이면 inheritGlobal(기존 데이터를 넘겨받는 소유자) 이면 공통 설정을, 아니면 기본값으로 시작.
    /// </summary>
    public static void UseProfile(bool inheritGlobal)
    {
        if (!AppPaths.UsingProfile) { Current = Global; return; }
        if (File.Exists(FilePath)) { Current = Load(FilePath); return; }
        Current = inheritGlobal
            ? JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Global)) ?? new AppSettings()
            : new AppSettings();
        try { SafeFile.WriteAtomic(FilePath, JsonSerializer.Serialize(Current, WriteOptions)); } catch { }
    }

    /// <summary>지금 계정의 설정 변경 (테마·글꼴·SFTP·기본 포트 …)</summary>
    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        try { SafeFile.WriteAtomic(FilePath, JsonSerializer.Serialize(Current, WriteOptions)); }
        catch { }
        Changed?.Invoke();
    }

    /// <summary>화면 상태(창 크기·그룹 접힘 등) 저장 — 열린 터미널에 설정을 다시 보내지 않는다</summary>
    public static void UpdateState(Action<AppSettings> change)
    {
        change(Current);
        try { SafeFile.WriteAtomic(FilePath, JsonSerializer.Serialize(Current, WriteOptions)); }
        catch { }
    }

    /// <summary>공통 설정 변경 (언어·로그인)</summary>
    public static void UpdateGlobal(Action<AppSettings> change)
    {
        change(Global);
        try { SafeFile.WriteAtomic(GlobalPath, JsonSerializer.Serialize(Global, WriteOptions)); }
        catch { }
        Changed?.Invoke();
    }
}

/// <summary>SSH 서버 호스트키 지문 저장소 ("host:port" → "keytype SHA256:...")</summary>
/// <remarks>
/// known_hosts.json 을 읽지 못하면(손상·다른 PC 파일, .bak 복구도 실패) 빈 목록으로 보지 않는다 — 그러면 모든 서버가
/// "처음 보는 서버" 가 되어 비대화형 연결이 키를 전부 자동 신뢰(accept-new)하고 바뀐 키 경고도 사라진다.
/// 대신 <see cref="LoadFailed"/> 를 켜고 <see cref="Get"/> 이 어떤 지문과도 같지 않은 <see cref="UnavailableMark"/> 를 돌려준다:
/// 호출하는 쪽은 "저장값과 다름" 으로 처리하게 된다 (VerifyAcceptNew·WinRM 은 거부, 대화형 접속은 확인 창).
/// 그동안 Set 은 파일에 쓰지 않고(손상된 원본 위에 키 하나짜리 목록을 쓰면 나머지 서버가 다시 accept-new 가 됨)
/// 이번 실행 동안만 기억한다. 파일을 고치거나 지우면(수정 시각·크기가 바뀌면) 다시 읽는다.
/// </remarks>
public static class KnownHostStore
{
    private static readonly object Gate = new();
    // 읽기 실패를 한 번만 처리 (Get 마다 .corrupt 사본과 경고가 새로 생기던 문제). 같은 파일(경로·수정 시각·크기)이면 다시 읽지 않는다
    private static (string path, DateTime stamp, long length)? _failedFile;
    // 파일을 읽지 못하는 동안 대화형 확인 창에서 신뢰한 키 (이번 실행에서만)
    private static readonly Dictionary<string, string> SessionTrust = new();

    /// <summary>known_hosts.json 을 읽지 못해 저장된 호스트키를 확인할 수 없는 상태</summary>
    public static bool LoadFailed { get; private set; }

    /// <summary>LoadFailed 동안 Get 이 돌려주는 값 — 어떤 지문과도 같지 않다 (호출하는 쪽이 "저장된 값과 다름" 으로 처리)</summary>
    public static string UnavailableMark => L.T("(known_hosts.json 을 읽지 못해 저장된 호스트키를 확인할 수 없음)");

    /// <summary>읽은 목록. 읽지 못했으면 null (LoadFailed). Gate 안에서 부른다</summary>
    private static Dictionary<string, string>? TryLoad()
    {
        var path = AppPaths.KnownHostsFile;
        if (!File.Exists(path)) { LoadFailed = false; _failedFile = null; return new Dictionary<string, string>(); }
        var info = new FileInfo(path);
        var stamp = (path, info.LastWriteTimeUtc, info.Length);
        if (_failedFile is { } f && f == stamp) { LoadFailed = true; return null; }
        var all = SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<Dictionary<string, string>>(s),
            () => new Dictionary<string, string>(), out var failed);
        if (failed)
        {
            _failedFile = stamp;
            if (!LoadFailed)
                lock (SafeFile.LoadWarnings)
                    SafeFile.LoadWarnings.Add(L.T("저장된 SSH 호스트키를 확인할 수 없어, 파일을 복구하거나 지울 때까지 접속할 때마다 호스트키를 확인합니다 (Claude 일괄 실행·SFTP 의 자동 신뢰는 거부)."));
            LoadFailed = true;
            return null;
        }
        LoadFailed = false;
        _failedFile = null;
        SessionTrust.Clear();
        return all;
    }

    /// <summary>저장된 호스트키 목록 (화면 표시용). 파일을 읽지 못했으면 이번 실행에서 신뢰한 키만</summary>
    public static Dictionary<string, string> Load()
    {
        lock (Gate)
            return TryLoad() ?? new Dictionary<string, string>(SessionTrust);
    }

    /// <summary>
    /// 저장된 지문. 처음 보는 서버면 null. 파일을 읽지 못했으면 <see cref="UnavailableMark"/> (이번 실행에서 확인 창으로 신뢰한 키는 그 값)
    /// </summary>
    public static string? Get(string key)
    {
        lock (Gate)
        {
            if (TryLoad() is { } all) return all.TryGetValue(key, out var v) ? v : null;
            return SessionTrust.TryGetValue(key, out var t) ? t : UnavailableMark;
        }
    }

    // 사용자가 대화형 확인 창에서 거부한 키 표시 ("REJECTED " + 지문)
    private const string RejectedPrefix = "REJECTED ";
    public static string RejectedMark(string fingerprint) => RejectedPrefix + fingerprint;
    public static bool IsRejected(string value) => value.StartsWith(RejectedPrefix, StringComparison.Ordinal);

    public static void Set(string key, string value)
    {
        lock (Gate)
        {
            if (TryLoad() is not { } all) { SessionTrust[key] = value; return; }   // 손상된 원본 위에 쓰지 않는다
            all[key] = value;
            SafeFile.WriteProtected(AppPaths.KnownHostsFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            if (TryLoad() is not { } all) { SessionTrust.Remove(key); return; }
            if (all.Remove(key))
                SafeFile.WriteProtected(AppPaths.KnownHostsFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
