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
            ReleaseEditDir(Path.GetDirectoryName(Local));
        }
    }

    private readonly Dictionary<string, EditSession> _edits = new(StringComparer.Ordinal);
    private static string EditTempRoot => Path.Combine(Path.GetTempPath(), "Nubis", "sftp-edit");

    /// <summary>
    /// 이 프로세스의 모든 패널(탭)이 지금 편집 중인 임시 폴더 — CleanupEditTemp 가 다른 탭의 편집 폴더를 지우지 않게.
    /// (패널마다 자기 _edits 만 보면 하루 넘게 열어 둔 다른 탭의 편집 파일이 지워졌다)
    /// </summary>
    private static readonly HashSet<string> ActiveEditDirs = new(StringComparer.OrdinalIgnoreCase);
    private static void HoldEditDir(string dir) { lock (ActiveEditDirs) ActiveEditDirs.Add(Path.GetFullPath(dir)); }
    private static void ReleaseEditDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        lock (ActiveEditDirs) ActiveEditDirs.Remove(Path.GetFullPath(dir));
    }
    private static bool IsEditDirHeld(string dir) { lock (ActiveEditDirs) return ActiveEditDirs.Contains(Path.GetFullPath(dir)); }

    /// <summary>편집 중인 파일 수</summary>
    public int EditingCount => _edits.Count;

    private async Task EditFileAsync(SftpItem item)
    {
        if (_edits.TryGetValue(item.FullName, out var existing))
        {
            // 이미 편집 중이면 받은 파일을 그대로 다시 연다 (편집기에서 고친 내용을 덮어쓰지 않게)
            if (File.Exists(existing.Local)) { OpenInEditor(existing.Local); return; }
            // 임시 파일이 지워짐 → 옛 감시·디바운스를 정리하고 새로 받는다 (감시가 새지 않게)
            existing.Dispose();
            _edits.Remove(item.FullName);
        }
        if (item.IsSpecial) { ShowStatus(L.F("장치·FIFO·소켓 같은 특수 파일은 열 수 없습니다: {0}", item.Name)); return; }
        if (_busy) { ShowBusyHint(); return; }
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
            // 충돌 확인 기준(시각·크기)은 받기 "전"에 잡는다 — 받는 동안 서버 파일이 바뀌면 첫 저장 때 충돌로 묻게
            // (받은 뒤에 잡으면 받은 내용보다 새 서버 파일을 기준으로 삼아, 그 변경을 말없이 덮어쓴다)
            var attr = await Task.Run(() => c.GetAttributes(item.FullName), ct);
            // 목록을 읽은 뒤 바뀌었거나 링크 대상이 장치 파일인 경우 — /dev/zero 등은 끝없이 받게 된다
            if (attr.IsDirectory || IsSpecialFile(attr.IsSocket, attr.IsNamedPipe, attr.IsCharacterDevice, attr.IsBlockDevice))
                throw new InvalidOperationException(L.F("장치·FIFO·소켓 같은 특수 파일은 열 수 없습니다: {0}", item.Name));
            CleanupEditTemp();
            var host = SafeLocalName(_getAuth()?.Host.DisplayName ?? "sftp");
            dir = Path.Combine(EditTempRoot, host, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, SafeLocalName(item.Name));

            // 받는 중에는 .part 로 — 취소·실패 때 반쯤 받은 파일이 편집 파일로 남지 않게
            var progress = new Progress<Renci.SshNet.DownloadFileProgressReport>(r => SetProgress((long)r.TotalBytesDownloaded, attr.Size));
            var part = local + ".part";
            await using (var fs = File.Create(part))
                await c.DownloadFileAsync(item.FullName, fs, progress, ct);
            // 받는 사이 탭이 닫힘(Close 가 _edits 를 비운 뒤) — 여기서 세션을 만들면 감시가 새고(ActiveEditDirs 영구 보유),
            // 닫힌 탭의 파일이 편집기로 열려 저장해도 아무 데도 올라가지 않는다
            if (_closed) throw new OperationCanceledException();
            File.Move(part, local);
            MarkFromInternet(local);

            var s = new EditSession { Remote = item.FullName, Local = local, RemoteMtime = attr.LastWriteTimeUtc, RemoteSize = attr.Size, Hash = HashFile(local) };
            HoldEditDir(dir);
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
            var attr = await WithClientLockAsync(c => Task.Run(() => TryGetAttributes(c, s.Remote)));
            if (attr != null && (attr.LastWriteTimeUtc != s.RemoteMtime || attr.Size != s.RemoteSize) &&
                Dialog.Show(Window.GetWindow(this)!,
                    L.F("편집을 시작한 뒤 서버의 파일이 바뀌었습니다.\n\n{0}\n서버: {1:yyyy-MM-dd HH:mm:ss} · {2}\n\n내가 편집한 내용으로 덮어쓸까요? (아니요를 누르면 이번 저장은 올리지 않습니다)",
                        s.Remote, attr.LastWriteTime, HumanSize(attr.Size)),
                    L.T("편집 충돌"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                s.Hash = hash;   // 같은 내용으로 다시 묻지 않음
                ShowStatus(L.F("올리지 않음: {0} (서버 파일이 바뀌어 있음)", name), keepIfBusy: true);
                return;
            }
            // 2) 임시 이름으로 올린 뒤 바꿔치기 (UploadOneAsync) — 중간에 끊겨도 원래 파일(/etc 설정 등)은 그대로.
            //    올리는 동안 연결 잠금을 잡아, 사용자 전환(RunAsAsync)·재연결·패널 닫기가 쓰는 중인 연결을 닫지 않게 한다
            //    채널이 멈추면 잠금을 영원히 잡고 있지 않게 시간 제한 (2분 + 크기 비례) — 목록·사용자 전환·연결 열기가 막히지 않게
            using var timeout = new CancellationTokenSource(EditUploadTimeout(data.Length));
            await WithClientLockAsync(async c =>
            {
                await UploadOneAsync(c, () => new MemoryStream(data, writable: false), s.Remote,
                    new Progress<Renci.SshNet.UploadFileProgressReport>(_ => { }), timeout.Token);
                var a = await Task.Run(() => c.GetAttributes(s.Remote));
                // 기준값은 잠금을 놓기 전에 바꾼다 — 그 사이 탭을 닫으면 Close 가 옛 기준으로 "서버 파일이 바뀜"이라 잘못 판단하지 않게
                s.RemoteMtime = a.LastWriteTimeUtc;
                s.RemoteSize = a.Size;
                s.Hash = hash;
                return a;
            });
            ShowStatus(L.F("저장됨 → 서버에 올림: {0}  ({1:HH:mm:ss}, {2})", name, DateTime.Now, HumanSize(data.Length)));
            if (ParentPath(s.Remote) == _cwd && !_busy) await NavigateAsync(_cwd);   // 목록의 크기·시각 갱신
        }
        // 실패는 전송 중이어도 버리지 않는다 — 전송이 끝날 때 결과 문구에 붙여 보인다 (저장이 서버에 안 갔는데 모르는 일이 없게)
        catch (OperationCanceledException) when (!_closed) { ShowStatus(L.F("자동 업로드 시간 초과: {0} (다시 저장하면 재시도)", name), keepIfBusy: true); }
        catch (Exception) when (_closed) { /* 탭이 닫힘 — Close 가 마지막으로 올리고 실패하면 알린다 */ }
        catch (Exception ex) { ShowStatus(L.F("자동 업로드 실패: {0} — {1} (다시 저장하면 재시도)", name, ex.Message), keepIfBusy: true); }
        finally { s.Uploading = false; }
    }

    /// <summary>
    /// 탭을 닫을 때(연결 잠금을 잡은 백그라운드에서): 저장했지만 아직 올리지 않은 편집을 올린다.
    /// 서버 파일이 바뀌었으면 묻지 못하므로 올리지 않는다. 올리지 못했으면 사용자에게 보일 사유(로컬 파일 위치 포함), 아니면 null
    /// </summary>
    /// <param name="locked">연결 잠금을 잡았음 — 못 잡았으면(다른 업로드가 멈춰 있음) 같은 연결에 겹쳐 쓰지 않고 올리지 못한 것으로 알린다</param>
    /// <param name="ct">닫기 전체 시간 제한 (CloseBudget) — 지나면 남은 것은 올리지 않고 알린다</param>
    private static async Task<string?> FlushEditOnCloseAsync(SftpClient? c, EditSession s, bool locked, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(s.Local)) return null;
            byte[] data;
            using (var fs = new FileStream(s.Local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                data = ms.ToArray();
            }
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
            if (hash == s.Hash) return null;   // 이미 올렸거나 바뀐 것 없음
            if (!locked || ct.IsCancellationRequested)
                return L.F("{0}\n→ 시간 안에 올리지 못했습니다. ({1})", s.Remote, s.Local);
            if (c is not { IsConnected: true })
                return L.F("{0}\n→ 연결이 끊겨 올리지 못했습니다. ({1})", s.Remote, s.Local);
            // GetAttributes 는 취소를 모른다(응답 대기 최대 60초) — 기다리기만 멈추고, 연결은 Close 가 곧 닫는다
            var attr = await Task.Run(() => TryGetAttributes(c, s.Remote)).WaitAsync(ct);
            if (attr != null && (attr.LastWriteTimeUtc != s.RemoteMtime || attr.Size != s.RemoteSize))
                return L.F("{0}\n→ 편집을 시작한 뒤 서버 파일이 바뀌어 올리지 않았습니다. ({1})", s.Remote, s.Local);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(EditUploadTimeout(data.Length));
            await UploadOneAsync(c, () => new MemoryStream(data, writable: false), s.Remote,
                new Progress<Renci.SshNet.UploadFileProgressReport>(_ => { }), timeout.Token);
            s.Hash = hash;
            return null;
        }
        catch (OperationCanceledException) { return L.F("{0}\n→ 시간 안에 올리지 못했습니다. ({1})", s.Remote, s.Local); }
        catch (Exception ex) { return $"{s.Remote}\n→ {ex.Message} ({s.Local})"; }
    }

    /// <summary>속성 읽기 — 없으면 null. Exists + GetAttributes 를 따로 부르면 서버 왕복이 두 배라 한 번에</summary>
    private static SftpFileAttributes? TryGetAttributes(SftpClient c, string path)
    {
        try { return c.GetAttributes(path); }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { return null; }
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
        // 빠져 있던 것: 원격 데스크톱 연결 파일(임의 서버로 드라이브 공유 연결)·Windows Sandbox 설정·MSH/PS2 스크립트·
        // 스크립틀릿·앱 설치 파일·Access DB(매크로)·바이트코드 등
        ".rdp", ".wsb", ".ws", ".sct", ".msh", ".msh1", ".msh2", ".mshxml", ".msh1xml", ".msh2xml", ".ps2", ".ps2xml", ".psc2",
        ".appinstaller", ".jnlp", ".ade", ".adp", ".mda", ".mdb", ".mde", ".accde", ".ins", ".isp", ".job", ".pyc", ".pyo", ".mof",
        ".mht", ".mhtml",
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
            if (!string.IsNullOrEmpty(d)) ForceDeleteDir(d);
    }

    /// <summary>폴더 삭제 — 읽기 전용 사본(읽기 전용 속성)이 있으면 속성을 풀고 다시. 다른 프로그램이 열고 있으면 하루 뒤 정리</summary>
    private static void ForceDeleteDir(string d)
    {
        try { if (Directory.Exists(d)) Directory.Delete(d, true); }
        catch (UnauthorizedAccessException)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(d, true);
            }
            catch { }
        }
        catch { }
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
                        ForceDeleteDir(d);   // 읽기 전용 사본이 들어 있다
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

    /// <summary>
    /// 하루 지난 편집 임시 폴더 정리. 이 프로세스의 어느 탭이든 편집 중인 폴더(ActiveEditDirs)와
    /// 하루 안에 저장된 파일이 있는 폴더(다른 Nubis 창·편집기에 열린 채 저장한 것 — 파일을 직접 쓰면 폴더 시각은 안 바뀐다)는 남긴다
    /// </summary>
    private static void CleanupEditTemp()
    {
        try
        {
            if (!Directory.Exists(EditTempRoot)) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var hostDir in Directory.GetDirectories(EditTempRoot))
            foreach (var d in Directory.GetDirectories(hostDir))
                if (!IsEditDirHeld(d) && Directory.GetLastWriteTimeUtc(d) < cutoff && LatestFileWriteUtc(d) < cutoff)
                    ForceDeleteDir(d);
        }
        catch { }
    }

    private static DateTime LatestFileWriteUtc(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch { return DateTime.MaxValue; }   // 못 읽으면 지우지 않는다
    }

    /// <summary>
    /// 아래 상태 줄에 잠깐 알림. 전송 중이면 기본은 버리고(진행 표시를 가리지 않게),
    /// keepIfBusy 면 전송이 끝날 때 결과 문구에 붙인다 (자동 업로드 실패처럼 놓치면 안 되는 알림)
    /// </summary>
    private async void ShowStatus(string text, bool keepIfBusy = false)
    {
        if (_busy)
        {
            if (keepIfBusy && !(_pendingNotice?.Contains(text, StringComparison.Ordinal) ?? false))
                _pendingNotice = AppendNotice(_pendingNotice, text);
            return;
        }
        var seq = ++_statusSeq;
        TransferBar.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Value = 1;
        TransferText.Text = text;
        TransferText.ToolTip = text;
        await Task.Delay(7000);
        if (!_busy && seq == _statusSeq) TransferBar.Visibility = Visibility.Collapsed;
    }
}
