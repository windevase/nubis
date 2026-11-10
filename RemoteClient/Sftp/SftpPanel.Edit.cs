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
        try
        {
            var c = await ClientAsync();
            CleanupEditTemp();
            var host = SafeLocalName(_getAuth()?.Host.DisplayName ?? "sftp");
            var dir = Path.Combine(EditTempRoot, host, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, SafeLocalName(item.Name));

            var ct = BeginTransfer(L.F("편집용으로 받는 중: {0}", item.Name), item.Size);
            var progress = new Progress<Renci.SshNet.DownloadFileProgressReport>(r => SetProgress((long)r.TotalBytesDownloaded, item.Size));
            await using (var fs = File.Create(local))
                await c.DownloadFileAsync(item.FullName, fs, progress, ct);
            var attr = await Task.Run(() => c.GetAttributes(item.FullName), ct);

            var s = new EditSession { Remote = item.FullName, Local = local, RemoteMtime = attr.LastWriteTimeUtc, RemoteSize = attr.Size, Hash = HashFile(local) };
            Watch(s);
            _edits[item.FullName] = s;
            EndTransfer(L.F("편집 중: {0} — 저장하면 서버에 자동으로 올립니다", item.Name));
            OpenInEditor(local);
        }
        catch (OperationCanceledException) { EndTransfer(L.T("편집 열기를 취소했습니다.")); }
        catch (Exception ex) { EndTransfer(L.F("편집 열기 실패: {0}", ex.Message)); }
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
            var c = await ClientAsync();
            var attr = await Task.Run(() => c.Exists(s.Remote) ? c.GetAttributes(s.Remote) : null);
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
            using (var up = new MemoryStream(data))
                await c.UploadFileAsync(up, s.Remote, true, new Progress<Renci.SshNet.UploadFileProgressReport>(_ => { }), CancellationToken.None);
            var after = await Task.Run(() => c.GetAttributes(s.Remote));
            s.RemoteMtime = after.LastWriteTimeUtc;
            s.RemoteSize = after.Size;
            s.Hash = hash;
            ShowStatus(L.F("저장됨 → 서버에 올림: {0}  ({1:HH:mm:ss}, {2})", name, DateTime.Now, HumanSize(data.Length)));
            if (ParentPath(s.Remote) == _cwd && !_busy) await NavigateAsync(_cwd);   // 목록의 크기·시각 갱신
        }
        catch (Exception ex) { ShowStatus(L.F("자동 업로드 실패: {0} — {1} (다시 저장하면 재시도)", name, ex.Message)); }
        finally { s.Uploading = false; }
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

    /// <summary>설정의 편집기로 연다 (비우면 Windows 기본 프로그램, 연결된 프로그램이 없으면 메모장)</summary>
    private void OpenInEditor(string local)
    {
        var editor = SettingsStore.Current.SftpEditor.Trim().Trim('"');
        try
        {
            if (editor.Length > 0) Process.Start(new ProcessStartInfo(editor, $"\"{local}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowStatus(L.F("편집기를 열지 못했습니다: {0}", ex.Message)); }
        }
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
