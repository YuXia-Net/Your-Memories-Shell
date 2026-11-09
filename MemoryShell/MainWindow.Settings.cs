using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private bool settingsOpen;
    private async void Settings_Click(object sender, RoutedEventArgs args) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync(string? selected = null)
    {
        if (database is not { } active || settingsOpen) return;
        settingsOpen = true;
        AppLockSetupEditor? lockEditor = null;
        IDisposable? appearancePreview = null;
        SizeChangedEventHandler? resizeSettings = null;
        PasswordBox? cloudPassword = null;
        CloudSettingsEditor? cloudEditor = null;
        try
        {
            var width = Math.Clamp(Root.ActualWidth - 40, 320, 1280);
            var tabs = new SettingsTabHost(Math.Clamp(Root.ActualHeight - 190, 160, 760)) { Width = width - 48 };
            var dialog = Dialog(L10n.Get("SettingsTitle"), tabs, "");
            dialog.CloseButtonText = L10n.Get("S3FD47EDCE45B");
            dialog.Resources["ContentDialogMaxWidth"] = width;
            resizeSettings = (_, _) =>
            {
                var availableWidth = Math.Clamp(Root.ActualWidth - 40, 320, 1280);
                dialog.Resources["ContentDialogMaxWidth"] = availableWidth;
                tabs.Width = availableWidth - 48;
                tabs.Height = Math.Clamp(Root.ActualHeight - 190, 160, 760);
            };
            Root.SizeChanged += resizeSettings;
            Action? afterClose = null;
            Button ActionButton(string key, RoutedEventHandler handler)
            {
                var button = new Button { Content = L10n.Get(key) };
                button.Click += (_, _) => { afterClose = () => handler(this, new RoutedEventArgs()); dialog.Hide(); };
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
                var state = AppLockStore.Load();
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
            var security = new StackPanel { Spacing = 20, Children = {
                SettingsPages.Section(L10n.Get("S0DE05D2CC49C"), privacy),
                SettingsPages.Section(L10n.Get("ChangeStartupLock"), changeLock, lockFields) } };
            tabs.Add(L10n.Get("SettingsSecurity"), security, async () =>
            {
                if (changeLock.IsChecked == true)
                {
                    if (lockEditor!.ValidationError() is { } error) throw new InvalidOperationException(error);
                    if (currentLock is not null)
                    {
                        var value = currentLock.Value;
                        var result = await Task.Run(() => AppLockStore.Verify(value));
                        if (!result.Accepted) throw new InvalidOperationException(L10n.Get("StartupLockNotAccepted"));
                    }
                    await lockEditor.SaveAsync();
                    changeLock.IsChecked = false;
                    RefreshLockFields();
                }
                if (store.HideDeviceAddress != privacy.IsOn) HideDeviceAddress_Click(this, new RoutedEventArgs());
            });
            var vaultInfo = new StackPanel { Spacing = 16, Children =
            {
                new TextBlock { Text = L10n.Get("S48558A5E37C4"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = active.IsRandomOnly ? L10n.Get("IrreversibleMode") : L10n.Get("DatabasePassword"), FontSize = 20 },
                new TextBlock { Text = L10n.Format("SCEF1E6472271", store.Connections.Count, store.RemoteDesktops.Count, store.Commands.Count), TextWrapping = TextWrapping.Wrap }
            } };
            var location = new TextBox { Text = Path.GetDirectoryName(active.FilePath), IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
            var move = ActionButton("ChangeDatabaseLocation", ChangeDatabaseLocation_Click);
            var backup = new StackPanel { Spacing = 20, Children = {
                SettingsPages.Section(L10n.Get("SBE6224667126"), vaultInfo),
                SettingsPages.Section(L10n.Get("DatabaseLocation"), location, move,
                    new TextBlock { Text = L10n.Get("DatabaseMoveNote"), TextWrapping = TextWrapping.Wrap, Foreground = WorkspaceTheme.Brush("ShellMutedBrush") }) } };
            var backupActions = new StackPanel { Spacing = 12 };
            if (!active.IsRandomOnly)
            {
                backupActions.Children.Add(ActionButton("SE047DA4161AA", ExportVault_Click));
                backupActions.Children.Add(ActionButton("ExportDatabaseExcel", ExportConnectionsExcel_Click));
            }
            backupActions.Children.Add(ActionButton("S5EDCDCAC3347", RestoreVault_Click));
            backup.Children.Add(SettingsPages.Section(L10n.Get("S6BEDA481D19D"), backupActions));
            tabs.Add(L10n.Get("S6BEDA481D19D"), backup);
            if (!active.IsRandomOnly)
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
            await dialog.ShowAsync();
            if (!closed && database == active) afterClose?.Invoke();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            if (resizeSettings is not null) Root.SizeChanged -= resizeSettings;
            if (cloudPassword is not null) cloudPassword.Password = "";
            cloudEditor?.Clear(); lockEditor?.Clear(); appearancePreview?.Dispose(); settingsOpen = false;
        }
    }
}
