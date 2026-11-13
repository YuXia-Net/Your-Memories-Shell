using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;

namespace MemoryShell;

internal sealed partial class SessionPane : Grid, IDisposable
{
    private readonly nint hwnd;
    private readonly ConnectionProfile profile;
    private readonly WorkspaceStore store;
    private readonly Action saveWorkspace;
    private readonly Func<HostKeyIdentity, CancellationToken, Task<bool>> trustHost;
    private readonly Action<string> statusChanged;
    private readonly Action<string> metricsChanged;
    private readonly Action<bool> connectionCompleted;
    private bool disconnectRequested;
    private bool closingConnection;
    private bool attemptFinished;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken cancellation;
    private readonly TerminalView terminal = new();
    private readonly TextBox command = new();
    private readonly TextBox path = new() { PlaceholderText = L10n.Get("S1CFA6840570C") };
    private readonly TextBox fileFilter = new() { PlaceholderText = L10n.Get("S43D3DF4FE719"), Width = 190, MinWidth = 0, Margin = new Thickness(0, 0, 12, 0) };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? fileFilterTimer;
    private readonly ListView files = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly List<string> history = [];
    private readonly ListView forwards = new();
    private readonly Dictionary<ListViewItem, (ForwardKind Kind, int Port)> forwardingRows = [];
    private enum ForwardKind { Local, Remote, Dynamic }
    private readonly ComboBox forwardKind = new() { Header = L10n.Get("S34A28FE5C438"), Width = 115 };
    private readonly TextBox localPort = new() { Header = L10n.Get("S41455B9C3FB0"), Text = "0", Width = 143 };
    private readonly TextBox forwardHost = new() { Header = L10n.Get("S252E26071AC9"), Text = "127.0.0.1", Width = 150 };
    private readonly TextBox forwardPort = new() { Header = L10n.Get("SA57EAE462AF9"), Width = 104 };
    private bool editingForward;
    private Button? downloadFileButton;
    private readonly CommandBar fileActions = new() { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock fileStatus = new() { Opacity = .75, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private SshSession? session;
    private string directory = "/";
    private int historyIndex;
    private bool busy;
    private bool disposed;
    private bool monitorVisible;
    private CancellationTokenSource? monitorLifetime;
    private FilePanelPreferences filePreferences = new();
    private Action? refreshFileColumns;
    private bool lowerUpdateQueued;
    private readonly ContentControl[] lowerContentHosts = new ContentControl[4];
    private bool remoteDirectoryOpened;
    private bool IsFilePaneVisible => IsLoaded && !lowerPanelCollapsed && lower.SelectedIndex == 0;

    public string StatusText { get; private set; } = L10n.Get("S8BE0EA36A9BB");
    internal ConnectionProfile Profile => profile;
    internal void KeepTerminalHistory(SessionPane previous)
    {
        terminal.KeepHistoryFrom(previous.terminal);
        terminal.Write("\r\n── " + L10n.Get("ReconnectingHere") + " ──\r\n");
    }
    internal bool IsConnected => session is not null && !disconnectRequested && !disposed;
    internal bool IsTransferring => transferCancellation is not null && !disposed;
    internal event Action? ActivityChanged;
    internal string DisplayStatus => MainWindow.MaskDeviceAddress(profile, StatusText, store.HideDeviceAddress);
    public string MetricsText { get; private set; } = L10n.Get("S336B6DE50359");
    public LinuxMetrics? MetricsSnapshot { get; private set; }

    public SessionPane(nint hwnd, ConnectionProfile profile, WorkspaceStore store, Action saveWorkspace,
        Func<HostKeyIdentity, CancellationToken, Task<bool>> trustHost, Action<string> statusChanged,
        Action<string> metricsChanged, Action<bool> connectionCompleted, Func<Task> reconnect, Func<Task> editConnection)
    {
        this.hwnd = hwnd;
        this.profile = profile;
        this.store = store;
        this.saveWorkspace = saveWorkspace;
        this.trustHost = trustHost;
        this.statusChanged = statusChanged;
        this.metricsChanged = metricsChanged;
        this.connectionCompleted = connectionCompleted;
        history.AddRange(store.CommandHistory.Where(item => item.ProfileId == profile.Id).TakeLast(200).Select(item => item.Text));
        historyIndex = history.Count;
        cancellation = lifetime.Token;
        FlowDirection = FlowDirection.LeftToRight;
        Padding = new Thickness(10, 8, 10, 8);

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.3, GridUnitType.Star), MinHeight = 130 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(7) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });
        splitter = BuildPaneSplitter();
        SetRow(splitter, 2);
        Children.Add(splitter);
        if (store.Appearance is { } appearance)
            terminal.SetAppearance(appearance);
        var commandFrame = BuildCommandBar(reconnect, editConnection);
        SetRow(commandFrame, 1);
        Children.Add(commandFrame);
        Children.Add(new Border { BorderThickness = new Thickness(1, 1, 1, 0), CornerRadius = new CornerRadius(8, 8, 0, 0), BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), Child = terminal });

        lower = new TabView { IsAddTabButtonVisible = false, TabWidthMode = TabViewWidthMode.SizeToContent, Background = WorkspaceTheme.Brush("ShellPaneBrush") };
        var lowerHeaders = new[] { "S39932F24FE11", "QuickCommands", "SB52D10315237", "SA2F59F64D262" };
        var lowerIcons = new[] { "\uE8B7", "\uE756", "\uE968", "\uE8AB" };
        for (var index = 0; index < lowerHeaders.Length; index++)
        {
            var host = new ContentControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Background = WorkspaceTheme.Brush("ShellPaneBrush")
            };
            host.Loaded += (_, _) => QueueLowerPaneUpdate();
            lowerContentHosts[index] = host;
            lower.TabItems.Add(new TabViewItem
            {
                IsClosable = false, Header = L10n.Get(lowerHeaders[index]), Content = host,
                IconSource = new FontIconSource { Glyph = lowerIcons[index], FontSize = 14 }
            });
        }
        lower.SelectedIndex = 0;
        lower.SelectionChanged += (_, _) => { QueueLowerPaneUpdate(); SavePanelLayout(); };
        Loaded += (_, _) => QueueLowerPaneUpdate();
        Unloaded += (_, _) => { directoryTimer?.Stop(); fileFilterTimer?.Stop(); };
        lowerFrame = new Border { Child = lower, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), Background = WorkspaceTheme.Brush("ShellPaneBrush") };
        SetRow(lowerFrame, 3);
        Children.Add(lowerFrame);

        command.KeyDown += Command_KeyDown;
        path.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await FileActionAsync(() => RefreshFilesAsync(path.Text.Trim(), useCache: true));
            }
        };
        AttachDirectoryFollow();
        AttachZmodem();
        terminal.InputReceived += async text => await SendTerminalAsync(text);
        terminal.TerminalResized += (columns, rows) =>
        {
            try { session?.Resize(columns, rows); }
            catch (Exception ex) { if (!disposed) SetStatus(ex.Message); }
        };
    }

    private void QueueLowerPaneUpdate()
    {
        if (disposed || lowerUpdateQueued)
            return;
        lowerUpdateQueued = DispatcherQueue.TryEnqueue(() =>
        {
            lowerUpdateQueued = false;
            if (disposed || !IsLoaded || lowerPanelCollapsed || lower.SelectedIndex is < 0 or >= 4)
                return;

            // Keep the TabView's selected content presenter attached while each pane loads.
            var host = lowerContentHosts[lower.SelectedIndex];
            switch (lower.SelectedIndex)
            {
                case 0:
                    host.Content ??= BuildFilePane();
                    SetFileActionsEnabled(session is not null && !busy);
                    if (!remoteDirectoryOpened && session is { } connected)
                    {
                        remoteDirectoryOpened = true;
                        _ = OpenRemoteDirectoryAsync(connected);
                    }
                    else
                    {
                        ScheduleDirectoryFollow();
                    }
                    break;
                case 1:
                    if (host.Content is null)
                        host.Content = BuildCommandPane();
                    else
                        RefreshCommands();
                    break;
                case 2:
                    host.Content ??= BuildForwardPane();
                    break;
                case 3:
                    host.Content ??= BuildTransferHistory();
                    RefreshTransferHistory();
                    break;
            }
        });
    }

    private UIElement BuildFilePane()
    {
        var panel = new Grid { Padding = new Thickness(10, 8, 10, 6), RowSpacing = 6 };
        var browser = new Grid();
        var fullTemplate = (DataTemplate)Application.Current.Resources["RemoteFileRowExplorerTemplate"];
        var compactTemplate = (DataTemplate)Application.Current.Resources["RemoteFileRowCompactTemplate"];
        files.ItemTemplate = fullTemplate;
        UpdateFileRowStyle();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var address = new Grid { ColumnSpacing = 6 };
        address.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        address.ColumnDefinitions.Add(new ColumnDefinition());
        address.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        address.Children.Add(BuildDirectoryNavigation());
        var explorerAddress = BuildExplorerAddress();
        SetColumn(explorerAddress, 1);
        address.Children.Add(explorerAddress);
        var go = BuildPathHistoryButton();
        SetColumn(go, 2);
        address.Children.Add(go);
        address.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var follow = BuildDirectoryFollowButton();
        SetColumn(follow, 3);
        address.Children.Add(follow);
        panel.Children.Add(address);

        fileActions.Content = fileFilter;
        fileFilter.TextChanged += (_, _) =>
        {
            if (fileFilterTimer is null)
            {
                fileFilterTimer = DispatcherQueue.CreateTimer();
                fileFilterTimer.IsRepeating = false;
                fileFilterTimer.Interval = TimeSpan.FromMilliseconds(120);
                fileFilterTimer.Tick += (_, _) => { if (!disposed) PresentDirectory(refreshTree: false); };
            }
            fileFilterTimer.Stop();
            fileFilterTimer.Start();
        };

        fileActions.PrimaryCommands.Add(FileCommand(L10n.Get("SAEE887434131"), Symbol.Refresh, () => FileActionAsync(() => RefreshFilesAsync(directory))));
        fileActions.PrimaryCommands.Add(FileCommand(L10n.Get("S9E07E3C0532D"), Symbol.Upload, UploadAsync));
        downloadFileButton = FileCommand(L10n.Get("S4673A2306165"), Symbol.Download, DownloadAsync);
        fileActions.PrimaryCommands.Add((AppBarButton)downloadFileButton);
        fileActions.PrimaryCommands.Add(FileCommand(L10n.Get("S84244ABC71DE"), Symbol.NewFolder, CreateFolderAsync));
        var tools = new MenuFlyout();
        AddTool(L10n.Get("S462E85CBDEC4"), UploadFolderAsync);
        AddTool(L10n.Get("SD31450598CFE"), DownloadResumableAsync);
        AddTool(L10n.Get("SA6F914426720"), DownloadArchiveAsync);
        tools.Items.Add(new MenuFlyoutSeparator());
        AddTool(L10n.Get("S0EBDC9A48FB8"), () => FileActionAsync(() => RefreshFilesAsync("/", useCache: true)));
        AddTool(L10n.Get("SC82E44DAE349"), () => CopyRemotePathAsync(directory));
        var treeToggle = new ToggleMenuFlyoutItem { Text = L10n.Get("SF290E9E5C3E7"), IsChecked = directoryTreeVisible };
        treeToggle.Click += (_, _) => { directoryTreeVisible = treeToggle.IsChecked; UpdateTreeVisibility(); SavePanelLayout(); };
        tools.Opening += (_, _) => treeToggle.IsChecked = directoryTreeVisible;
        tools.Items.Add(treeToggle);
        AddTool(L10n.Get("SF1FE6A743824"), InsertRemotePathAsync);
        AddTool(L10n.Get("S0D0CBAC2EEE5"), RenameAsync);
        AddTool(L10n.Get("S60ED26534627"), EditRemoteTextAsync);
        AddTool(L10n.Get("S978CBCA6265D"), ChangeRemotePermissionsAsync);
        AddTool(L10n.Get("S2F9DAA828907"), DeleteAsync);
        fileActions.PrimaryCommands.Add(new AppBarButton { Label = L10n.Get("S9F07D2CE4115"), Icon = new SymbolIcon(Symbol.More), Flyout = tools });
        void AddTool(string title, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = title };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception ex) { if (!disposed) fileStatus.Text = ex.Message; }
            };
            tools.Items.Add(item);
        }

        var fileActionScroll = fileActions;
        SetRow(fileActionScroll, 1);
        panel.Children.Add(fileActionScroll);
        SetFileActionsEnabled(false);
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var header = new Grid { Opacity = .65, Margin = new Thickness(12, 0, 12, 0) };
        var fullHeader = FileHeader(10, [76, 120, 152], [L10n.Get("SD44E9B3D3B31"), L10n.Get("S50DB7447B966"), L10n.Get("SBA40014FF496"), L10n.Get("S257BBCC44C83")], explorer: true);
        var compactHeader = FileHeader(8, [65, 112], [L10n.Get("SD44E9B3D3B31"), L10n.Get("S50DB7447B966"), L10n.Get("S257BBCC44C83")]);
        compactHeader.Visibility = Visibility.Collapsed;
        header.Children.Add(fullHeader);
        header.Children.Add(compactHeader);
        var listing = new Grid();
        listing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listing.RowDefinitions.Add(new RowDefinition());
        listing.Children.Add(new Border { Child = header, Background = WorkspaceTheme.Brush("ShellToolbarBrush"),
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), BorderThickness = new Thickness(0, 0, 0, 1) });
        browser.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        browser.ColumnDefinitions.Add(new ColumnDefinition());
        browser.Children.Add(BuildDirectoryTree());
        SetColumn(listing, 1);
        browser.Children.Add(listing);
        SetRow(browser, 2);
        SetRowSpan(browser, 2);
        SetColumnSpan(browser, 2);
        panel.Children.Add(browser);
        void UpdateTreeVisibility()
        {
            var visible = !filePreferences.ExplorerStyle || (directoryTreeVisible && panel.ActualWidth >= 720);
            browser.ColumnDefinitions[0].Width = new GridLength(visible ? Math.Min(220, Math.Max(120, panel.ActualWidth * .25)) : 0);
            if (directoryTreeBorder is not null) directoryTreeBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        refreshDirectoryTreeVisibility = UpdateTreeVisibility;
        var compact = false;
        void UpdateColumns()
        {
            var narrow = !filePreferences.ExplorerStyle || filePreferences.Columns == FileColumnLayout.Compact ||
                (filePreferences.Columns == FileColumnLayout.Automatic && listing.ActualWidth is > 0 and < 550);
            if (narrow == compact)
                return;
            compact = narrow;
            files.ItemTemplate = narrow ? compactTemplate : fullTemplate;
            fullHeader.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            compactHeader.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        }
        refreshFileColumns = UpdateColumns;
        listing.SizeChanged += (_, _) => UpdateColumns();

        var fileMenu = new MenuFlyout();
        AddFileAction(L10n.Get("S8AE7102BAB9C"), OpenSelectedFileAsync);
        AddFileAction(L10n.Get("S4673A2306165"), DownloadAsync);
        AddFileAction(L10n.Get("SD31450598CFE"), DownloadResumableAsync);
        AddFileAction(L10n.Get("SA6F914426720"), DownloadArchiveAsync);
        AddFileAction(L10n.Get("SB97C49ACB930"), CopyRemotePathAsync);
        AddFileAction(L10n.Get("SF1FE6A743824"), InsertRemotePathAsync);
        fileMenu.Items.Add(new MenuFlyoutSeparator());
        AddFileAction(L10n.Get("S0D0CBAC2EEE5"), RenameAsync);
        AddFileAction(L10n.Get("S978CBCA6265D"), ChangeRemotePermissionsAsync);
        AddFileAction(L10n.Get("S2F9DAA828907"), DeleteAsync);
        fileMenu.Opening += (_, _) =>
        {
            foreach (var item in fileMenu.Items.OfType<MenuFlyoutItem>())
                item.IsEnabled = !busy && session is not null && (SelectedFile is not null ||
                    files.SelectedItems.Count > 0 && (Equals(item.Text, L10n.Get("S4673A2306165")) || Equals(item.Text, L10n.Get("S2F9DAA828907"))));
        };
        files.ContextFlyout = fileMenu;
        files.RightTapped += (_, args) =>
        {
            for (var node = args.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ListViewItem item)
                {
                    if (!files.SelectedItems.Contains(item.Content)) files.SelectedItem = item.Content;
                    break;
                }
                if (ReferenceEquals(node, files))
                    break;
            }
        };

        void AddFileAction(string label, Func<Task> operation)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += async (_, _) =>
            {
                try { if (!busy && files.SelectedItems.Count > 0) await operation(); }
                catch (Exception ex) { if (!disposed) fileStatus.Text = ex.Message; }
            };
            fileMenu.Items.Add(item);
        }

        files.SelectionChanged += (_, _) =>
        {
            SetFileActionsEnabled(session is not null && !busy);
            if (!busy && files.SelectedItems.Count > 0) fileStatus.Text = L10n.Format("FilesSelected", files.SelectedItems.Count);
        };
        files.KeyDown += async (_, args) =>
        {
            if (busy || session is null) return;
            Func<Task>? action = args.Key switch
            {
                VirtualKey.Enter => OpenSelectedFileAsync,
                VirtualKey.Back => () => FileActionAsync(() => RefreshFilesAsync(ParentPath(directory), useCache: true)),
                VirtualKey.F2 => RenameAsync,
                VirtualKey.F5 => () => FileActionAsync(() => RefreshFilesAsync(directory)),
                VirtualKey.Delete => DeleteAsync,
                _ => null
            };
            if (action is null) return;
            args.Handled = true;
            try { await action(); } catch (Exception ex) { if (!disposed) fileStatus.Text = ex.Message; }
        };
        files.DoubleTapped += async (_, _) =>
        {
            await OpenSelectedFileAsync();
        };
        files.AllowDrop = true;
        files.DragOver += (_, e) =>
        {
            if (!busy && session is not null && e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        };
        files.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;

            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                await UploadDroppedAsync(items);
            }
            catch (Exception ex) { if (!disposed) fileStatus.Text = ex.Message; }
            finally { deferral.Complete(); }
        };
        SetRow(files, 1);
        listing.Children.Add(files);
        foreach (var (key, step) in new[] { (VirtualKey.Left, -1), (VirtualKey.Right, 1) })
        {
            var shortcut = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Menu };
            shortcut.Invoked += async (_, args) => { args.Handled = true; await StepDirectoryAsync(step); };
            panel.KeyboardAccelerators.Add(shortcut);
        }

        fileStatus.Text = L10n.Get("S6AFD2D34BE03");
        var transferFooter = new StackPanel { Spacing = 5 };
        transferFooter.Children.Add(BuildTransferCard());
        transferFooter.Children.Add(fileStatus);
        SetRow(transferFooter, 4);
        panel.Children.Add(transferFooter);
        SetColumnSpan(transferFooter, 2);
        panel.SizeChanged += (_, args) =>
        {
            UpdateTreeVisibility();
            var wide = args.NewSize.Width >= 1280;
            SetColumnSpan(address, wide ? 1 : 2);
            SetColumn(fileActionScroll, wide ? 1 : 0);
            SetRow(fileActionScroll, wide ? 0 : 1);
            SetColumnSpan(fileActionScroll, wide ? 1 : 2);
        };
        return panel;
    }

    private Task OpenSelectedFileAsync() => SelectedFile is { IsDirectory: true } entry
        ? FileActionAsync(() => RefreshFilesAsync(entry.FullPath, useCache: true))
        : EditRemoteTextAsync();

    private UIElement BuildForwardPane()
    {
        var panel = new Grid { Padding = new Thickness(8, 0, 8, 8), RowSpacing = 6 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        var inputs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var option in new[] { L10n.Get("SBF4AD761840F"), L10n.Get("S7BA00927DDE9"), L10n.Get("S855437C7C610") })
            forwardKind.Items.Add(option);
        forwardKind.SelectedIndex = 0;
        forwardKind.SelectionChanged += (_, _) =>
        {
            var visibility = forwardKind.SelectedIndex == (int)ForwardKind.Dynamic ? Visibility.Collapsed : Visibility.Visible;
            forwardHost.Visibility = forwardPort.Visibility = visibility;
        };
        inputs.Children.Add(forwardKind);
        inputs.Children.Add(localPort);
        inputs.Children.Add(forwardHost);
        inputs.Children.Add(forwardPort);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Button(L10n.Get("S776D8B0FA88B"), StartForwardAsync));
        actions.Children.Add(Button(L10n.Get("SCAE1ECD8FA8E"), StopForwardAsync));
        SetRow(actions, 1);
        SetRow(forwards, 2);
        panel.Children.Add(inputs);
        panel.Children.Add(actions);
        panel.Children.Add(forwards);
        return panel;
    }

    private async Task StartForwardAsync()
    {
        if (session is not { } connected || editingForward)
            return;
        var kind = (ForwardKind)forwardKind.SelectedIndex;
        var destination = forwardHost.Text.Trim();
        var to = 0;
        if (!int.TryParse(localPort.Text, out var from) || from is < 0 or > 65535 ||
            (kind != ForwardKind.Dynamic && (!int.TryParse(forwardPort.Text, out to) || to is < 1 or > 65535 || destination.Length == 0)))
        {
            SetStatus(L10n.Get("SA00BF07767D7"));
            return;
        }

        editingForward = true;
        try
        {
            var boundPort = kind switch
            {
                ForwardKind.Remote => await connected.StartRemoteForwardAsync(from, destination, to, cancellation),
                ForwardKind.Dynamic => await connected.StartDynamicForwardAsync(from, cancellation),
                _ => await connected.StartLocalForwardAsync(from, destination, to, cancellation)
            };
            if (!disposed && ReferenceEquals(session, connected))
            {
                var label = kind switch
                {
                    ForwardKind.Remote => L10n.Format("SC360596E007B", boundPort, destination, to),
                    ForwardKind.Dynamic => L10n.Format("SC5B9A0189E2F", boundPort),
                    _ => L10n.Format("S9A1AD4B169A7", boundPort, destination, to)
                };
                var item = new ListViewItem { Content = label };
                forwardingRows.Add(item, (kind, boundPort));
                forwards.Items.Add(item);
                SetStatus(L10n.Format("SBA2E44D24B55", label));
            }
        }
        finally { editingForward = false; }
    }

    private async Task StopForwardAsync()
    {
        if (session is not { } connected || editingForward || forwards.SelectedItem is not ListViewItem item || !forwardingRows.TryGetValue(item, out var forward))
            return;

        editingForward = true;
        try
        {
            await (forward.Kind switch
            {
                ForwardKind.Remote => connected.StopRemoteForwardAsync(forward.Port, cancellation),
                ForwardKind.Dynamic => connected.StopDynamicForwardAsync(forward.Port, cancellation),
                _ => connected.StopLocalForwardAsync(forward.Port, cancellation)
            });
            if (!disposed)
            {
                forwardingRows.Remove(item);
                forwards.Items.Remove(item);
                SetStatus(L10n.Format("S393297427966", forward.Port));
            }
        }
        finally { editingForward = false; }
    }

    internal void Disconnect()
    {
        if (disposed || attemptFinished || disconnectRequested)
            return;
        disconnectRequested = true;
        UpdateConnectionButton();
        SetStatus(L10n.Get("S1CDE2F91091B"));
        lifetime.Cancel();
    }

    public async Task<SshConnectionFailure?> ConnectAsync(string? secret, byte[]? privateKey)
    {
        SetStatus(L10n.Format("S0A46800A0FEF", profile.Username, profile.Host, profile.Port));
        terminal.Write(L10n.Get("SD43BD8B0E037"));
        var reported = false;
        SshConnectionFailure? failure = null;
        try
        {
            SshSession connected;
            try { connected = await SshSession.ConnectAsync(profile, secret, trustHost, cancellation, privateKey); }
            finally
            {
                if (privateKey is not null)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(privateKey);
                if (profile.Jump?.PrivateKey is { } jumpKey)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(jumpKey);
            }
            if (disposed || disconnectRequested)
            {
                await CloseTransportAsync(connected);
                return null;
            }

            session = connected;
            UpdateConnectionButton();
            reported = true;
            connectionCompleted(true);
            session.Resize(terminal.Columns, terminal.Rows);
            SetStatus(L10n.Format("SA30CAE28841C", profile.Username, profile.Host, profile.Port));
            terminal.FocusTerminal();
            UpdateMonitoring();
            QueueLowerPaneUpdate();
            await Task.Run(() => connected.ReadTerminalBytesAsync(ReceiveSshBytesAsync, cancellation), cancellation);
            if (!disposed && !disconnectRequested)
            {
                if (!connected.IsTransportConnected)
                    failure = new SshConnectionFailure(L10n.Get("S4E472165EB0D"), L10n.Get("SEE5E0A2AABE1"), SshRetryAction.Reconnect);
                SetStatus(failure?.Message ?? L10n.Get("SE68917D674D1"));
            }
        }
        catch (Exception) when (disposed || disconnectRequested) { }
        catch (Exception ex)
        {
            if (!reported) connectionCompleted(false);
            failure = SshConnectionErrors.Classify(ex, profile, privateKey is not null || !string.IsNullOrEmpty(profile.KeyPath));
            var detail = failure.Message;
            SetStatus(detail);
            terminal.Write(L10n.Get("S434BC85FCCCA") + SafeTerminalText(MainWindow.MaskDeviceAddress(profile, detail, store.HideDeviceAddress)) + "\r\n");
        }
        finally
        {
            if (!disposed)
            {
                CloseZmodem();
                var disconnected = session;
                session = null;
                closingConnection = true;
                UpdateConnectionButton();
                UpdateMonitoring();
                SetFileActionsEnabled(false);
                forwards.Items.Clear();
                forwardingRows.Clear();
                SetMetrics(L10n.Get("SFD1C442912FC"));
                if (disconnected is not null)
                    await CloseTransportAsync(disconnected);
                if (!disposed)
                {
                    attemptFinished = true;
                    closingConnection = false;
                    UpdateConnectionButton();
                    if (disconnectRequested)
                        SetStatus(L10n.Get("SB13C23335121"));
                }
            }
        }
        return failure;
    }

    private async Task OpenRemoteDirectoryAsync(SshSession connected)
    {
        await FileActionAsync(async () =>
        {
            var working = await connected.GetWorkingDirectoryAsync(cancellation);
            if (homeDirectoryNode is not null) treePaths[homeDirectoryNode] = working;
            await RefreshFilesAsync(working);
            if (homeDirectoryNode is not null) homeDirectoryNode.IsExpanded = true;
        });
    }

    public void SetMonitoringVisible(bool visible)
    {
        monitorVisible = visible;
        UpdateMonitoring();
    }

    private void UpdateMonitoring()
    {
        if (!disposed && monitorVisible && session is { } connected)
        {
            if (monitorLifetime is null)
            {
                monitorLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                _ = MonitorAsync(connected, monitorLifetime.Token);
            }
        }
        else if (monitorLifetime is { } monitoring)
        {
            monitorLifetime = null;
            monitoring.Cancel();
            monitoring.Dispose();
        }
    }

    private async Task MonitorAsync(SshSession connected, CancellationToken token)
    {
        var monitor = new LinuxMonitor(connected);
        while (!token.IsCancellationRequested && ReferenceEquals(session, connected))
        {
            try
            {
                var sample = await monitor.ReadAsync(token);
                if (token.IsCancellationRequested || !ReferenceEquals(session, connected))
                    return;
                SetMetrics("", sample);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && ReferenceEquals(session, connected))
                    SetMetrics(MainWindow.MaskDeviceAddress(profile, L10n.Format("SAE0E6212EA77", ex.Message), store.HideDeviceAddress));
            }

            try { await Task.Delay(TimeSpan.FromSeconds(4), token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SendTerminalAsync(string text)
    {
        if (session is not { } connected || IsZmodemBusy)
            return;
        try { await connected.WriteAsync(text, cancellation); }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) { if (!disposed) SetStatus(ex.Message); }
    }

    private async Task SendCommandAsync(string text, bool clear)
    {
        if (session is null || IsZmodemBusy || string.IsNullOrWhiteSpace(text))
            return;

        if (history.Count == 0 || history[^1] != text)
            history.Add(text);
        if (history.Count > 200)
            history.RemoveAt(0);
        historyIndex = history.Count;
        if (clear)
        {
            var records = store.CommandHistory;
            var item = new CommandEvent(profile.Id, text);
            records.Add(item);
            var oldest = records.Count > 1000 ? records[0] : null;
            if (oldest is not null)
                records.RemoveAt(0);
            try { saveWorkspace(); }
            catch (Exception ex)
            {
                records.RemoveAt(records.Count - 1);
                if (oldest is not null)
                    records.Insert(0, oldest);
                SetStatus(L10n.Format("SFDC7003A1DF1", ex.Message));
            }
        }
        if (clear && store.ClearCommandAfterSend)
            command.Text = "";
        await SendTerminalAsync(text + "\r");
        if (!disposed && !clear)
            terminal.FocusTerminal();
    }

    private async void Command_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            terminal.FocusTerminal();
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SendCommandAsync(command.Text, true);
        }
        else if ((e.Key is VirtualKey.Up or VirtualKey.Down) && history.Count > 0)
        {
            e.Handled = true;
            historyIndex = Math.Clamp(historyIndex + (e.Key == VirtualKey.Up ? -1 : 1), 0, history.Count);
            command.Text = historyIndex == history.Count ? "" : history[historyIndex];
            command.SelectionStart = command.Text.Length;
        }
    }

    private async Task RefreshFilesAsync(string requested, bool useCache = false, int? navigationPosition = null)
    {
        var connected = session ?? throw new InvalidOperationException(L10n.Get("SE94C16412A7E"));
        var target = NormalizePath(requested.StartsWith('/') ? requested : RemotePath(directory, requested));
        if (!useCache)
            directoryCache.Clear();
        if (!directoryCache.TryGetValue(target, out var cached) || store.DirectoryCacheSeconds <= 0 ||
            Environment.TickCount64 - cached.FetchedAt >= store.DirectoryCacheSeconds * 1000L)
        {
            cached = (await connected.ListDirectoryAsync(target, cancellation), Environment.TickCount64);
            if (disposed || !ReferenceEquals(session, connected))
                return;
            if (store.DirectoryCacheSeconds > 0 && cached.Entries.Count <= 2000)
            {
                if (directoryCache.Count >= 16)
                    directoryCache.Remove(directoryCache.MinBy(pair => pair.Value.FetchedAt).Key);
                directoryCache[target] = cached;
            }
        }
        if (disposed || !ReferenceEquals(session, connected))
            return;

        if (directory != target) fileFilter.Text = "";
        directory = target;
        path.Text = target;
        editingAddress = false;
        UpdateExplorerAddress();
        directoryEntries = cached.Entries;
        PresentDirectory();
        RecordDirectoryNavigation(target, navigationPosition);
        RememberRemotePath(target);
    }

    private void PresentDirectory(bool refreshTree = true)
    {
        if (refreshFileColumns is null)
            return;
        fileFilterTimer?.Stop();
        var selectedPath = SelectedFile?.FullPath;
        var query = fileFilter.Text.Trim();
        var rows = SortFileEntries(directoryEntries.Where(entry => (store.ShowHiddenFiles || !entry.Name.StartsWith('.')) &&
                entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .Select(entry => new RemoteFileRow(entry, entry.Name,
            entry.IsDirectory ? "—" : FormatSize(entry.Size),
            entry.IsSymbolicLink ? L10n.Get("S305378DDD2B8") : entry.IsDirectory ? L10n.Get("S7C7802D8ADAE") : L10n.Get("S39932F24FE11"),
            FormatRemotePermissions(entry.UnixMode),
            entry.UserId is { } uid && entry.GroupId is { } gid ? $"{uid}/{gid}" : "—",
            entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm"))).ToArray();
        if (refreshTree) UpdateDirectoryTree();
        files.ItemsSource = rows;
        if (selectedPath is not null)
            files.SelectedItem = rows.FirstOrDefault(item => item.Entry.FullPath == selectedPath);
        fileStatus.Text = L10n.Format("S8AF892E81407", rows.Length);
        foreach (var (label, title, sort) in sortHeaders)
            label.Text = title + (filePreferences.SortBy == sort ? filePreferences.Descending ? " ↓" : " ↑" : "");
    }

    internal void ApplyFilePreferences(FilePanelPreferences preferences)
    {
        if (filePreferences == preferences)
            return;
        filePreferences = preferences;
        if (refreshFileColumns is null)
            return;
        UpdateFileRowStyle();
        refreshFileColumns();
        refreshDirectoryTreeVisibility?.Invoke();
        UpdateExplorerAddress();
        if (session is not null)
            PresentDirectory();
    }

    private void UpdateFileRowStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, (double)filePreferences.RowHeight));
        files.ItemContainerStyle = style;
    }

    private IEnumerable<RemoteEntry> SortFileEntries(IEnumerable<RemoteEntry> entries)
    {
        var grouped = entries.OrderBy(entry => filePreferences.FoldersFirst && !entry.IsDirectory);
        return filePreferences.SortBy switch
        {
            FileSortOrder.Size => Sort(entry => entry.Size),
            FileSortOrder.Modified => Sort(entry => entry.LastWriteTime),
            FileSortOrder.Kind => Sort(entry => entry.IsSymbolicLink ? 2 : entry.IsDirectory ? 0 : 1),
            _ => Sort(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        };

        IEnumerable<RemoteEntry> Sort<T>(Func<RemoteEntry, T> key, IComparer<T>? comparer = null) =>
            (filePreferences.Descending ? grouped.ThenByDescending(key, comparer) : grouped.ThenBy(key, comparer))
                .ThenBy(entry => entry.Name, StringComparer.Ordinal);
    }

    private readonly List<(TextBlock Label, string Title, FileSortOrder Sort)> sortHeaders = [];

    private Grid FileHeader(double spacing, int[] widths, string[] labels, bool explorer = false)
    {
        var row = new Grid { ColumnSpacing = spacing };
        if (explorer) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var width in widths)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        for (var i = 0; i < labels.Length; i++)
        {
            var cell = new TextBlock { Text = labels[i], FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            var sort = i switch
            {
                0 => (FileSortOrder?)FileSortOrder.Name, 1 => FileSortOrder.Size,
                2 when labels.Length == 3 => FileSortOrder.Modified,
                2 => FileSortOrder.Kind, 3 when explorer => FileSortOrder.Modified, 5 => FileSortOrder.Modified, _ => null
            };
            FrameworkElement element = cell;
            if (sort is { } order)
            {
                sortHeaders.Add((cell, labels[i], order));
                var button = new Button
                {
                    Content = cell, BorderThickness = new Thickness(0), Padding = new Thickness(0, 3, 0, 3),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    MinWidth = 0, MinHeight = 24, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                };
                ToolTipService.SetToolTip(button, L10n.Format("SEEC049EE6EBE", labels[i]));
                button.Click += (_, _) =>
                {
                    filePreferences = filePreferences with { SortBy = order, Descending = filePreferences.SortBy == order && !filePreferences.Descending };
                    PresentDirectory();
                };
                element = button;
            }
            SetColumn(element, i + (explorer ? 1 : 0));
            row.Children.Add(element);
        }
        return row;
    }

    private AppBarButton FileCommand(string label, Symbol icon, Func<Task> action)
    {
        var button = new AppBarButton { Label = label, Icon = new SymbolIcon(icon) };
        ToolTipService.SetToolTip(button, label);
        button.Click += async (_, _) =>
        {
            try { await action(); } catch (Exception ex) { if (!disposed) fileStatus.Text = ex.Message; }
        };
        return button;
    }

    private void SetFileActionsEnabled(bool enabled)
    {
        UpdateDirectoryNavigation();
        foreach (var control in fileActions.PrimaryCommands.Concat(fileActions.SecondaryCommands).OfType<Control>())
            control.IsEnabled = enabled;
        if (downloadFileButton is not null) downloadFileButton.IsEnabled = enabled && files.SelectedItems.Count > 0;
    }

    private RemoteEntry? SelectedFile => files.SelectedItems.Count == 1 ? (files.SelectedItems[0] as RemoteFileRow)?.Entry : null;

    private async Task FileActionAsync(Func<Task> action)
    {
        if (busy || session is null || disposed)
            return;
        busy = true;
        SetFileActionsEnabled(false);
        try { await action(); }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) { if (!disposed) fileStatus.Text = ex.Message; }
        finally
        {
            busy = false;
            if (!disposed)
            {
                SetFileActionsEnabled(session is not null);
                ScheduleDirectoryFollow();
            }
        }
    }

    private async Task CreateFolderAsync() => await FileActionAsync(async () =>
    {
        var name = await AskNameAsync(L10n.Get("S84244ABC71DE"), "");
        if (name is not null && session is { } connected)
        {
            await connected.CreateDirectoryAsync(RemotePath(directory, name), cancellation);
            await RefreshFilesAsync(directory);
        }
    });

    private async Task RenameAsync()
    {
        if (SelectedFile is not { } entry)
            return;
        await FileActionAsync(async () =>
        {
            var name = await AskNameAsync(L10n.Get("S0D0CBAC2EEE5"), entry.Name);
            if (name is not null && name != entry.Name && session is { } connected)
            {
                await connected.RenameAsync(entry.FullPath, RemotePath(directory, name), cancellation);
                await RefreshFilesAsync(directory);
            }
        });
    }

    private async Task DeleteAsync()
    {
        var selected = SelectedFiles;
        if (selected.Length == 0) return;
        await FileActionAsync(async () =>
        {
            if (!await ConfirmAsync(L10n.Get("SCDE89AFAAE2F"),
                L10n.Format("DeleteFilesPrompt", selected.Length) + "\n" + string.Join("\n", selected.Take(8).Select(f => f.Name)),
                L10n.Get("S2F9DAA828907")) || session is not { } connected) return;
            var batch = new FileBatch();
            foreach (var entry in selected)
            {
                cancellation.ThrowIfCancellationRequested();
                await batch.Run(entry.Name, async () => { await connected.DeleteAsync(entry, cancellation); batch.Completed++; });
            }
            await RefreshFilesAsync(directory);
            FinishBatch(batch);
        });
    }

    private async Task<string?> AskNameAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial, Header = L10n.Get("SD44E9B3D3B31") };
        var dialog = Dialog(title, box, L10n.Get("SFAC2A67AD878"));
        dialog.PrimaryButtonClick += (_, e) =>
        {
            var value = box.Text.Trim();
            e.Cancel = value.Length == 0 || value is "." or ".." || value.Contains('/') || value.Any(char.IsControl);
            if (e.Cancel)
                box.Header = L10n.Get("S0D4E749EFA1F");
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private async Task<bool> ConfirmAsync(string title, string text, string primary) =>
        await Dialog(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, primary)
            .ShowAsync() == ContentDialogResult.Primary;

    private QueuedContentDialog Dialog(string title, UIElement content, string primary) => new()
    {
        CanShow = () => !disposed, Cancellation = cancellation, XamlRoot = XamlRoot, Title = title, Content = content, PrimaryButtonText = primary,
        CloseButtonText = L10n.Get("S2CD0F3BE8738"), DefaultButton = ContentDialogButton.Primary
    };

    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

    private Button Button(string text, Func<Task> click)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) =>
        {
            try { await click(); }
            catch (Exception ex) { if (!disposed) SetStatus(ex.Message); }
        };
        return button;
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        statusChanged(text);
    }

    private void SetMetrics(string text, LinuxMetrics? sample = null)
    {
        MetricsText = text;
        MetricsSnapshot = sample;
        metricsChanged(text);
    }

    private static string RemotePath(string parent, string name) => parent.TrimEnd('/') + "/" + name;
    private static string ParentPath(string target) => target == "/" ? "/" : target[..target.LastIndexOf('/')].PadLeft(1, '/');

    private static string NormalizePath(string target)
    {
        var parts = new List<string>();
        foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }
        return "/" + string.Join('/', parts);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };

    private static string SafeTerminalText(string text) =>
        new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static async Task CloseTransportAsync(SshSession connected)
    {
        try { await Task.Run(connected.Dispose); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        UpdateMonitoring();
        directoryTimer?.Stop();
        fileFilterTimer?.Stop();
        CloseZmodem();
        lifetime.Cancel();
        if (session is { } connected) _ = CloseTransportAsync(connected);
        session = null;
        ActivityChanged?.Invoke();
        ActivityChanged = null;
        terminal.Dispose();
        lifetime.Dispose();
    }
}























