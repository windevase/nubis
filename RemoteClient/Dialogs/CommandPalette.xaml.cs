using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nubis;

/// <summary>팔레트 항목을 실행할 때 함께 누른 키</summary>
internal enum PaletteModifier
{
    None,
    /// <summary>Shift/Alt+Enter — 호스트를 공인 IP 로 연결 (공인 IP 가 있을 때)</summary>
    PublicIp,
    /// <summary>Ctrl+Enter — 호스트 편집</summary>
    Edit,
}

/// <summary>
/// 명령 팔레트 (Ctrl+K). 메인 창 가운데 위쪽에 뜨는 소유된 별도 창 — 터미널(WebView2) · RDP 위에도 보인다.
/// · 항목은 열 때 한 번 모은다 (MainWindow.BuildPaletteItems) → 검색 인덱스를 만들고 글자마다 바로 거른다 (디바운스 없음)
/// · 실행은 창을 닫은 뒤 메인 창 디스패처에서 (MainWindow.ExecutePaletteItem) — 실행이 띄우는 대화상자가 팔레트 뒤로 가지 않게
/// · 포커스를 잃으면(다른 곳 클릭 · 다른 앱) 닫힌다. 한 번에 하나만 (다시 부르면 이미 떠 있는 것을 앞으로)
/// · 누수 방지: 정적 필드는 떠 있는 창 하나(<see cref="_current"/>)뿐이고 닫히면 비운다. 바깥 개체의 이벤트는 구독하지 않는다.
///   닫힐 때 결과 목록 · 인덱스(호스트 · 탭 · 동작 대리자 참조)를 모두 놓는다
/// </summary>
public partial class CommandPalette : Window
{
    private static CommandPalette? _current;
    private static readonly Color DwmBorder = Color.FromRgb(0x3A, 0x3E, 0x48);

    private MainWindow? _owner;
    private PaletteIndex? _index;
    private readonly List<PaletteItem> _scratch = new(PaletteIndex.DefaultLimit);
    private readonly ResultList _results = new();
    private bool _closing;
    private bool _closed;
    private bool _executed;
    // 닫기 전에 포커스를 잃었는지 (다른 곳 클릭 · 다른 앱으로 전환) — Esc 로 닫을 때 생기는 비활성화는 세지 않는다
    private bool _deactivated;

    private CommandPalette() => InitializeComponent();

    /// <summary>팔레트를 연다 (이미 떠 있으면 앞으로 가져와 검색창에 포커스)</summary>
    public static void Show(MainWindow owner)
    {
        if (_current is { _closing: false } open)
        {
            open.Activate();
            open.QueryBox.Focus();
            open.QueryBox.SelectAll();
            return;
        }
        if (!owner.IsLoaded || owner.WindowState == WindowState.Minimized) return;

        var p = new CommandPalette { _owner = owner, Owner = owner };
        _current = p;
        p._index = new PaletteIndex(owner.BuildPaletteItems());
        p.Results.ItemsSource = p._results;
        p.Place(owner);
        p.Refresh();
        p.SourceInitialized += OnPaletteSourceInitialized;
        p.Loaded += (_, _) => p.QueryBox.Focus();
        p.Show();
        p.Activate();
    }

