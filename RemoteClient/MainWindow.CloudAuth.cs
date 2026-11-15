using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace Nubis;

// Cloud 화면: 클라우드 인증 파일(폴더) 경로 지정 · CLI 로그인
public partial class MainWindow
{
    /// <summary>클라우드 인증 파일(폴더) 위치를 직접 선택</summary>
    private void OnChangeCloudPath(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudSection section) return;
        var current = CloudPaths.Effective(section.Provider);
        string? chosen;
        if (CloudPaths.IsFolder(section.Provider))
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = L.F("{0} 인증 폴더 선택 ({1})", section.Title, section.Provider == CloudProvider.Tencent ? L.T("*.credential 파일이 있는 폴더") : L.T("az CLI 설정 폴더")),
                InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            chosen = dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.F("{0} 인증 파일 선택 ({1})", section.Title, section.Provider switch
                {
                    CloudProvider.Aws => "AWS config",
                    CloudProvider.Oci => "OCI config",
                    _ => "ADC JSON",
                }),
                Filter = section.Provider == CloudProvider.Gcp ? L.T("JSON (*.json)|*.json|모든 파일|*.*") : L.T("모든 파일|*.*"),
                InitialDirectory = Path.GetDirectoryName(current) is { } d && Directory.Exists(d) ? d : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                FileName = Path.GetFileName(current),
            };
            chosen = dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }
        if (chosen == null) return;
        SetCloudPath(section, chosen);
    }

    private void OnResetCloudPath(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CloudSection section) SetCloudPath(section, null);
    }

    private void SetCloudPath(CloudSection section, string? path)
    {
        SettingsStore.Update(s =>
        {
            if (path == null) s.CloudPaths.Remove(section.Provider.ToString());
            else s.CloudPaths[section.Provider.ToString()] = path;
        });
        CloudPaths.Apply();
        section.NotifyPath();
        section.Message = path == null
            ? L.T("기본 경로로 되돌렸습니다. 프로필 추가·동기화부터 적용됩니다.")
            : L.T("경로를 바꿨습니다. 프로필 추가·동기화와 로그인 버튼에 바로 적용됩니다.");
    }

    /// <summary>CLI 로그인 명령을 새 콘솔 창에서 실행 (브라우저 인증은 사용자가 진행)</summary>
    private void OnProviderLogin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudSection section) return;
        var p = section.Profiles.FirstOrDefault();
        if (section.Provider == CloudProvider.Aws && p == null)
        {
            section.Message = L.T("먼저 프로필을 추가하세요. (같은 SSO 포털을 쓰는 프로필은 한 번 로그인으로 모두 사용 가능)");
            return;
        }
        section.Message = StartLogin(section.Provider, p);
    }

    /// <summary>그룹 우클릭 "CLI 로그인": 그 프로필로 로그인 (SSO 포털이 여러 개여도 프로필마다 로그인 가능)</summary>
    private void RunProviderLogin(CloudProfile p)
    {
        var msg = StartLogin(p.Provider, p);
        p.StatusText = msg;
        SectionOf(p).Message = msg;
    }

    /// <summary>
    /// UI 로그인 버튼 경로. 프로필 이름은 사용자 PC 의 설정 파일에서 온 값이라 cmd 메타 문자가 섞일 수 있으므로
    /// MCP cloud_login 과 같은 기준으로 검증한 뒤에만 실행한다
    /// </summary>
    private static string StartLogin(CloudProvider provider, CloudProfile? p)
    {
        var cmd = CloudProviders.Get(provider).LoginCommand(p);
        if (!McpCloudHelpers.IsSafeLogin(p?.Name, p?.TenantId, cmd))
            return L.F("프로필 이름 또는 로그인 명령에 명령줄에 넣을 수 없는 문자가 있어 실행하지 않습니다. 터미널에서 직접 로그인하세요: {0}", cmd);
        return LaunchLoginCommand(cmd, out _);
    }

    /// <summary>로그인 명령을 새 cmd 창에서 실행 (UI 로그인 버튼 · MCP cloud_login 공용). 화면에 보여 줄 안내 문구를 돌려준다</summary>
    private static string LaunchLoginCommand(string cmd, out bool started)
    {
        started = false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k {cmd}") { UseShellExecute = true });
            started = true;
            return L.F("새 창에서 '{0}' 를 실행했습니다. 로그인을 마친 뒤 동기화하세요.", cmd);
        }
        catch (Exception ex) { return L.F("실행 실패: {0}", ex.Message); }
    }
}
