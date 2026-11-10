using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>처음 실행할 때 한 번만 뜨는 언어 선택 (한국어가 기본). 그 뒤에는 Settings 에서 바꾼다</summary>
public partial class LanguageWindow : Window
{
    private string _choice = L.Korean;

    private LanguageWindow() => InitializeComponent();

    /// <summary>언어를 아직 고르지 않았으면 고르게 하고 공통 설정에 저장한다. 창을 그냥 닫으면 한국어</summary>
    public static void EnsureChosen()
    {
        if (SettingsStore.Global.Language != null) return;
        var w = new LanguageWindow();
        w.ShowDialog();
        var lang = w._choice;
        SettingsStore.UpdateGlobal(s => s.Language = lang);
    }

    private void OnKorean(object sender, RoutedEventArgs e) { _choice = L.Korean; DialogResult = true; }
    private void OnEnglish(object sender, RoutedEventArgs e) { _choice = L.English; DialogResult = true; }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
