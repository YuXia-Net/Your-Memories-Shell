using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private MonitorWallView? monitorWall;
    private TabViewItem? monitorWallTab;

    private void ShowMonitorWall_Click(object sender, RoutedEventArgs args)
    {
        if (database is null) return;
        if (monitorWallTab is not null) { Sessions.SelectedItem = monitorWallTab; return; }
        monitorWall = new MonitorWallView(() => store.Connections.ToArray(), () => store.HostKeys.ToArray(), DeviceName,
            (profile, text) => MaskDeviceAddress(profile, text, store.HideDeviceAddress), store.ProbeLayout,
            next => { var old = store.ProbeLayout; store.ProbeLayout = next; try { SaveWorkspace(); } catch { store.ProbeLayout = old; throw; } });
        monitorWall.ConnectRequested += async id =>
        {
            if (store.Connections.Find(item => item.Profile.Id == id) is { } saved) await OpenSavedConnectionAsync(saved);
        };
        monitorWall.EditRequested += async id =>
        {
            if (store.Connections.Find(item => item.Profile.Id == id) is { } saved) await EditConnectionAsync(saved, true);
        };
        monitorWall.FullScreenRequested += () => ToggleWindowFullScreen_Click(this, new RoutedEventArgs());
        monitorWallTab = new TabViewItem { Header = L10n.Get("MonitorWallTitle"), IconSource = new SymbolIconSource { Symbol = Symbol.ViewAll }, Content = monitorWall };
        Sessions.TabItems.Add(monitorWallTab);
        Sessions.SelectedItem = monitorWallTab;
    }

    private void CloseMonitorWall()
    {
        monitorWall?.Dispose(); monitorWall = null;
        if (monitorWallTab is { } tab) { monitorWallTab = null; Sessions.TabItems.Remove(tab); }
    }
}
