using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class SettingsTabHost : Grid
{
    private readonly TabView tabs = new() { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false,
        TabWidthMode = TabViewWidthMode.SizeToContent };
    private readonly Dictionary<TabViewItem, Func<Task>?> saves = [];
    private readonly Button save = new() { Content = L10n.Get("SA3030BF8F16D"), MinWidth = 108 };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private bool busy;

    public SettingsTabHost(double height)
    {
        Height = height;
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(tabs);
        var footer = new Grid { ColumnSpacing = 16, Padding = new Thickness(12, 12, 12, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(message); Grid.SetColumn(save, 1); footer.Children.Add(save);
        Grid.SetRow(footer, 1); Children.Add(footer);
        tabs.SelectionChanged += (_, _) => { message.Text = ""; RefreshSave(); };
        save.Click += async (_, _) =>
        {
            if (busy || tabs.SelectedItem is not TabViewItem item || saves.GetValueOrDefault(item) is not { } action) return;
            busy = true; tabs.IsEnabled = save.IsEnabled = false; message.Text = "";
            try { await action(); message.Foreground = WorkspaceTheme.Brush("ShellMutedBrush"); message.Text = L10n.Get("SC8550237BA70"); }
            catch (Exception ex) { message.Foreground = WorkspaceTheme.Brush("ShellErrorBrush"); message.Text = ex.Message; }
            finally { busy = false; tabs.IsEnabled = true; RefreshSave(); }
        };
    }

    public void Add(string title, UIElement content, Func<Task>? apply = null, bool scroll = true)
    {
        var item = new TabViewItem { Header = title, IsClosable = false,
            Content = scroll ? new ScrollViewer { Content = content, Padding = new Thickness(8, 10, 8, 6),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch }
                : new Grid { Padding = new Thickness(8, 10, 8, 6), Children = { content } } };
        saves[item] = apply; tabs.TabItems.Add(item); tabs.SelectedItem ??= item; RefreshSave();
    }

    public void Select(string? title)
    {
        if (title is null) return;
        if (tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(item => Equals(item.Header, title)) is { } selected) tabs.SelectedItem = selected;
    }

    private void RefreshSave() { save.IsEnabled = !busy; save.Visibility = tabs.SelectedItem is TabViewItem item && saves.GetValueOrDefault(item) is not null
        ? Visibility.Visible : Visibility.Collapsed; }
}
