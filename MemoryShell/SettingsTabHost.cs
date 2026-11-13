using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class SettingsTabHost : Grid
{
    private readonly TabView tabs = new() { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false,
        TabWidthMode = TabViewWidthMode.SizeToContent };
    private readonly Dictionary<TabViewItem, Func<Task>?> saves = [];
    private readonly Button save = new() { Content = L10n.Get("SaveAllSettings"), MinWidth = 108 };
    private readonly Dictionary<TabViewItem, string> snapshots = [];
    private readonly HashSet<TabViewItem> dirty = [];
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private bool busy;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, object> IgnoredControls = new();
    internal static void IgnoreChanges(DependencyObject control) => IgnoredControls.GetOrCreateValue(control);
    public bool IsSaving => busy;
    public bool HasChanges => dirty.Count > 0 || saves.Any(pair => pair.Value is not null && snapshots.GetValueOrDefault(pair.Key) != Snapshot(pair.Key.Content as DependencyObject));

    public SettingsTabHost()
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(tabs);
        save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var footer = new Grid { ColumnSpacing = 16, Padding = new Thickness(16, 12, 16, 0),
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(message); Grid.SetColumn(save, 1); footer.Children.Add(save);
        Grid.SetRow(footer, 1); Children.Add(footer);
        tabs.SelectionChanged += (_, _) => { message.Text = ""; RefreshSave(); };
        save.Click += async (_, _) =>
        {
            if (busy) return;
            message.Text = "";
            try { await SaveChangesAsync(); message.Foreground = WorkspaceTheme.Brush("ShellMutedBrush"); message.Text = L10n.Get("SettingsSaved"); }
            catch (Exception ex) { message.Foreground = WorkspaceTheme.Brush("ShellErrorBrush"); message.Text = ex.Message; }
        };
    }

    public void Add(string title, UIElement content, Func<Task>? apply = null, bool scroll = true)
    {
        var item = new TabViewItem { Header = title, IsClosable = false,
            Content = scroll ? new ScrollViewer { Content = new Grid { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left, Children = { content } }, Padding = new Thickness(20, 24, 20, 20),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch }
                : new Grid { Padding = new Thickness(20, 24, 20, 12), Children = { content } } };
        saves[item] = apply; tabs.TabItems.Add(item); tabs.SelectedItem ??= item; MarkClean(title); RefreshSave();
    }

    public void Select(string? title)
    {
        if (title is null) return;
        if (tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(item => Equals(item.Header, title)) is { } selected) tabs.SelectedItem = selected;
    }

    public void MarkClean(string title)
    {
        foreach (var item in saves.Keys.Where(t => Equals(t.Header, title))) { snapshots[item] = Snapshot(item.Content as DependencyObject); dirty.Remove(item); }
    }
    public void MarkChanged(Func<Task> action) { foreach (var pair in saves.Where(p => Equals(p.Value, action))) dirty.Add(pair.Key); }

    public async Task SaveChangesAsync()
    {
        if (busy) throw new InvalidOperationException(L10n.Get("SettingsSaving"));
        busy = true; tabs.IsEnabled = save.IsEnabled = false;
        try
        {
            foreach (var (item, action) in saves)
            {
                if (action is null || !dirty.Contains(item) && snapshots.GetValueOrDefault(item) == Snapshot(item.Content as DependencyObject)) continue;
                try { await action(); }
                catch { tabs.SelectedItem = item; throw; }
                foreach (var related in saves.Where(p => Equals(p.Value, action)).Select(p => p.Key))
                { snapshots[related] = Snapshot(related.Content as DependencyObject); dirty.Remove(related); }
            }
        }
        finally { busy = false; tabs.IsEnabled = true; RefreshSave(); }
    }

    private static string Snapshot(DependencyObject? root)
    {
        var data = new System.Text.StringBuilder();
        void Visit(DependencyObject? node)
        {
            if (node is null || IgnoredControls.TryGetValue(node, out _)) return;
            object? value = node switch {
                TextBox t => t.Text, PasswordBox p => p.Password, ComboBox c => c.SelectedIndex,
                RadioButtons r => r.SelectedIndex, ToggleSwitch t => t.IsOn,
                Microsoft.UI.Xaml.Controls.Primitives.ToggleButton t => t.IsChecked,
                NumberBox n => n.Value, _ => null };
            if (value is not null) { var text = value.ToString()!; data.Append(node.GetType().Name).Append(':').Append(text.Length).Append(':').Append(text).Append(';'); return; }
            switch (node)
            {
                case Panel p: foreach (var child in p.Children) Visit(child); break;
                case Border b: Visit(b.Child); break;
                case ContentControl c: Visit(c.Content as DependencyObject); break;
            }
        }
        Visit(root);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data.ToString())));
    }

    private void RefreshSave() { save.IsEnabled = !busy; save.Visibility = Visibility.Visible; }
}
