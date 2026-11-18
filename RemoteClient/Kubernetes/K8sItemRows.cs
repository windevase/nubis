using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;

namespace Nubis;

/// <summary>
/// 리소스 패널의 행 (상태 색·이름 표시). 자동 새로고침 때 행을 새로 만들지 않고 내용만 바꿔
/// 키보드 포커스 · 선택 · 툴팁 · 스크롤 위치가 그대로 남게 한다
/// </summary>
public sealed class K8sItemRow : INotifyPropertyChanged
{
    private K8sItem _item;
    private string _displayName;
    private Brush _statusBrush;

    public K8sItemRow(K8sItem item, string displayName, Brush statusBrush)
    {
        _item = item;
        _displayName = displayName;
        _statusBrush = statusBrush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>행을 가리키는 키 (네임스페이스/이름)</summary>
    public string Key => K8sRowSync.KeyOf(_item);
    public K8sItem Item => _item;
    public string DisplayName => _displayName;
    public Brush StatusBrush => _statusBrush;
    public string ToolTipText => _item.ToolTipText;
    // 화면 바인딩은 이 행(INotifyPropertyChanged)의 속성에만 건다 — 알림 없는 record 속성에 OneWay 로 걸지 않게
    public string Ready => _item.Ready;
    public string Status => _item.Status;
    public string Restarts => _item.Restarts;
    public string Age => _item.Age;

    /// <summary>내용이 바뀌었을 때만 바꾸고 알린다</summary>
    public void Update(K8sItem item, string displayName, Brush statusBrush)
    {
        if (K8sResources.SameItem(_item, item) && _displayName == displayName && ReferenceEquals(_statusBrush, statusBrush)) return;
        _item = item;
        _displayName = displayName;
        _statusBrush = statusBrush;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));   // 모든 속성
    }
}

/// <summary>목록을 통째 바꾸지 않고 필요한 만큼만 고친다 (화면 무관, K8sPanelLogicTests)</summary>
public static class K8sRowSync
{
    public static string KeyOf(K8sItem i) => i.Namespace + "/" + i.Name;

    /// <summary>
    /// target 을 wanted 순서·내용으로 맞춘다. 같은 키의 기존 행은 다시 쓰고(update 로 내용만 바꿈) 없는 것만 만든다.
    /// 반환: 목록 구성(추가·삭제·이동)이 바뀌었는가
    /// </summary>
    public static bool Apply<TRow, TData>(IList<TRow> target, IReadOnlyList<TData> wanted, Func<TRow, string> rowKey, Func<TData, string> dataKey,
        Func<TData, TRow> create, Action<TRow, TData> update)
    {
        var structural = false;
        // 1) 없어질 행(과 같은 키의 중복 행)을 먼저 뒤에서부터 뺀다 — 앞에서부터 맞추면 행 하나가 빠질 때
        //    그 아래 행이 전부 한 칸씩 Move 된다 (행 수천 개면 새로고침마다 Move 수천 번)
        var wantedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in wanted) wantedKeys.Add(dataKey(w));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keep = new bool[target.Count];
        for (var i = 0; i < target.Count; i++) { var k = rowKey(target[i]); keep[i] = wantedKeys.Contains(k) && seen.Add(k); }
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep[i]) { target.RemoveAt(i); structural = true; }

        // 2) 남은 행은 모두 wanted 에 있다 → 앞에서부터 맞추면 추가는 Insert 한 번, 이동은 순서가 실제로 바뀐 행만
        var existing = new Dictionary<string, TRow>(StringComparer.Ordinal);
        foreach (var r in target) existing.TryAdd(rowKey(r), r);
        for (var i = 0; i < wanted.Count; i++)
        {
            var key = dataKey(wanted[i]);
            if (i < target.Count && rowKey(target[i]) == key)
            {
                update(target[i], wanted[i]);
                continue;
            }
            structural = true;
            if (existing.TryGetValue(key, out var row))
            {
                var at = IndexOf(target, row, i + 1);
                if (at >= 0)
                {
                    if (target is System.Collections.ObjectModel.ObservableCollection<TRow> oc) oc.Move(at, i);
                    else { target.RemoveAt(at); target.Insert(i, row); }
                    update(row, wanted[i]);
                    continue;
                }
            }
            target.Insert(i, create(wanted[i]));
        }
        // wanted 에 같은 키가 두 번 있으면 기존 행은 앞쪽에만 쓰이므로 여기서 남는 것은 없다 (안전망)
        while (target.Count > wanted.Count) { target.RemoveAt(target.Count - 1); structural = true; }
        return structural;
    }

    /// <summary>
    /// 추가·삭제할 행이 많으면(검색어 변경 · 롤아웃 등) 하나씩 고치는 것보다 새로 만드는 편이 빠르다 — 그 판단
    /// </summary>
    public static bool ShouldRebuild(IEnumerable<string> currentKeys, IEnumerable<string> wantedKeys, int maxEdits = 100)
    {
        var current = new HashSet<string>(currentKeys, StringComparer.Ordinal);
        var wanted = new HashSet<string>(wantedKeys, StringComparer.Ordinal);
        var edits = 0;
        foreach (var k in wanted) if (!current.Contains(k) && ++edits > maxEdits) return true;
        foreach (var k in current) if (!wanted.Contains(k) && ++edits > maxEdits) return true;
        return false;
    }

    private static int IndexOf<TRow>(IList<TRow> list, TRow row, int from)
    {
        for (var i = from; i < list.Count; i++)
            if (ReferenceEquals(list[i], row)) return i;
        return -1;
    }
}
