using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Renci.SshNet;

namespace Nubis;

public partial class SftpPanel
{
    // ===================== 파일 작업: 새 폴더 · 이름 변경 · 삭제 · 경로 복사 =====================

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(Window.GetWindow(this), L.T("새 폴더"), L.F("{0} 에 만들 폴더 이름", _cwd));
        if (string.IsNullOrEmpty(name)) return;
        if (ValidateRemoteName(name) is { } invalid) { Dialog.Show(Window.GetWindow(this)!, invalid, L.T("새 폴더")); return; }
        try
        {
            var c = await ClientAsync();
            await Task.Run(() => c.CreateDirectory(Combine(_cwd, name)));
            await NavigateAsync(_cwd);
        }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, L.T("새 폴더")); }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not SftpItem item) return;
        var name = PromptDialog.Ask(Window.GetWindow(this), L.T("이름 변경"), item.FullName, item.Name);
        if (string.IsNullOrEmpty(name) || name == item.Name) return;
        // "/" 가 들어가면 다른 폴더로 옮겨지고, ".." 등은 엉뚱한 경로가 된다
        if (ValidateRemoteName(name) is { } invalid) { Dialog.Show(Window.GetWindow(this)!, invalid, L.T("이름 변경")); return; }
        try
        {
            var c = await ClientAsync();
            var newPath = Combine(ParentPath(item.FullName), name);
            // 링크는 경로를 그대로 보낸다 — RenameFile 은 REALPATH 로 링크를 따라가 원본을 옮겨 버린다
            await Task.Run(() =>
            {
                if (item.IsLink) SftpRaw.Rename(c, item.FullName, newPath);
                else c.RenameFile(item.FullName, newPath);
            });
            await NavigateAsync(_cwd);
        }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, L.T("이름 변경")); }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0) return;
        if (_busy) { ShowBusyHint(); return; }
        var dirs = items.Count(i => i.IsDir && !i.IsLink);   // 폴더를 가리키는 링크는 링크만 지운다 (안의 내용은 그대로)
        var msg = L.F("{0}개 항목을 서버에서 삭제할까요?", items.Count) + (dirs > 0 ? L.F("\n(폴더 {0}개는 안의 내용까지 모두 삭제)", dirs) : "") +
                  "\n\n" + string.Join("\n", items.Take(10).Select(i => i.FullName)) + (items.Count > 10 ? "\n..." : "");
        if (Dialog.Show(Window.GetWindow(this)!, msg, L.T("삭제 확인"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_busy) { ShowBusyHint(); return; }   // 확인 창을 띄운 사이 다른 전송이 시작됨
        // 연결을 기다리기 전에 전송 중으로 표시 — 그 사이 다른 작업·사용자 전환이 끼어들지 않게
        var ct = BeginTransfer(L.T("삭제 중..."), 0);
        try
        {
            var c = await ClientAsync();
            await Task.Run(() => { foreach (var i in items) DeleteRecursive(c, i.FullName, i.IsDir && !i.IsLink, i.IsLink, ct); }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, L.T("삭제")); }
        finally { EndTransfer(null); }
        await NavigateAsync(_cwd);
    }

    /// <summary>
    /// 항목 하나씩 지우면서 취소 버튼을 확인한다 (이미 지운 것은 되돌릴 수 없음).
    /// 링크는(폴더를 가리키는 링크 포함) 안으로 들어가지 않고 링크 자체만 지운다 —
    /// DeleteFile 은 REALPATH 로 링크를 따라가 폴더 밖의 원본까지 지우므로 링크에는 쓰지 않는다.
    /// </summary>
    private static void DeleteRecursive(SftpClient c, string path, bool isDir, bool isLink, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isLink) { SftpRaw.Remove(c, path); return; }
        if (!isDir)
        {
            // 일반 파일도 경로를 그대로 지울 수 있으면 그렇게 한다 (목록을 읽은 뒤 링크로 바뀌었어도 원본이 지워지지 않게)
            if (SftpRaw.Supported) SftpRaw.Remove(c, path); else c.DeleteFile(path);
            return;
        }
        foreach (var f in c.ListDirectory(path).Where(f => f.Name is not "." and not ".."))
            DeleteRecursive(c, f.FullName, f.IsDirectory && !f.IsSymbolicLink, f.IsSymbolicLink, ct);
        c.DeleteDirectory(path);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        try { Clipboard.SetText(items.Count > 0 ? string.Join("\n", items.Select(i => i.FullName)) : _cwd); } catch { }
    }
}
