using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private const long MaxWallpaperBytes = 24L * 1024 * 1024;
    private WorkspaceAppearanceFile appearanceFile = new();
    private WorkspaceAppearance workspaceAppearance = WorkspaceAppearance.Default;
    private BitmapImage? workspaceWallpaper;

    private static ComboBox CreateSkinPicker(WorkspaceSkin selected)
    {
        var picker = new ComboBox { Header = L10n.Get("SD77BEEBD1232"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in new[] { L10n.Get("SB676597E1697"), L10n.Get("SDD43589D28AD"), L10n.Get("SB9256B8051B9") })
            picker.Items.Add(item);
        picker.SelectedIndex = (int)selected;
        return picker;
    }

    private void ApplyWorkspaceAppearance()
    {
        try { appearanceFile = WorkspaceAppearanceFile.Load(); }
        catch (Exception ex)
        {
            appearanceFile = new WorkspaceAppearanceFile();
            Status.Text = L10n.Format("SB001EC97CFA9", ex.Message);
        }
        PresentWorkspaceAppearance(appearanceFile.Current);
        _ = ReloadWallpaperAsync(appearanceFile.Current);
    }

    private void SaveInitialWorkspaceSkin(WorkspaceSkin skin)
    {
        var next = workspaceAppearance with { Skin = skin };
        SaveAppearanceFile(next, appearanceFile.Presets);
        PresentWorkspaceAppearance(next, workspaceWallpaper);
        if (workspaceWallpaper is null)
            _ = ReloadWallpaperAsync(next);
    }

    private void SaveAppearanceFile(WorkspaceAppearance appearance, Dictionary<string, WorkspaceAppearance> presets)
    {
        var next = new WorkspaceAppearanceFile { Current = appearance, Presets = presets };
        next.Save();
        appearanceFile = next;
    }

    private void PresentWorkspaceAppearance(WorkspaceAppearance appearance, BitmapImage? wallpaper = null)
    {
        workspaceAppearance = appearance;
        Grid.SetColumn(ToolbarLogo, appearance.LogoOnLeft ? 0 : 2);
        Grid.SetColumn(ToolbarCommands, appearance.LogoOnLeft ? 1 : 0);
        Grid.SetColumnSpan(ToolbarCommands, 2);
        ToolbarCommands.HorizontalAlignment = appearance.LogoOnLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        workspaceWallpaper = wallpaper;
        WorkspaceTheme.Apply(appearance);
        Root.RequestedTheme = appearance.Skin == WorkspaceSkin.WindowsLight ? ElementTheme.Light : ElementTheme.Dark;
        var font = new FontFamily(appearance.InterfaceFont ?? "Segoe UI Variable Text");
        ApplyInterfaceFont(Root, font);
        foreach (var pane in panes.Values)
            pane.ApplyWorkspaceAppearance(appearance, wallpaper);
        memoryTerminal?.ApplyWorkspaceAppearance(appearance, wallpaper, store.Appearance);
        RefreshSessionActions();
    }

    private static void ApplyInterfaceFont(DependencyObject element, FontFamily font)
    {
        switch (element)
        {
            case IconElement or TerminalView:
                return;
            case Control control:
                control.FontFamily = font;
                return;
            case TextBlock text:
                text.FontFamily = font;
                return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            ApplyInterfaceFont(VisualTreeHelper.GetChild(element, i), font);
    }

    private async Task ReloadWallpaperAsync(WorkspaceAppearance appearance)
    {
        if (appearance.Wallpaper is not { } fileName)
            return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.Combine(WorkspaceStore.CustomFolder, fileName));
            var image = await ReadWallpaperAsync(file);
            if (!closed && ReferenceEquals(appearance, workspaceAppearance))
                PresentWorkspaceAppearance(appearance, image);
        }
        catch (Exception ex)
        {
            if (!closed && ReferenceEquals(appearance, workspaceAppearance))
                Status.Text = L10n.Format("S6203D3A9AA51", ex.Message);
        }
    }

    private static async Task<BitmapImage> ReadWallpaperAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        if (stream.Size is 0 or > MaxWallpaperBytes)
            throw new InvalidDataException(L10n.Get("S62EEF66745D7"));
        var image = new BitmapImage { DecodePixelWidth = 1920 };
        await image.SetSourceAsync(stream);
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            throw new InvalidDataException(L10n.Get("S9C11E10C2722"));
        return image;
    }

    private async void OpenCustomFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WorkspaceStore.CustomFolder);
            var folder = await StorageFolder.GetFolderFromPathAsync(WorkspaceStore.CustomFolder);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
                Status.Text = L10n.Get("SD94C55C3EBD2");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void AppearanceSettings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync(L10n.Get("S86A63F23A076"));

    private IDisposable AddAppearanceSettings(SettingsTabHost tabs)
    {
        var presets = new Dictionary<string, WorkspaceAppearance>(appearanceFile.Presets, StringComparer.OrdinalIgnoreCase);
        var skin = CreateSkinPicker(workspaceAppearance.Skin);
        var logoSide = new ToolbarLayoutPicker(workspaceAppearance.LogoOnLeft);
        var terminalActions = new TerminalActionsPicker(workspaceAppearance);
        var terminalFont = Field(L10n.Get("SE9573BBD1CD8"), L10n.Get("SEFFCFD85CA96"));
        var interfaceFont = Field(L10n.Get("S7E8F3BF3EB30"), L10n.Get("S050F572D02A0"));
        var terminalSize = Number(L10n.Get("S2CA0EEBACFE2"), 9, 32);
        var commandSize = Number(L10n.Get("S8EEA30424756"), 10, 24);
        var scrollback = Number(L10n.Get("SD98258386138"), 100, 50000);
        var blink = new CheckBox { Content = L10n.Get("S9342B40E1C88") };
        var shellBase = Field(L10n.Get("S22418A869D6E"), "#RRGGBB");
        var panel = Field(L10n.Get("S124821392BCA"), "#RRGGBB");
        var text = Field(L10n.Get("SD80483F7670F"), "#RRGGBB");
        var accent = Field(L10n.Get("SBC19DC89E3A4"), "#RRGGBB");
        var terminalBack = Field(L10n.Get("SE4EC0FADBCAD"), "#RRGGBB");
        var terminalText = Field(L10n.Get("S4D8EFD14ACA3"), "#RRGGBB");
        var cursor = Field(L10n.Get("SFCA47BBC10FB"), "#RRGGBB");
        var selection = Field(L10n.Get("S6B39AB374D59"), "#RRGGBB");
        var ansi = Field(L10n.Get("S5A01BA9B4F1C"), L10n.Get("SDB09903E23F1"));
        ansi.TextWrapping = TextWrapping.Wrap;
        ansi.MaxLength = 250;
        var opacity = Number(L10n.Get("S1230C7E21DD4"), 0, 100);
        var stretch = new ComboBox { Header = L10n.Get("SD8A74EB2E8C0"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in new[] { L10n.Get("S301A69C74DFC"), L10n.Get("S5D7D4DACA250"), L10n.Get("S530AA0210369"), L10n.Get("SB9F9FD909370") })
            stretch.Items.Add(item);
        var wallpaperLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = WorkspaceTheme.Brush("ShellMutedBrush") };
        var selectImage = new Button { Content = L10n.Get("S9AD12A1AB4F6") };
        var removeImage = new Button { Content = L10n.Get("S86D72AABA84D") };
        var chooser = new ComboBox { Header = L10n.Get("SF7D013552098"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var loadPreset = new Button { Content = L10n.Get("SF716B5F91E72") };
        var deletePreset = new Button { Content = L10n.Get("SD777A073E1A5") };
        var reset = new Button { Content = L10n.Get("S3FFA384ADF09") };
        var presetName = Field(L10n.Get("S4E1E2FE34A05"), L10n.Get("SBA6745EE22A7"));
        presetName.MaxLength = 40;
        var error = ErrorText();
        error.Foreground = WorkspaceTheme.Brush("ShellErrorBrush");
        string? wallpaper = null;
        StorageFile? stagedWallpaper = null;
        var preview = new TerminalView { IsHitTestVisible = false, MinHeight = 100 };
        var commandPreview = new TextBlock { Text = "user@your-server:~$ ", Margin = new Thickness(10, 5, 10, 5) };
        BitmapImage? previewImage = workspaceWallpaper;
        preview.Write("\u001b[36muser@your-server\u001b[0m:~$ ssh production\r\n\u001b[32mWelcome to Your Memories Shell\u001b[0m\r\n$ ls -lh\r\n\u001b[34mworkspace/\u001b[0m  notes.txt  backup.tar.gz\r\n$ ");
        WorkspaceAppearance ReadAppearance() => new()
        {
            Skin = (WorkspaceSkin)skin.SelectedIndex, LogoOnLeft = logoSide.SelectedIndex == 0, TerminalActionsAtTabs = terminalActions.AtTabs,
            TerminalActionsOnLeft = terminalActions.OnLeft,
            TerminalFont = OptionalText(terminalFont), InterfaceFont = OptionalText(interfaceFont),
            TerminalFontSize = ReadNumber(terminalSize), CommandFontSize = ReadNumber(commandSize),
            Scrollback = ReadNumber(scrollback), CursorBlink = blink.IsChecked == true,
            BaseColor = OptionalText(shellBase), PanelColor = OptionalText(panel), TextColor = OptionalText(text),
            AccentColor = OptionalText(accent), TerminalBackground = OptionalText(terminalBack),
            TerminalForeground = OptionalText(terminalText), TerminalCursor = OptionalText(cursor),
            TerminalSelection = OptionalText(selection), AnsiColors = OptionalText(ansi),
            Wallpaper = wallpaper, WallpaperOpacity = ReadNumber(opacity), WallpaperStretch = (Stretch)stretch.SelectedIndex
        };
        void UpdatePreview()
        {
            try
            {
                var next = ReadAppearance(); next.Validate();
                preview.ApplyWorkspaceAppearance(next, previewImage, store.Appearance);
                commandPreview.FontFamily = new FontFamily(next.TerminalFont ?? "Consolas");
                commandPreview.FontSize = next.CommandFontSize ?? 13;
                error.Text = "";
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }

        void Fill(WorkspaceAppearance settings)
        {
            var defaults = settings.ResolveTerminal(store.Appearance);
            skin.SelectedIndex = (int)settings.Skin;
            logoSide.SelectedIndex = settings.LogoOnLeft ? 0 : 1;
            terminalActions.AtTabs = settings.TerminalActionsAtTabs;
            terminalActions.OnLeft = settings.TerminalActionsOnLeft;
            terminalFont.Text = settings.TerminalFont ?? "";
            interfaceFont.Text = settings.InterfaceFont ?? "";
            terminalSize.Value = defaults.FontSize;
            commandSize.Value = settings.CommandFontSize ?? 13;
            scrollback.Value = defaults.Scrollback;
            blink.IsChecked = defaults.CursorBlink;
            shellBase.Text = settings.BaseColor ?? "";
            panel.Text = settings.PanelColor ?? "";
            text.Text = settings.TextColor ?? "";
            accent.Text = settings.AccentColor ?? "";
            terminalBack.Text = settings.TerminalBackground ?? "";
            terminalText.Text = settings.TerminalForeground ?? "";
            cursor.Text = settings.TerminalCursor ?? "";
            selection.Text = settings.TerminalSelection ?? "";
            ansi.Text = settings.AnsiColors ?? "";
            opacity.Value = settings.WallpaperOpacity;
            stretch.SelectedIndex = (int)settings.WallpaperStretch;
            stagedWallpaper = null;
            wallpaper = settings.Wallpaper;
            previewImage = settings.Wallpaper == workspaceAppearance.Wallpaper ? workspaceWallpaper : null;
            wallpaperLabel.Text = wallpaper ?? L10n.Get("SAD625FD2ED94");
            UpdatePreview();
            if (wallpaper is { } fileName && settings.Wallpaper != workspaceAppearance.Wallpaper) _ = LoadPreviewImageAsync(fileName);
        }

        async Task LoadPreviewImageAsync(string fileName)
        {
            try
            {
                var image = await ReadWallpaperAsync(await StorageFile.GetFileFromPathAsync(Path.Combine(WorkspaceStore.CustomFolder, fileName)));
                if (!settingsOpen || wallpaper != fileName || stagedWallpaper is not null) return;
                previewImage = image; UpdatePreview();
            }
            catch (Exception ex) { if (settingsOpen && wallpaper == fileName) error.Text = ex.Message; }
        }

        void RefreshPresets()
        {
            chooser.ItemsSource = presets.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            chooser.SelectedIndex = chooser.Items.Count > 0 ? 0 : -1;
            loadPreset.IsEnabled = deletePreset.IsEnabled = chooser.SelectedIndex >= 0;
        }
        RefreshPresets();
        Fill(workspaceAppearance);
        loadPreset.Click += (_, _) =>
        {
            if (chooser.SelectedItem is string name && presets.TryGetValue(name, out var selected))
                Fill(selected);
        };
        deletePreset.Click += (_, _) =>
        {
            if (chooser.SelectedItem is string name && presets.Remove(name))
                RefreshPresets();
        };
        reset.Click += (_, _) => Fill(WorkspaceAppearance.Default with
        {
            Skin = (WorkspaceSkin)skin.SelectedIndex, TerminalFontSize = 14, Scrollback = 10000, CursorBlink = true
        });
        selectImage.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
                    picker.FileTypeFilter.Add(extension);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                if (await picker.PickSingleFileAsync() is { } file)
                {
                    stagedWallpaper = file;
                    wallpaperLabel.Text = file.Name;
                    previewImage = await ReadWallpaperAsync(file);
                    UpdatePreview();
                }
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        removeImage.Click += (_, _) =>
        {
            stagedWallpaper = null;
            wallpaper = null;
            wallpaperLabel.Text = L10n.Get("SAD625FD2ED94");
            previewImage = null;
            UpdatePreview();
        };

        AddTab(L10n.Get("S86A63F23A076"),
            new SettingsColumns(SettingsPages.Section(L10n.Get("SD77BEEBD1232"), skin, logoSide, terminalActions),
            SettingsPages.Section(L10n.Get("SF7D013552098"), chooser, Row(loadPreset, deletePreset), presetName, reset)));
        var terminalFields = new StackPanel { Spacing = 12, Children =
        {
            SettingsPages.Section(L10n.Get("S46A8CE191892"), interfaceFont, terminalFont,
                new SettingsColumns(terminalSize, commandSize, 300), scrollback, blink),
            SettingsPages.Section(L10n.Get("S2AE75E168123"), terminalBack, terminalText, cursor, selection, ansi),
            SettingsPages.Section(L10n.Get("S75BDE8E3BBA4"), wallpaperLabel, Row(selectImage, removeImage), opacity, stretch), error
        } };
        var previewArea = new Grid();
        previewArea.RowDefinitions.Add(new RowDefinition()); previewArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewArea.Children.Add(preview); Grid.SetRow(commandPreview, 1); previewArea.Children.Add(commandPreview);
        var terminalPage = new TerminalSettingsLayout(previewArea, terminalFields);
        tabs.Add(L10n.Get("S46A8CE191892"), terminalPage, ApplyAppearanceAsync, scroll: false);
        AddTab(L10n.Get("S2AE75E168123"), new TextBlock
        {
            Text = L10n.Get("S4057D7C53B60"), TextWrapping = TextWrapping.Wrap,
            Foreground = WorkspaceTheme.Brush("ShellMutedBrush")
        }, SettingsPages.Section(L10n.Get("S2AE75E168123"), shellBase, panel, text, accent));
        foreach (var field in new[] { terminalFont, interfaceFont, shellBase, panel, text, accent, terminalBack, terminalText, cursor, selection, ansi })
            field.TextChanged += (_, _) => UpdatePreview();
        foreach (var field in new[] { terminalSize, commandSize, scrollback, opacity })
            field.ValueChanged += (_, _) => UpdatePreview();
        skin.SelectionChanged += (_, _) => UpdatePreview();
        stretch.SelectionChanged += (_, _) => UpdatePreview();
        blink.Checked += (_, _) => UpdatePreview(); blink.Unchecked += (_, _) => UpdatePreview();
        preview.Loaded += (_, _) => UpdatePreview();
        UpdatePreview();
        async Task ApplyAppearanceAsync()
        {
            string? created = null;
            var committed = false;
            try
            {
                error.Text = "";
                var name = presetName.Text.Trim();
                if (name.Any(char.IsControl) || (name.Length > 0 && !presets.ContainsKey(name) && presets.Count >= 50))
                    throw new InvalidDataException(L10n.Get("S395C7135B535"));
                var next = ReadAppearance();
                next.Validate();

                BitmapImage? image = null;
                if (stagedWallpaper is { } candidate)
                {
                    image = await ReadWallpaperAsync(candidate);
                    var folder = await StorageFolder.GetFolderFromPathAsync(WorkspaceStore.CustomFolder);
                    var fileName = $"wallpaper-{Guid.NewGuid():N}{candidate.FileType.ToLowerInvariant()}";
                    var copied = await candidate.CopyAsync(folder, fileName, NameCollisionOption.FailIfExists);
                    created = copied.Path;
                    next = next with { Wallpaper = fileName };
                    next.Validate();
                }
                else if (wallpaper is not null)
                {
                    var file = await StorageFile.GetFileFromPathAsync(Path.Combine(WorkspaceStore.CustomFolder, wallpaper));
                    image = await ReadWallpaperAsync(file);
                }

                if (closed)
                    return;
                var saved = new Dictionary<string, WorkspaceAppearance>(presets, StringComparer.OrdinalIgnoreCase);
                if (name.Length > 0)
                    saved[name] = next;
                SaveAppearanceFile(next, saved);
                presets = saved;
                wallpaper = next.Wallpaper;
                stagedWallpaper = null;
                wallpaperLabel.Text = wallpaper ?? L10n.Get("SAD625FD2ED94");
                RefreshPresets();
                committed = true;
                PresentWorkspaceAppearance(next, image);
                Status.Text = L10n.Get("SB842CA803370");
            }
            catch (Exception ex) { error.Text = ex.Message; throw; }
            finally
            {
                if (!committed && created is not null)
                    TryDelete(created);
            }
        }

        void AddTab(string title, params UIElement[] children)
        {
            var panel = new StackPanel { Spacing = 20 };
            foreach (var child in children)
                panel.Children.Add(child);
            tabs.Add(title, panel, ApplyAppearanceAsync);
        }
        return preview;
    }

    private static TextBox Field(string label, string placeholder) =>
        new() { Header = label, PlaceholderText = placeholder, MaxLength = 100 };

    private static NumberBox Number(string label, double min, double max) =>
        new() { Header = label, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

    private static StackPanel Row(Button first, Button second) =>
        new() { Orientation = Orientation.Horizontal, Spacing = 7, Children = { first, second } };

    private static string? OptionalText(TextBox field) => field.Text.Trim() is { Length: > 0 } value ? value : null;

    private static int ReadNumber(NumberBox field) =>
        double.IsFinite(field.Value) && field.Value == Math.Truncate(field.Value)
            ? (int)field.Value : throw new InvalidDataException(L10n.Format("SC307AFD68D52", field.Header));
}
