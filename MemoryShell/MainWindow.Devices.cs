using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private static string ConnectionTarget(ConnectionProfile profile) => $"{profile.Username}@{profile.Host}:{profile.Port}";

    private string DeviceName(ConnectionProfile profile) =>
        store.HideDeviceAddress && profile.Name.Equals(profile.Host, StringComparison.OrdinalIgnoreCase)
            ? L10n.Get("S78F79FC36EFE") : MaskDeviceAddress(profile, profile.Name, store.HideDeviceAddress);

    internal static string MaskDeviceAddress(ConnectionProfile profile, string value, bool hidden)
    {
        if (!hidden)
            return value;

        value = HideEndpoint(value, profile.Host, profile.Port, profile.Username);
        if (profile.Proxy is { } proxy)
            value = HideEndpoint(value, proxy.Host, proxy.Port, proxy.Username);
        if (profile.Jump is { } jump)
            value = HideEndpoint(value, jump.Host, jump.Port, jump.Username);
        return value;
    }

    internal static string HideEndpoint(string text, string host, int port, string? username)
    {
        var terms = new[] { $"[{host}]:{port}", $"{host}:{port}", host, username };
        var pattern = string.Join("|", terms.Where(term => !string.IsNullOrEmpty(term)).Select(term =>
            System.Text.RegularExpressions.Regex.Escape(term!)));
        return System.Text.RegularExpressions.Regex.Replace(text,
            $@"(?<![\p{{L}}\p{{N}}_.-])(?:{pattern})(?![\p{{L}}\p{{N}}_.-])", "•••",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private string DisplaySessionStatus(ConnectionProfile profile, string text) =>
        MaskDeviceAddress(profile, text, store.HideDeviceAddress);

    private MenuFlyout BuildDeviceMenu(Guid id, HashSet<string> collapsed, Action<Func<Task>, bool>? defer, Action refresh)
    {
        var menu = new MenuFlyout();
        Add(L10n.Get("SA5574109F020"), OpenSavedConnectionAsync);
        Add(L10n.Get("S894A3CD7021E"), entry => EditConnectionAsync(entry, true));
        var move = new MenuFlyoutSubItem { Text = L10n.Get("SD36A32FF891F"), Icon = new SymbolIcon(Symbol.Folder) };
        menu.Opening += (_, _) =>
        {
            move.Items.Clear();
            var currentGroup = store.Connections.Find(entry => entry.Profile.Id == id)?.Profile.Group;
            foreach (var group in DeviceGroupChoices().Prepend(""))
            {
                var item = new MenuFlyoutItem
                {
                    Text = DeviceGroupLabel(group), IsEnabled = !DeviceGroupComparer.Equals(currentGroup, group)
                };
                item.Click += (_, _) =>
                {
                    try
                    {
                        collapsed.Remove(group);
                        MoveDeviceToGroup(id, group);
                        if (defer is not null)
                            refresh();
                    }
                    catch (Exception ex) { Status.Text = ex.Message; }
                };
                move.Items.Add(item);
            }
            move.Items.Add(new MenuFlyoutSeparator());
            var newGroup = new MenuFlyoutItem { Text = L10n.Get("SAD8CA4718EE9") };
            newGroup.Click += async (_, _) =>
            {
                if (defer is not null)
                    defer(() => CreateDeviceGroupAsync(id), true);
                else
                {
                    try { await CreateDeviceGroupAsync(id); }
                    catch (Exception ex) { Status.Text = ex.Message; }
                }
            };
            move.Items.Add(newGroup);
        };
        menu.Items.Add(move);
        Add(L10n.Get("S1A728ABA043D"), DuplicateDeviceAsync);
        Add(L10n.Get("SA1905A7E9CCD"), ShowCredentialsAsync);
        if (!store.HideDeviceAddress)
        {
            Add(L10n.Get("SCFFE2665BE8F"), entry =>
            {
                var data = new DataPackage();
                data.SetText(ConnectionTarget(entry.Profile));
                Clipboard.SetContent(data);
                return Task.CompletedTask;
            }, closeManager: false);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L10n.Get("S6A5B1CB18FB6"), DeleteDeviceAsync);
        return menu;

        void Add(string label, Func<SavedConnection, Task> action, bool closeManager = true)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += async (_, _) =>
            {
                if (store.Connections.Find(entry => entry.Profile.Id == id) is not { } entry)
                    return;
                if (defer is not null && closeManager)
                {
                    defer(() => action(entry), false);
                    return;
                }
                try { await action(entry); }
                catch (Exception ex) { Status.Text = MaskDeviceAddress(entry.Profile, ex.Message, store.HideDeviceAddress); }
            };
            menu.Items.Add(item);
        }
    }

    private async Task DuplicateDeviceAsync(SavedConnection entry)
    {
        var copy = new SavedConnection(entry.Profile with
        {
            Id = Guid.NewGuid(), Name = entry.Profile.Name + L10n.Get("S6DED4E175CD7"),
            Jump = entry.Profile.Jump is { } jump ? jump with { PrivateKey = jump.PrivateKey?.ToArray() } : null
        }, entry.Secret, entry.PrivateKey?.ToArray());
        store.Connections.Add(copy);
        try { SaveWorkspace(); }
        catch
        {
            store.Connections.Remove(copy);
            if (copy.PrivateKey is { } key)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            if (copy.Profile.Jump?.PrivateKey is { } jumpKey)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(jumpKey);
            throw;
        }
        RefreshProfiles(copy.Profile.Id);
        await EditConnectionAsync(copy, true);
    }

    private void HideDeviceAddress_Click(object sender, RoutedEventArgs e)
    {
        if (database is null)
            return;
        var previous = store.HideDeviceAddress;
        store.HideDeviceAddress = !previous;
        try { SaveWorkspace(); }
        catch (Exception ex)
        {
            store.HideDeviceAddress = previous;
            Status.Text = ex.Message;
        }
        RefreshPrivacyPresentation();
    }

    private void RefreshPrivacyPresentation()
    {
        RefreshProfiles();
        foreach (var (tab, state) in connectionTabs)
            tab.Header = DeviceName(state.Profile);
        if (Sessions.SelectedItem is TabViewItem selected && panes.TryGetValue(selected, out var active))
            Status.Text = active.DisplayStatus;
        RefreshRecentMenu();
        RefreshMonitor();
    }

    private IEnumerable<(Guid? SshId, string Name, DateTimeOffset Time, Func<Task> Open)> RecentDevices()
    {
        var seen = new HashSet<Guid>();
        foreach (var item in store.ConnectionHistory.AsEnumerable().Reverse())
        {
            if (!item.Connected)
                continue;
            if (item.Protocol == "SSH")
            {
                var saved = store.Connections.Find(entry => item.ProfileId is { } id
                    ? entry.Profile.Id == id : ConnectionTarget(entry.Profile) == item.Target);
                if (saved is not null && seen.Add(saved.Profile.Id))
                    yield return (saved.Profile.Id, DeviceName(saved.Profile), item.Timestamp, () => OpenSavedConnectionAsync(saved));
            }
            else if (item.Protocol == "RDP")
            {
                var saved = store.RemoteDesktops.Find(profile => item.ProfileId is { } id
                    ? profile.Id == id : $"{profile.Host}:{profile.Port}" == item.Target);
                if (saved is not null && seen.Add(saved.Id))
                {
                    var label = store.HideDeviceAddress ? HideEndpoint(saved.Name, saved.Host, saved.Port, saved.Username) : saved.Name;
                    yield return (null, label, item.Timestamp, () =>
                    {
                        LaunchRdp(saved);
                        return Task.CompletedTask;
                    });
                }
            }
        }
    }

    private void RefreshRecentMenu()
    {
        if (RecentConnectionsMenu is null)
            return;
        var entries = RecentDevices().Take(12).ToArray();
        RecentConnectionsMenu.Items.Clear();
        if (WelcomeRecent is not null)
            WelcomeRecent.Items.Clear();
        foreach (var entry in entries)
        {
            var item = new MenuFlyoutItem { Text = $"{entry.Name}  ·  {entry.Time.LocalDateTime:MM-dd HH:mm}" };
            item.Click += async (_, _) =>
            {
                try { await entry.Open(); }
                catch (Exception ex) { Status.Text = ex.Message; }
            };
            RecentConnectionsMenu.Items.Add(item);
            if (WelcomeRecent is not null)
            {
                var line = new Grid { ColumnSpacing = 10 };
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                var time = new TextBlock
                {
                    Text = entry.Time.LocalDateTime.ToString("MM-dd HH:mm"),
                    Foreground = WorkspaceTheme.Brush("ShellMutedBrush"), FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(time, 1);
                line.Children.Add(time);
                var recentRow = new ListViewItem
                {
                    Content = line, Tag = entry.Open, MinHeight = 36,
                    ContextFlyout = entry.SshId is Guid id ? BuildDeviceMenu(id, collapsedDeviceGroups, null, () => RefreshProfiles()) : null,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, IsDoubleTapEnabled = true
                };
                recentRow.AddHandler(UIElement.DoubleTappedEvent, new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(async (_, args) =>
                {
                    args.Handled = true;
                    try { await entry.Open(); } catch (Exception ex) { Status.Text = ex.Message; }
                }), true);
                recentRow.KeyDown += async (_, args) =>
                {
                    if (args.Key != Windows.System.VirtualKey.Enter) return;
                    args.Handled = true;
                    try { await entry.Open(); } catch (Exception ex) { Status.Text = ex.Message; }
                };
                WelcomeRecent.Items.Add(recentRow);
            }
        }
        if (WelcomeEmpty is not null)
            WelcomeEmpty.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (RecentConnectionsMenu.Items.Count == 0)
            RecentConnectionsMenu.Items.Add(new MenuFlyoutItem { Text = L10n.Get("S167E2C411206"), IsEnabled = false });
    }

    private async void WelcomeRecent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (database is null || e.ClickedItem is not ListViewItem { Tag: Func<Task> open })
            return;
        try { await open(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void ConnectionManager_Click(object sender, RoutedEventArgs e)
    {
        SetSidebarVisible(true);
        SetSidebarMode(false);
        ConnectionFilter.Focus(FocusState.Programmatic);
    }

}
