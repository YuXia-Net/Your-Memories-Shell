using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private static readonly string UngroupedLabel = L10n.Get("SE86EA3F280D9");
    private const int DeviceGroupNameLimit = 80;
    private static readonly StringComparer DeviceGroupComparer = StringComparer.OrdinalIgnoreCase;
    private readonly HashSet<string> collapsedDeviceGroups = new(DeviceGroupComparer);
    private readonly string deviceDragFormat = $"MemoryShell.Device.{Guid.NewGuid():N}";

    private sealed record DeviceGroupRow(string Name);

    private IEnumerable<string> DeviceGroupChoices() => store.DeviceGroups
        .Concat(store.Connections.Select(item => item.Profile.Group))
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(DeviceGroupComparer)
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);

    private static bool IsDeviceGroupNameValid(string name) =>
        name.Length is > 0 and <= DeviceGroupNameLimit && !name.Any(char.IsControl) &&
        !DeviceGroupComparer.Equals(name, UngroupedLabel);

    private static string DeviceGroupLabel(string group) => group.Length == 0 ? UngroupedLabel : group;

    private void RefreshProfiles(Guid? selectId = null)
    {
        if (Profiles is null || ConnectionFilter is null)
            return;
        if (selectId is { } id && store.Connections.Find(item => item.Profile.Id == id) is { } device)
            collapsedDeviceGroups.Remove(device.Profile.Group);

        FillDeviceList(Profiles, ConnectionFilter.Text.Trim(), collapsedDeviceGroups, selectId,
            async entry => await OpenSavedConnectionAsync(entry), () => RefreshProfiles());
    }

    private void FillDeviceList(ListView list, string query, HashSet<string> collapsed, Guid? selectId,
        Action<SavedConnection> open, Action refresh, Action<Func<Task>, bool>? defer = null)
    {
        var selectedId = selectId ?? (list.SelectedItem as ListViewItem)?.Tag as Guid?;
        var grouped = store.Connections.OrderBy(entry => entry.Profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToLookup(entry => entry.Profile.Group, DeviceGroupComparer);
        var groups = DeviceGroupChoices().Prepend("");
        list.Items.Clear();
        if (grouped.Count > 0 || store.DeviceGroups.Count > 0)
        {
            foreach (var group in groups)
            {
                var groupMatches = DeviceGroupLabel(group).Contains(query, StringComparison.CurrentCultureIgnoreCase);
                var visible = grouped[group].Select(entry =>
                {
                    var profile = entry.Profile;
                    var route = (profile.Proxy, profile.Jump) switch
                    {
                        (not null, not null) => L10n.Get("SD1AD12359482"),
                        (not null, null) => L10n.Get("S5E84EA61E838"),
                        (null, not null) => L10n.Get("SE7988FBD7AAB"),
                        _ => ""
                    };
                    var target = store.HideDeviceAddress ? "•••" : ConnectionTarget(profile);
                    return (Entry: entry, Name: DeviceName(profile), Detail: route.Length > 0 ? $"{target}  ·  {route}" : target);
                }).Where(row => groupMatches || row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    row.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
                if (query.Length > 0 && !groupMatches && visible.Length == 0)
                    continue;

                var expanded = query.Length > 0 || !collapsed.Contains(group);
                list.Items.Add(BuildDeviceGroupRow(list, group, visible.Length, expanded, query.Length == 0,
                    collapsed, refresh, defer));
                if (!expanded)
                    continue;
                foreach (var (entry, name, detail) in visible)
                {
                    var id = entry.Profile.Id;
                    var item = new ListViewItem
                    {
                        Tag = id, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(18, 5, 6, 5),
                        Content = new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis },
                                new TextBlock { Text = detail, FontSize = 11, Opacity = .66, TextTrimming = TextTrimming.CharacterEllipsis }
                            }
                        }
                    };
                    void Open()
                    {
                        if (store.Connections.Find(device => device.Profile.Id == id) is { } current)
                            open(current);
                    }
                    item.RightTapped += (_, _) => list.SelectedItem = item;
                    item.ContextFlyout = BuildDeviceMenu(id, collapsed, defer, refresh);
                    item.IsDoubleTapEnabled = true;
                    item.AddHandler(UIElement.DoubleTappedEvent, new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler((_, args) =>
                    { args.Handled = true; Open(); }), true);
                    item.KeyDown += async (_, args) =>
                    {
                        if (args.Key == VirtualKey.F2)
                        {
                            args.Handled = true;
                            if (defer is not null) defer(() => EditConnectionAsync(entry, true), false);
                            else
                            {
                                try { await EditConnectionAsync(entry, true); }
                                catch (Exception ex) { Status.Text = ex.Message; }
                            }
                            return;
                        }
                        if (args.Key != VirtualKey.Enter)
                            return;
                        args.Handled = true;
                        Open();
                    };
                    list.Items.Add(item);
                    if (id == selectedId)
                        list.SelectedItem = item;
                }
            }
        }

        if (list.Items.Count == 0)
        {
            list.Items.Add(new ListViewItem
            {
                IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock
                {
                    Text = query.Length == 0 ? L10n.Get("S5B3B51A3166D") : L10n.Get("SBAF57908CD6D"),
                    TextWrapping = TextWrapping.Wrap, Opacity = .75, Margin = new Thickness(3, 8, 3, 8)
                }
            });
        }
    }

    private ListViewItem BuildDeviceGroupRow(ListView list, string group, int count, bool expanded, bool canCollapse,
        HashSet<string> collapsed, Action refresh, Action<Func<Task>, bool>? defer)
    {
        var title = DeviceGroupLabel(group);
        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon
        {
            Glyph = expanded ? "\uE70D" : "\uE76C", FontSize = 10, Opacity = .72,
            VerticalAlignment = VerticalAlignment.Center
        });
        var name = new TextBlock
        {
            Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var badge = new Border
        {
            Background = WorkspaceTheme.Brush("ShellCardBrush"), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 1, 6, 1),
            Child = new TextBlock { Text = count.ToString(), Foreground = WorkspaceTheme.Brush("ShellMutedBrush"), FontSize = 11 }
        };
        Grid.SetColumn(badge, 2);
        row.Children.Add(badge);

        var item = new ListViewItem
        {
            Tag = new DeviceGroupRow(group), Padding = new Thickness(7, 4, 6, 4), MinHeight = 34,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, AllowDrop = true
        };
        AutomationProperties.SetName(item, L10n.Format("S5249B915DECE", title, count, (expanded ? L10n.Get("SDF647B73B990") : L10n.Get("SEE60469308D3"))));
        if (canCollapse)
        {
            var button = new Button
            {
                Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 4, 6, 4), MinHeight = 30
            };
            AutomationProperties.SetName(button, AutomationProperties.GetName(item));
            item.IsTabStop = false;
            item.Padding = new Thickness(0);
            item.Content = button;
            void Toggle()
            {
                var restoreKeyboardFocus = button.FocusState == FocusState.Keyboard;
                if (!collapsed.Add(group))
                    collapsed.Remove(group);
                refresh();
                var current = list.Items.OfType<ListViewItem>().FirstOrDefault(candidate =>
                    candidate.Tag is DeviceGroupRow label && DeviceGroupComparer.Equals(label.Name, group));
                if (current is not null)
                {
                    list.ScrollIntoView(current);
                    list.SelectedItem = current;
                    if (restoreKeyboardFocus && current.Content is Button updated)
                        updated.Focus(FocusState.Keyboard);
                }
            }
            button.Click += (_, _) => Toggle();
        }
        else
        {
            item.Content = row;
        }

        var menu = new MenuFlyout();
        Add(L10n.Get("SBE8F04E7C1D6"), () => EditConnectionAsync(null, true, initialGroup: group));
        Add(L10n.Get("S0000C8080F64"), () => CreateDeviceGroupAsync(), reopen: true);
        if (group.Length > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(L10n.Get("S13888A54EDAA"), () => RenameDeviceGroupAsync(group), reopen: true);
            Add(L10n.Get("S2FCE1D553FDA"), () => RemoveDeviceGroupAsync(group), reopen: true);
        }
        item.ContextFlyout = menu;
        item.DragOver += (_, args) =>
        {
            if (!args.DataView.Contains(deviceDragFormat))
                return;
            args.AcceptedOperation = DataPackageOperation.Move;
            args.DragUIOverride.Caption = L10n.Format("S83BF259822CC", title);
            args.Handled = true;
        };
        item.Drop += async (_, args) =>
        {
            if (!args.DataView.Contains(deviceDragFormat))
                return;
            args.AcceptedOperation = DataPackageOperation.Move;
            args.Handled = true;
            var deferral = args.GetDeferral();
            try
            {
                if (await args.DataView.GetDataAsync(deviceDragFormat) is string token && Guid.TryParse(token, out var id))
                {
                    collapsed.Remove(group);
                    MoveDeviceToGroup(id, group);
                    if (defer is not null)
                        refresh();
                }
            }
            catch (Exception ex) { Status.Text = ex.Message; }
            finally { deferral.Complete(); }
        };
        return item;

        void Add(string label, Func<Task> action, bool reopen = false)
        {
            var command = new MenuFlyoutItem { Text = label };
            command.Click += async (_, _) =>
            {
                if (defer is not null)
                {
                    defer(action, reopen);
                    return;
                }
                try { await action(); }
                catch (Exception ex) { Status.Text = ex.Message; }
            };
            menu.Items.Add(command);
        }
    }

    private void Profiles_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (args.Items.Count != 1 || args.Items[0] is not ListViewItem { Tag: Guid id })
        {
            args.Cancel = true;
            return;
        }
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetData(deviceDragFormat, id.ToString("D"));
    }

    private async void CreateDeviceGroup_Click(object sender, RoutedEventArgs args)
    {
        try { await CreateDeviceGroupAsync(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async Task CreateDeviceGroupAsync(Guid? deviceId = null)
    {
        if (database is null || await AskDeviceGroupNameAsync() is not { } name)
            return;
        SaveDeviceGroupChange(() =>
        {
            RememberDeviceGroup(name);
            if (deviceId is { } id)
                UpdateDeviceGroup(id, name);
        }, deviceId);
        Status.Text = deviceId is null ? L10n.Format("SE22FF20316D1", name) : L10n.Format("S0503D6055A50", name);
    }

    private async Task RenameDeviceGroupAsync(string group)
    {
        if (await AskDeviceGroupNameAsync(group) is not { } name || group == name)
            return;
        ChangeDeviceGroup(group, name);
        if (collapsedDeviceGroups.Remove(group))
        {
            collapsedDeviceGroups.Add(name);
            RefreshProfiles();
        }
        Status.Text = L10n.Format("S7D34F0CDE1DB", name);
    }

    private async Task RemoveDeviceGroupAsync(string group)
    {
        var dialog = Dialog(L10n.Get("S7605C1D82EED"), L10n.Format("S621E01DC4771", group, UngroupedLabel), L10n.Get("S7605C1D82EED"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        ChangeDeviceGroup(group, "");
        collapsedDeviceGroups.Remove(group);
        Status.Text = L10n.Format("SD35434EE16D6", group);
    }

    private async Task<string?> AskDeviceGroupNameAsync(string? original = null)
    {
        var name = new TextBox { Header = L10n.Get("SD7E266BDC806"), Text = original ?? "", MaxLength = DeviceGroupNameLimit };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush(255, 171, 160) };
        var content = new StackPanel { Spacing = 8, MinWidth = 245, Children = { name, error } };
        var dialog = Dialog(original is null ? L10n.Get("S3D49D9FCB6D1") : L10n.Get("S5218CD8D059E"), content, L10n.Get("SA3030BF8F16D"));
        dialog.Opened += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var value = name.Text.Trim();
            if (!IsDeviceGroupNameValid(value))
                error.Text = L10n.Format("SD5BF40A10DAF", UngroupedLabel);
            else if (DeviceGroupChoices().Any(group => DeviceGroupComparer.Equals(group, value) &&
                !DeviceGroupComparer.Equals(group, original)))
                error.Text = L10n.Get("SB397AC35AE30");
            else
                return;
            args.Cancel = true;
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? name.Text.Trim() : null;
    }

    private void MoveDeviceToGroup(Guid id, string destination)
    {
        var device = store.Connections.Find(entry => entry.Profile.Id == id);
        if (device is null || DeviceGroupComparer.Equals(device.Profile.Group, destination))
            return;
        SaveDeviceGroupChange(() => UpdateDeviceGroup(id, destination), id);
        Status.Text = L10n.Format("S677833966B5F", DeviceName(device.Profile), DeviceGroupLabel(destination));
    }

    private void UpdateDeviceGroup(Guid id, string destination)
    {
        var index = store.Connections.FindIndex(entry => entry.Profile.Id == id);
        if (index < 0)
            return;
        var entry = store.Connections[index];
        RememberDeviceGroup(entry.Profile.Group);
        RememberDeviceGroup(destination);
        store.Connections[index] = entry with { Profile = entry.Profile with { Group = destination } };
    }

    private void RememberDeviceGroup(string group)
    {
        if (!string.IsNullOrWhiteSpace(group) && !store.DeviceGroups.Contains(group, DeviceGroupComparer))
            store.DeviceGroups.Add(group);
    }

    private void ChangeDeviceGroup(string group, string destination)
    {
        SaveDeviceGroupChange(() =>
        {
            store.DeviceGroups.RemoveAll(name => DeviceGroupComparer.Equals(name, group));
            RememberDeviceGroup(destination);
            for (var i = 0; i < store.Connections.Count; i++)
            {
                var entry = store.Connections[i];
                if (DeviceGroupComparer.Equals(entry.Profile.Group, group))
                    store.Connections[i] = entry with { Profile = entry.Profile with { Group = destination } };
            }
            for (var i = 0; i < store.DeletedConnections.Count; i++)
            {
                var deleted = store.DeletedConnections[i];
                var entry = deleted.Connection;
                if (DeviceGroupComparer.Equals(entry.Profile.Group, group))
                    store.DeletedConnections[i] = deleted with { Connection = entry with { Profile = entry.Profile with { Group = destination } } };
            }
        });
    }

    private void SaveDeviceGroupChange(Action edit, Guid? selectId = null)
    {
        var groups = store.DeviceGroups.ToArray();
        var connections = store.Connections.ToArray();
        var deleted = store.DeletedConnections.ToArray();
        try
        {
            edit();
            SaveWorkspace();
        }
        catch
        {
            store.DeviceGroups.Clear();
            store.DeviceGroups.AddRange(groups);
            store.Connections.Clear();
            store.Connections.AddRange(connections);
            store.DeletedConnections.Clear();
            store.DeletedConnections.AddRange(deleted);
            throw;
        }
        RefreshProfiles(selectId);
        RefreshRecentMenu();
    }
}
