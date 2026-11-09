using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private TextBox commandEditor = null!;

    private UIElement BuildCommandWorkspace(UIElement savedCommands)
    {
        commandEditor = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MaxLength = 65536,
            FontFamily = new FontFamily("Consolas"), FontSize = 13,
            PlaceholderText = L10n.Get("S0AF85CC7EAFB"), MinHeight = 60,
            VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(9)
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        layout.Children.Add(savedCommands);
        var divider = new Border { Background = WorkspaceTheme.Brush("ShellLineBrush") };
        SetColumn(divider, 1);
        layout.Children.Add(divider);

        var editorPane = new Grid { Padding = new Thickness(8, 0, 8, 8), RowSpacing = 6 };
        editorPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorPane.RowDefinitions.Add(new RowDefinition());
        editorPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = L10n.Get("SCD19D9662F99"), FontSize = 13, Margin = new Thickness(0, 6, 0, 4) };
        editorPane.Children.Add(heading);
        AutomationProperties.SetName(commandEditor, L10n.Get("SB79D10605F09"));
        ScrollViewer.SetHorizontalScrollBarVisibility(commandEditor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(commandEditor, ScrollBarVisibility.Auto);
        SetRow(commandEditor, 1);
        editorPane.Children.Add(commandEditor);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Button(L10n.Get("S7E0C393C4D57"), SendEditorCommandAsync));
        actions.Children.Add(Button(L10n.Get("SC7956589EF38"), () => EditCommandTemplateAsync(null, commandEditor.Text)));
        actions.Children.Add(Button(L10n.Get("S1EF3DE06B32E"), () => { commandEditor.Text = ""; return Task.CompletedTask; }));
        var actionScroll = new ScrollViewer { Content = actions, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetRow(actionScroll, 2);
        editorPane.Children.Add(actionScroll);
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.Enter, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += async (_, args) =>
        {
            args.Handled = true;
            try { await SendEditorCommandAsync(); }
            catch (Exception ex) { if (!disposed) SetStatus(ex.Message); }
        };
        commandEditor.KeyboardAccelerators.Add(accelerator);
        var load = new MenuFlyoutItem { Text = L10n.Get("SC29B6C098AD4") };
        load.Click += (_, _) =>
        {
            if (SelectedCommand is not { } selected) return;
            commandEditor.Text = selected.Text;
            commandEditor.Focus(FocusState.Programmatic);
        };
        if (commandList.ContextFlyout is MenuFlyout menu) menu.Items.Insert(1, load);
        SetColumn(editorPane, 2);
        layout.Children.Add(editorPane);
        var workspace = new Grid { RowSpacing = 6 };
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition());
        var savedTab = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = L10n.Get("S6F3D833B0FA1") };
        var editorTab = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = L10n.Get("SCD19D9662F99"), IsChecked = true };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0, 8, 0), Children = { savedTab, editorTab } };
        var showEditor = true;
        void Arrange()
        {
            var compact = workspace.ActualWidth < 760;
            tabs.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            savedCommands.Visibility = compact && showEditor ? Visibility.Collapsed : Visibility.Visible;
            editorPane.Visibility = compact && !showEditor ? Visibility.Collapsed : Visibility.Visible;
            divider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            layout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 1, GridUnitType.Star);
            layout.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 1);
            if (savedCommands is FrameworkElement saved) SetColumnSpan(saved, compact ? 3 : 1);
            SetColumn(editorPane, compact ? 0 : 2);
            SetColumnSpan(editorPane, compact ? 3 : 1);
            savedTab.IsChecked = !showEditor;
            editorTab.IsChecked = showEditor;
        }
        savedTab.Click += (_, _) => { showEditor = false; Arrange(); };
        editorTab.Click += (_, _) => { showEditor = true; Arrange(); };
        load.Click += (_, _) => { showEditor = true; Arrange(); };
        workspace.SizeChanged += (_, _) => Arrange();
        workspace.Children.Add(tabs);
        SetRow(layout, 1);
        workspace.Children.Add(layout);
        return workspace;
    }

    private async Task SendEditorCommandAsync()
    {
        var text = commandEditor.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;
        if (session is null) { SetStatus(L10n.Get("SE6801EB39BCD")); return; }
        if (text.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
        {
            SetStatus(L10n.Get("S02726CD2D17A"));
            return;
        }
        await RunQuickCommandAsync(new QuickCommand(L10n.Get("S04DE16B68F23"), text));
    }
}
