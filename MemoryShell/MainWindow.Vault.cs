using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private FileStream? vaultLock;
    private bool openingWorkspace;
    private CancellationTokenSource? unlockedLifetime;
    private bool closed;
    private long vaultRevision;

    private async void StartVault(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= StartVault;
#if UI_SMOKE
        if (Environment.GetCommandLineArgs().Contains("--ui-check")) { await RunUiChecksAsync(); return; }
#endif
        try
        {
            vaultLock = WorkspaceStore.Claim();
            ApplyWorkspaceAppearance();
            RestoreWorkspaceLayout();
            if (!await OpenWorkspaceAsync() && !closed)
                Close();
        }
        catch (Exception ex)
        {
            if (!closed)
            {
                var detail = ex switch
                {
                    UnauthorizedAccessException or SqliteException { SqliteErrorCode: 8 or 14 } =>
                        L10n.Get("S17509350159A"),
                    SqliteException { SqliteErrorCode: 5 or 6 } =>
                        L10n.Get("S0E1CCDF83668"),
                    _ => ex.Message
                };
                var dialog = Dialog(L10n.Get("SD584C8E48A95"), L10n.Format("S53C68AE0CC83", detail, WorkspaceStore.Folder), "");
                dialog.CloseButtonText = L10n.Get("S3FD47EDCE45B");
                dialog.DefaultButton = ContentDialogButton.Close;
                await dialog.ShowAsync();
                Close();
            }
        }
    }

    private async Task<bool> OpenWorkspaceAsync()
    {
        openingWorkspace = true;
        try
        {
            if (!await AppLockDialog.RequireAsync(Root.XamlRoot) || closed)
                return false;
            if (!File.Exists(WorkspaceStore.VaultPath))
                return await ShowInitializationWizardAsync();
            var opened = await Task.Run(WorkspaceStore.TryUnlockForCurrentUser);
            if (opened is not { } result)
                return !closed && await PromptUnlockAsync();
            if (closed)
            {
                result.Store.ClearSensitiveData();
                result.Database.Dispose();
                return false;
            }
            ActivateVault(result.Store, result.Database);
            return true;
        }
        finally
        {
            openingWorkspace = false;
            if (closed)
                ReleaseWorkspaceClaim();
        }
    }

    private async Task<bool> PromptUnlockAsync()
    {
        while (!closed && database is null)
        {
            if (!File.Exists(WorkspaceStore.VaultPath))
                return await ShowInitializationWizardAsync();
            var password = new PasswordBox { Header = L10n.Get("S45A471E9F9CB"), PasswordRevealMode = PasswordRevealMode.Hidden };
            var error = ErrorText();
            var layout = new StackPanel { Spacing = 10, MinWidth = 345 };
            layout.Children.Add(new TextBlock
            {
                Text = L10n.Get("SE34E0252D732"),
                TextWrapping = TextWrapping.Wrap
            });
            layout.Children.Add(password);
            layout.Children.Add(error);
            var dialog = Dialog(L10n.Get("SB84D0FB55607"), layout, L10n.Get("S70FBA17FFFB7"));
            dialog.CloseButtonText = L10n.Get("S498E1D59B4D7");
            dialog.SecondaryButtonText = L10n.Get("SC5CA9ED65700");
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                using var deferral = new DialogDeferral(args.GetDeferral());
                var supplied = password.Password;
                try
                {
                    var opened = await Task.Run(() => WorkspaceStore.Unlock(supplied));
                    if (closed)
                    {
                        opened.Store.ClearSensitiveData();
                        opened.Database.Dispose();
                        return;
                    }
                    ActivateVault(opened.Store, opened.Database);
                    password.Password = "";
                }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    error.Text = ex is CryptographicException ? L10n.Get("S49E6DB2CC782") : ex.Message;
                }
            };

            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); }
            finally { password.Password = ""; }
            if (database is not null)
                return true;
            if (result != ContentDialogResult.Secondary)
                return false;
            await RestoreAtStartupAsync();
        }
        return database is not null;
    }

    private void ActivateVault(WorkspaceStore opened, VaultDatabase vault)
    {
        store = opened;
        database = vault;
        unlockedLifetime = new CancellationTokenSource();
        vaultRevision = 1;
        uploadedRevision = 0;
        RefreshPrivacyPresentation();
        SetSidebarMode(false);
        Status.Text = L10n.Get("SB073DB9C0BD6");
        StartCloudSchedule();
    }

    private void ClearUnlockedWorkspace()
    {
        CloseMonitorWall();
        cloudTimer?.Stop();
        unlockedLifetime?.Cancel();
        unlockedLifetime?.Dispose();
        unlockedLifetime = null;
        foreach (var state in connectionTabs.Values)
            state.Dispose();
        connectionTabs.Clear();
        foreach (var pane in panes.Values)
            pane.Dispose();
        panes.Clear();
        CloseMemoryTerminal();
        if (!closed)
        {
            foreach (var tab in Sessions.TabItems.OfType<TabViewItem>())
                if (ReferenceEquals(tab.Content, Welcome)) tab.Content = null;
            Sessions.TabItems.Clear();
            if (!Sessions.TabItems.Contains(StartTab)) Sessions.TabItems.Add(StartTab);
            Sessions.SelectedItem = StartTab;
        }
        database?.Dispose();
        database = null;
        store.ClearSensitiveData();
        store = new WorkspaceStore();
        if (!closed)
        {
            ConnectionFilter.Text = "";
            RefreshProfiles();
            RefreshRecentMenu();
        }
    }

    private void CloseVault()
    {
        closed = true;
        try { ClearUnlockedWorkspace(); }
        finally
        {
            if (!openingWorkspace)
                ReleaseWorkspaceClaim();
        }
    }

    private void ReleaseWorkspaceClaim()
    {
        vaultLock?.Dispose();
        vaultLock = null;
    }

    private async void LockVault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (restoreInProgress || database is null)
                return;
            if (!AppLockDialog.IsEnabled)
            {
                Status.Text = L10n.Get("SD893992DB932");
                return;
            }
            ClearUnlockedWorkspace();
            if (!await OpenWorkspaceAsync() && !closed)
                Close();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void RecordConnection(Services.ConnectionProfile profile, bool connected)
    {
        if (database is null)
            return;
        AddConnectionEvent(new ConnectionEvent(DateTimeOffset.Now, profile.Name, $"{profile.Username}@{profile.Host}:{profile.Port}", connected, ProfileId: profile.Id));
    }

    private void AddConnectionEvent(ConnectionEvent entry)
    {
        var history = store.ConnectionHistory;

        history.Add(entry);
        var oldest = history.Count > 1000 ? history[0] : null;
        if (oldest is not null)
            history.RemoveAt(0);
        try { SaveWorkspace(); RefreshRecentMenu(); }
        catch (Exception ex)
        {
            history.Remove(entry);
            if (oldest is not null)
                history.Insert(0, oldest);
            Status.Text = L10n.Format("SBF48EAF13129", ex.Message);
        }
    }

    private async void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListView { MaxHeight = 480, MinWidth = 400 };
        foreach (var item in store.ConnectionHistory.AsEnumerable().Reverse())
        {
            list.Items.Add(new TextBlock
            {
                Text = $"{item.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {(item.Connected ? item.Protocol == "RDP" ? L10n.Get("S1101D0B9E3FF") : L10n.Get("S5BE0323E8ADC") : L10n.Get("S28384D7AFD2E"))}\n{(store.HideDeviceAddress ? L10n.Get("S6E66EC9258D6") : $"{item.Name}  ·  {item.Target}")}",
                Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap
            });
        }
        if (list.Items.Count == 0)
            list.Items.Add(L10n.Get("S549ADFA33015"));
        var dialog = Dialog(L10n.Get("S490156BE6D55"), list, L10n.Get("S3FD47EDCE45B"));
        dialog.CloseButtonText = "";
        await dialog.ShowAsync();
    }

    private async void RevealConnection_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
        {
            Status.Text = L10n.Get("S514A7F5BACC2");
            return;
        }
        await ShowCredentialsAsync(selected);
    }

    private async Task ShowCredentialsAsync(SavedConnection selected)
    {
        if (!await VerifyMasterAsync(L10n.Get("S3A5CBA61CD6A")))
            return;
        var content = new StackPanel { Spacing = 8, MinWidth = 360 };
        var revealed = new List<TextBox>();
        void Show(string title, string? value, bool multiline = false)
        {
            var box = new TextBox
            {
                Header = title, Text = value ?? L10n.Get("S6A1BE012C99C"), IsReadOnly = true, AcceptsReturn = multiline,
                MaxHeight = multiline ? 220 : double.PositiveInfinity
            };
            if (multiline)
                box.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
            content.Children.Add(box);
            revealed.Add(box);
        }
        Show(L10n.Get("SD9068F19FFE4"), selected.Profile.Username);
        Show(selected.PrivateKey is null ? L10n.Get("SF883C1AC46D1") : L10n.Get("S0550C993E3F9"), selected.Secret);
        if (selected.PrivateKey is not null)
            Show(L10n.Get("SE7D212076D9E"), Encoding.UTF8.GetString(selected.PrivateKey), multiline: true);
        if (selected.Profile.Proxy is { } proxy)
        {
            Show(L10n.Get("S025F931C5D8F"), proxy.Username);
            Show(L10n.Get("S82CFBC66B5DC"), proxy.Password);
        }
        if (selected.Profile.Jump is { } jump)
        {
            Show(L10n.Get("S01B6FCF6A966"), jump.Username);
            Show(jump.PrivateKey is null ? L10n.Get("SC7A9BF9BEEA1") : L10n.Get("S8E8F63B18931"), jump.Password);
            if (jump.PrivateKey is not null)
                Show(L10n.Get("SAF00F94E050C"), Encoding.UTF8.GetString(jump.PrivateKey), multiline: true);
        }
        var view = Dialog(L10n.Format("S2B63EEFE5105", DeviceName(selected.Profile)), new ScrollViewer { Content = content, MaxHeight = 510 }, L10n.Get("S3FD47EDCE45B"));
        view.CloseButtonText = "";
        try { await view.ShowAsync(); }
        finally
        {
            foreach (var box in revealed)
                box.Text = "";
        }
    }

    private async void ExportVault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var active = database ?? throw new InvalidOperationException(L10n.Get("S56E180F830C3"));
            if (!await EnsureMasterAsync(active)) return;
            var picker = new FileSavePicker { SuggestedFileName = "memoryshell-backup" };
            picker.FileTypeChoices.Add(L10n.Get("S520D8AB4F6DC"), new List<string> { ".sqlite" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            if (await picker.PickSaveFileAsync() is not { } file)
                return;
            var fullPath = Path.GetFullPath(file.Path);
            if (string.Equals(fullPath, WorkspaceStore.VaultPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L10n.Get("S684FFEEF1FD6"));
            var snapshot = await Task.Run(active.ExportSnapshot);
            var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".memoryshell-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, snapshot);
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally { TryDelete(temporary); }
            Status.Text = L10n.Format("S97724FB4EE0E", file.Name);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void RestoreVault_Click(object sender, RoutedEventArgs e) => await PickLocalRestoreAsync();

    private async Task PickLocalRestoreAsync(Action<string>? showError = null)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".sqlite");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            if (await picker.PickSingleFileAsync() is { } file)
                await RestoreSnapshotAsync(file.Path);
        }
        catch (Exception ex)
        {
            if (showError is not null)
                showError(ex.Message);
            else
                Status.Text = ex.Message;
        }
    }

    private async Task RestoreSnapshotAsync(string source)
    {
        if (restoreInProgress)
            return;
        if (database is { HasMaster: false } current && !await EnsureMasterAsync(current))
            return;

        var password = new PasswordBox { Header = L10n.Get("SFAE4AA626BC4") };
        var error = ErrorText();
        var warning = new TextBlock
        {
            Text = !File.Exists(WorkspaceStore.VaultPath)
                ? L10n.Get("SB7BE0831FA9B")
                : L10n.Get("S5180C4E354F3"),
            TextWrapping = TextWrapping.Wrap
        };
        var fields = new StackPanel { MinWidth = 350, Spacing = 9, Children = { warning, password, error } };
        var dialog = Dialog(L10n.Get("S444F1E08B1B3"), fields, L10n.Get("SE7EB07075F22"));
        var stage = Path.Combine(Path.GetDirectoryName(WorkspaceStore.VaultPath)!, $".restore-{Guid.NewGuid():N}.sqlite");
        restoreInProgress = true;
        try
        {
            if (new FileInfo(source).Length is <= 0 or > 128L * 1024 * 1024)
                throw new IOException(L10n.Get("SA94644C3956F"));

            await Task.Run(() => File.Copy(source, stage, overwrite: false));
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                using var deferral = new DialogDeferral(args.GetDeferral());
                var supplied = password.Password;
                try
                {
                    await Task.Run(() =>
                    {
                        using var candidate = VaultDatabase.Open(stage, supplied);
                        WorkspaceStore.Read(candidate).ClearSensitiveData();
                    });
                    if (closed)
                        return;

                    ClearUnlockedWorkspace();
                    var path = WorkspaceStore.VaultPath;
                    if (File.Exists(path))
                        File.Replace(stage, path, Path.Combine(Path.GetDirectoryName(path)!, $"workspace-before-restore-{Guid.NewGuid():N}.sqlite"));
                    else
                        File.Move(stage, path);
                    var opened = await Task.Run(() => WorkspaceStore.Unlock(supplied));
                    if (closed)
                    {
                        opened.Store.ClearSensitiveData();
                        opened.Database.Dispose();
                        return;
                    }
                    ActivateVault(opened.Store, opened.Database);
                    password.Password = "";
                    Status.Text = L10n.Get("S9503EF285950");
                }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    error.Text = ex is CryptographicException ? L10n.Get("S40370A245BB7") : ex.Message;
                }
            };

            var wasUnlocked = database is not null;
            await dialog.ShowAsync();
            if (wasUnlocked && database is null && !closed && !await PromptUnlockAsync())
                Close();
        }
        finally
        {
            password.Password = "";
            restoreInProgress = false;
            TryDelete(stage);
        }
    }

    private bool restoreInProgress;
    private static TextBlock ErrorText() => new() { TextWrapping = TextWrapping.Wrap };
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class DialogDeferral(ContentDialogButtonClickDeferral deferral) : IDisposable
    {
        public void Dispose() => deferral.Complete();
    }
}







