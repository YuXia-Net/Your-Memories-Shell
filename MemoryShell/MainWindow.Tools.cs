using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private async void ShowRemoteDetails_Click(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not TabViewItem tab || !panes.TryGetValue(tab, out var pane) || !pane.IsConnected)
        {
            Status.Text = L10n.Get("S92551328F93F");
            return;
        }
        try { await pane.ShowRemoteDetailsAsync(Root.XamlRoot); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void MultiLocationDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var points = panes.Values.Select(pane => pane.CreateNetworkProbePoint()).OfType<NetworkProbePoint>().ToArray();
        var profile = store.HideDeviceAddress ? null : SelectedProfile?.Profile;
        try { await MultiLocationDiagnosticsDialog.ShowAsync(Root.XamlRoot, points, profile?.Host, profile?.Port ?? 22); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void NetworkDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var profile = store.HideDeviceAddress ? null : SelectedProfile?.Profile;
        try { await NetworkDiagnosticsDialog.ShowAsync(Root.XamlRoot, profile?.Host, profile?.Port); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void RdpMenu_Click(object sender, RoutedEventArgs e)
    {
        if (database is null)
            return;

        try
        {
            var list = new ListView { MaxHeight = Math.Clamp(Root.ActualHeight - 230, 160, 440), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var empty = new TextBlock { Text = L10n.Get("S6AB2FD391E21"), Margin = new Thickness(8, 16, 8, 16), Foreground = WorkspaceTheme.Brush("ShellMutedBrush") };
            var body = new Grid { Width = Math.Clamp(Root.ActualWidth - 100, 260, 580) };
            body.Children.Add(list);
            body.Children.Add(empty);
            var dialog = Dialog(L10n.Get("SC255841F6809"), body, L10n.Get("SA5574109F020"));
            dialog.Resources["ContentDialogMaxWidth"] = 650d;
            dialog.SecondaryButtonText = L10n.Get("S2CB204A57873");
            dialog.CloseButtonText = L10n.Get("S3FD47EDCE45B");
            dialog.IsPrimaryButtonEnabled = false;
            Func<Task>? action = null;
            foreach (var profile in store.RemoteDesktops)
            {
                var label = store.HideDeviceAddress ? HideEndpoint(profile.Name, profile.Host, profile.Port, profile.Username) : profile.Name;
                var row = new ListViewItem { Tag = profile, MinHeight = 44, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                row.Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = store.HideDeviceAddress ? "•••" : $"{profile.Host}:{profile.Port}", FontSize = 11, Foreground = WorkspaceTheme.Brush("ShellMutedBrush") }
                    }
                };
                var menu = new MenuFlyout();
                Add(L10n.Get("SA5574109F020"), () => { LaunchRdp(profile); return Task.CompletedTask; });
                Add(L10n.Get("S051836569928"), () => AddOrEditRdpAsync(profile));
                Add(L10n.Get("S2F9DAA828907"), async () =>
                {
                    var confirmation = Dialog(L10n.Get("S92E11347ED70"), L10n.Format("S64C3878B7399", label), L10n.Get("S2F9DAA828907"));
                    if (await confirmation.ShowAsync() == ContentDialogResult.Primary) SaveRdpChange(profile, null);
                });
                void Add(string title, Func<Task> operation)
                {
                    var item = new MenuFlyoutItem { Text = title };
                    item.Click += (_, _) => { action = operation; dialog.Hide(); };
                    menu.Items.Add(item);
                }
                row.ContextFlyout = menu;
                row.RightTapped += (_, _) => list.SelectedItem = row;
                row.DoubleTapped += (_, args) =>
                {
                    args.Handled = true;
                    action = () => { LaunchRdp(profile); return Task.CompletedTask; };
                    dialog.Hide();
                };
                list.Items.Add(row);
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem;
            var result = await dialog.ShowAsync();
            if (action is not null) await action();
            else if (result == ContentDialogResult.Secondary) await AddOrEditRdpAsync(null);
            else if (result == ContentDialogResult.Primary && list.SelectedItem is ListViewItem { Tag: RdpProfile selected }) LaunchRdp(selected);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void LaunchRdp(RdpProfile profile)
    {
        RdpConnectionDialog.Launch(profile);
        AddConnectionEvent(new ConnectionEvent(DateTimeOffset.Now, profile.Name,
            $"{profile.Host}:{profile.Port}", true, "RDP", profile.Id));
        Status.Text = store.HideDeviceAddress ? L10n.Get("SEE5817915037") : L10n.Format("SD70D05F735C4", profile.Name);
    }

    private async Task AddOrEditRdpAsync(RdpProfile? original)
    {
        var result = await RdpConnectionDialog.ShowAsync(Root.XamlRoot, original, save: true);
        if (result is not null)
            SaveRdpChange(original, result);
    }

    private void SaveRdpChange(RdpProfile? original, RdpProfile? result)
    {
        var profiles = store.RemoteDesktops;
        var index = original is null ? profiles.Count : profiles.IndexOf(original);
        if (original is not null)
            profiles.RemoveAt(index);
        if (result is not null)
            profiles.Insert(index, result);
        try { SaveWorkspace(); }
        catch
        {
            if (result is not null)
                profiles.RemoveAt(index);
            if (original is not null)
                profiles.Insert(index, original);
            throw;
        }
        Status.Text = result is null ? L10n.Get("S38AE5E0A21DD") : L10n.Format("S478C2DF2AA75", result.Name);
    }
}






