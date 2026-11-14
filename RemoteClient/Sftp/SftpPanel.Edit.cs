using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Nubis;

public partial class SftpPanel
{
    // ===================== 편집 (저장하면 서버에 자동 업로드) =====================
    // 임시 폴더로 받아 편집기로 열고, 파일이 저장될 때마다(내용이 바뀌었을 때만) 같은 원격 경로로 다시 올린다.
    // 편집하는 동안 서버 쪽 파일이 바뀌었으면(수정 시각·크기) 덮어쓰기 전에 묻는다.

    private sealed class EditSession : IDisposable
    {
        public required string Remote { get; init; }
        public required string Local { get; init; }
        public DateTime RemoteMtime { get; set; }
        public long RemoteSize { get; set; }
        public string Hash { get; set; } = "";
        public bool Uploading { get; set; }
        public FileSystemWatcher? Watcher { get; set; }
        public DispatcherTimer? Debounce { get; set; }
        public void Dispose()
        {
            Debounce?.Stop();
            if (Watcher != null) { Watcher.EnableRaisingEvents = false; Watcher.Dispose(); }
        }
    }

    private readonly Dictionary<string, EditSession> _edits = new(StringComparer.Ordinal);
    private static string EditTempRoot => Path.Combine(Path.GetTempPath(), "Nubis", "sftp-edit");

    /// <summary>편집 중인 파일 수</summary>
    public int EditingCount => _edits.Count;

