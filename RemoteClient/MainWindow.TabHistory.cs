using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Nubis;

public partial class MainWindow
{
    // =================== 닫은 탭 다시 열기 · SFTP 편집 중 닫기 확인 ===================

    private readonly ClosedTabHistory _closedTabs = new();

    /// <summary>닫는 탭을 "닫은 탭 다시 열기" 기록에 남긴다. 목록에 저장된 호스트와 로컬 PowerShell 만 (빠른 접속은 계정을 남기지 않으려고 제외)</summary>
    private void RememberClosedTab(SessionTab tab)
    {
        if (tab.Protocol == Protocol.Local) _closedTabs.Push(new ClosedTabHistory.Entry(null, null));
        else if (tab.Host != null && _hosts.Contains(tab.Host)) _closedTabs.Push(new ClosedTabHistory.Entry(tab.Host.Id, TabUsePublic(tab)));
    }

    private bool HostExists(Guid id) => _hosts.Any(h => h.Id == id);

    private bool CanReopenClosedTab => _closedTabs.HasAny(HostExists);

    /// <summary>마지막으로 닫은 탭을 같은 호스트·같은 주소(공인/사설)로 다시 연다 (Ctrl+Shift+R). 그 사이 지운 호스트는 건너뛴다</summary>
    private void ReopenClosedTab()
    {
        if (_closedTabs.Pop(HostExists) is not { } e) return;
        if (e.HostId is not { } id) { OpenLocalTerminal(); return; }
        if (_hosts.FirstOrDefault(h => h.Id == id) is { } host) Connect(host, e.UsePublic);
    }

    /// <summary>SSH 탭의 SFTP 패널에서 편집기로 열어 둔(저장하면 자동 업로드되는) 파일 수</summary>
    private static int SftpEditingCount(SessionTab tab) =>
        tab.View is SshSessionView ssh ? FindSftpPanels(ssh).Sum(p => p.EditingCount) : 0;

    private static IEnumerable<SftpPanel> FindSftpPanels(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is SftpPanel p) { yield return p; continue; }
            foreach (var inner in FindSftpPanels(child)) yield return inner;
        }
    }

    /// <summary>
    /// 닫을 탭들에 SFTP 로 편집 중인 파일이 있으면 묻는다 (탭 닫기 확인 설정과 상관없이 — 이후 저장분이 조용히 사라지지 않게).
    /// 편집 중인 파일이 없으면 null(이 확인은 건너뜀), 있으면 사용자가 닫기를 골랐는지.
    /// </summary>
    private bool? ConfirmCloseWithSftpEdits(IEnumerable<SessionTab> tabs, bool closingApp)
    {
        var editing = tabs.Sum(SftpEditingCount);
        if (editing == 0) return null;
        var msg = L.F("SFTP 로 편집 중인 파일이 {0}개 있습니다.\n지금까지 저장한 내용은 서버에 올리지만, 닫은 뒤에 편집기에서 저장한 내용은 올라가지 않습니다.", editing)
                  + "\n\n" + (closingApp ? L.T("Nubis 를 닫을까요?") : L.T("탭을 닫을까요?"));
        return Dialog.Show(this, msg, closingApp ? "Nubis" : L.T("탭 닫기"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}

/// <summary>최근에 닫은 탭 (호스트 Id + 공인/사설 선택). 최대 <see cref="Max"/>개, 마지막에 닫은 것부터 다시 연다</summary>
internal sealed class ClosedTabHistory
{
    /// <param name="HostId">null = 로컬 PowerShell</param>
    /// <param name="UsePublic">탭을 열 때 고른 공인/사설 IP (null = 호스트 설정대로)</param>
    internal readonly record struct Entry(Guid? HostId, bool? UsePublic);

    public const int Max = 10;
    private readonly List<Entry> _items = new();

    public int Count => _items.Count;

    public void Push(Entry e)
    {
        _items.Add(e);
        if (_items.Count > Max) _items.RemoveAt(0);
    }

    /// <summary>다시 열 수 있는 마지막 항목을 꺼낸다. 그 사이 지운 호스트의 항목은 버리고 그 앞 것으로</summary>
    public Entry? Pop(Func<Guid, bool> hostExists)
    {
        while (_items.Count > 0)
        {
            var e = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            if (e.HostId is not { } id || hostExists(id)) return e;
        }
        return null;
    }

    public bool HasAny(Func<Guid, bool> hostExists) => _items.Any(e => e.HostId is not { } id || hostExists(id));
}
