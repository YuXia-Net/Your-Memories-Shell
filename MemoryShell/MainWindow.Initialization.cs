using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private async Task<bool> ShowInitializationWizardAsync()
    {
        if (closed) return false;
        if (File.Exists(WorkspaceStore.VaultPath)) return await PromptUnlockAsync();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();
        var busy = false;
        var currentStep = 0;
        var selectedSkin = workspaceAppearance.Skin;
        var password = new PasswordBox { Header = L10n.Get("S2EA324EC7475"), PasswordRevealMode = PasswordRevealMode.Hidden };
        var confirm = new PasswordBox { Header = L10n.Get("S76F3655F0E69"), PasswordRevealMode = PasswordRevealMode.Hidden };
        var keyFields = new StackPanel { Spacing = 12, Children = { password, confirm } };
        var keyHint = SetupText(L10n.Get("S986001347B84"), 14, "ShellMutedBrush");
        var security = SettingsPages.Section(L10n.Get("S3044EF88D051"),
            keyFields, keyHint);
        var restored = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Success, Message = L10n.Get("SA0E8E55A006B") };
        var local = new Button { Content = L10n.Get("S9870AD8E01B4"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var cloud = new Button { Content = L10n.Get("S59AE8B01B5FA"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var location = SetupText(Path.GetDirectoryName(WorkspaceStore.VaultPath)!, 12, "ShellMutedBrush");
        location.IsTextSelectionEnabled = true;
        var chooseLocation = new Button { Content = L10n.Get("DatabaseLocationChoose") };
        chooseLocation.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            if (await picker.PickSingleFolderAsync() is { } folder) location.Text = folder.Path;
        };
        var toolbarLayout = new ToolbarLayoutPicker(workspaceAppearance.LogoOnLeft, compact: true);
        var terminalActions = new TerminalActionsPicker(workspaceAppearance, showHeader: false);
        terminalActions.SelectionChanged += (_, _) => PresentWorkspaceAppearance(workspaceAppearance with
        { TerminalActionsAtTabs = terminalActions.AtTabs, TerminalActionsOnLeft = terminalActions.OnLeft }, workspaceWallpaper);
        toolbarLayout.SelectionChanged += (_, _) => PresentWorkspaceAppearance(workspaceAppearance with { LogoOnLeft = toolbarLayout.SelectedIndex == 0 }, workspaceWallpaper);
        var fileStyle = new RadioButtons { MaxColumns = 2 };
        fileStyle.Items.Add(L10n.Get("ExplorerStyle"));
        fileStyle.Items.Add(L10n.Get("CompactStyle"));
        fileStyle.SelectedIndex = 0;
        var restore = SettingsPages.Section(L10n.Get("SCC3B6A50215A"), local, cloud);
        var vaultPage = new StackPanel { Spacing = 12, Children = { restored, security,
            SettingsPages.Section(L10n.Get("DatabaseLocation"), location, chooseLocation), restore } };

        var themes = new RadioButtons { MaxColumns = 3 };
        foreach (var (skin, label, background, surface, ink) in new[]
        {
            (WorkspaceSkin.WindowsLight, L10n.Get("SDD43589D28AD"), 0xF8F8F8, 0xFFFFFF, 0x005FB8),
            (WorkspaceSkin.WindowsDark, L10n.Get("SB9256B8051B9"), 0x202020, 0x303030, 0x60CDFF),
            (WorkspaceSkin.Memory, L10n.Get("SB676597E1697"), 0x0C121E, 0x182D49, 0x83D6FF)
        })
        {
            var preview = new Grid { Height = 104, MinWidth = 128, Background = Rgb(background), Padding = new Thickness(8), CornerRadius = new CornerRadius(6) };
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            preview.ColumnDefinitions.Add(new ColumnDefinition());
            preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            preview.RowDefinitions.Add(new RowDefinition());
            var strip = new Border { Background = Rgb(surface), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(strip, 2); preview.Children.Add(strip);
            var rail = new Border { Background = Rgb(surface), Margin = new Thickness(0, 0, 5, 0), CornerRadius = new CornerRadius(3) };
            Grid.SetRow(rail, 1); preview.Children.Add(rail);
            var prompt = new TextBlock { Text = ">_", Foreground = Rgb(ink), FontSize = 20, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(6) };
            Grid.SetColumn(prompt, 1); Grid.SetRow(prompt, 1); preview.Children.Add(prompt);
            var radio = new RadioButton
            {
                Tag = skin, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 10, Children = { preview, SetupText(label, 14) } }
            };
            themes.Items.Add(radio);
            if (skin == selectedSkin) themes.SelectedItem = radio;
        }
        themes.SelectionChanged += (_, _) =>
        {
            if (themes.SelectedItem is not RadioButton { Tag: WorkspaceSkin skin }) return;
            selectedSkin = skin;
            PresentWorkspaceAppearance(workspaceAppearance with { Skin = skin }, workspaceWallpaper);
        };
        var appearance = new StackPanel { Spacing = 24, Children = { SettingsPages.Section(L10n.Get("SD77BEEBD1232"), themes) } };
        var lockEditor = new AppLockSetupEditor();
        var lockPage = SettingsPages.Section(L10n.Get("S99BBF3B2CC2F"), lockEditor);
        var hideAddresses = new ToggleSwitch { Header = L10n.Get("SC0DB9D8A9223"), IsOn = store.HideDeviceAddress };
        var hiddenFiles = new ToggleSwitch { Header = L10n.Get("SDDC6575A723D"), IsOn = store.ShowHiddenFiles };
        var clearCommand = new ToggleSwitch { Header = L10n.Get("S4052F4A2B7AC"), IsOn = store.ClearCommandAfterSend };
        var habits = new StackPanel { Spacing = 12, Children = {
            SetupSection(L10n.Get("TerminalActionsPosition"), terminalActions),
            new SettingsColumns(
            new StackPanel { Spacing = 12, Children = {
                SetupSection(L10n.Get("TopBarLayout"), toolbarLayout),
                SetupSection(L10n.Get("S82479CB6CA73"), hideAddresses, SetupText(L10n.Get("S8C8E211112F3"), 12, "ShellMutedBrush")) } },
            new StackPanel { Spacing = 12, Children = {
                SetupSection(L10n.Get("S595EFC6C69E5"), fileStyle, hiddenFiles, clearCommand) } }, 820) } };
        UIElement[] pages = [appearance, vaultPage, lockPage, habits];
        string[] titles = [L10n.Get("S73C8BFB65D65"), L10n.Get("S8A97BAD569D4"), L10n.Get("S8687D50A3DA0"), L10n.Get("SD806EDEF54B2")];
        string[] descriptions = [L10n.Get("S4533B5A8E211"), L10n.Get("S48558A5E37C4"), L10n.Get("S518D8FC77796"), L10n.Get("S1AC2799AABD0")];
        Symbol[] icons = [Symbol.View, Symbol.ProtectedDocument, Symbol.Permissions, Symbol.Setting];
        var navigation = new NavigationView
        {
            IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false, PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 218, IsPaneOpen = true, IsTitleBarAutoPaddingEnabled = false, AlwaysShowHeader = false
        };
        for (var i = 0; i < pages.Length; i++)
            navigation.MenuItems.Add(new NavigationViewItem { Content = titles[i], Icon = new SymbolIcon(icons[i]), Tag = i });
        var title = SetupText(titles[0], 28);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var description = SetupText(descriptions[0], 14, "ShellMutedBrush");
        var body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            body.ContentTransitions = new TransitionCollection { new EntranceThemeTransition { FromHorizontalOffset = 0, FromVerticalOffset = 12 } };
        var form = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { new StackPanel { Spacing = 4, Children = { title, description } }, body } };
        var scroll = new ScrollViewer { Content = form, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled };
        var error = SetupText("", 13, "ShellErrorBrush");
        error.Visibility = Visibility.Collapsed;
        var back = new Button { Content = L10n.Get("SDA336FDC0DBD") };
        var next = new Button { Content = L10n.Get("SACFC4E74A650"), MinWidth = 120, Style = (Style)Application.Current.Resources["ShellPrimaryButtonStyle"] };
        var progress = new ProgressRing { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        var stepLabel = SetupText("", 13, "ShellMutedBrush");
        stepLabel.VerticalAlignment = VerticalAlignment.Center;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Children = { progress, back, next } };
        var footer = new Grid { ColumnSpacing = 16, RowSpacing = 12, Padding = new Thickness(28, 16, 28, 16), BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(error, 2); footer.Children.Add(error);
        Grid.SetRow(stepLabel, 1); footer.Children.Add(stepLabel);
        Grid.SetRow(actions, 1); Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(scroll); Grid.SetRow(footer, 1); content.Children.Add(footer);
        navigation.Content = content;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(24, 16, 24, 16), Children =
        {
            new Image { Width = 32, Height = 32, Source = new BitmapImage(new Uri("ms-appx:///Assets/MemoryShell.png")) },
            new TextBlock { Text = L10n.Get("S1330FC51C5C3"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = WorkspaceTheme.Brush("ShellTextBrush") }
        } };
        var page = new Grid { Background = WorkspaceTheme.Brush("ShellBaseBrush") };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); page.RowDefinitions.Add(new RowDefinition());
        page.Children.Add(new Border { Child = header, BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), BorderThickness = new Thickness(0, 0, 0, 1) });
        Grid.SetRow(navigation, 1); page.Children.Add(navigation); Grid.SetRowSpan(page, Root.RowDefinitions.Count);
        page.SizeChanged += (_, args) =>
        {
            navigation.PaneDisplayMode = args.NewSize.Width < 850 ? NavigationViewPaneDisplayMode.Top : NavigationViewPaneDisplayMode.Left;
            scroll.Padding = new Thickness(args.NewSize.Width < 700 ? 12 : 16);
            themes.MaxColumns = args.NewSize.Width < 640 ? 1 : 3;
        };
        void SetError(string message)
        {
            error.Text = message;
            error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void ShowStep(int index)
        {
            currentStep = index;
            title.Text = titles[index]; description.Text = descriptions[index]; body.Content = pages[index];
            back.IsEnabled = index > 0;
            next.Content = index == pages.Length - 1 ? L10n.Get("SFDA1EB4D1923") : L10n.Get("SACFC4E74A650");
            stepLabel.Text = $"{index + 1} / {pages.Length}";
            scroll.ChangeView(null, 0, null, true);
            SetError("");
            if (index == 1 && database is null) password.Focus(FocusState.Programmatic);
        }
        void GoTo(int index) => navigation.SelectedItem = navigation.MenuItems[index];
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: int index }) ShowStep(index);
        };
        bool ValidateVault()
        {
            if (database is not null) return true;
            if (password.Password.Length == 0) { GoTo(1); SetError(L10n.Get("S759BCEF7F6D9")); password.Focus(FocusState.Programmatic); return false; }
            if (password.Password != confirm.Password) { GoTo(1); SetError(L10n.Get("S1FEE59BF77DC")); confirm.Focus(FocusState.Programmatic); return false; }
            return true;
        }
        bool ValidateLock()
        {
            if (lockEditor.ValidationError() is not { } message) return true;
            GoTo(2); SetError(message); return false;
        }
        async Task RunAsync(Func<Task> action)
        {
            if (busy || closed || finished.Task.IsCompleted) return;
            busy = true; navigation.IsEnabled = false; SetError("");
            progress.Visibility = Visibility.Visible; progress.IsActive = true;
            try { await action(); }
            catch (OperationCanceledException) when (closed) { }
            catch (Exception ex) { if (!closed) SetError(ex.Message); }
            finally
            {
                busy = false;
                if (closed) finished.TrySetResult(false);
                else { navigation.IsEnabled = true; progress.IsActive = false; progress.Visibility = Visibility.Collapsed; }
            }
        }
        async Task CompleteAsync()
        {
            if (!ValidateVault() || !ValidateLock()) return;
            var passphrase = password.Password;
            await RunAsync(async () =>
            {
                if (closed) return;
                if (database is null)
                {
                    var chosenPath = Path.Combine(location.Text, "workspace.sqlite");
                    if (File.Exists(chosenPath)) throw new IOException(L10n.Get("DatabaseAlreadyExists"));
                    var startupLock = await lockEditor.CreateAsync();
                    var opened = await Task.Run(() => WorkspaceStore.Create(passphrase, startupLock, chosenPath));
                    try { WorkspaceStore.SaveDatabaseFolder(location.Text); }
                    catch { opened.Store.ClearSensitiveData(); opened.Database.Dispose(); throw; }
                    if (closed) { opened.Store.ClearSensitiveData(); opened.Database.Dispose(); return; }
                    ActivateVault(opened.Store, opened.Database);
                }
                else await lockEditor.SaveAsync(new AppLockStore(store, database));
                store.HideDeviceAddress = hideAddresses.IsOn;
                store.ShowHiddenFiles = hiddenFiles.IsOn;
                store.ClearCommandAfterSend = clearCommand.IsOn;
                SaveWorkspace();
                workspaceLayout = workspaceLayout with { DirectoryTreeVisible = fileStyle.SelectedIndex == 1 };
                workspaceLayout.Save();
                SaveInitialFileStyle(fileStyle.SelectedIndex == 0);
                SaveInitialWorkspaceSkin(selectedSkin);
                RefreshPrivacyPresentation();
                finished.TrySetResult(true);
            });
        }
        back.Click += (_, _) => GoTo(Math.Max(0, currentStep - 1));
        next.Click += async (_, _) =>
        {
            if (busy) return;
            if (currentStep == 1 && !ValidateVault() || currentStep == 2 && !ValidateLock()) return;
            if (currentStep == pages.Length - 1) await CompleteAsync(); else GoTo(currentStep + 1);
        };
        void AfterRestore()
        {
            if (database is null || closed) return;
            password.Password = confirm.Password = "";
            security.Visibility = restore.Visibility = Visibility.Collapsed;
            restored.IsOpen = true;
            hideAddresses.IsOn = store.HideDeviceAddress; hiddenFiles.IsOn = store.ShowHiddenFiles; clearCommand.IsOn = store.ClearCommandAfterSend;
            GoTo(2);
        }
        local.Click += async (_, _) => await RunAsync(async () =>
        { await PickLocalRestoreAsync(SetError, Path.Combine(location.Text, "workspace.sqlite")); AfterRestore(); });
        cloud.Click += async (_, _) => await RunAsync(async () =>
        {
            if (await PromptCloudSettingsAsync(null) is { } settings && !closed)
            { await BrowseCloudAsync(settings, lifetime.Token, Path.Combine(location.Text, "workspace.sqlite")); AfterRestore(); }
        });
        void WindowClosed(object sender, WindowEventArgs args)
        { if (!busy) finished.TrySetResult(false); lifetime.Cancel(); }
        GoTo(0);
        var hidden = Root.Children.Select(child => (Child: child, child.Visibility)).ToArray();
        foreach (var (child, _) in hidden) child.Visibility = Visibility.Collapsed;
        Root.Children.Add(page); Closed += WindowClosed;
        try { return await finished.Task; }
        finally
        {
            Closed -= WindowClosed; password.Password = confirm.Password = ""; lockEditor.Clear();
            if (!closed)
            {
                Root.Children.Remove(page);
                foreach (var (child, visibility) in hidden) child.Visibility = visibility;
            }
        }
    }

    private static SolidColorBrush Rgb(int rgb) => new(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    private static TextBlock SetupText(string text, double size, string color = "ShellTextBrush") => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left,
        HorizontalAlignment = HorizontalAlignment.Left, Foreground = WorkspaceTheme.Brush(color)
    };

    private static StackPanel SetupSection(string title, params UIElement[] controls)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var control in controls) content.Children.Add(control);
        return new StackPanel { Spacing = 6, Children = {
            SetupText(title, 14), new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"),
                Background = WorkspaceTheme.Brush("ShellCardBrush"), Child = content } } };
    }
}