    private async Task EditFileAsync(SftpItem item)
    {
        // 이미 편집 중이면 받은 파일을 그대로 다시 연다 (편집기에서 고친 내용을 덮어쓰지 않게)
        if (_edits.TryGetValue(item.FullName, out var existing) && File.Exists(existing.Local)) { OpenInEditor(existing.Local); return; }
        if (_busy) return;
        if (item.Size > 50L * 1024 * 1024 &&
            Dialog.Show(Window.GetWindow(this)!, L.F("{0} 파일입니다. 받아서 편집할까요?", HumanSize(item.Size)), L.T("편집"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_busy) { ShowBusyHint(); return; }   // 확인 창을 띄운 사이 다른 전송이 시작됨
        // 연결을 기다리기 전에 전송 중으로 표시 — 그 사이 다른 작업·사용자 전환이 끼어들지 않게
        var ct = BeginTransfer(L.F("편집용으로 받는 중: {0}", item.Name), item.Size);
        string? dir = null;
        try
        {
            var c = await ClientAsync();
            CleanupEditTemp();
            var host = SafeLocalName(_getAuth()?.Host.DisplayName ?? "sftp");
            dir = Path.Combine(EditTempRoot, host, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, SafeLocalName(item.Name));

            // 받는 중에는 .part 로 — 취소·실패 때 반쯤 받은 파일이 편집 파일로 남지 않게
            var progress = new Progress<Renci.SshNet.DownloadFileProgressReport>(r => SetProgress((long)r.TotalBytesDownloaded, item.Size));
            var part = local + ".part";
            await using (var fs = File.Create(part))
                await c.DownloadFileAsync(item.FullName, fs, progress, ct);
            File.Move(part, local);
            MarkFromInternet(local);
            var attr = await Task.Run(() => c.GetAttributes(item.FullName), ct);

            var s = new EditSession { Remote = item.FullName, Local = local, RemoteMtime = attr.LastWriteTimeUtc, RemoteSize = attr.Size, Hash = HashFile(local) };
            Watch(s);
            _edits[item.FullName] = s;
            TrackTempDir(dir);
            EndTransfer(L.F("편집 중: {0} — 저장하면 서버에 자동으로 올립니다", item.Name));
            OpenInEditor(local);
        }
        catch (OperationCanceledException) { DeleteTempDirs([dir]); EndTransfer(L.T("편집 열기를 취소했습니다.")); }
        catch (Exception ex) { DeleteTempDirs([dir]); EndTransfer(L.F("편집 열기 실패: {0}", ex.Message)); }
    }

    private void Watch(EditSession s)
    {
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        debounce.Tick += async (_, _) => { debounce.Stop(); await SyncEditAsync(s); };
        // 편집기에 따라 직접 쓰거나(VS Code·메모장) 임시 파일로 쓴 뒤 이름을 바꾼다 → 폴더 전체를 보고 이름으로 거른다
        var name = Path.GetFileName(s.Local);
        var w = new FileSystemWatcher(Path.GetDirectoryName(s.Local)!)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        void Poke(string? changed)
        {
            if (!string.Equals(changed, name, StringComparison.OrdinalIgnoreCase)) return;
            Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        }
        w.Changed += (_, e) => Poke(e.Name);
        w.Created += (_, e) => Poke(e.Name);
        w.Renamed += (_, e) => Poke(e.Name);
        w.EnableRaisingEvents = true;
        s.Watcher = w;
        s.Debounce = debounce;
    }

    private async Task SyncEditAsync(EditSession s)
    {
        if (!_edits.ContainsValue(s) || !File.Exists(s.Local)) return;
        if (s.Uploading) { s.Debounce?.Start(); return; }
        byte[] data;
        try
        {
            using var fs = new FileStream(s.Local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (IOException) { s.Debounce?.Start(); return; }   // 편집기가 아직 쓰는 중 → 잠시 뒤 다시
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        if (hash == s.Hash) return;   // 내용이 같으면(저장만 누름) 올리지 않음

        var name = RemoteName(s.Remote);
        s.Uploading = true;
        try
        {
            // 1) 충돌 확인 — 연결 잠금은 서버에 묻는 동안만 잡는다 (확인 창을 띄운 채로 잡고 있지 않게)
            var attr = await WithClientLockAsync(c => Task.Run(() => c.Exists(s.Remote) ? c.GetAttributes(s.Remote) : null));
            if (attr != null && (attr.LastWriteTimeUtc != s.RemoteMtime || attr.Size != s.RemoteSize) &&
                Dialog.Show(Window.GetWindow(this)!,
                    L.F("편집을 시작한 뒤 서버의 파일이 바뀌었습니다.\n\n{0}\n서버: {1:yyyy-MM-dd HH:mm:ss} · {2}\n\n내가 편집한 내용으로 덮어쓸까요? (아니요를 누르면 이번 저장은 올리지 않습니다)",
                        s.Remote, attr.LastWriteTime, HumanSize(attr.Size)),
                    L.T("편집 충돌"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                s.Hash = hash;   // 같은 내용으로 다시 묻지 않음
                ShowStatus(L.F("올리지 않음: {0} (서버 파일이 바뀌어 있음)", name));
                return;
            }
            // 2) 임시 이름으로 올린 뒤 바꿔치기 (UploadOneAsync) — 중간에 끊겨도 원래 파일(/etc 설정 등)은 그대로.
            //    올리는 동안 연결 잠금을 잡아, 사용자 전환(RunAsAsync)·재연결·패널 닫기가 쓰는 중인 연결을 닫지 않게 한다
            //    채널이 멈추면 잠금을 영원히 잡고 있지 않게 시간 제한 (2분 + 크기 비례) — 목록·사용자 전환·연결 열기가 막히지 않게
            using var timeout = new CancellationTokenSource(EditUploadTimeout(data.Length));
            var after = await WithClientLockAsync(async c =>
            {
                await UploadOneAsync(c, () => new MemoryStream(data, writable: false), s.Remote,
                    new Progress<Renci.SshNet.UploadFileProgressReport>(_ => { }), timeout.Token);
                return await Task.Run(() => c.GetAttributes(s.Remote));
            });
            s.RemoteMtime = after.LastWriteTimeUtc;
            s.RemoteSize = after.Size;
            s.Hash = hash;
            ShowStatus(L.F("저장됨 → 서버에 올림: {0}  ({1:HH:mm:ss}, {2})", name, DateTime.Now, HumanSize(data.Length)));
            if (ParentPath(s.Remote) == _cwd && !_busy) await NavigateAsync(_cwd);   // 목록의 크기·시각 갱신
        }
        catch (OperationCanceledException) when (!_closed) { ShowStatus(L.F("자동 업로드 시간 초과: {0} (다시 저장하면 재시도)", name)); }
        catch (Exception ex) { ShowStatus(L.F("자동 업로드 실패: {0} — {1} (다시 저장하면 재시도)", name, ex.Message)); }
        finally { s.Uploading = false; }
    }

    /// <summary>편집 자동 업로드의 시간 제한 — 2분 + 64KB/s 로 올리는 데 걸리는 시간 (큰 파일도 느린 회선에서 잘리지 않게)</summary>
    public static TimeSpan EditUploadTimeout(long bytes) =>
        TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(Math.Max(0, bytes) / (64 * 1024));

    /// <summary>연결 잠금(_clientLock)을 잡고 연결을 얻어 작업한다 — 그동안 사용자 전환·패널 닫기가 이 연결을 닫지 않는다</summary>
    private async Task<T> WithClientLockAsync<T>(Func<SftpClient, Task<T>> work)
    {
        await _clientLock.WaitAsync();
        try { return await work(await ClientCoreAsync()); }
        finally { _clientLock.Release(); }
    }

    private static string RemoteName(string remote)
    {
        var p = remote.TrimEnd('/');
        return p[(p.LastIndexOf('/') + 1)..];
    }

    private static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    /// <summary>
    /// 설정의 편집기로 연다 (비우면 Windows 기본 프로그램, 연결된 프로그램이 없으면 메모장).
    /// 실행될 수 있는 형식(.bat·.exe·.hta·.js 등)은 기본 프로그램으로 열지 않고 메모장으로 — 원격 파일이 이 PC 에서 실행되지 않게
    /// </summary>
    private void OpenInEditor(string local)
    {
        var editor = SettingsStore.Current.SftpEditor.Trim().Trim('"');
        try
        {
            if (editor.Length > 0) Process.Start(new ProcessStartInfo(editor, $"\"{local}\"") { UseShellExecute = true });
            else if (IsRiskyToOpen(local))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = true });
                ShowStatus(L.F("실행될 수 있는 형식이라 메모장으로 엽니다: {0}", Path.GetFileName(local)));
            }
            else Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowStatus(L.F("편집기를 열지 못했습니다: {0}", ex.Message)); }
        }
    }

    /// <summary>임시 폴더로 받은 파일을 기본 프로그램으로 열기. 실행될 수 있는 형식은 실행하지 않고 탐색기에서 선택만 한다</summary>
    private void OpenDownloaded(string local)
    {
        MarkFromInternet(local);
        try
        {
            if (IsRiskyToOpen(local))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{local}\"") { UseShellExecute = true });
                ShowStatus(L.F("실행될 수 있는 형식이라 열지 않고 폴더만 엽니다: {0}", Path.GetFileName(local)));
            }
            else Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 연결된 프로그램이 없음 → 메모장
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowStatus(L.F("편집기를 열지 못했습니다: {0}", ex.Message)); }
        }
        catch (Exception ex) { ShowStatus(L.F("편집기를 열지 못했습니다: {0}", ex.Message)); }
    }

    /// <summary>
    /// 더블클릭(기본 프로그램)으로 열면 실행되는 형식인가 — 실행 파일·스크립트·바로 가기·설치 파일·도움말 등.
    /// 끝의 점·공백은 Windows 가 무시하므로 떼고 본다
    /// </summary>
    public static bool IsRiskyToOpen(string path)
    {
        var ext = Path.GetExtension(path.TrimEnd('.', ' ')).ToLowerInvariant();
        return ext.Length > 0 && RiskyExtensions.Contains(ext);
    }

    private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".dll", ".ocx", ".sys", ".drv",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml", ".psc1", ".vb", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".wsc", ".hta",
        ".msi", ".msp", ".mst", ".msc", ".msix", ".msixbundle", ".appx", ".appxbundle", ".application", ".appref-ms", ".xbap", ".vsto",
        ".lnk", ".url", ".website", ".scf", ".shb", ".shs", ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms",
        ".reg", ".inf", ".chm", ".hlp", ".jar", ".gadget", ".diagcab", ".xll", ".iso", ".img", ".vhd", ".vhdx",
        ".py", ".pyw", ".pyz", ".sh", ".bash", ".pl", ".rb", ".theme", ".themepack",
    };

    /// <summary>받은 파일에 "인터넷에서 받음" 표시(Zone.Identifier, ZoneId=3) — Office 보호된 보기·SmartScreen 이 적용되게. 실패해도 무시</summary>
    private static void MarkFromInternet(string local)
    {
        try { File.WriteAllText(local + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch { /* FAT 등 대체 스트림이 없는 드라이브 */ }
    }

    // ===================== 패널 임시 폴더 (원격 파일 내용이 들어 있음) =====================

    private readonly List<string> _tempDirs = [];

    /// <summary>패널이 닫힐 때 지울 임시 폴더 (열기·끌어내기용으로 받은 파일 — sudo 로 읽은 파일 포함). 편집용은 닫을 때 남긴다 (IsEditTempDir)</summary>
    private void TrackTempDir(string dir) { lock (_tempDirs) _tempDirs.Add(dir); }

    private List<string> TakePanelTempDirs()
    {
        lock (_tempDirs)
        {
            var list = _tempDirs.ToList();
            _tempDirs.Clear();
            return list;
        }
    }

    /// <summary>편집용 임시 폴더(EditTempRoot 아래)인가 — 패널을 닫아도 지우지 않는다</summary>
    private static bool IsEditTempDir(string dir) => IsUnder(dir, EditTempRoot);

    /// <summary>dir 이 root 폴더 아래인가 (대소문자 무시, 경로 구분자 기준 — "sftp-edit2" 같은 이름은 아래로 보지 않음)</summary>
    public static bool IsUnder(string dir, string root)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(dir).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteTempDirs(IEnumerable<string?> dirs)
    {
        foreach (var d in dirs)
            if (!string.IsNullOrEmpty(d))
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { /* 다른 프로그램이 열고 있음 — 하루 뒤 정리 */ }
    }

    private static string OpenTempRoot => Path.Combine(Path.GetTempPath(), "Nubis", "sftp");

    /// <summary>하루 지난 열기용 임시 폴더 정리 (&lt;루트&gt;\&lt;호스트&gt;\&lt;임의 이름&gt;)</summary>
    private static void CleanupOpenTemp()
    {
        try
        {
            if (!Directory.Exists(OpenTempRoot)) return;
            foreach (var hostDir in Directory.GetDirectories(OpenTempRoot))
            {
                foreach (var d in Directory.GetDirectories(hostDir))
                    if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                        try { Directory.Delete(d, true); } catch { }
                try { if (!Directory.EnumerateFileSystemEntries(hostDir).Any()) Directory.Delete(hostDir); } catch { }
            }
        }
        catch { }
    }

    /// <summary>VS Code 실행 파일 (설치돼 있지 않으면 null)</summary>
    public static string? FindVsCode()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>하루 지난 편집 임시 폴더 정리 (지금 편집 중인 것은 남김)</summary>
    private void CleanupEditTemp()
    {
        try
        {
            if (!Directory.Exists(EditTempRoot)) return;
            var inUse = _edits.Values.Select(e => Path.GetDirectoryName(e.Local)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var hostDir in Directory.GetDirectories(EditTempRoot))
            foreach (var d in Directory.GetDirectories(hostDir))
                if (!inUse.Contains(d) && Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>전송 중이 아닐 때 아래 상태 줄에 잠깐 알림</summary>
    private async void ShowStatus(string text)
    {
        if (_busy) return;
        TransferBar.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Value = 1;
        TransferText.Text = text;
        TransferText.ToolTip = text;
        await Task.Delay(7000);
        if (!_busy && TransferText.Text == text) TransferBar.Visibility = Visibility.Collapsed;
    }
}
