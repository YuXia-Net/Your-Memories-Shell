using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private BreadcrumbBar? addressBreadcrumb;
    private Grid? addressEditor;
    private bool editingAddress;
    private sealed record PathCrumb(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    private FrameworkElement BuildExplorerAddress()
    {
        addressBreadcrumb = new BreadcrumbBar { VerticalAlignment = VerticalAlignment.Center };
        addressBreadcrumb.ItemClicked += async (_, args) =>
        {
            if (args.Item is PathCrumb crumb)
                await FileActionAsync(() => RefreshFilesAsync(crumb.Path, useCache: true));
        };
        var edit = new Button { Content = new FontIcon { Glyph = "\uE70F", FontSize = 13 },
            Padding = new Thickness(8), MinWidth = 30 };
        ToolTipService.SetToolTip(edit, L10n.Get("EditPath"));
        edit.Click += (_, _) => EditAddress();
        addressEditor = new Grid { ColumnSpacing = 4, Background = WorkspaceTheme.Brush("ShellPaneBrush"),
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 0, 0, 0) };
        addressEditor.ColumnDefinitions.Add(new ColumnDefinition());
        addressEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addressEditor.Children.Add(addressBreadcrumb);
        addressEditor.Children.Add(path);
        Grid.SetColumn(edit, 1); addressEditor.Children.Add(edit);
        path.LostFocus += (_, _) => { editingAddress = false; UpdateExplorerAddress(); };
        path.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true; path.Text = directory; editingAddress = false; UpdateExplorerAddress();
            files.Focus(FocusState.Programmatic);
        };
        var addressShortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = VirtualKey.D, Modifiers = VirtualKeyModifiers.Menu };
        addressShortcut.Invoked += (_, args) => { if (!IsFilePaneVisible) return; args.Handled = true; EditAddress(); };
        KeyboardAccelerators.Add(addressShortcut);
        UpdateExplorerAddress();
        return addressEditor;
    }

    private void EditAddress()
    {
        editingAddress = true; UpdateExplorerAddress();
        path.Focus(FocusState.Programmatic); path.SelectAll();
    }

    private void UpdateExplorerAddress()
    {
        if (addressBreadcrumb is null) return;
        var crumbs = new List<PathCrumb> { new("/", "/") };
        var fullPath = "";
        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            fullPath += "/" + segment;
            crumbs.Add(new(segment, fullPath));
        }
        addressBreadcrumb.ItemsSource = crumbs;
        var showPath = editingAddress || !filePreferences.ExplorerStyle;
        path.Visibility = showPath ? Visibility.Visible : Visibility.Collapsed;
        addressBreadcrumb.Visibility = showPath ? Visibility.Collapsed : Visibility.Visible;
        addressBreadcrumb.IsEnabled = session is not null && !busy;
    }
}