    private static void OnPaletteSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not CommandPalette p) return;
        // Windows 11: DWM 이 둥근 모서리 · 테두리를 그린다 (WPF 머리카락 테두리는 끈다)
        if (WindowBackdrop.RoundCorners(new WindowInteropHelper(p).Handle, DwmBorder))
            p.Frame.BorderThickness = new Thickness(0);
    }

    /// <summary>메인 창 가운데, 위에서 조금 내려온 자리 (VS Code 와 비슷). 창이 좁으면 폭을 줄인다</summary>
    private void Place(Window owner)
    {
        var w = Math.Max(360, Math.Min(Width, owner.ActualWidth - 48));
        Width = w;
        // 최대화된 창의 Left/Top 은 화면 밖 테두리 기준이라, 실제 그려진 위치(화면 좌표 → DIP)로 맞춘다
        var origin = new Point(owner.Left, owner.Top);
        if (PresentationSource.FromVisual(owner) is { CompositionTarget: { } ct })
            origin = ct.TransformFromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
        Left = origin.X + (owner.ActualWidth - w) / 2;
        Top = origin.Y + Math.Clamp(owner.ActualHeight * 0.12, 48, 140);
    }

    // ----- 검색 -----

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_index == null) return;
        _index.Search(QueryBox.Text, _scratch);
        _results.ReplaceAll(_scratch);
        _scratch.Clear();
        var empty = _results.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty)
        {
            Results.SelectedIndex = 0;
            Results.ScrollIntoView(_results[0]);
        }
    }

    private void Move(int delta)
    {
        var n = _results.Count;
        if (n == 0) return;
        var i = Results.SelectedIndex;
        // 끝에서 한 칸씩 넘기면 반대쪽으로 돈다. 페이지 이동(여러 칸)은 끝에서 멈춘다
        i = Math.Abs(delta) == 1
            ? ((i < 0 ? (delta > 0 ? -1 : 0) : i) + delta + n) % n
            : Math.Clamp((i < 0 ? 0 : i) + delta, 0, n - 1);
        Results.SelectedIndex = i;
        Results.ScrollIntoView(_results[i]);
    }

    // ----- 키보드 · 마우스 -----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        switch (key)
        {
            case Key.Escape: CloseOnce(); e.Handled = true; return;
            case Key.Down: Move(+1); e.Handled = true; return;
            case Key.Up: Move(-1); e.Handled = true; return;
            case Key.PageDown: Move(+8); e.Handled = true; return;
            case Key.PageUp: Move(-8); e.Handled = true; return;
            case Key.Tab: e.Handled = true; return;   // 포커스는 늘 검색창
            case Key.Enter:
                var mod = mods.HasFlag(ModifierKeys.Control) ? PaletteModifier.Edit
                    : mods.HasFlag(ModifierKeys.Shift) || mods.HasFlag(ModifierKeys.Alt) ? PaletteModifier.PublicIp
                    : PaletteModifier.None;
                if (Results.SelectedItem is PaletteItem item) Run(item, mod);
                e.Handled = true;
                return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnResultsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d ||
            ItemsControl.ContainerFromElement(Results, d) is not ListBoxItem { DataContext: PaletteItem item }) return;
        var mods = Keyboard.Modifiers;
        Run(item, mods.HasFlag(ModifierKeys.Control) ? PaletteModifier.Edit
            : mods.HasFlag(ModifierKeys.Shift) ? PaletteModifier.PublicIp : PaletteModifier.None);
        e.Handled = true;
    }

    /// <summary>창을 먼저 닫고, 메인 창이 다시 활성화된 뒤 실행한다</summary>
    private void Run(PaletteItem item, PaletteModifier mod)
    {
        var owner = _owner;
        _executed = true;
        CloseOnce();
        if (owner == null) return;
        owner.Activate();
        owner.Dispatcher.BeginInvoke(() => owner.ExecutePaletteItem(item, mod), DispatcherPriority.Input);
    }

    // ----- 닫기 -----

    /// <summary>다른 곳을 누르거나 다른 앱으로 가면 닫는다</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_closing) _deactivated = true;
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        // Deactivated 처리 중에 바로 Close 하면 WPF 가 예외를 낼 수 있어 한 박자 늦춘다
        Dispatcher.BeginInvoke(() => { if (!_closed) Close(); }, DispatcherPriority.Normal);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        if (ReferenceEquals(_current, this)) _current = null;
        SourceInitialized -= OnPaletteSourceInitialized;
        // 호스트 · 탭(세션 뷰) · 동작 대리자(메인 창) 참조를 모두 놓는다
        Results.ItemsSource = null;
        _results.ReplaceAll(Array.Empty<PaletteItem>());
        _scratch.Clear();
        _index = null;
        _owner = null;
        // 실행 없이 닫혔으면(Esc · 바깥 클릭) 메인 창에 포커스를 돌려준다.
        // 다른 앱으로 전환해서 닫힌 경우에는 메인 창을 앞으로 끌어오지 않는다 (OnPaletteDismissed 가 활성 여부를 다시 본다)
        if (Owner is MainWindow { IsVisible: true } o && !_executed)
        {
            // 메인 창이 닫히는 중(소유 창이 함께 닫힘)이면 Activate 가 예외를 낸다 — 그때는 할 일이 없다
            try
            {
                if (!_deactivated) o.Activate();
                o.OnPaletteDismissed();
            }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// 결과 목록: 다시 채울 때 Reset 알림 한 번만 (ObservableCollection 의 Clear + Add 50번 = 알림 51번 대신).
    /// 같은 개체를 계속 쓰므로 ListBox 의 컬렉션 뷰도 하나만 생긴다
    /// </summary>
    private sealed class ResultList : ObservableCollection<PaletteItem>
    {
        // 알림 인자는 불변이라 함께 쓴다 (글자마다 새로 만들지 않게)
        private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
        private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
        private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

        public void ReplaceAll(IReadOnlyList<PaletteItem> items)
        {
            Items.Clear();
            for (var i = 0; i < items.Count; i++) Items.Add(items[i]);
            OnPropertyChanged(CountChanged);
            OnPropertyChanged(IndexerChanged);
            OnCollectionChanged(ResetArgs);
        }
    }
}
