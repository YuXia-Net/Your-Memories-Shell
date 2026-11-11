using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private bool settingsOpen;
    private Func<bool>? finishSettings;
    private SettingsTabHost? activeSettingsTabs;
    private async void Settings_Click(object sender, RoutedEventArgs args) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync(string? selected = null)
    {
        if (database is not { } active) return;
        if (settingsOpen) { NavigateWorkspace(WorkspacePage.Settings); activeSettingsTabs?.Select(selected); return; }
        settingsOpen = true;
        AppLockSetupEditor? lockEditor = null;
        IDisposable? appearancePreview = null;
        PasswordBox? cloudPassword = null;
        CloudSettingsEditor? cloudEditor = null;
        try
        {
            var tabs = new SettingsTabHost();
            activeSettingsTabs = tabs;
            var finished = new TaskCompletionSource<bool>();
            var page = new Grid { Padding = new Thickness(24, 16, 24, 16), RowSpacing = 16 };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition());
            var back = new Button { Content = new FontIcon { Glyph = "\uE72B", FontSize = 16 }, Width = 36, Height = 36, MinWidth = 0 };
            ToolTipService.SetToolTip(back, L10n.Get("BackToWorkspace"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(back, L10n.Get("BackToWorkspace"));
            back.Click += ReturnToSessions_Click;
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children =
            {
                back, new TextBlock { Text = L10n.Get("SettingsTitle"), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            } };
            page.Children.Add(heading); Grid.SetRow(tabs, 1); page.Children.Add(tabs);
            finishSettings = () =>
            {
                if (tabs.IsSaving && !closed) return false;
                SettingsPageHost.Content = null;
                finished.TrySetResult(true);
                return true;
            };
            Action? afterClose = null;
            Button ActionButton(string key, RoutedEventHandler handler)
            {
                var button = new Button { Content = L10n.Get(key) };
                button.Click += (_, _) => { afterClose = () => handler(this, new RoutedEventArgs()); NavigateWorkspace(WorkspacePage.Sessions); };
                return button;
            }
            appearancePreview = AddAppearanceSettings(tabs);
            AddFileSettings(tabs);
            AddLanguageSettings(tabs);

            var privacy = new ToggleSwitch { Header = L10n.Get("S0DE05D2CC49C"), IsOn = store.HideDeviceAddress };
            var changeLock = new CheckBox { Content = L10n.Get("ChangeStartupLock") };
            var lockFields = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed };
            AppLockDialog.CredentialInput? currentLock = null;
            void RefreshLockFields()
            {
                currentLock?.Clear(); lockEditor?.Clear(); lockFields.Children.Clear();
                var state = new AppLockStore(store, active).Load();
                lockEditor = new AppLockSetupEditor(state?.Kind);
                currentLock = state is null ? null : new(state.Kind);
                if (state is not null) CryptographicOperations.ZeroMemory(state.Hash);
                if (currentLock is not null)
                {
                    lockFields.Children.Add(new TextBlock { Text = L10n.Get("CurrentStartupLock"), TextWrapping = TextWrapping.Wrap });
                    lockFields.Children.Add(currentLock);
                }
                lockFields.Children.Add(lockEditor);
            }
            RefreshLockFields();
            changeLock.Checked += (_, _) => lockFields.Visibility = Visibility.Visible;
            changeLock.Unchecked += (_, _) => { lockFields.Visibility = Visibility.Collapsed; currentLock?.Clear(); lockEditor?.Clear(); };
            var security = new SettingsColumns(SettingsPages.Section(L10n.Get("S0DE05D2CC49C"), privacy),
                SettingsPages.Section(L10n.Get("ChangeStartupLock"), changeLock, lockFields));
            tabs.Add(L10n.Get("SettingsSecurity"), security, async () =>
            {
                if (changeLock.IsChecked == true)
                {
                    if (lockEditor!.ValidationError() is { } error) throw new InvalidOperationException(error);
                    if (currentLock is not null)
                    {
                        var value = currentLock.Value;
                        var result = await new AppLockStore(store, active).VerifyAsync(value);
                        if (!result.Accepted) throw new InvalidOperationException(L10n.Get("StartupLockNotAccepted"));
                    }
                    await lockEditor.SaveAsync(new AppLockStore(store, active));
                    SaveWorkspace();
                    changeLock.IsChecked = false;
                    RefreshLockFields();
                }
                if (store.HideDeviceAddress != privacy.IsOn) HideDeviceAddress_Click(this, new RoutedEventArgs());
            });
            var vaultInfo = new StackPanel { Spacing = 16, Children =
            {
                new TextBlock { Text = L10n.Get("S48558A5E37C4"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = L10n.Format("SCEF1E6472271", store.Connections.Count, store.RemoteDesktops.Count, store.Commands.Count), TextWrapping = TextWrapping.Wrap }
            } };
            var location = new TextBox { Text = Path.GetDirectoryName(active.FilePath), IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
            var move = ActionButton("ChangeDatabaseLocation", ChangeDatabaseLocation_Click);
            var backup = new StackPanel { Spacing = 20, Children = {
                SettingsPages.Section(L10n.Get("SBE6224667126"), vaultInfo),
                SettingsPages.Section(L10n.Get("DatabaseLocation"), location, move,
                    new TextBlock { Text = L10n.Get("DatabaseMoveNote"), TextWrapping = TextWrapping.Wrap, Foreground = WorkspaceTheme.Brush("ShellMutedBrush") }) } };
            var backupActions = new StackPanel { Spacing = 12 };
            backupActions.Children.Add(ActionButton("SE047DA4161AA", ExportVault_Click));
            backupActions.Children.Add(ActionButton("ExportDatabaseExcel", ExportConnectionsExcel_Click));
            backupActions.Children.Add(ActionButton("S5EDCDCAC3347", RestoreVault_Click));
            backup.Children.Add(SettingsPages.Section(L10n.Get("S6BEDA481D19D"), backupActions));
            tabs.Add(L10n.Get("S6BEDA481D19D"), backup);
            {
                var cloudBody = new StackPanel { Spacing = 14 };
                CloudSettingsEditor? editor = null;
                var password = new PasswordBox { Header = L10n.Get("DatabasePassword") };
                cloudPassword = password;
                var reveal = new Button { Content = L10n.Get("CloudUnlockSettings") };
                var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = WorkspaceTheme.Brush("ShellErrorBrush") };
                void ShowEditor()
                {
                    cloudEditor?.Clear();
                    editor = new CloudSettingsEditor(store.Cloud) { MaxHeight = double.PositiveInfinity, MinWidth = 0 };
                    cloudEditor = editor;
                    cloudBody.Children.Clear(); cloudBody.Children.Add(editor);
                    cloudBody.Children.Add(ActionButton("SA12EBE9CF670", UploadCloud_Click));
                    cloudBody.Children.Add(ActionButton("S5A6364830EED", BrowseCloud_Click));
                    var clear = new Button { Content = L10n.Get("S4B19F5A595D0") };
                    clear.Click += (_, _) =>
                    {
                        try { UpdateCloudSettings(null); ShowEditor(); }
                        catch (Exception ex) { error.Text = ex.Message; }
                    };
                    cloudBody.Children.Add(clear); cloudBody.Children.Add(error);
                }
                if (store.Cloud is null) ShowEditor();
                else
                {
                    cloudBody.Children.Add(password); cloudBody.Children.Add(reveal); cloudBody.Children.Add(error);
                    reveal.Click += async (_, _) =>
                    {
                        reveal.IsEnabled = false;
                        try { var supplied = password.Password; await Task.Run(() => active.VerifyPassphrase(supplied)); password.Password = ""; ShowEditor(); }
                        catch (Exception ex) { error.Text = ex is CryptographicException ? L10n.Get("S49E6DB2CC782") : ex.Message; }
                        finally { password.Password = ""; reveal.IsEnabled = true; }
                    };
                }
                tabs.Add(L10n.Get("S6101C7FAF34F"), SettingsPages.Section(L10n.Get("S6101C7FAF34F"), cloudBody), () =>
                {
                    if (editor is null) throw new InvalidOperationException(L10n.Get("CloudUnlockSettings"));
                    UpdateCloudSettings(editor.Read()); return Task.CompletedTask;
                });
            }
            tabs.Select(selected);
            SettingsPageHost.Content = page;
            NavigateWorkspace(WorkspacePage.Settings);
            await finished.Task;
            if (!closed && database == active) afterClose?.Invoke();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            finishSettings = null;
            activeSettingsTabs = null;
            if (cloudPassword is not null) cloudPassword.Password = "";
            cloudEditor?.Clear(); lockEditor?.Clear(); appearancePreview?.Dispose(); settingsOpen = false;
        }
    }
}
