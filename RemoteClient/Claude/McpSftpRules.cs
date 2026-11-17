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

    // ---------------- 여러 항목 (sftp_download paths · sftp_delete paths · sftp_upload local_paths) ----------------

    /// <summary>한 번에 다룰 수 있는 최대 항목 수 (paths · local_paths) — 승인 창에 목록을 다 보일 수 있게</summary>
    public const int MaxMultiPaths = 100;

    /// <summary>
    /// 단일 인자(single)와 목록 인자(many) 중 하나만 받아 목록으로. 둘 다 · 둘 다 없음 · 빈 항목 · 너무 많음은 사유를 돌려준다.
    /// 앞뒤 공백은 지우고, 글자가 같은 항목(대소문자 구분은 ignoreCase)은 하나로 합친다 (순서 유지)
    /// </summary>
    public static string? CollectPaths(string singleName, string? single, string manyName, IReadOnlyList<string>? many, bool ignoreCase, out List<string> paths)
    {
        paths = new List<string>();
        var hasSingle = !string.IsNullOrWhiteSpace(single);
        if (hasSingle && many != null) return $"{singleName} 와 {manyName} 중 하나만 주세요.";
        if (!hasSingle && (many == null || many.Count == 0)) return $"{singleName} 또는 {manyName} 가 필요합니다.";
        var src = hasSingle ? new[] { single! } : many!;
        if (src.Count > MaxMultiPaths) return $"{manyName} 는 한 번에 {MaxMultiPaths}개까지입니다 ({src.Count}개).";
        var seen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var raw in src)
        {
            var p = (raw ?? "").Trim();
            if (p.Length == 0) return $"{manyName} 에 빈 항목이 있습니다.";
            if (HasBadChars(p)) return $"경로에 제어 문자(줄바꿈 등)나 보이지 않는 문자(양방향 제어 등)가 있습니다: {McpHelpers.VisibleControlChars(p)}";
            if (seen.Add(p)) paths.Add(p);
        }
        return null;
    }

    /// <summary>
    /// 정리된 원격 절대 경로 목록에서 하나가 다른 것을 품는가 (/a 와 /a/b) — 삭제 목록에서 거부한다
    /// (앞 항목을 지우며 뒤 항목이 함께 지워져 승인 창의 수와 결과가 어긋나지 않게). 겹치면 사유
    /// </summary>
    public static string? NestedRemotePaths(IReadOnlyList<string> normalized)
    {
        for (var i = 0; i < normalized.Count; i++)
            for (var j = 0; j < normalized.Count; j++)
            {
                if (i == j) continue;
                var parent = normalized[i].TrimEnd('/');
                if (normalized[j] == normalized[i] || normalized[j].StartsWith(parent + "/", StringComparison.Ordinal))
                    return $"목록의 경로가 서로 겹칩니다: {normalized[i]} 안에 {normalized[j]} — 바깥 경로만 주세요.";
            }
        return null;
    }

    /// <summary>
    /// 올릴 로컬 경로 목록 검사 (전체 경로, Windows 라 대소문자 무시): 한 항목이 다른 항목 안에 있거나,
    /// 원격 폴더에서 같은 이름(마지막 이름)이 되는 항목이 둘 이상이면 사유 (같은 원격 경로에 두 번 쓰지 않게)
    /// </summary>
    public static string? LocalPathListProblem(IReadOnlyList<string> fullPaths)
    {
        static string Norm(string p) => p.TrimEnd('\\', '/');
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < fullPaths.Count; i++)
        {
            var a = Norm(fullPaths[i]);
            var name = System.IO.Path.GetFileName(a);
            if (name.Length == 0) return $"드라이브 루트는 올릴 수 없습니다: {fullPaths[i]}";
            if (names.TryGetValue(name, out var other)) return $"원격 폴더에서 이름이 겹칩니다: {other} 와 {fullPaths[i]} (이름 {name})";
            names[name] = fullPaths[i];
            for (var j = 0; j < fullPaths.Count; j++)
            {
                if (i == j) continue;
                var b = Norm(fullPaths[j]);
                if (b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase))
                    return $"목록의 경로가 서로 겹칩니다: {fullPaths[i]} 안에 {fullPaths[j]} — 바깥 경로만 주세요.";
            }
        }
        return null;
    }

    /// <summary>sftp_upload 의 같은 이름 처리: overwrite = 덮어씀, skip = 건너뜀, fail = 하나라도 있으면 아무것도 올리지 않음. 폴더는 어느 경우든 병합</summary>
    public static readonly string[] UploadConflictModes = { "overwrite", "skip", "fail" };

    /// <summary>
    /// on_conflict 와 예전 인자 overwrite 를 하나로. 둘 다 없으면 fail, overwrite=true 만 있으면 overwrite.
    /// 둘이 어긋나면(overwrite=true 인데 on_conflict=skip 등) 사유를 돌려준다
    /// </summary>
    public static string? ResolveConflictMode(string? onConflict, bool? overwrite, out string mode)
    {
        mode = "fail";
        var oc = (onConflict ?? "").Trim().ToLowerInvariant();
        if (oc.Length == 0) { mode = overwrite == true ? "overwrite" : "fail"; return null; }
        if (!UploadConflictModes.Contains(oc)) return $"on_conflict 는 {string.Join(" / ", UploadConflictModes)} 중 하나여야 합니다.";
        if (overwrite == true && oc != "overwrite") return $"overwrite=true 와 on_conflict={oc} 가 어긋납니다. on_conflict 하나만 주세요.";
        if (overwrite == false && oc == "overwrite") return "overwrite=false 와 on_conflict=overwrite 가 어긋납니다. on_conflict 하나만 주세요.";
        mode = oc;
        return null;
    }

    public enum UploadAction { Create, Merge, Overwrite, Skip, Conflict, Refuse }

    /// <summary>
    /// 올릴 항목 하나를 원격의 지금 상태(LSTAT)와 비교해 할 일을 정한다 — SFTP 패널 업로드의 같은 이름 덮어쓰기 / 건너뛰기, 폴더 병합과 같은 기준.
    /// 단 심볼릭 링크에는 쓰지 않는다 (폴더 링크 안에 넣거나 파일 링크를 덮어쓰면 링크가 가리키는 다른 곳이 바뀜):
    /// skip 이면 건너뛰고, 아니면 거부(Refuse — 아무것도 올리기 전에 멈춤). fail 에서 같은 이름 파일은 Conflict
    /// </summary>
    public static (UploadAction Action, string? Reason) UploadDecision(string mode, bool wantDir, bool exists, bool isLink, bool isDir, bool isRegular)
    {
        var skip = mode == "skip";
        if (!exists) return (UploadAction.Create, null);
        if (wantDir)
        {
            if (isLink) return skip ? (UploadAction.Skip, "폴더 자리에 심볼릭 링크") : (UploadAction.Refuse, "폴더를 올릴 자리에 심볼릭 링크가 있어 올리지 않았습니다 (링크를 따라 다른 곳에 쓰지 않게)");
            if (isDir) return (UploadAction.Merge, null);
            return skip ? (UploadAction.Skip, "폴더 자리에 파일") : (UploadAction.Refuse, "폴더를 올릴 자리에 같은 이름의 파일이 있습니다");
        }
        if (isLink)
            return skip ? (UploadAction.Skip, "심볼릭 링크") : mode == "fail" ? (UploadAction.Conflict, "심볼릭 링크")
                : (UploadAction.Refuse, "대상이 심볼릭 링크라 덮어쓰지 않았습니다 (링크가 가리키는 다른 파일이 바뀌는 것을 막기 위함)");
        if (isDir) return skip ? (UploadAction.Skip, "같은 이름의 폴더") : (UploadAction.Refuse, "파일을 올릴 자리에 같은 이름의 폴더가 있습니다");
        if (!isRegular) return skip ? (UploadAction.Skip, "일반 파일이 아님") : (UploadAction.Refuse, "같은 이름의 특수 파일(장치·FIFO·소켓 등)이 있습니다");
        return mode switch
        {
            "overwrite" => (UploadAction.Overwrite, null),
            "skip" => (UploadAction.Skip, "같은 이름의 파일"),
            _ => (UploadAction.Conflict, "같은 이름의 파일"),
        };
    }

    /// <summary>
    /// sftp_panel_navigate 가 승인을 받아야 하는가 — sftp_list 와 같은 기준 (패널 목록은 화면 캡처로 읽을 수 있음):
    /// 읽기 전용 자동 승인이 꺼져 있거나, 패널이 sudo 로 다른 사용자 권한이거나, 비밀이 든 경로면 승인
    /// </summary>
    public static bool PanelNavigateNeedsApproval(bool autoApproveReadOnly, bool elevated, bool sensitive) =>
        !autoApproveReadOnly || elevated || sensitive;

    /// <summary>목록 문구: 앞 max 개만 한 줄씩 (들여쓰기), 넘으면 "… 외 N개"</summary>
    public static string ListText(IReadOnlyCollection<string> items, int max = 20, string indent = "  ")
    {
        var sb = new StringBuilder();
        foreach (var x in items.Take(max)) sb.Append('\n').Append(indent).Append(x);
        if (items.Count > max) sb.Append('\n').Append(indent).Append($"… 외 {items.Count - max}개");
        return sb.ToString();
    }
}
