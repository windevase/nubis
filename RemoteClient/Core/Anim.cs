using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Nubis;

/// <summary>
/// 짧은 상호작용 애니메이션 모음 (페이지 전환 · 카드 hover 들림).
/// 규칙: 모두 1회 · 150ms 이하 · FillBehavior.Stop + 최종값을 기본값으로 먼저 넣어 끝나면 시계가 남지 않는다(렌더 루프를 붙잡지 않음).
/// Windows 의 "애니메이션 효과" 를 끈 사용자(SystemParameters.ClientAreaAnimation=false)에게는 즉시 최종 상태만 적용한다.
///
/// XAML:  &lt;Grid l:Anim.FadeInOnShow="True"&gt;   — Visibility 로 전환하는 페이지가 보일 때마다 살짝 올라오며 나타남
///        &lt;Border l:Anim.HoverLift="True"&gt;     — 마우스가 올라간 1장만 2px 들리고 그림자 (가상화 목록 카드에 써도 됨: 상시 그림자 없음)
/// 코드:  Anim.FadeIn(element);  Anim.FadeOut(element, () =&gt; ...);
/// </summary>
public static class Anim
{
    public const int FastMs = 100;
    public const int NormalMs = 150;

    private static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>hover 로 들린 카드 1장에만 거는 그림자 (공유 · Freeze)</summary>
    private static readonly DropShadowEffect LiftShadow = Freeze(new DropShadowEffect
    {
        BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.30, Color = Colors.Black, RenderingBias = RenderingBias.Performance,
    });

    // ---------------- 나타나기 / 사라지기 ----------------

    /// <summary>투명 → 불투명 + 아래에서 offsetY 만큼 올라오며 나타남. RenderTransform 이 비어 있거나 TranslateTransform 일 때만 위치를 움직인다</summary>
    public static void FadeIn(UIElement el, int ms = NormalMs, double offsetY = 6)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1;
        if (!Enabled) return;
        var d = TimeSpan.FromMilliseconds(ms);
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        if (offsetY != 0 && Translate(el) is { } t)
        {
            t.Y = 0;
            t.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(offsetY, 0, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        }
    }

    /// <summary>불투명 → 투명. 끝나면 done (예: Collapsed 로 바꾸기 · 목록에서 빼기). 애니메이션을 끈 환경이면 즉시 done</summary>
    public static void FadeOut(UIElement el, Action? done = null, int ms = FastMs)
    {
        if (!Enabled) { done?.Invoke(); return; }
        var a = new DoubleAnimation(el.Opacity, 0, TimeSpan.FromMilliseconds(ms)) { FillBehavior = FillBehavior.Stop };
        a.Completed += (_, _) =>
        {
            done?.Invoke();
            el.Opacity = 1;   // 다시 보일 때를 위해 원래대로 (done 에서 감추거나 떼어 냈다고 가정)
        };
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>요소의 TranslateTransform (없으면 만든다). 다른 변환이 이미 있으면 null — 남의 변환을 덮어쓰지 않는다</summary>
    private static TranslateTransform? Translate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        if (el.RenderTransform == null || el.RenderTransform == Transform.Identity)
        {
            t = new TranslateTransform();
            el.RenderTransform = t;
            return t;
        }
        return null;
    }

    // ---------------- 페이지가 보일 때 나타나기 (첨부 속성) ----------------

    public static readonly DependencyProperty FadeInOnShowProperty = DependencyProperty.RegisterAttached(
        "FadeInOnShow", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnFadeInOnShowChanged));

    public static bool GetFadeInOnShow(DependencyObject o) => (bool)o.GetValue(FadeInOnShowProperty);
    public static void SetFadeInOnShow(DependencyObject o, bool value) => o.SetValue(FadeInOnShowProperty, value);

    private static void OnFadeInOnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        // 정적 메서드 처리기 — 요소 → 처리기 방향 참조만 생기므로 누수 없음
        el.IsVisibleChanged -= OnVisibleChanged;
        if ((bool)e.NewValue) el.IsVisibleChanged += OnVisibleChanged;
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement el && e.NewValue is true) FadeIn(el);
    }

    // ---------------- hover 들림 (첨부 속성) ----------------

    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnHoverLiftChanged));

    public static bool GetHoverLift(DependencyObject o) => (bool)o.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject o, bool value) => o.SetValue(HoverLiftProperty, value);

    /// <summary>들리기 전 요소의 Effect (떠날 때 되돌림)</summary>
    private static readonly DependencyProperty SavedEffectProperty = DependencyProperty.RegisterAttached(
        "SavedEffect", typeof(Effect), typeof(Anim), new PropertyMetadata(null));

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.MouseEnter -= OnLiftEnter;
        el.MouseLeave -= OnLiftLeave;
        if ((bool)e.NewValue)
        {
            el.MouseEnter += OnLiftEnter;
            el.MouseLeave += OnLiftLeave;
        }
    }

    private static void OnLiftEnter(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement el) return;
        if (el.Effect != LiftShadow)
        {
            el.SetValue(SavedEffectProperty, el.Effect);
            el.Effect = LiftShadow;
        }
        MoveY(el, -2);
    }

    private static void OnLiftLeave(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement el) return;
        if (el.Effect == LiftShadow)
        {
            el.Effect = (Effect?)el.GetValue(SavedEffectProperty);
            el.ClearValue(SavedEffectProperty);
        }
        MoveY(el, 0);
    }

    private static void MoveY(UIElement el, double to)
    {
        if (Translate(el) is not { } t) return;
        var from = t.Y;
        t.BeginAnimation(TranslateTransform.YProperty, null);
        t.Y = to;
        if (!Enabled || from == to) return;
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(FastMs)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }
}
