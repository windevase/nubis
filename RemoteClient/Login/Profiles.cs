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
    public static bool ClaimOwner(LoginConfig cfg, string dir, string rootDir, string usersDir) =>
        ClaimOwner(cfg, dir, rootDir, usersDir, ShowWarning);

    /// <summary>
    /// ClaimOwner 본체. 예전 프로필을 옮기지 못하면 warn 으로 알리고, 새 폴더를 만들지 않는다 (PreviousOwnerKey 가 남아 다음 실행 때 다시 시도).
    /// 새 프로필에 이미 데이터가 있으면 합치지 않고 알린다. 옮겼으면 true, 옮길 것이 없었어도 true, 실패·충돌이면 false.
    /// </summary>
    public static bool ClaimOwner(LoginConfig cfg, string dir, string rootDir, string usersDir, Action<string>? warn)
    {
        var prev = cfg.PreviousOwnerKey.Length > 0 ? Path.Combine(usersDir, cfg.PreviousOwnerKey) : null;
        if (prev != null && Directory.Exists(prev) && !SamePath(prev, dir) && !(HasUserData(dir) && !HasUserData(prev)))
        {
            if (HasUserData(dir))
            {
                // 둘 다 데이터가 있으면 어느 쪽이 맞는지 알 수 없어 합치지 않는다
                warn?.Invoke(L.F("예전 소유자 프로필({0})과 새 프로필({1})에 모두 데이터가 있어 자동으로 합치지 않았습니다.\n필요하면 Nubis 를 끈 뒤 직접 옮기세요:\n{2}\n→ {3}",
                    cfg.PreviousOwnerKey, Path.GetFileName(dir), prev, dir));
                ClaimLegacy(rootDir, dir);
                return false;
            }
            string? aside = null;
            try
            {
                Directory.CreateDirectory(usersDir);
                if (Directory.Exists(dir))
                {
                    // 데이터 없는 새 폴더(예전에 옮기기를 실패한 뒤 만들어진 것 등)는 지우지 않고 옆으로 비켜 둔다
                    aside = dir + ".empty-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    Directory.Move(dir, aside);
                }
                Directory.Move(prev, dir);
            }
            catch (Exception ex)
            {
                if (aside != null && !Directory.Exists(dir)) { try { Directory.Move(aside, dir); } catch { } }
                // 빈 새 폴더를 만들지 않는다 (ClaimLegacy 도 건너뜀) — PreviousOwnerKey 가 남아 있어 다음 실행 때 다시 옮긴다
                warn?.Invoke(L.F("예전 소유자 프로필을 새 프로필로 옮기지 못했습니다: {0}\n{1}\n→ {2}\n\n다른 프로그램(탐색기·다른 Nubis 등)이 폴더를 쓰고 있지 않은지 확인한 뒤 Nubis 를 다시 시작하면 다시 옮깁니다.",
                    ex.Message, prev, dir));
                return false;
            }
        }
        ClaimLegacy(rootDir, dir);
        return true;
    }

    /// <summary>
    /// 이번 실행에 쓸 소유자 폴더. ClaimOwner 가 예전 프로필을 옮기지 못했고(claimed = false) 새 폴더에 데이터가 없으면
    /// 예전 소유자 프로필을 그대로 쓴다 — 새 폴더를 쓰면 빈 호스트 목록으로 시작해 데이터가 사라진 것처럼 보이고,
    /// 그 상태에서 무엇이든 저장하면 다음 실행부터는 "둘 다 데이터 있음" 이 되어 옮기지 못한다.
    /// 옮겼거나, 두 쪽 다 데이터가 있어 합치지 않은 경우는 새 폴더.
    /// </summary>
    public static string DirForRun(LoginConfig cfg, string dir, string usersDir, bool claimed)
    {
        if (claimed || HasUserData(dir) || cfg.PreviousOwnerKey.Length == 0) return dir;
        var prev = Path.Combine(usersDir, cfg.PreviousOwnerKey);
        return Directory.Exists(prev) && !SamePath(prev, dir) ? prev : dir;
    }

    /// <summary>호스트·Vault·클라우드 프로필 중 하나라도 있는지 (빈 폴더·설정 파일만 있는 폴더는 데이터 없음으로 본다)</summary>
    internal static bool HasUserData(string dir) =>
        Directory.Exists(dir) && new[] { "hosts.json", "vault.json", "cloud_profiles.json" }.Any(f => File.Exists(Path.Combine(dir, f)));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    /// <summary>앱 시작 중 경고 (테스트 등 WPF 앱이 없으면 표시하지 않음)</summary>
    private static void ShowWarning(string message)
    {
        if (System.Windows.Application.Current == null) return;
        Dialog.Show(message, L.T("프로필 옮기기"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    /// <summary>프로필 폴더가 있고 데이터가 하나라도 있는지</summary>
    public static bool Exists(string key)
    {
        var dir = Dir(key);
        return Directory.Exists(dir) && (DataFiles.Any(f => File.Exists(Path.Combine(dir, f))) || File.Exists(Path.Combine(dir, "settings.json")));
    }
}
