using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Nubis;

/// <summary>호스트 편집 패널: 점프 서버(ProxyJump) 고르기 · 안내 (MainWindow.HostEditor.cs 에서 분리)</summary>
public partial class MainWindow
{
    // 편집 중인 점프 서버 (ProxyJump)
    private Guid? _edJumpId;

    private void UpdateJumpUi()
    {
        var j = _edJumpId is { } id ? _hosts.FirstOrDefault(x => x.Id == id) : null;
        EdJumpButton.Content = _edJumpId == null ? L.T("없음 (바로 연결)")
            : j == null ? L.T("삭제된 호스트 — 다른 호스트를 고르거나 없음으로")
            : $"{j.DisplayName}  ({j.Address})";
        // 이 호스트를 점프 서버로 쓰는 호스트가 있으면 — 점프는 한 단계만이라 그 호스트들은 여기서 고른 점프 서버를 거치지 않는다
        var users = _edJumpId == null ? 0 : JumpUsers(_hosts, _editing);
        JumpWarning.Text = users > 0
            ? L.F("이 호스트를 점프 서버로 쓰는 호스트가 {0}개 있습니다. 점프 서버는 한 단계만 지원해, 그 호스트들은 여기서 고른 점프 서버를 거치지 않고 이 호스트에 바로 연결합니다.", users)
            : "";
        JumpWarning.Visibility = users > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>editing 을 점프 서버로 쓰는 (자기 자신이 아닌) 호스트 수. 새 호스트·복제는 0</summary>
    internal static int JumpUsers(IEnumerable<HostEntry> all, HostEntry? editing) =>
        editing == null ? 0 : all.Count(x => x.JumpHostId == editing.Id && !ReferenceEquals(x, editing));

    /// <summary>점프 서버로 쓸 SSH 호스트 고르기 (자기 자신 제외)</summary>
    private void OnPickJump(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = EdJumpButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var none = new MenuItem { Header = L.T("없음 (바로 연결)") };
        none.Click += (_, _) => { _edJumpId = null; UpdateJumpUi(); };
        menu.Items.Add(none);
        menu.Items.Add(new Separator());
        foreach (var h in _hosts.Where(x => x.Protocol == Protocol.Ssh && !ReferenceEquals(x, _editing))
                                .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            // 점프 서버는 한 단계만 지원 (AttachJump) — 자기도 점프 서버를 거치는 호스트는 점프 서버로 쓰면 그 점프 없이 직접 붙어
            // 연결되지 않으므로 고르지 못하게 한다 (이미 지정돼 있으면 그대로 보여 준다)
            var nested = h.JumpHostId != null && h.Id != _edJumpId;
            var item = new MenuItem
            {
                Header = MenuText($"{h.DisplayName}  ({h.Address})" + (h.Group.Length > 0 ? $"  · {h.Group}" : "")),
                IsChecked = h.Id == _edJumpId,
                IsEnabled = !nested,
            };
            if (nested)
            {
                item.ToolTip = L.T("이 호스트도 점프 서버를 거쳐 접속합니다. 점프 서버는 한 단계만 지원합니다.");
                ToolTipService.SetShowOnDisabled(item, true);
            }
            var id = h.Id;
            item.Click += (_, _) => { _edJumpId = id; UpdateJumpUi(); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 2) menu.Items.Add(new MenuItem { Header = L.T("점프 서버로 쓸 SSH 호스트가 없습니다"), IsEnabled = false });
        menu.IsOpen = true;
    }

    /// <summary>메뉴 항목 글자 그대로 (MenuItem 은 '_' 를 단축키 표시로 먹어 web_01 이 web01 로 보였다)</summary>
    internal static string MenuText(string s) => s.Replace("_", "__");
}
