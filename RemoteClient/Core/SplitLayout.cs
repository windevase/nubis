using System.Collections.Generic;
using System.Linq;

namespace Nubis;

/// <summary>
/// 나란히 보기 짝: Left 는 왼쪽(기본) 영역, Right 는 오른쪽(보조) 영역에 보이는 탭.
/// 탭은 그대로 탭 줄에 남고, 둘 중 하나를 고르면 둘이 함께 보인다
/// </summary>
internal sealed record SplitPair<T>(T Left, T Right) where T : class
{
    public bool Contains(T? tab) => tab != null && (ReferenceEquals(Left, tab) || ReferenceEquals(Right, tab));

    /// <summary>짝의 다른 쪽. 짝에 없는 탭이면 null</summary>
    public T? Partner(T? tab) => ReferenceEquals(Left, tab) ? Right : ReferenceEquals(Right, tab) ? Left : null;

    public SplitPair<T> Swapped() => new(Right, Left);
}

/// <summary>나란히 보기의 화면 무관 판단 (탭 종류와 무관 — 테스트는 문자열 등으로)</summary>
internal static class SplitLayout
{
    /// <summary>
    /// 지금 보일 (왼쪽, 오른쪽) 탭. 고른 탭이 짝에 들어 있으면 짝 둘 다, 아니면 고른 탭 하나만 왼쪽에(오른쪽 null).
    /// 아무 탭도 고르지 않았으면(홈) 둘 다 null
    /// </summary>
    public static (T? Left, T? Right) Visible<T>(T? selected, SplitPair<T>? pair) where T : class
    {
        if (selected == null) return (null, null);
        return pair != null && pair.Contains(selected) ? (pair.Left, pair.Right) : (selected, null);
    }

    /// <summary>anchor 의 오른쪽에 beside 를 나란히 둘 수 있는지 — 서로 다른, 둘 다 열려 있는 탭</summary>
    public static bool CanPair<T>(IReadOnlyCollection<T> tabs, T? anchor, T? beside) where T : class =>
        anchor != null && beside != null && !ReferenceEquals(anchor, beside) && tabs.Contains(anchor) && tabs.Contains(beside);

    /// <summary>anchor(왼쪽) 옆에 나란히 볼 수 있는 탭 (탭 줄 순서). 짝은 한 번에 하나라 이미 다른 짝에 든 탭도 고를 수 있다</summary>
    public static List<T> Candidates<T>(IReadOnlyList<T> tabs, T anchor) where T : class =>
        tabs.Where(t => !ReferenceEquals(t, anchor)).ToList();

    /// <summary>
    /// 탭 일부를 닫은 뒤: 짝 중 하나라도 닫히면 나란히 보기를 푼다(null).
    /// Survivor = 짝에서 닫히지 않고 남은 탭 — 닫힌 쪽이 고른 탭이었으면 이 탭이 화면을 채운다. 없으면 null
    /// </summary>
    public static (SplitPair<T>? Pair, T? Survivor) AfterClose<T>(SplitPair<T>? pair, ICollection<T> closing) where T : class
    {
        if (pair == null) return (null, null);
        bool closedL = closing.Contains(pair.Left), closedR = closing.Contains(pair.Right);
        if (!closedL && !closedR) return (pair, null);
        if (closedL && closedR) return (null, null);
        return (null, closedL ? pair.Right : pair.Left);
    }

    /// <summary>
    /// 고른 탭 selected 가 closing 과 함께 닫힐 때 화면을 채울 짝의 남은 쪽. selected 가 짝이 아니거나 남는 쪽이 없으면 null
    /// (짝 밖의 탭을 닫는데 무관한 짝의 탭으로 옮기지 않게)
    /// </summary>
    public static T? NextAfterClose<T>(SplitPair<T>? pair, T selected, ICollection<T> closing) where T : class =>
        pair != null && pair.Contains(selected) ? AfterClose(pair, closing).Survivor : null;

    /// <summary>
    /// 세션 뷰 안 왼쪽 패널(SFTP · 쿠버네티스 리소스)의 최대 너비: 뷰 너비의 절반, 단 패널 최소 너비 아래로는 줄이지 않는다.
    /// 나란히 보기로 뷰가 반으로 줄면 고정 너비 패널이 터미널을 거의 다 가리던 문제
    /// </summary>
    public static double SidePanelMaxWidth(double viewWidth, double panelMin) =>
        viewWidth > 0 && !double.IsInfinity(viewWidth) ? System.Math.Max(panelMin, viewWidth / 2) : double.PositiveInfinity;

    /// <summary>오른쪽 영역 너비 비율 (왼쪽 · 오른쪽 열 너비). 0 이하·이상한 값이면 반반, 15%~85% 로 제한 (한쪽이 사라지지 않게)</summary>
    public static double RightRatio(double leftWidth, double rightWidth)
    {
        if (!(leftWidth > 0) || !(rightWidth > 0) || double.IsInfinity(leftWidth) || double.IsInfinity(rightWidth)) return 0.5;
        var r = rightWidth / (leftWidth + rightWidth);
        return r < 0.15 ? 0.15 : r > 0.85 ? 0.85 : r;
    }
}
