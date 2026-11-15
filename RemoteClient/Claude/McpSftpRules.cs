using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nubis;

/// <summary>
/// MCP SFTP 쓰기 도구(sftp_mkdir · sftp_rename · sftp_delete · sftp_write · sftp_upload 덮어쓰기)의 UI 와 무관한 판단 (테스트 대상).
/// 돌려주는 사유 문구는 Claude 가 읽는 MCP 결과라 번역하지 않는다 (이름 검사만 패널과 같은 ValidateRemoteName 문구)
/// </summary>
public static class McpSftpRules
{
    /// <summary>
    /// 쓰기 도구에 쓸 원격 경로 검사·정리. 절대 경로만 받는다 (상대 경로는 서버 작업 폴더 기준이라 의도와 다른 곳을 가리킬 수 있음).
    /// 빈 조각("//")과 끝의 "/" 는 정리하고, "." · ".." 조각과 제어 문자는 거부한다 (".." 로 다른 폴더를 가리키지 않게 — 정리하지 않고 거부).
    /// 안 되면 사유, 되면 null (normalized 에 정리한 경로)
    /// </summary>
    public static string? ValidateRemotePath(string? path, out string normalized)
    {
        normalized = "";
        var p = (path ?? "").Trim();
        if (p.Length == 0) return "경로가 필요합니다.";
        if (HasBadChars(p)) return "경로에 제어 문자(줄바꿈 등)나 보이지 않는 문자(양방향 제어 등)가 있습니다.";
        if (!p.StartsWith('/')) return $"절대 경로('/' 로 시작)를 쓰세요: {p}";
        var parts = new List<string>();
        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0) continue;
            if (seg is "." or "..") return $"경로에 '.' · '..' 은 쓸 수 없습니다: {p}";
            parts.Add(seg);
        }
        normalized = "/" + string.Join("/", parts);
        return null;
    }

    /// <summary>
    /// 새로 만들 · 바꿀 이름 하나 검사 — SFTP 패널의 새 폴더 · 이름 변경과 같은 규칙 (SftpPanel.ValidateRemoteName) 에 더해,
    /// 승인 창에서 다르게 보일 수 있는 C1 제어 문자 · 줄 구분자 · 양방향 제어 같은 보이지 않는 문자도 거부한다
    /// </summary>
    public static string? ValidateName(string name) =>
        SftpPanel.ValidateRemoteName(name) ?? (HasBadChars(name) ? "이름에 제어 문자나 보이지 않는 문자(양방향 제어 등)는 쓸 수 없습니다." : null);

    /// <summary>제어 문자(C0 · DEL · C1) 또는 보이지 않는 서식 문자(줄 · 문단 구분자, 양방향 제어 등)가 있는가</summary>
    public static bool HasBadChars(string s) =>
        s.Any(ch => ch < 32 || ch is >= '\x7f' and <= '\x9f') || McpHelpers.HasInvisibleChars(s);

    /// <summary>"/" 와 최상위 시스템 폴더 — 지우거나 이름을 바꾸지 않는다 (Claude 의 실수 하나로 서버가 망가지지 않게)</summary>
    private static readonly HashSet<string> ProtectedTop = new(StringComparer.Ordinal)
    {
        "bin", "boot", "dev", "etc", "home", "lib", "lib32", "lib64", "libx32", "media", "mnt", "opt", "proc", "root",
        "run", "sbin", "snap", "srv", "sys", "tmp", "usr", "var",
    };

    /// <summary>삭제 · 이름 변경 대상으로 막는 경로인가 (정리된 절대 경로 기준) — 막으면 사유</summary>
    public static string? ProtectedPathReason(string normalized)
    {
        if (normalized == "/") return "루트(/)는 지우거나 이름을 바꿀 수 없습니다.";
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && ProtectedTop.Contains(parts[0])) return $"시스템 최상위 폴더는 지우거나 이름을 바꿀 수 없습니다: {normalized}";
        return null;
    }

    /// <summary>
    /// path 를 지우거나 이름을 바꾸면 홈 폴더(home)가 함께 사라지는가 — 홈 자체이거나 홈을 품은 상위 폴더 (예: 홈 /data/u 에 /data).
    /// 둘 다 정리된 절대 경로 기준, 끝의 '/' 는 무시. home 을 모르면(빈 값) false
    /// </summary>
    public static bool ContainsHome(string path, string? home)
    {
        var h = (home ?? "").TrimEnd('/');
        if (h.Length == 0 || !home!.StartsWith('/')) return false;
        var p = path.TrimEnd('/');
        return p.Length == 0 || h == p || h.StartsWith(p + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// 덮어쓸 대상의 링크 판단 (sftp_write · sftp_upload overwrite). 링크에 쓰면 링크가 가리키는 다른 파일(폴더 밖 /etc 등)이 바뀐다 → 거부.
    /// isLink: LSTAT 으로 본 값 (null = 확인할 수 없음 — 있으면 안전하게 거부). 없는 경로(exists=false)는 새로 만들므로 괜찮다
    /// </summary>
    public static string? LinkOverwriteRefusal(string path, bool exists, bool? isLink)
    {
        if (!exists) return null;
        if (isLink == true) return $"대상이 심볼릭 링크라 쓰지 않았습니다 (링크가 가리키는 다른 파일이 바뀌는 것을 막기 위함): {path} — 실제 파일 경로를 지정하세요.";
        if (isLink == null) return $"대상이 심볼릭 링크인지 확인할 수 없어 덮어쓰지 않았습니다: {path}";
        return null;
    }

    /// <summary>
    /// sftp_write 충돌 확인 (SFTP 패널 편집의 저장 충돌 확인과 같은 기준: 수정 시각 · 크기).
    /// 있는 파일을 덮어쓰려면 expected 수정 시각 · 크기를 둘 다 줘야 한다 (sftp_read 결과의 mtime_utc · size) — 읽은 뒤 바뀌었으면 거부.
    /// createOnly 면 없는 파일만 만든다. 수정 시각은 초 단위로 비교 (SFTP v3 는 초 단위).
    /// showActual=false 면 지금 수정 시각 · 크기를 사유에 넣지 않는다 (승인 전 sudo 권한으로 본 값이 Claude 에게 새지 않게)
    /// </summary>
    public static string? WriteConflict(bool exists, DateTime? actualMtimeUtc, long? actualSize,
        DateTime? expectedMtimeUtc, long? expectedSize, bool createOnly, bool showActual = true)
    {
        if (!exists)
        {
            if (expectedMtimeUtc != null || expectedSize != null)
                return "읽은 뒤 파일이 없어졌습니다 (삭제 · 이름 변경됨). 새로 만들려면 expected_* 없이 create=true 로 다시 요청하세요.";
            return createOnly ? null : "파일이 없습니다. 새로 만들려면 create=true 를 주세요.";
        }
        if (createOnly) return "이미 있는 파일입니다 (create=true 는 새 파일만). 덮어쓰려면 sftp_read 로 읽고 expected_mtime_utc · expected_size 를 주세요.";
        if (expectedMtimeUtc == null || expectedSize == null)
            return "있는 파일을 덮어쓰려면 expected_mtime_utc 와 expected_size 가 필요합니다 (sftp_read 결과의 값 — 읽은 뒤 바뀌었는지 확인).";
        if (actualSize != expectedSize || actualMtimeUtc is not { } a || Seconds(a) != Seconds(expectedMtimeUtc.Value))
            return "읽은 뒤 서버 파일이 바뀌었습니다" + (showActual ? $" (지금: mtime_utc={FormatMtime(actualMtimeUtc)}, size={actualSize})" : "") + ". " +
                   "다시 sftp_read 로 읽고 바뀐 내용을 반영해 요청하세요.";
        return null;
    }

    private static long Seconds(DateTime t) => (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t).Ticks / TimeSpan.TicksPerSecond;

    /// <summary>sftp_read · sftp_write 결과에 넣는 수정 시각 형식 (UTC, 초 단위) — 그대로 expected_mtime_utc 로 받는다</summary>
    public static string FormatMtime(DateTime? utc) =>
        utc is { } t ? DateTime.SpecifyKind(t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ") : "-";

    /// <summary>expected_mtime_utc 인자 해석 (ISO 8601, 시간대가 없으면 UTC). 잘못된 값은 null</summary>
    public static DateTime? ParseMtime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
    }

    /// <summary>
    /// sftp_write 승인 창에 보일 바뀜 요약: 크기 변화 · 줄 수 변화 · 처음 바뀐 줄 부근(앞뒤 몇 줄). 이전 내용이 없으면(새 파일) 앞 몇 줄.
    /// 승인 창에 보이는 글이라 줄 길이를 자르고 보이지 않는 문자는 드러낸다. oldText 가 null 이면 이전 내용을 읽지 않음(너무 큼 · 바이너리)
    /// </summary>
    public static string DiffSummary(string? oldText, long oldSize, string newText, long newSize, int maxLines = 8)
    {
        var sb = new StringBuilder();
        var delta = newSize - oldSize;
        sb.Append(oldSize < 0 ? $"새 파일: {SftpPanel.HumanSize(newSize)}" :
            $"크기: {SftpPanel.HumanSize(oldSize)} → {SftpPanel.HumanSize(newSize)} ({(delta >= 0 ? "+" : "-")}{Math.Abs(delta)} B)");
        var newLines = SplitLines(newText);
        if (oldText == null)
        {
            if (oldSize >= 0) sb.Append("\n(이전 내용은 크거나 텍스트가 아니어서 비교하지 않음)");
            sb.Append($"\n새 내용 {newLines.Length}줄 — 앞 {Math.Min(maxLines, newLines.Length)}줄:");
            foreach (var l in newLines.Take(maxLines)) sb.Append("\n+ ").Append(Clip(l));
            return sb.ToString();
        }
        var oldLines = SplitLines(oldText);
        sb.Append($"\n줄 수: {oldLines.Length} → {newLines.Length}");
        var first = 0;
        while (first < oldLines.Length && first < newLines.Length && oldLines[first] == newLines[first]) first++;
        if (first == oldLines.Length && first == newLines.Length) { sb.Append("\n(내용이 같음)"); return sb.ToString(); }
        // 끝에서부터 같은 줄을 빼 바뀐 구간만
        var oe = oldLines.Length; var ne = newLines.Length;
        while (oe > first && ne > first && oldLines[oe - 1] == newLines[ne - 1]) { oe--; ne--; }
        sb.Append($"\n처음 바뀐 줄: {first + 1} (바뀐 구간 - {oe - first}줄 / + {ne - first}줄)");
        var half = Math.Max(1, maxLines / 2);
        foreach (var l in oldLines.Skip(first).Take(Math.Min(half, oe - first))) sb.Append("\n- ").Append(Clip(l));
        if (oe - first > half) sb.Append($"\n- … ({oe - first - half}줄 더)");
        foreach (var l in newLines.Skip(first).Take(Math.Min(half, ne - first))) sb.Append("\n+ ").Append(Clip(l));
        if (ne - first > half) sb.Append($"\n+ … ({ne - first - half}줄 더)");
        return sb.ToString();
    }

    private static string[] SplitLines(string s) => s.Length == 0 ? Array.Empty<string>() : s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static string Clip(string line)
    {
        var l = McpHelpers.VisibleControlChars(line.TrimEnd('\r'));
        return l.Length > 160 ? l[..160] + "…" : l;
    }

    /// <summary>삭제 승인 창의 항목 수 문구 — 세다가 한도에 걸렸으면 "N개 이상"</summary>
    public static string CountText(int files, int dirs, int links, bool capped) =>
        $"파일 {files}개 · 폴더 {dirs}개 · 링크 {links}개{(capped ? " 이상 (너무 많아 세다 멈춤)" : "")}";
}
