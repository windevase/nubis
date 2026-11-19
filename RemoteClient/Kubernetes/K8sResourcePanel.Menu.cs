using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nubis;

/// <summary>리소스 패널: 더블클릭 · Enter · 우클릭 메뉴 동작</summary>
public partial class K8sResourcePanel
{
    private K8sItem? Selected => (ItemList.SelectedItem as K8sItemRow)?.Item;

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ItemList, d) is ListBoxItem && Selected is { } item) DefaultAction(item);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected is { } item) { DefaultAction(item); e.Handled = true; }
    }

    /// <summary>더블클릭·Enter: 파드는 셸, 그 밖은 describe</summary>
    private void DefaultAction(K8sItem item)
    {
        if (item.Kind == K8sKind.Pods) ShellRequested?.Invoke(item, item.Containers.FirstOrDefault());
        else Describe(item);
    }

    private static string NsArg(K8sItem i) => i.Namespace.Length > 0 ? $" -n {i.Namespace}" : "";

    private void Describe(K8sItem i) => TerminalCommand?.Invoke($"kubectl describe {i.Ref}{NsArg(i)}");

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ItemMenu.Items.Clear();
        if (Selected is not { } item) { e.Handled = true; return; }
        MenuItem Item(string header, Action act, bool danger = false)
        {
            var m = new MenuItem { Header = MainWindow.MenuText(header) };
            if (danger) m.Foreground = (Brush)FindResource("Danger");
            m.Click += (_, _) => act();
            return m;
        }
        if (item.Kind == K8sKind.Pods)
        {
            var containers = item.Containers;
            if (containers.Count <= 1)
            {
                ItemMenu.Items.Add(Item(L.T("셸 열기"), () => ShellRequested?.Invoke(item, containers.FirstOrDefault())));
                ItemMenu.Items.Add(Item(L.T("로그 보기"), () => LogsRequested?.Invoke(item, containers.FirstOrDefault(), false)));
                ItemMenu.Items.Add(Item(L.T("이전 컨테이너 로그 (재시작 전)"), () => LogsRequested?.Invoke(item, containers.FirstOrDefault(), true)));
            }
            else
            {
                // 컨테이너가 여럿이면 컨테이너마다 (재시작한 컨테이너가 첫 번째가 아닐 수 있다)
                var shell = new MenuItem { Header = L.T("셸 열기") };
                var logs = new MenuItem { Header = L.T("로그 보기") };
                var previous = new MenuItem { Header = L.T("이전 컨테이너 로그 (재시작 전)") };
                foreach (var c in containers)
                {
                    shell.Items.Add(Item(c, () => ShellRequested?.Invoke(item, c)));
                    logs.Items.Add(Item(c, () => LogsRequested?.Invoke(item, c, false)));
                    previous.Items.Add(Item(c, () => LogsRequested?.Invoke(item, c, true)));
                }
                ItemMenu.Items.Add(shell);
                ItemMenu.Items.Add(logs);
                ItemMenu.Items.Add(previous);
            }
            ItemMenu.Items.Add(new Separator());
        }
        if (K8sPortForward.CanForward(item))
        {
            ItemMenu.Items.Add(Item(L.T("포트 포워딩..."), () => PortForwardRequested?.Invoke(item)));
            ItemMenu.Items.Add(new Separator());
        }
        ItemMenu.Items.Add(Item("describe", () => Describe(item)));
        ItemMenu.Items.Add(Item(L.T("YAML 보기"), () => TerminalCommand?.Invoke($"kubectl get {item.Ref}{NsArg(item)} -o yaml")));
        ItemMenu.Items.Add(Item(L.T("이름 복사"), () => { try { Clipboard.SetText(item.Name); } catch { } }));
        if (item.Kind == K8sKind.Pods)
        {
            ItemMenu.Items.Add(new Separator());
            ItemMenu.Items.Add(Item(L.T("삭제 (컨트롤러가 다시 만듦)..."), () => _ = ConfirmRunAsync(item,
                L.F("파드 '{0}' 을(를) 삭제할까요? 디플로이먼트 등이 관리하는 파드면 새로 만들어집니다.", item.Name),
                new[] { "delete", "pod", item.Name, "-n", item.Namespace, "--wait=false" }), danger: true));
        }
        else if (item.Kind == K8sKind.Deployments)
        {
            ItemMenu.Items.Add(new Separator());
            ItemMenu.Items.Add(Item(L.T("다시 시작 (rollout restart)..."), () => _ = ConfirmRunAsync(item,
                L.F("디플로이먼트 '{0}' 의 파드를 차례로 다시 시작할까요?", item.Name),
                new[] { "rollout", "restart", "deployment/" + item.Name, "-n", item.Namespace }), danger: true));
        }
    }

    /// <summary>바꾸는 동작은 묻고 실행한 뒤 결과를 상태 줄에 보이고 목록을 다시 받는다 (불러오는 중이면 끝난 뒤)</summary>
    private async Task ConfirmRunAsync(K8sItem item, string question, string[] args)
    {
        if (Dialog.Show(Window.GetWindow(this), question + "\n\n" + L.F("클러스터: {0}", _cluster.Name), "Kubernetes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var r = await RunAsync(args);
            if (r.TimedOut || r.ExitCode != 0) { ShowError(K8sResources.ErrorText(r)); return; }
            var line = K8sResources.FirstLine(r.Output);
            SetStatus(line.Length > 0 ? line : L.T("완료했습니다."));
            await Task.Delay(800, _cts.Token);
            await LoadItemsAsync(quiet: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
