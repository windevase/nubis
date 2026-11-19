using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Nubis;

/// <summary>호스트 편집 패널: SSH 로컬 포트 포워딩 규칙 목록 (저장 전까지는 호스트에 반영하지 않음)</summary>
public partial class MainWindow
{
    // 편집 중인 포워딩 규칙 (사본)
    private List<PortForwardRule> _edForwards = new();
    private PortForwardForm? _edForwardForm;

    /// <summary>편집 패널을 열 때 규칙을 사본으로 불러온다</summary>
    private void LoadForwardEditor(HostEntry? h)
    {
        _edForwards = h?.PortForwards?.Select(r => r.Clone()).ToList() ?? new();
        if (_edForwardForm == null)
        {
            _edForwardForm = new PortForwardForm(L.T("추가")) { Submit = AddEditorForward };
            EdForwardFormHost.Content = _edForwardForm;
        }
        _edForwardForm.Clear();
        RenderEditorForwards();
    }

    /// <summary>입력 칸의 규칙을 목록에 추가 — 목록 전체 검사(켜진 규칙끼리 로컬 포트 중복·개수)를 통과해야 한다</summary>
    private string? AddEditorForward(PortForwardRule rule)
    {
        var next = _edForwards.Append(rule).ToList();
        if (PortForwards.CheckList(next) is { } err) return err;
        _edForwards = next;
        RenderEditorForwards();
        return null;
    }

    private void RenderEditorForwards()
    {
        EdForwardList.Children.Clear();
        foreach (var r in _edForwards)
        {
            var rule = r;
            // 편집 패널은 좁아 로컬 쪽은 포트만 (늘 127.0.0.1 — 아래 안내문)
            var row = new PortForwardRow(rule.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PortForwards.TargetAddress(rule.TargetHost, rule.TargetPort), rule.Name);
            row.SetState(rule.Enabled ? PortForwardRowState.Open : PortForwardRowState.Off,
                rule.Enabled ? L.T("연결하면 자동으로 엶") : L.T("꺼짐"));
            row.AddAction("", L.T("고치기 (아래 칸으로 꺼냄)"), () =>
            {
                _edForwards.Remove(rule);
                RenderEditorForwards();
                _edForwardForm?.Load(rule);
            });
            row.AddAction("", L.T("삭제"), () =>
            {
                _edForwards.Remove(rule);
                RenderEditorForwards();
            });
            row.AddToggle(rule.Enabled, L.T("연결하면 자동으로 열기"), on =>
            {
                rule.Enabled = on;
                // 켜면서 같은 로컬 포트를 켜 둔 규칙이 생기면 되돌린다
                if (on && PortForwards.CheckList(_edForwards) is { } err)
                {
                    rule.Enabled = false;
                    _edForwardForm?.ShowError(err);
                }
                RenderEditorForwards();
            });
            EdForwardList.Children.Add(row);
        }
        EdForwardList.Visibility = _edForwards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>저장 전 확인: 칸에 적고 추가하지 않은 값이 있거나 목록이 잘못됐으면 오류 문구</summary>
    private string? CheckEditorForwards(bool ssh)
    {
        if (!ssh) return null;
        if (_edForwardForm?.HasInput == true) return L.T("포트 포워딩 칸에 추가하지 않은 값이 있습니다. '추가' 를 누르거나 칸을 비우세요.");
        return PortForwards.CheckList(_edForwards);
    }
}
