using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private readonly Button connectionButton = new();
    private readonly FontIcon connectionGlyph = new() { Glyph = "\uE945", FontSize = 15 };
    private readonly FontIcon collapseGlyph = new() { Glyph = "\uE70D", FontSize = 14 };
    private readonly Dictionary<string, (IReadOnlyList<RemoteEntry> Entries, long FetchedAt)> directoryCache = new(StringComparer.Ordinal);
    private IReadOnlyList<RemoteEntry> directoryEntries = [];
    private Grid splitter = null!;
    private TabView lower = null!;
    private bool lowerPanelCollapsed;
    private GridLength expandedLowerHeight;
    private double expandedLowerMinimum;

    internal bool LowerPanelCollapsed => lowerPanelCollapsed;

    private Border BuildCommandBar(Func<Task> reconnect, Func<Task> editConnection, Action fullScreen)
    {
        command.PlaceholderText = L10n.Get("S87B144420284");
        command.MinWidth = 0;
        command.MinHeight = 30;
        command.FontSize = 13;
        command.Padding = new Thickness(7, 4, 7, 4);
        command.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(command, L10n.Get("SFDD8A3C76E36"));
        AutomationProperties.SetName(command, L10n.Get("SF4E679127B7C"));

        var historyButton = BuildHistoryButton();
        var optionsButton = BuildOptionsButton();
        var copy = ToolbarButton(new Button(), L10n.Get("S4D7EAACC6707"), new SymbolIcon(Symbol.Copy), () =>
        {
            terminal.CopySelection();
            terminal.FocusTerminal();
            return Task.CompletedTask;
        });
        var paste = ToolbarButton(new Button(), L10n.Get("SDFC1AAD74612"), new SymbolIcon(Symbol.Paste), terminal.PasteAsync);
        var find = ToolbarButton(new Button(), L10n.Get("S7E8D195DE11B"), new SymbolIcon(Symbol.Find), () =>
        {
            terminal.ShowSearch();
            return Task.CompletedTask;
        });
        var settings = ToolbarButton(new Button(), L10n.Get("SAC0F877228FF"), new SymbolIcon(Symbol.Setting), editConnection);
        collapseButton = ToolbarButton(new Button(), L10n.Get("S764D6C9AC754"), collapseGlyph, () =>
        {
            SetLowerPanelCollapsed(!lowerPanelCollapsed);
            terminal.FocusTerminal();
            return Task.CompletedTask;
        });
        var fullscreen = ToolbarButton(new Button(), L10n.Get("S2C34A5B066E2"), new FontIcon { Glyph = "\uE740", FontSize = 14 }, () =>
        {
            fullScreen();
            terminal.FocusTerminal();
            return Task.CompletedTask;
        });

        ToolbarButton(connectionButton, L10n.Get("S3E0CA6923EE4"), connectionGlyph, async () =>
        {
            if (attemptFinished)
                await reconnect();
            else
                Disconnect();
        });
        UpdateConnectionButton();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { historyButton, optionsButton, connectionButton, copy, paste, find, settings, collapseButton, fullscreen }
        };
        var content = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(command);
        SetColumn(actions, 1);
        content.Children.Add(actions);
        EnableCommandFocusShortcut();
        content.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 880;
            SetColumnSpan(command, compact ? 2 : 1);
            SetColumn(actions, compact ? 0 : 1);
            SetColumnSpan(actions, compact ? 2 : 1);
            SetRow(actions, compact ? 1 : 0);
        };

        var bar = new Border
        {
            Background = WorkspaceTheme.Brush("ShellToolbarBrush"),
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5, 3, 5, 3), Child = content
        };
        return new Border
        {
            Background = WorkspaceTheme.Brush("ShellBaseBrush"),
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"),
            BorderThickness = new Thickness(1, 0, 1, 1),
            Padding = new Thickness(5, 3, 5, 5),
            Child = bar
        };
    }

    private Button? collapseButton;

    private void EnableCommandFocusShortcut()
    {
        var controlOnly = false;
        long lastTap = -1000;
        static bool IsControl(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl;
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (IsControl(args.Key))
                controlOnly = true;
            else
            {
                controlOnly = false;
                lastTap = -1000;
            }
        }), handledEventsToo: true);
        AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, args) =>
        {
            if (!IsControl(args.Key) || !controlOnly)
                return;
            controlOnly = false;
            var now = Environment.TickCount64;
            if (now - lastTap > 350)
            {
                lastTap = now;
                return;
            }
            lastTap = -1000;
            if (command.FocusState == FocusState.Unfocused)
                command.Focus(FocusState.Keyboard);
            else
                terminal.FocusTerminal(FocusState.Keyboard);
        }), handledEventsToo: true);
    }

    internal void SetLowerPanelCollapsed(bool collapsed)
    {
        if (lowerPanelCollapsed == collapsed)
            return;
        lowerPanelCollapsed = collapsed;
        var row = RowDefinitions[3];
        if (collapsed)
        {
            expandedLowerHeight = row.Height;
            expandedLowerMinimum = row.MinHeight;
        }
        row.MinHeight = collapsed ? 0 : expandedLowerMinimum;
        row.Height = collapsed ? new GridLength(0) : expandedLowerHeight;
        RowDefinitions[2].Height = new GridLength(collapsed ? 0 : 7);
        splitter.Visibility = lower.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        collapseGlyph.Glyph = collapsed ? "\uE70E" : "\uE70D";
        if (collapseButton is { } button)
            SetToolbarLabel(button, collapsed ? L10n.Get("S8F8D334AF280") : L10n.Get("S764D6C9AC754"));
        if (collapsed)
            directoryTimer?.Stop();
        else
            QueueLowerPaneUpdate();
        SavePanelLayout();
    }

    private void UpdateConnectionButton()
    {
        var connected = session is not null && !disconnectRequested;
        connectionButton.IsEnabled = attemptFinished || (!disconnectRequested && !closingConnection);
        connectionGlyph.Foreground = connected ? ToolbarBrush(45, 178, 99) : ToolbarBrush(235, 70, 82);
        SetToolbarLabel(connectionButton, attemptFinished ? L10n.Get("S3E0CA6923EE4") :
            disconnectRequested || closingConnection ? L10n.Get("S1CDE2F91091B") : connected ? L10n.Get("SA33F3804768E") : L10n.Get("S3687F953CFE8"));
    }

    private Button ToolbarButton(Button button, string label, IconElement icon, Func<Task> action)
    {
        button.Content = icon;
        button.Width = 34;
        button.MinWidth = 0;
        button.Height = 34;
        button.MinHeight = 0;
        button.Padding = new Thickness(0);
        button.Background = WorkspaceTheme.Brush("ShellToolbarBrush");
        button.BorderThickness = new Thickness(0);
        SetToolbarLabel(button, label);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { if (!disposed) SetStatus(ex.Message); }
        };
        return button;
    }

    private static void SetToolbarLabel(Button button, string label)
    {
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    private Button BuildHistoryButton()
    {
        var button = new Button { Content = L10n.Get("SB0385CFE4B42"), FontSize = 12, Padding = new Thickness(7, 3, 7, 3), MinHeight = 29 };
        SetToolbarLabel(button, L10n.Get("S418F802E4C3F"));
        var list = new ListView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = 280 };
        var filter = new TextBox { PlaceholderText = L10n.Get("SDBFAD9F5567B"), FontSize = 13 };
        var clear = new Button { Content = L10n.Get("SA23802C6323D") };
        var body = new StackPanel { Spacing = 6, Children = { list, filter, clear } };
        var flyout = new Flyout { Content = body, Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        var focusCommand = false;

        void Fill()
        {
            list.ItemsSource = history.AsEnumerable().Reverse()
                .Where(text => text.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            clear.IsEnabled = history.Count > 0;
        }
        flyout.Opening += (_, _) =>
        {
            body.Width = Math.Clamp(ActualWidth - 68, 220, 650);
            filter.Text = "";
            Fill();
        };
        filter.TextChanged += (_, _) => Fill();
        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not string value)
                return;
            command.Text = value;
            command.SelectionStart = value.Length;
            focusCommand = true;
            flyout.Hide();
        };
        flyout.Closed += (_, _) =>
        {
            if (focusCommand && !disposed)
            {
                focusCommand = false;
                DispatcherQueue.TryEnqueue(() => { if (!disposed) command.Focus(FocusState.Programmatic); });
            }
        };
        clear.Click += (_, _) =>
        {
            var original = store.CommandHistory.ToArray();
            store.CommandHistory.RemoveAll(item => item.ProfileId == profile.Id);
            try { saveWorkspace(); }
            catch (Exception ex)
            {
                store.CommandHistory.Clear();
                store.CommandHistory.AddRange(original);
                SetStatus(ex.Message);
                return;
            }
            history.Clear();
            historyIndex = 0;
            Fill();
        };
        button.Flyout = flyout;
        var shortcut = new KeyboardAccelerator { Key = VirtualKey.H, Modifiers = VirtualKeyModifiers.Menu, ScopeOwner = this };
        shortcut.Invoked += (_, args) => { flyout.ShowAt(button); args.Handled = true; };
        button.KeyboardAccelerators.Add(shortcut);
        return button;
    }

    private DropDownButton BuildOptionsButton()
    {
        var clearCommand = new ToggleMenuFlyoutItem { Text = L10n.Get("SE9ED4BF42755") };
        var hidden = new ToggleMenuFlyoutItem { Text = L10n.Get("SC822B522CB88") };
        var clearPaths = new MenuFlyoutItem { Text = L10n.Get("SF0C02FD792D6") };
        var cacheTime = new MenuFlyoutSubItem { Text = L10n.Get("S01B1CC0274BF") };
        var menu = new MenuFlyout();
        menu.Items.Add(clearCommand);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(hidden);
        menu.Items.Add(clearPaths);
        menu.Items.Add(cacheTime);
        var periods = new Dictionary<ToggleMenuFlyoutItem, int>();
        foreach (var (label, seconds) in new[] { (L10n.Get("S363B6AFC345C"), 0), (L10n.Get("SFEEED561BD87"), 30), (L10n.Get("S49A64A97CDBB"), 60), (L10n.Get("S9D9A5E5793D1"), 300) })
        {
            var choice = new ToggleMenuFlyoutItem { Text = label };
            periods.Add(choice, seconds);
            cacheTime.Items.Add(choice);
            choice.Click += (_, _) =>
            {
                if (SaveOption(store.DirectoryCacheSeconds, seconds, value => store.DirectoryCacheSeconds = value))
                    directoryCache.Clear();
                foreach (var (item, duration) in periods)
                    item.IsChecked = store.DirectoryCacheSeconds == duration;
            };
        }
        menu.Opening += (_, _) =>
        {
            clearCommand.IsChecked = store.ClearCommandAfterSend;
            hidden.IsChecked = store.ShowHiddenFiles;
            foreach (var (item, duration) in periods)
                item.IsChecked = store.DirectoryCacheSeconds == duration;
        };
        clearCommand.Click += (_, _) =>
            SaveOption(store.ClearCommandAfterSend, clearCommand.IsChecked, value => store.ClearCommandAfterSend = value);
        hidden.Click += (_, _) =>
        {
            if (SaveOption(store.ShowHiddenFiles, hidden.IsChecked, value => store.ShowHiddenFiles = value))
                PresentDirectory();
        };
        clearPaths.Click += (_, _) => { directoryCache.Clear(); SetStatus(L10n.Get("S2CDBAC113F77")); };
        return new DropDownButton { Content = L10n.Get("SBB7486F4410F"), Flyout = menu, FontSize = 12, Padding = new Thickness(6, 3, 6, 3), MinHeight = 29 };
    }

    private bool SaveOption<T>(T previous, T next, Action<T> assign)
    {
        assign(next);
        try { saveWorkspace(); return true; }
        catch (Exception ex) { assign(previous); SetStatus(ex.Message); return false; }
    }

    private static SolidColorBrush ToolbarBrush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
}
