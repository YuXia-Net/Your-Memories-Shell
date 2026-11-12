using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MemoryShell.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace MemoryShell;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<TabViewItem, SessionPane> panes = [];
    private readonly HashSet<Guid> openingProfiles = [];
    private readonly HostMonitorView monitorView;
    private readonly nint hwnd;
    private WorkspaceStore store;
    private VaultDatabase? database;

    public MainWindow()
    {
        store = new WorkspaceStore();
        InitializeComponent();
        monitorView = new HostMonitorView();
        Root.Language = L10n.Code;
        Metrics.Content = monitorView;
        ProjectLinkHost.Content = ProjectLink();
        SessionIndicatorsHost.Content = sessionIndicators;
        InitializeWorkspaceShortcuts();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MemoryShell.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)Math.Round(1280 * scale), workArea.Width);
        var height = Math.Min((int)Math.Round(850 * scale), workArea.Height);
        AppWindow.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height));
        Root.Loaded += StartVault;
        Closed += (_, _) => CloseVault();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private void SaveWorkspace()
    {
        store.Save(database ?? throw new InvalidOperationException(L10n.Get("S259F4BBC9031")));
        vaultRevision++;
        QueueChangedBackup();
        monitorWall?.RefreshConnections();
    }


    private SavedConnection? SelectedProfile => Profiles.SelectedItem is ListViewItem { Tag: Guid id }
        ? store.Connections.Find(c => c.Profile.Id == id) : null;

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => RefreshProfiles();
    private void ShowConnections_Click(object sender, RoutedEventArgs e)
    {
        NavigateWorkspace(WorkspacePage.Sessions);
        SetSidebarVisible(true);
        SetSidebarMode(false);
    }

    private void ShowMonitor_Click(object sender, RoutedEventArgs e)
    {
        NavigateWorkspace(WorkspacePage.Sessions);
        SetSidebarVisible(true);
        SetSidebarMode(true);
    }
    private async void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var selectedGroup = (Profiles.SelectedItem as ListViewItem)?.Tag as DeviceGroupRow;
        await EditConnectionAsync(null, true, selectedGroup?.Name ?? SelectedProfile?.Profile.Group);
    }
    private async void QuickConnect_Click(object sender, RoutedEventArgs e) => await EditConnectionAsync(null, false);
    private async Task OpenSavedConnectionAsync(SavedConnection entry)
    {
        if (!openingProfiles.Add(entry.Profile.Id)) return;
        try
        {
            if ((!entry.Profile.RememberCredentials && string.IsNullOrEmpty(entry.Profile.KeyPath)) ||
                (entry.Secret is null && entry.PrivateKey is null && string.IsNullOrEmpty(entry.Profile.KeyPath)))
            {
                await EditConnectionAsync(entry, false);
                return;
            }
            await OpenSessionAsync(entry.Profile, entry.Secret, entry.PrivateKey?.ToArray());
        }
        catch (Exception ex)
        {
            Status.Text = MaskDeviceAddress(entry.Profile, ex.Message, store.HideDeviceAddress);
        }
        finally { openingProfiles.Remove(entry.Profile.Id); }
    }

    private async Task DeleteDeviceAsync(SavedConnection entry)
    {
        var dialog = Dialog(L10n.Get("S6A5B1CB18FB6"), L10n.Format("S513B65CD4E15", DeviceName(entry.Profile)), L10n.Get("S6A5B1CB18FB6"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var index = store.Connections.IndexOf(entry);
        if (index < 0)
            return;
        var deleted = new DeletedConnection(entry, DateTimeOffset.Now);
        var previousGroupCount = store.DeviceGroups.Count;
        RememberDeviceGroup(entry.Profile.Group);
        store.Connections.RemoveAt(index);
        store.DeletedConnections.Add(deleted);
        try
        {
            SaveWorkspace();
            RefreshProfiles();
            RefreshRecentMenu();
        }
        catch (Exception ex)
        {
            store.DeviceGroups.RemoveRange(previousGroupCount, store.DeviceGroups.Count - previousGroupCount);
            store.DeletedConnections.Remove(deleted);
            store.Connections.Insert(index, entry);
            Status.Text = ex.Message;
        }
    }

    private async Task EditConnectionAsync(SavedConnection? entry, bool save, string? initialGroup = null,
        Action<SavedConnection>? applyToSession = null)
    {
        if (database is null)
            return;

        var original = entry?.Profile;
        var name = new TextBox { Header = L10n.Get("SD44E9B3D3B31"), Text = original?.Name ?? "" };
        var host = new TextBox { Header = L10n.Get("SE87D9F23A3F5"), PlaceholderText = "server.example.com", Text = original?.Host ?? "" };
        var port = new TextBox { Header = L10n.Get("SE71AC32B544B"), PlaceholderText = L10n.Get("SD28D0A73ADC9"), Text = original?.Port.ToString() ?? "" };
        var user = new TextBox { Header = L10n.Get("S1A3F0617D6DE"), Text = original?.Username ?? "" };
        var group = new ComboBox { Header = L10n.Get("S399EE6934772"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var ungrouped = new ComboBoxItem { Content = UngroupedLabel };
        var createGroup = new ComboBoxItem { Content = L10n.Get("S0000C8080F64") };
        var newGroup = new TextBox { Header = L10n.Get("S44CA101D5FF1"), MaxLength = DeviceGroupNameLimit, Visibility = Visibility.Collapsed };
        var currentGroup = original?.Group ?? initialGroup ?? "";
        group.Items.Add(ungrouped);
        group.SelectedItem = ungrouped;
        foreach (var groupName in DeviceGroupChoices())
        {
            var choice = new ComboBoxItem { Content = groupName, Tag = groupName };
            group.Items.Add(choice);
            if (DeviceGroupComparer.Equals(groupName, currentGroup))
                group.SelectedItem = choice;
        }
        group.Items.Add(createGroup);
        group.SelectionChanged += (_, _) =>
        {
            newGroup.Visibility = ReferenceEquals(group.SelectedItem, createGroup) ? Visibility.Visible : Visibility.Collapsed;
        };
        var method = new ComboBox { Header = L10n.Get("SD1FF8F98BC3F"), HorizontalAlignment = HorizontalAlignment.Stretch };
        method.Items.Add(L10n.Get("SA621AB606DB2"));
        method.Items.Add(L10n.Get("S3FFE935F9D0F"));
        var hadKey = original?.UsesPrivateKey == true || entry?.PrivateKey is not null || !string.IsNullOrEmpty(original?.KeyPath);
        method.SelectedIndex = hadKey ? 1 : 0;
        var key = new TextBox
        {
            Header = L10n.Get("S5108BDE065EC"), Text = entry?.PrivateKey is null ? original?.KeyPath ?? "" : "",
            PlaceholderText = entry?.PrivateKey is null ? L10n.Get("SED5B2AB22F6C") : L10n.Get("SEF7D5375BC4B")
        };
        var chooseKey = new Button { Content = L10n.Get("S38FF0696A230") };
        chooseKey.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                if (await picker.PickSingleFileAsync() is { } file)
                    key.Text = file.Path;
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        };
        var wallet = new KeyWalletPicker(store.KeyWallet);
        var keyName = new TextBox { Header = L10n.Get("KeyName"), Text = original?.Name ?? "" };
        var secret = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Hidden };
        var remember = new CheckBox
        {
            Content = L10n.Get("SaveCredentialsToVault"),
            IsChecked = original?.RememberCredentials ?? true
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var device = new StackPanel { Spacing = 10 };
        var auth = new StackPanel { Spacing = 10 };
        var includeInMonitor = new CheckBox { Content = L10n.Get("MonitorInclude"), IsChecked = original?.IncludeInMonitor == true };
        var probeTags = new TextBox { Header = L10n.Get("ProbeTags"), Text = original?.MonitorTags ?? "", MaxLength = 80 };
        var probeWidth = Number(L10n.Get("ProbeWidth"), 1, 32); probeWidth.Value = original?.MonitorColumnSpan ?? 1;
        var probeHeight = Number(L10n.Get("ProbeHeight"), 1, 32); probeHeight.Value = original?.MonitorRowSpan ?? 1;
        var probeOptions = new StackPanel { Spacing = 8, Children = { probeTags, probeWidth, probeHeight }, Visibility = includeInMonitor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed };
        includeInMonitor.Checked += (_, _) => probeOptions.Visibility = Visibility.Visible;
        includeInMonitor.Unchecked += (_, _) => probeOptions.Visibility = Visibility.Collapsed;
        if (save) device.Children.Add(name);
        var address = new Grid { ColumnSpacing = 10 };
        address.ColumnDefinitions.Add(new ColumnDefinition());
        address.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        address.Children.Add(host);
        Grid.SetColumn(port, 1);
        address.Children.Add(port);
        device.Children.Add(address);
        if (save)
        {
            device.Children.Add(group);
            device.Children.Add(newGroup);
        }
        auth.Children.Add(user);
        auth.Children.Add(method);
        auth.Children.Add(wallet);
        auth.Children.Add(key);
        auth.Children.Add(chooseKey);
        auth.Children.Add(secret);
        auth.Children.Add(remember);
        auth.Children.Add(keyName);
        var proxyEditor = new ProxyEditor(original?.Proxy, store.ProxyPresets, SaveWorkspace, store.HideDeviceAddress);
        var jumpEditor = new JumpEditor(original?.Jump, hwnd, store.JumpPresets, SaveWorkspace, store.KeyWallet);

        string KeyPath() => key.Text.Trim().Trim('"');
        string HostName()
        {
            var value = host.Text.Trim();
            return value.StartsWith('[') && value.EndsWith(']') ? value[1..^1] : value;
        }
        bool TryReadPort(out int number) => int.TryParse(string.IsNullOrWhiteSpace(port.Text) ? "22" : port.Text.Trim(), out number);
        bool CanReuseSecret()
        {
            var usingKey = method.SelectedIndex == 1;
            return wallet.SelectedKey is null && entry?.Secret is not null && original is not null && hadKey == usingKey &&
                string.Equals(HostName(), original.Host, StringComparison.OrdinalIgnoreCase) &&
                TryReadPort(out var number) && number == original.Port && user.Text.Trim() == original.Username &&
                (!usingKey || KeyPath().Length == 0 && entry?.PrivateKey is not null ||
                    string.Equals(KeyPath(), original.KeyPath, StringComparison.OrdinalIgnoreCase));
        }
        void RefreshSecretHint() => secret.PlaceholderText = wallet.SelectedKey is { } selectedKey
            ? string.IsNullOrEmpty(selectedKey.Passphrase) ? L10n.Get("SC156CEF0B568") : L10n.Get("SAADCE4DEC186")
            : CanReuseSecret()
            ? entry!.Secret!.Length > 0 ? L10n.Get("SAADCE4DEC186") : L10n.Get("S0D49858EE6BC")
            : method.SelectedIndex == 1 ? L10n.Get("SC156CEF0B568") : L10n.Get("S498BC5C77E73");

        void RefreshMethod()
        {
            var usingKey = method.SelectedIndex == 1;
            wallet.Visibility = usingKey ? Visibility.Visible : Visibility.Collapsed;
            wallet.IsEnabled = remember.IsChecked == true;
            keyName.Visibility = usingKey && remember.IsChecked == true && wallet.SelectedKey is null ? Visibility.Visible : Visibility.Collapsed;
            chooseKey.Visibility = key.Visibility = usingKey && wallet.SelectedKey is null ? Visibility.Visible : Visibility.Collapsed;
            secret.Header = usingKey ? L10n.Get("S465458FC7FBB") : L10n.Get("SA621AB606DB2");
            RefreshSecretHint();
        }
        wallet.SelectionChanged += (_, _) => { secret.Password = ""; RefreshMethod(); };
        method.SelectionChanged += (_, _) => RefreshMethod();
        remember.Checked += (_, _) => RefreshMethod();
        remember.Unchecked += (_, _) => { wallet.SelectedIndex = -1; RefreshMethod(); };
        host.TextChanged += (_, _) => RefreshSecretHint();
        port.TextChanged += (_, _) => RefreshSecretHint();
        user.TextChanged += (_, _) => RefreshSecretHint();
        key.TextChanged += (_, _) => RefreshSecretHint();
        RefreshMethod();

        var title = applyToSession is not null ? L10n.Get("S8B4E5FC935E6") :
            save ? (entry is null ? L10n.Get("S94AA4F221201") : L10n.Get("SA7F143E0A81D")) :
            original is null ? L10n.Get("SBEC3D896410B") : L10n.Format("S43DEE2EE9C40", DeviceName(original));
        var dialog = ConnectionDialog(title,
            save, error, device, auth, proxyEditor, jumpEditor, save ? new StackPanel { Spacing = 10, Children =
            {
                includeInMonitor,
                new TextBlock { Text = L10n.Get("MonitorIncludeNote"), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                probeOptions
            } } : null);
        if (applyToSession is not null)
            dialog.PrimaryButtonText = L10n.Get("SC8550237BA70");
        else if (save)
            dialog.SecondaryButtonText = L10n.Get("S107FECEDCCBA");
        ConnectionProfile? profile = null;
        string? suppliedSecret = null;
        byte[]? sessionKey = null;
        var openMemory = false;
        SavedConnection? savedEntry = null;

        bool Submit(bool connectNow)
        {
            var hostName = HostName();
            var username = user.Text.Trim();
            var portText = port.Text.Trim();
            var creatingGroup = save && ReferenceEquals(group.SelectedItem, createGroup);
            var groupName = !save ? currentGroup : creatingGroup ? newGroup.Text.Trim() :
                (group.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            if (connectNow && entry is null && string.Equals(username, "x", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(host.Text) &&
                (portText.Length == 0 || portText == "22") && string.IsNullOrWhiteSpace(name.Text) && !creatingGroup &&
                string.IsNullOrWhiteSpace(key.Text) && method.SelectedIndex == 0 && secret.Password.Length == 0 &&
                !proxyEditor.IsRouteEnabled && !jumpEditor.IsRouteEnabled)
            {
                openMemory = true;
                return true;
            }

            if (Uri.CheckHostName(hostName) == UriHostNameType.Unknown && !IPAddress.TryParse(hostName, out _))
            {
                error.Text = L10n.Get("S6FF25AE0C677");
                host.Focus(FocusState.Programmatic);
                return false;
            }
            if (!TryReadPort(out var portNumber) || portNumber is < 1 or > 65535)
            {
                error.Text = L10n.Get("S1AD1DA9AE6F0");
                port.Focus(FocusState.Programmatic);
                return false;
            }
            if (username.Length == 0)
            {
                error.Text = L10n.Get("S73B024D6DF1D");
                user.Focus(FocusState.Programmatic);
                return false;
            }
            if (creatingGroup)
            {
                var groupError = !IsDeviceGroupNameValid(groupName) ? L10n.Format("S03D36A259CF8", UngroupedLabel) :
                    DeviceGroupChoices().Contains(groupName, DeviceGroupComparer) ? L10n.Get("SCCE9936BB94E") : null;
                if (groupError is not null)
                {
                    error.Text = groupError;
                    newGroup.Focus(FocusState.Programmatic);
                    return false;
                }
            }

            var keepCredentials = remember.IsChecked == true;
            var usingKey = method.SelectedIndex == 1;
            var selectedKey = usingKey ? wallet.SelectedKey : null;
            var keyPath = usingKey && selectedKey is null ? KeyPath() : "";
            var prepareSession = connectNow || applyToSession is not null;
            if (usingKey && !keepCredentials && keyPath.Length == 0)
            {
                error.Text = L10n.Get("KeyLocalPathRequired");
                return false;
            }
            if (usingKey && selectedKey is null &&
                (keyPath.Length == 0 ? entry?.PrivateKey is null : !File.Exists(keyPath)))
            {
                error.Text = L10n.Get("SB7214DD6470B");
                return false;
            }

            byte[]? importedKey = null;
            byte[]? importedJumpKey = null;
            byte[]? persistedKey = null;
            byte[]? persistedJumpKey = null;
            SavedPrivateKey? addedWalletKey = null;
            try
            {
                var proxy = proxyEditor.Read();
                var jump = jumpEditor.Read(prepareSession || keepCredentials);
                if (jump?.PrivateKey is { } jumpKey && !ReferenceEquals(jumpKey, original?.Jump?.PrivateKey))
                    importedJumpKey = jumpKey;
                var reusedKey = usingKey && keyPath.Length == 0;
                var keyBytes = !keepCredentials ? null : selectedKey is not null ? importedKey = selectedKey.PrivateKey.ToArray() : reusedKey ? entry?.PrivateKey :
                    usingKey && keyPath.Length > 0 ? importedKey = WorkspaceStore.ImportPrivateKey(keyPath) : null;
                suppliedSecret = secret.Password.Length > 0 ? secret.Password :
                    selectedKey is not null ? selectedKey.Passphrase : CanReuseSecret() ? entry!.Secret : usingKey ? null : "";

                var submitted = new ConnectionProfile
                {
                    Id = original?.Id ?? Guid.NewGuid(),
                    Name = string.IsNullOrWhiteSpace(name.Text) ? original?.Name ?? hostName : name.Text.Trim(),
                    Host = hostName, Port = portNumber, Username = username, Group = groupName,
                    UsesPrivateKey = usingKey, RememberCredentials = keepCredentials, Proxy = proxy, Jump = jump,
                    KeyPath = usingKey && !keepCredentials ? Path.GetFullPath(keyPath) : "",
                    IncludeInMonitor = includeInMonitor.IsChecked == true, MonitorTags = probeTags.Text.Trim(),
                    MonitorColumnSpan = ReadNumber(probeWidth), MonitorRowSpan = ReadNumber(probeHeight)
                };
                if (prepareSession)
                {
                    profile = ConnectionTab.CloneJumpKey(submitted);
                    sessionKey = keyBytes?.ToArray();
                }

                var index = store.Connections.FindIndex(item => item.Profile.Id == submitted.Id);
                if (usingKey || keepCredentials || (save && (applyToSession is null || index >= 0)))
                {
                    var persisted = keepCredentials ? submitted : submitted with
                    {
                        Proxy = proxy is null ? null : proxy with { Password = null },
                        Jump = jump is null ? null : jump with { Password = null, PrivateKey = null }
                    };
                    var saved = new SavedConnection(persisted, keepCredentials ? suppliedSecret : null, keepCredentials ? keyBytes : null);
                    if (usingKey && keepCredentials && keyBytes is not null && selectedKey is null &&
                        !store.KeyWallet.Any(k => k.PrivateKey.AsSpan().SequenceEqual(keyBytes) && k.Passphrase == suppliedSecret))
                    {
                        var walletName = string.IsNullOrWhiteSpace(keyName.Text) ? submitted.Name : keyName.Text.Trim();
                        ValidateWalletName(walletName);
                        addedWalletKey = new SavedPrivateKey(Guid.NewGuid(), walletName, keyBytes.ToArray(), suppliedSecret);
                        store.KeyWallet.Add(addedWalletKey);
                    }
                    var previous = index < 0 ? null : store.Connections[index];
                    var previousGroupCount = store.DeviceGroups.Count;
                    RememberDeviceGroup(previous?.Profile.Group ?? "");
                    RememberDeviceGroup(groupName);
                    if (index < 0)
                        store.Connections.Add(saved);
                    else
                        store.Connections[index] = saved;
                    try { SaveWorkspace(); }
                    catch
                    {
                        store.DeviceGroups.RemoveRange(previousGroupCount, store.DeviceGroups.Count - previousGroupCount);
                        if (index < 0)
                            store.Connections.Remove(saved);
                        else
                            store.Connections[index] = previous!;
                        throw;
                    }
                    savedEntry = saved;
                    persistedKey = saved.PrivateKey;
                    persistedJumpKey = saved.Profile.Jump?.PrivateKey;
                    if (previous?.PrivateKey is { } previousKey && !ReferenceEquals(previousKey, persistedKey))
                        CryptographicOperations.ZeroMemory(previousKey);
                    if (previous?.Profile.Jump?.PrivateKey is { } previousJump && !ReferenceEquals(previousJump, persistedJumpKey))
                        CryptographicOperations.ZeroMemory(previousJump);
                }

                profile ??= submitted;
            }
            catch (Exception ex)
            {
                if (addedWalletKey is not null)
                {
                    store.KeyWallet.Remove(addedWalletKey);
                    CryptographicOperations.ZeroMemory(addedWalletKey.PrivateKey);
                }
                if (sessionKey is not null)
                    CryptographicOperations.ZeroMemory(sessionKey);
                if (profile?.Jump?.PrivateKey is { } attemptKey)
                    CryptographicOperations.ZeroMemory(attemptKey);
                sessionKey = null;
                profile = null;
                error.Text = ex.Message;
                return false;
            }
            finally
            {
                if (importedKey is not null && !ReferenceEquals(importedKey, persistedKey))
                    CryptographicOperations.ZeroMemory(importedKey);
                if (importedJumpKey is not null && !ReferenceEquals(importedJumpKey, persistedJumpKey))
                    CryptographicOperations.ZeroMemory(importedJumpKey);
            }
            return true;
        }

        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Submit(connectNow: !save);
        dialog.SecondaryButtonClick += (_, args) => args.Cancel = !Submit(connectNow: true);

        ContentDialogResult outcome;
        try { outcome = await dialog.ShowAsync(); }
        finally { secret.Password = ""; }
        if (openMemory && outcome != ContentDialogResult.None)
        {
            OpenMemoryTerminal();
            return;
        }
        if (outcome == ContentDialogResult.None || profile is null)
            return;
        if (savedEntry is not null)
        {
            RefreshProfiles(profile.Id);
            Status.Text = L10n.Format("S478C2DF2AA75", DeviceName(profile));
            RefreshRecentMenu();
        }
        if (applyToSession is not null || !save || outcome == ContentDialogResult.Secondary)
        {
            try
            {
                if (applyToSession is not null)
                    applyToSession(new SavedConnection(profile, suppliedSecret, sessionKey));
                else
                    await OpenSessionAsync(profile, suppliedSecret, sessionKey);
            }
            catch (Exception ex) { Status.Text = DisplaySessionStatus(profile, ex.Message); }
            finally
            {
                if (sessionKey is not null) CryptographicOperations.ZeroMemory(sessionKey);
                if (profile.Jump?.PrivateKey is { } newJump)
                    CryptographicOperations.ZeroMemory(newJump);
            }
        }
    }

    private readonly SemaphoreSlim hostDialog = new(1);

    private async Task<bool> TrustHostAsync(HostKeyIdentity identity, CancellationToken ct)
    {
        await hostDialog.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Root.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var pinned = store.HostKeys.Find(key =>
                        string.Equals(key.Host, identity.Host, StringComparison.OrdinalIgnoreCase) && key.Port == identity.Port);
                    if (pinned is not null)
                    {
                        if (pinned.Fingerprint != identity.Fingerprint || pinned.Algorithm != identity.Algorithm)
                            throw new SshHostKeyException(SshHostKeyFailure.Changed);
                        result.TrySetResult(true);
                        return;
                    }

                    var endpoint = store.HideDeviceAddress ? L10n.Get("S607748B52E6E") : $"{identity.Host}:{identity.Port}";
                    var dialog = Dialog(L10n.Get("SCB42E378E722"),
                        L10n.Format("S5960AD953E7F", endpoint, identity.Algorithm, identity.Fingerprint), L10n.Get("S52F94743915F"));
                    using var cancelDialog = ct.Register(() => Root.DispatcherQueue.TryEnqueue(dialog.Hide));
                    var accepted = await dialog.ShowAsync() == ContentDialogResult.Primary;
                    ct.ThrowIfCancellationRequested();
                    if (accepted)
                    {
                        var key = new SavedHostKey(identity.Host, identity.Port, identity.Algorithm, identity.Fingerprint);
                        store.HostKeys.Add(key);
                        try { SaveWorkspace(); }
                        catch
                        {
                            store.HostKeys.Remove(key);
                            throw;
                        }
                    }
                    result.TrySetResult(accepted);
                }
                catch (Exception ex)
                {
                    result.TrySetException(ex);
                }
            }))
                return false;
            return await result.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            hostDialog.Release();
        }
    }

    private void Sessions_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => CloseSessionTab(args.Tab);

    private void CloseSessionTab(TabViewItem tab)
    {
        if (ReferenceEquals(tab.Content, Welcome)) tab.Content = null;
        if (ReferenceEquals(tab, memoryTab))
            CloseMemoryTerminal();
        else
        {
            if (connectionTabs.Remove(tab, out var connection)) connection.Dispose();
            if (panes.Remove(tab, out var pane))
                pane.Dispose();
            Sessions.TabItems.Remove(tab);
        }
        if (Sessions.TabItems.Count == 0) AddStartTab();
        AttachWelcomePage();
        if (panes.Count == 0)
            SetSidebarMode(false);
    }

    private void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AttachWelcomePage();
        if (Status is null || Metrics is null)
            return;
        var pane = Sessions.SelectedItem is TabViewItem tab ? panes.GetValueOrDefault(tab) : null;
        RefreshSessionActions();
        UpdateVisibleMonitor();
        Status.Text = ReferenceEquals(Sessions.SelectedItem, memoryTab) && memoryTab is not null ? L10n.Get("SD584CA9D18AE") : pane is null ? L10n.Get("SB073DB9C0BD6") : pane.DisplayStatus;
        RefreshMonitor();
        if (Sessions.SelectedItem is TabViewItem selected && connectionTabs.TryGetValue(selected, out var connection))
            _ = OfferRetryAsync(selected, connection);
    }

    private void SetSidebarMode(bool monitor)
    {
        SidebarTitle.Text = L10n.Get(monitor ? "SC797A5342DCF" : "SA5574109F020");
        MonitorMode.IsChecked = monitor && workspacePage == WorkspacePage.Sessions;
        ConnectionsMode.IsChecked = !monitor && workspacePage == WorkspacePage.Sessions;
        MonitorPanel.Visibility = monitor ? Visibility.Visible : Visibility.Collapsed;
        Profiles.Visibility = ConnectionActions.Visibility =
            monitor ? Visibility.Collapsed : Visibility.Visible;
        UpdateVisibleMonitor();
        RefreshMonitor();
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) =>
        SetSidebarVisible(SidebarPane.Visibility != Visibility.Visible);

    private void SetSidebarVisible(bool visible)
    {
        SidebarPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ShowSidebar.IsChecked = visible;
        if (SidebarToggle is not null)
        {
            var label = L10n.Get(visible ? "CollapseSidebar" : "ExpandSidebar");
            ToolTipService.SetToolTip(SidebarToggle, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SidebarToggle, label);
        }
        SetSidebarWidth(Root.ActualWidth - 56);
        UpdateVisibleMonitor();
        SaveWorkspaceLayout();
    }

    private void Workbench_SizeChanged(object sender, SizeChangedEventArgs args) => SetSidebarWidth(args.NewSize.Width);

    private void SetSidebarWidth(double width)
    {
        if (SidebarPane is null || SidebarColumn is null)
            return;
        var target = SidebarPane.Visibility == Visibility.Collapsed ? 0 : Math.Clamp(sidebarPreferredWidth, 166, Math.Max(166, width - 360));
        SidebarGrip.Visibility = SidebarPane.Visibility;
        SidebarGripColumn.Width = new GridLength(target == 0 ? 0 : 5);
        if (SidebarColumn.Width.Value != target)
            SidebarColumn.Width = new GridLength(target);
    }

    private void RefreshMonitor()
    {
        if (Metrics is null || MonitorDetails is null)
            return;
        var pane = Sessions.SelectedItem is TabViewItem tab ? panes.GetValueOrDefault(tab) : null;
        MonitorDetails.IsEnabled = pane?.IsConnected == true;
        monitorView.Present(pane, pane is null ? "" : DeviceName(pane.Profile), pane?.MetricsSnapshot,
            pane?.MetricsText ?? L10n.Get("SADED90448D11"));
    }

    private void UpdateVisibleMonitor()
    {
        var selected = workspacePage == WorkspacePage.Sessions && SidebarPane?.Visibility == Visibility.Visible && MonitorPanel?.Visibility == Visibility.Visible
            ? Sessions.SelectedItem : null;
        foreach (var (tab, pane) in panes)
            pane.SetMonitoringVisible(ReferenceEquals(tab, selected));
    }

    private void SwitchWorkspaceSkin_Click(object sender, RoutedEventArgs e)
    {
        if (database is null || sender is not MenuFlyoutItem { Tag: string name } ||
            !Enum.TryParse<WorkspaceSkin>(name, out var skin))
            return;
        try { SaveInitialWorkspaceSkin(skin); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private ContentDialog Dialog(string title, object content, string primary) => new()
    {
        XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = title,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(workspaceAppearance.InterfaceFont ?? "Segoe UI Variable Text"),
        Content = content is string text ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 450 } : content,
        PrimaryButtonText = primary, CloseButtonText = L10n.Get("S2CD0F3BE8738"), DefaultButton = ContentDialogButton.Primary
    };
}

























