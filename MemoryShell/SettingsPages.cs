using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace MemoryShell;

internal sealed class SettingsPages : Grid
{
    private readonly NavigationView navigation = new()
    {
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        OpenPaneLength = 176, IsPaneOpen = true, AlwaysShowHeader = false,
        IsTitleBarAutoPaddingEnabled = false
    };
    private readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer scroll;

    public SettingsPages(double height)
    {
        Height = height;
        scroll = new ScrollViewer
        {
            Content = page, Padding = new Thickness(20, 6, 8, 16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled
        };
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            page.ContentTransitions = new TransitionCollection { new EntranceThemeTransition { FromHorizontalOffset = 0, FromVerticalOffset = 8 } };
        navigation.Content = scroll;
        Children.Add(navigation);
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem { Tag: UIElement content }) return;
            page.Content = content;
            scroll.ChangeView(null, 0, null, true);
        };
        SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 620;
            navigation.PaneDisplayMode = narrow ? NavigationViewPaneDisplayMode.Top : NavigationViewPaneDisplayMode.Left;
            scroll.Padding = new Thickness(narrow ? 4 : 20, 8, 8, 16);
        };
    }

    public void Add(string title, UIElement content)
    {
        var item = new NavigationViewItem { Content = title, Tag = content };
        navigation.MenuItems.Add(item);
        navigation.SelectedItem ??= item;
    }

    public void Select(int index)
    {
        if (index >= 0 && index < navigation.MenuItems.Count) navigation.SelectedItem = navigation.MenuItems[index];
    }

    public static StackPanel Section(string title, params UIElement[] controls)
    {
        var body = new StackPanel { Spacing = 8 };
        foreach (var control in controls) body.Children.Add(control);
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = WorkspaceTheme.Brush("ShellTextBrush"), TextWrapping = TextWrapping.Wrap },
                new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12),
                    Background = WorkspaceTheme.Brush("ShellCardBrush"), BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"),
                    BorderThickness = new Thickness(1), Child = body }
            }
        };
    }
}
