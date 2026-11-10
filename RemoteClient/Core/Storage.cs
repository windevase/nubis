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

    public static string DataDir { get; } = Ensure(OverrideDir ?? Migrate(Environment.SpecialFolder.ApplicationData));

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
                    if (!File.Exists(target)) File.Copy(f, target);
                }
            }
        }
        catch { }
        return dir;
    }
}

/// <summary>설정 파일 안전 저장/읽기: 임시 파일에 쓴 뒤 교체, 읽기 실패 시 원본 보관 후 경고</summary>
public static class SafeFile
{
    /// <summary>시작할 때 사용자에게 보여줄 읽기 실패 경고</summary>
    public static List<string> LoadWarnings { get; } = new();

    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
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

    public static void WriteProtected(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ProtectBytes(content));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 암호화 파일 읽기. 예전 평문 파일이면 읽은 뒤 바로 암호화해 다시 쓴다.
    /// 풀 수 없으면(다른 PC·Windows 계정) Load 와 같이 원본을 보관하고 빈 값으로 시작한다.
    /// </summary>
    public static T LoadProtected<T>(string path, Func<string, T?> parse, Func<T> fallback) where T : class
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            var bytes = File.ReadAllBytes(path);
            var value = parse(UnprotectBytes(bytes)) ?? fallback();
            if (!IsProtected(bytes))
            {
                try { WriteProtected(path, File.ReadAllText(path)); } catch { }
            }
            return value;
        }
        catch (Exception ex)
        {
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(path, backup, overwrite: true); } catch { }
            var why = ex is CryptographicException ? "이 PC·이 Windows 계정에서 만든 파일이 아니라 풀 수 없습니다" : ex.Message.Split('\n')[0];
            lock (LoadWarnings)
                LoadWarnings.Add($"{Path.GetFileName(path)} 을(를) 읽지 못해 빈 상태로 시작했습니다.\n원본은 {backup} 에 보관했습니다.\n({why})");
            return fallback();
        }
    }

    public static T Load<T>(string path, Func<string, T?> parse, Func<T> fallback) where T : class
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            return parse(File.ReadAllText(path)) ?? fallback();
        }
        catch (Exception ex)
        {
            // 손상된 파일을 빈 값으로 덮어쓰기 전에 원본을 따로 보관
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(path, backup, overwrite: true); } catch { }
            lock (LoadWarnings)
                LoadWarnings.Add($"{Path.GetFileName(path)} 을(를) 읽지 못해 빈 상태로 시작했습니다.\n원본은 {backup} 에 보관했습니다.\n({ex.Message.Split('\n')[0]})");
            return fallback();
        }
    }
}

/// <summary>Windows DPAPI(현재 사용자 범위)로 비밀번호를 암호화한다. 다른 계정/PC 에서는 복호화되지 않는다.</summary>
public static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RemoteClient.v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
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
    /// <summary>앱 시작 화면 로그인 (GitHub · Google · Microsoft). Claude 는 바꿀 수 없다</summary>
    public LoginConfig Login { get; set; } = new();
    /// <summary>클라우드 인증 파일 경로 (비우면 기본 위치). 키: Aws, Tencent, Gcp, Azure</summary>
    public Dictionary<string, string> CloudPaths { get; set; } = new();
}

/// <summary>앱 설정 (settings.json). 변경 시 Changed 로 열린 터미널에 즉시 반영한다.</summary>
public static class SettingsStore
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Current { get; private set; } = Load();

    public static event Action? Changed;

    private static AppSettings Load() =>
        SafeFile.Load(FilePath, s => JsonSerializer.Deserialize<AppSettings>(s), () => new AppSettings());

    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        try { SafeFile.WriteAtomic(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
        Changed?.Invoke();
    }
}

/// <summary>SSH 서버 호스트키 지문 저장소 ("host:port" → "keytype SHA256:...")</summary>
public static class KnownHostStore
{
    private static readonly object Gate = new();

    public static Dictionary<string, string> Load()
    {
        lock (Gate)
        {
            return SafeFile.LoadProtected(AppPaths.KnownHostsFile, s => JsonSerializer.Deserialize<Dictionary<string, string>>(s),
                () => new Dictionary<string, string>());
        }
    }

    public static string? Get(string key) => Load().TryGetValue(key, out var v) ? v : null;

    // 사용자가 대화형 확인 창에서 거부한 키 표시 ("REJECTED " + 지문)
    private const string RejectedPrefix = "REJECTED ";
    public static string RejectedMark(string fingerprint) => RejectedPrefix + fingerprint;
    public static bool IsRejected(string value) => value.StartsWith(RejectedPrefix, StringComparison.Ordinal);

    public static void Set(string key, string value)
    {
        lock (Gate)
        {
            var all = Load();
            all[key] = value;
            SafeFile.WriteProtected(AppPaths.KnownHostsFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            var all = Load();
            if (all.Remove(key))
                SafeFile.WriteProtected(AppPaths.KnownHostsFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
