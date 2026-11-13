using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private MonitorWallView? monitorWall;
    private enum WorkspacePage { Sessions, Probe, Settings }
    private WorkspacePage workspacePage;
    private bool probeFullScreen;

    private void NavigateWorkspace(WorkspacePage page)
    {
        if (workspaceLocked) return;
        if (page != WorkspacePage.Probe && probeFullScreen) ToggleProbeFullScreen();
        if (page != WorkspacePage.Settings) requestedSettingsExit = page;
        if (page != WorkspacePage.Settings && finishSettings?.Invoke() == false) return;
        workspacePage = page;
        SessionWorkspace.Visibility = page == WorkspacePage.Sessions ? Visibility.Visible : Visibility.Collapsed;
        ProbePageHost.Visibility = page == WorkspacePage.Probe ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageHost.Visibility = page == WorkspacePage.Settings ? Visibility.Visible : Visibility.Collapsed;
        ProbeMode.IsChecked = page == WorkspacePage.Probe;
        SettingsMode.IsChecked = page == WorkspacePage.Settings;
        ConnectionsMode.IsChecked = page == WorkspacePage.Sessions && MonitorPanel.Visibility != Visibility.Visible;
        MonitorMode.IsChecked = page == WorkspacePage.Sessions && MonitorPanel.Visibility == Visibility.Visible;
        monitorWall?.SetActive(page == WorkspacePage.Probe);
        UpdateVisibleMonitor();
    }

    private void ReturnToSessions_Click(object sender, RoutedEventArgs args) => NavigateWorkspace(WorkspacePage.Sessions);

    private void ToggleProbeFullScreen()
    {
        if (workspacePage != WorkspacePage.Probe) return;
        ToggleWindowFullScreen();
        probeFullScreen = AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        TopBar.Visibility = StatusBar.Visibility = NavigationRail.Visibility = probeFullScreen ? Visibility.Collapsed : Visibility.Visible;
        Root.RowDefinitions[0].Height = new GridLength(probeFullScreen ? 0 : 48);
        Root.RowDefinitions[2].Height = new GridLength(probeFullScreen ? 0 : 24);
        NavigationColumn.Width = new GridLength(probeFullScreen ? 0 : 56);
        monitorWall?.SetFullScreen(probeFullScreen);
    }

    private void ShowMonitorWall_Click(object sender, RoutedEventArgs args)
    {
        if (database is null) return;
        if (monitorWall is not null) { NavigateWorkspace(WorkspacePage.Probe); return; }
        monitorWall = new MonitorWallView(() => store.Connections.ToArray(), () => store.HostKeys.ToArray(), DeviceName,
            (profile, text) => MaskDeviceAddress(profile, text, store.HideDeviceAddress), store.ProbeLayout,
            next => { var old = store.ProbeLayout; store.ProbeLayout = next; try { SaveWorkspace(); } catch { store.ProbeLayout = old; throw; } });
        monitorWall.ConnectRequested += async id =>
        {
            if (store.Connections.Find(item => item.Profile.Id == id) is { } saved)
            {
                NavigateWorkspace(WorkspacePage.Sessions);
                await OpenSavedConnectionAsync(saved);
            }
        };
        monitorWall.EditRequested += async id =>
        {
            if (store.Connections.Find(item => item.Profile.Id == id) is { } saved) await EditConnectionAsync(saved, true);
        };
        monitorWall.FullScreenRequested += ToggleProbeFullScreen;
        monitorWall.BackRequested += () => NavigateWorkspace(WorkspacePage.Sessions);
        ProbePageHost.Content = monitorWall;
        NavigateWorkspace(WorkspacePage.Probe);
    }

    private void CloseMonitorWall()
    {
        if (workspacePage == WorkspacePage.Probe) NavigateWorkspace(WorkspacePage.Sessions);
        monitorWall?.Dispose(); monitorWall = null;
        ProbePageHost.Content = null;
    }
}
