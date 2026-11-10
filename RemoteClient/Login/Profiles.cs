using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Nubis;

/// <summary>
/// 로그인 계정별 프로필 폴더 (%APPDATA%\Nubis\users\&lt;제공자&gt;_&lt;계정 ID&gt;).
/// 호스트·Vault·클라우드 프로필·known_hosts·Claude 작업 폴더·감사 로그·화면 설정이 계정마다 따로 저장된다.
/// 암호화는 Windows 계정 단위(DPAPI)라, 같은 Windows 계정을 쓰는 사람끼리 파일을 직접 푸는 것까지 막지는 못한다
/// (앱 화면에서 섞이지 않게 나누는 것).
/// </summary>
public static class Profiles
{
    /// <summary>프로필로 옮기는 데이터 (로그인을 켜기 전 공통 폴더에 있던 것)</summary>
    public static readonly string[] DataFiles = { "hosts.json", "cloud_profiles.json", "known_hosts.json", "vault.json", "claude_audit.log" };
    public static readonly string[] DataDirs = { "claude" };

    /// <summary>
    /// 프로필 이름: 제공자 + 바뀌지 않는 계정 ID (GitHub 사용자 번호 등. 아이디를 바꿔도 같은 프로필).
    /// ID 가 없으면 계정 이름. 파일 이름에 쓸 수 없는 문자는 _ 로.
    /// </summary>
    public static string Key(LoginProviderKind provider, string id, string account)
    {
        var raw = (id ?? "").Trim().Length > 0 ? id!.Trim() : (account ?? "").Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in raw)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.' or '@' ? ch : '_');
        var safe = sb.ToString();
        while (safe.Contains("..")) safe = safe.Replace("..", "_");
        safe = safe.Trim('.', '_');
        if (safe.Length == 0) safe = "user";
        if (safe.Length > 80) safe = safe[..80];
        return $"{provider.ToString().ToLowerInvariant()}_{safe}";
    }

    public static string Dir(string key) => Path.Combine(AppPaths.UsersDir, key);

    /// <summary>
    /// 시작할 때 쓸 프로필: 로그인을 쓰면 로그인한 계정의 프로필, 로그인을 껐어도 소유자 프로필이 있으면 그것,
    /// 둘 다 아니면 null (공통 폴더를 그대로 씀).
    /// </summary>
    public static string? Choose(LoginConfig cfg, string? signedInKey, Func<string, bool> profileExists)
    {
        if (cfg.Enabled) return signedInKey;
        return cfg.OwnerKey.Length > 0 && profileExists(cfg.OwnerKey) ? cfg.OwnerKey : null;
    }

    /// <summary>
    /// 로그인을 켜기 전 공통 폴더의 데이터를 소유자 프로필로 옮긴다. 프로필에 이미 같은 파일이 있으면 건드리지 않는다.
    /// 옮긴 항목 수를 돌려준다.
    /// </summary>
    public static int ClaimLegacy(string rootDir, string profileDir)
    {
        Directory.CreateDirectory(profileDir);
        var moved = 0;
        foreach (var f in DataFiles)
        {
            var src = Path.Combine(rootDir, f);
            var dst = Path.Combine(profileDir, f);
            if (!File.Exists(src) || File.Exists(dst)) continue;
            try { File.Move(src, dst); moved++; } catch { }
        }
        foreach (var d in DataDirs)
        {
            var src = Path.Combine(rootDir, d);
            var dst = Path.Combine(profileDir, d);
            if (!Directory.Exists(src) || Directory.Exists(dst)) continue;
            try { Directory.Move(src, dst); moved++; } catch { }
        }
        return moved;
    }

    /// <summary>
    /// 소유자가 처음 로그인했을 때: 로그인 방식을 바꾼 경우 예전 프로필을 새 이름으로 옮기고,
    /// 로그인을 켜기 전 공통 폴더의 데이터를 넘겨받는다.
    /// </summary>
    public static void ClaimOwner(LoginConfig cfg, string dir, string rootDir, string usersDir)
    {
        var prev = cfg.PreviousOwnerKey.Length > 0 ? Path.Combine(usersDir, cfg.PreviousOwnerKey) : null;
        if (!Directory.Exists(dir) && prev != null && Directory.Exists(prev))
        {
            try { Directory.CreateDirectory(usersDir); Directory.Move(prev, dir); } catch { }
        }
        ClaimLegacy(rootDir, dir);
    }

    /// <summary>프로필 폴더가 있고 데이터가 하나라도 있는지</summary>
    public static bool Exists(string key)
    {
        var dir = Dir(key);
        return Directory.Exists(dir) && (DataFiles.Any(f => File.Exists(Path.Combine(dir, f))) || File.Exists(Path.Combine(dir, "settings.json")));
    }
}
