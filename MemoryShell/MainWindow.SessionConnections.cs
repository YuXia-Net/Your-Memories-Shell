using System.Security.Cryptography;
using MemoryShell.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private readonly Dictionary<TabViewItem, ConnectionTab> connectionTabs = [];
    private bool retryDialogOpen;
    private AppWindowPresenter? windowedSessionPresenter;

    private async Task OpenSessionAsync(ConnectionProfile profile, string? secret, byte[]? privateKey, bool forceNewTab = false)
    {
        NavigateWorkspace(WorkspacePage.Sessions);
        var state = new ConnectionTab(profile, secret, privateKey);
        if (privateKey is not null)
            CryptographicOperations.ZeroMemory(privateKey);

        var tab = !forceNewTab && Sessions.SelectedItem is TabViewItem empty && !connectionTabs.ContainsKey(empty) && (empty == StartTab || Equals(empty.Tag, "start"))
            ? empty : new TabViewItem();
        if (ReferenceEquals(Sessions.SelectedItem, tab)) Sessions.SelectedItem = null;
        tab.Content = null; tab.Header = DeviceName(profile); tab.Tag = profile.Id; tab.IsClosable = true;
        tab.Content = state.ContentHost;
        var menu = new MenuFlyout();
        var quick = new MenuFlyoutItem { Text = L10n.Get("SEE7F78AFE268") };
        quick.Click += QuickConnect_Click;
        menu.Items.Add(quick);
        var duplicate = new MenuFlyoutItem { Text = L10n.Get("S5B3A9FA8C7B5") };
        duplicate.Click += async (_, _) =>
        {
            if (connectionTabs.TryGetValue(tab, out var original))
            {
                try { await OpenSessionAsync(original.Profile, original.Secret, original.PrivateKey?.ToArray(), forceNewTab: true); }
                catch (Exception ex) { Status.Text = DisplaySessionStatus(profile, ex.Message); }
            }
        };
        menu.Items.Add(duplicate);
        menu.Items.Add(new MenuFlyoutSeparator());
        var toggle = new MenuFlyoutItem();
        toggle.Click += async (_, _) =>
        {
            if (panes.TryGetValue(tab, out var current) && state.Running) current.Disconnect();
            else await OfferRetryAsync(tab, state, automatic: false);
        };
        menu.Items.Add(toggle);
        var edit = new MenuFlyoutItem { Text = L10n.Get("SA7F143E0A81D") };
        edit.Click += async (_, _) => await EditSessionSettingsAsync(tab, state);
        menu.Items.Add(edit);
        menu.Items.Add(new MenuFlyoutSeparator());
        var close = new MenuFlyoutItem { Text = L10n.Get("S965DF3BCE4C1") };
        close.Click += (_, _) => CloseSessionTab(tab);
        menu.Items.Add(close);
        var others = new MenuFlyoutItem { Text = L10n.Get("SE77F10BBB0D7") };
        others.Click += (_, _) =>
        {
            foreach (var other in Sessions.TabItems.OfType<TabViewItem>().Where(item => item != tab).ToArray())
                CloseSessionTab(other);
        };
        menu.Items.Add(others);
        menu.Opening += (_, _) =>
        {
            toggle.Text = state.Running ? panes.GetValueOrDefault(tab)?.IsConnected == true ? L10n.Get("SA33F3804768E") : L10n.Get("S3687F953CFE8") : L10n.Get("SA5574109F020");
            others.IsEnabled = Sessions.TabItems.Count > 1;
        };
        tab.ContextFlyout = menu;
        connectionTabs.Add(tab, state);
        SetSidebarMode(true);
        if (!Sessions.TabItems.Contains(tab)) Sessions.TabItems.Add(tab);
        Sessions.SelectedItem = tab;
        await RunConnectionAsync(tab, state);
    }

    private static void SetTabConnectionState(TabViewItem tab, bool? connected)
    {
        var color = connected is true ? Microsoft.UI.ColorHelper.FromArgb(255, 45, 178, 99) :
            connected is false ? Microsoft.UI.ColorHelper.FromArgb(255, 235, 70, 82) : Microsoft.UI.ColorHelper.FromArgb(255, 216, 157, 55);
        tab.IconSource = new FontIconSource { Glyph = "●", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI"),
            FontSize = 10, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color) };
        ToolTipService.SetToolTip(tab, $"{tab.Header} · {(connected is true ? L10n.Get("S5BE0323E8ADC") : connected is false ? L10n.Get("S1F0AC6953E04") : L10n.Get("S694186911AE3"))}");
    }

    private bool IsOpen(TabViewItem tab, ConnectionTab state) =>
        !closed && connectionTabs.TryGetValue(tab, out var active) && ReferenceEquals(active, state);

    private async Task RunConnectionAsync(TabViewItem tab, ConnectionTab state)
    {
        if (!IsOpen(tab, state) || state.Running)
            return;

        state.Running = true;
        state.ApplyPendingConfiguration();
        tab.Header = DeviceName(state.Profile);
        SetTabConnectionState(tab, null);
        state.Failure = null;
        state.AutomaticOffered = false;
        try
        {
            panes.TryGetValue(tab, out var previous);
            var panelLayout = previous?.CapturePanelLayout(workspaceLayout) ?? workspaceLayout;
            
            var configuration = state.Profile;
            var profile = ConnectionTab.CloneJumpKey(configuration);
            var pane = new SessionPane(hwnd, profile, store, SaveWorkspace, TrustHostAsync, text =>
            {
                if (ReferenceEquals(Sessions.SelectedItem, tab))
                    Status.Text = DisplaySessionStatus(profile, text);
            }, _ =>
            {
                if (ReferenceEquals(Sessions.SelectedItem, tab))
                    RefreshMonitor();
            }, connected =>
            {
                SetTabConnectionState(tab, connected);
                if (connected && state.CredentialUpdate is { } saved)
                    SaveRetriedCredential(saved, state.Secret, state.WalletCredentialUpdates);
                state.CredentialUpdate = null;
                state.WalletCredentialUpdates = [];
                RecordConnection(profile, connected);
                if (ReferenceEquals(Sessions.SelectedItem, tab))
                    RefreshMonitor();
            },
                () => OfferRetryAsync(tab, state, automatic: false), () => EditSessionSettingsAsync(tab, state));
            if (previous is not null) { pane.KeepTerminalHistory(previous); previous.Dispose(); }
            pane.ApplyPanelLayout(panelLayout, layout =>
            {
                workspaceLayout = workspaceLayout with
                {
                    TerminalFraction = layout.TerminalFraction, LowerCollapsed = layout.LowerCollapsed, LowerTab = layout.LowerTab,
                    DirectoryTreeVisible = layout.DirectoryTreeVisible
                };
                SaveWorkspaceLayout();
            });
            pane.ApplyFilePreferences(FilePreferences);
            pane.ApplyWorkspaceAppearance(workspaceAppearance, workspaceWallpaper);
            panes[tab] = pane;
            pane.ActivityChanged += RefreshSessionIndicators;
            state.ContentHost.Content = pane;
            RefreshSessionActions();
            UpdateVisibleMonitor();
            var failure = await pane.ConnectAsync(state.Secret, state.PrivateKey?.ToArray());
            if (ReferenceEquals(configuration, state.Profile))
                state.Failure = failure;
        }
        catch (Exception ex)
        {
            if (IsOpen(tab, state))
                Status.Text = DisplaySessionStatus(state.Profile, ex.Message);
        }
        finally { state.Running = false; SetTabConnectionState(tab, false); }

        if (state.ReconnectRequested && IsOpen(tab, state))
        {
            state.ReconnectRequested = false;
            await RunConnectionAsync(tab, state);
        }
        else await OfferRetryAsync(tab, state);
    }

    private async Task EditSessionSettingsAsync(TabViewItem tab, ConnectionTab state, bool reconnectAfterSave = false)
    {
        if (!IsOpen(tab, state) || !await hostDialog.WaitAsync(0))
            return;
        var reconnect = false;
        try
        {
            var saved = store.Connections.Find(item => item.Profile.Id == state.Profile.Id);
            var current = state.PendingConfiguration ?? new SavedConnection(state.Profile with { Group = saved?.Profile.Group ?? state.Profile.Group },
                state.Secret, state.PrivateKey);
            await EditConnectionAsync(current, true, applyToSession: (updated, connect) =>
            {
                if (!IsOpen(tab, state))
                    return;
                reconnect = connect || reconnectAfterSave;
                if (state.Running && !reconnect) state.SetPendingConfiguration(updated);
                else state.UpdateConfiguration(updated);
                tab.Header = DeviceName(state.Profile);
                Status.Text = L10n.Get("S8DC8A2A32D2F");
            });
        }
        finally { hostDialog.Release(); }
        if (reconnect && IsOpen(tab, state))
        {
            if (state.Running)
            {
                state.ReconnectRequested = true;
                if (panes.TryGetValue(tab, out var pane)) pane.Disconnect();
            }
            else await RunConnectionAsync(tab, state);
        }
    }

    private void ToggleWindowFullScreen()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            if (windowedSessionPresenter is { } previous)
                AppWindow.SetPresenter(previous);
            else
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            windowedSessionPresenter = null;
        }
        else
        {
            windowedSessionPresenter = AppWindow.Presenter;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
    }

    private void ToggleWindowFullScreen_Click(object sender, RoutedEventArgs e)
    {
        try { if (workspacePage == WorkspacePage.Probe) ToggleProbeFullScreen(); else ToggleWindowFullScreen(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async Task OfferRetryAsync(TabViewItem tab, ConnectionTab state, bool automatic = true)
    {
        if (!IsOpen(tab, state) || state.Running || !ReferenceEquals(Sessions.SelectedItem, tab))
            return;

        var failure = state.Failure;
        if (automatic && (state.AutomaticOffered || failure is null || failure.Retry == SshRetryAction.None))
            return;
        if (!automatic && (failure is null || failure.Retry == SshRetryAction.None))
        {
            await RunConnectionAsync(tab, state);
            return;
        }
        if (failure is null || retryDialogOpen)
            return;

        retryDialogOpen = true;
        await hostDialog.WaitAsync();
        ContentDialogResult result;
        string? replacement = null;
        SavedConnection? credentialUpdate = null;
        Guid[] walletUpdates = [];
        var profile = state.Profile;
        try
        {
            if (!IsOpen(tab, state) || !ReferenceEquals(Sessions.SelectedItem, tab)) return;
            var auth = failure.Retry == SshRetryAction.AskCredentials;
            var edit = failure.Retry == SshRetryAction.EditConnection;
            var message = new TextBlock
            {
                Text = DisplaySessionStatus(profile, failure.Message), TextWrapping = TextWrapping.Wrap,
                MaxWidth = 430
            };
            var content = new StackPanel { Spacing = 12, Children = { message } };
            var secret = new PasswordBox
            {
                Header = profile.UsesPrivateKey || state.PrivateKey is not null || !string.IsNullOrEmpty(profile.KeyPath)
                    ? L10n.Get("S68CD85F2A69D") : L10n.Get("S1E1BBB4EFF9A")
            };
            var saved = auth ? store.Connections.Find(item => item.Profile.Id == profile.Id &&
                item.Profile.Host == profile.Host && item.Profile.Port == profile.Port &&
                item.Profile.Username == profile.Username && item.Profile.UsesPrivateKey == profile.UsesPrivateKey) : null;
            var remember = new CheckBox { Content = L10n.Get("S0052B3E121C2"), IsChecked = saved?.Profile.RememberCredentials == true };
            var walletKeys = state.PrivateKey is null ? [] : store.KeyWallet.Where(k =>
                k.PrivateKey.AsSpan().SequenceEqual(state.PrivateKey) && k.Passphrase == state.Secret).ToArray();
            var updateWallet = new CheckBox { Content = L10n.Format("UpdateWalletPassphrase", string.Join(", ", walletKeys.Select(k => k.Name))) };
            if (auth)
            {
                content.Children.Add(secret);
                if (saved?.Profile.RememberCredentials == true && profile.RememberCredentials)
                {
                    content.Children.Add(remember);
                    if (walletKeys.Length > 0) content.Children.Add(updateWallet);
                }
            }

            var dialog = Dialog(failure.Title, content, edit ? L10n.Get("SA502ED6AF633") : L10n.Get("S68891AB35CAB"));
            dialog.CanShow = () => IsOpen(tab, state) && ReferenceEquals(Sessions.SelectedItem, tab);
            state.RetryDialog = dialog;
            var operation = dialog.ShowAsync();
            state.AutomaticOffered = true;
            try { result = await operation; }
            finally { state.RetryDialog = null; }
            if (auth && result == ContentDialogResult.Primary)
            {
                replacement = secret.Password;
                if (content.Children.Contains(remember) && remember.IsChecked == true)
                {
                    credentialUpdate = saved;
                    if (updateWallet.IsChecked == true) walletUpdates = walletKeys.Select(k => k.Id).ToArray();
                }
            }
            secret.Password = "";
        }
        catch (Exception)
        {
            if (IsOpen(tab, state))
                Status.Text = DisplaySessionStatus(profile, failure.Message);
            return;
        }
        finally { retryDialogOpen = false; hostDialog.Release(); }

        if (result != ContentDialogResult.Primary || !IsOpen(tab, state))
            return;
        if (failure.Retry == SshRetryAction.EditConnection)
        {
            try
            {
                await EditSessionSettingsAsync(tab, state, reconnectAfterSave: true);
            }
            catch (Exception ex) { Status.Text = DisplaySessionStatus(profile, ex.Message); }
            return;
        }

        if (replacement is not null)
        {
            state.Secret = replacement;
            state.CredentialUpdate = credentialUpdate;
            state.WalletCredentialUpdates = walletUpdates;
        }
        await RunConnectionAsync(tab, state);
    }

    private void SaveRetriedCredential(SavedConnection entry, string? secret, Guid[]? walletIds = null)
    {
        var index = store.Connections.FindIndex(candidate => ReferenceEquals(candidate, entry));
        if (index < 0)
            return;

        var walletChanges = store.KeyWallet.Select((key, position) => (key, position))
            .Where(pair => walletIds?.Contains(pair.key.Id) == true && entry.PrivateKey is not null &&
                pair.key.PrivateKey.AsSpan().SequenceEqual(entry.PrivateKey)).ToArray();
        foreach (var (key, position) in walletChanges) store.KeyWallet[position] = key with { Passphrase = secret };
        store.Connections[index] = entry with { Secret = secret };
        try { SaveWorkspace(); }
        catch (Exception ex)
        {
            store.Connections[index] = entry;
            foreach (var (key, position) in walletChanges) store.KeyWallet[position] = key;
            Status.Text = DisplaySessionStatus(entry.Profile, L10n.Format("SAD30CE1C5FB1", ex.Message));
        }
    }

    private sealed class ConnectionTab(ConnectionProfile profile, string? secret, byte[]? privateKey) : IDisposable
    {
        public ContentControl ContentHost { get; } = new()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        public ConnectionProfile Profile { get; private set; } = CloneJumpKey(profile);
        public string? Secret { get; set; } = secret;
        public SavedConnection? CredentialUpdate { get; set; }
        public Guid[] WalletCredentialUpdates { get; set; } = [];
        public SavedConnection? PendingConfiguration { get; private set; }
        public void SetPendingConfiguration(SavedConnection entry)
        {
            ClearPendingConfiguration();
            PendingConfiguration = entry with { Profile = CloneJumpKey(entry.Profile), PrivateKey = entry.PrivateKey?.ToArray() };
        }
        public void ApplyPendingConfiguration()
        {
            if (PendingConfiguration is { } pending) UpdateConfiguration(pending);
        }
        private void ClearPendingConfiguration()
        {
            if (PendingConfiguration?.PrivateKey is { } key) CryptographicOperations.ZeroMemory(key);
            if (PendingConfiguration?.Profile.Jump?.PrivateKey is { } jumpKey) CryptographicOperations.ZeroMemory(jumpKey);
            PendingConfiguration = null;
        }
        public byte[]? PrivateKey { get; private set; } = privateKey?.ToArray();
        public bool Running { get; set; }
        public bool ReconnectRequested { get; set; }
        public bool AutomaticOffered { get; set; }
        public SshConnectionFailure? Failure { get; set; }
        public QueuedContentDialog? RetryDialog { get; set; }

        public void UpdateConfiguration(SavedConnection entry)
        {
            var profile = CloneJumpKey(entry.Profile);
            var privateKey = entry.PrivateKey?.ToArray();
            ClearPendingConfiguration();
            ClearKeys();
            Profile = profile;
            PrivateKey = privateKey;
            Secret = entry.Secret;
            Failure = null;
            CredentialUpdate = null;
            WalletCredentialUpdates = [];
        }

        internal static ConnectionProfile CloneJumpKey(ConnectionProfile profile)
        {
            if (profile.Jump is not { PrivateKey: { } key } jump)
                return profile;
            return profile with
            {
                Jump = jump with { PrivateKey = key.ToArray() }
            };
        }

        public void Dispose()
        {
            ClearPendingConfiguration();
            RetryDialog?.Hide();
            Secret = null;
            CredentialUpdate = null;
            ClearKeys();
        }

        private void ClearKeys()
        {
            if (PrivateKey is { } key)
                CryptographicOperations.ZeroMemory(key);
            if (Profile.Jump?.PrivateKey is { } jumpKey)
                CryptographicOperations.ZeroMemory(jumpKey);
        }
    }
}



