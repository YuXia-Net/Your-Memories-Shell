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
        var body = new StackPanel { Spacing = 12 };
        foreach (var control in controls) body.Children.Add(control);
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = WorkspaceTheme.Brush("ShellTextBrush"), TextWrapping = TextWrapping.Wrap },
                new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16),
                    Background = WorkspaceTheme.Brush("ShellCardBrush"), BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"),
                    BorderThickness = new Thickness(1), Child = body }
            }
        };
    }
}

internal sealed class SettingsColumns : Panel
{
    private readonly FrameworkElement left;
    private readonly FrameworkElement right;
    private readonly double breakpoint;
    public SettingsColumns(FrameworkElement left, FrameworkElement right, double breakpoint = 760)
    {
        this.left = left; this.right = right; this.breakpoint = breakpoint;
        Children.Add(left); Children.Add(right);
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : breakpoint;
        var wide = width >= breakpoint;
        var itemWidth = wide ? (width - 20) / 2 : width;
        var constraint = new Windows.Foundation.Size(itemWidth, double.PositiveInfinity);
        left.Measure(constraint); right.Measure(constraint);
        return new Windows.Foundation.Size(width, wide ? Math.Max(left.DesiredSize.Height, right.DesiredSize.Height) : left.DesiredSize.Height + right.DesiredSize.Height + 20);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size size)
    {
        var wide = size.Width >= breakpoint;
        var width = wide ? (size.Width - 20) / 2 : size.Width;
        left.Arrange(new Windows.Foundation.Rect(0, 0, width, left.DesiredSize.Height));
        right.Arrange(new Windows.Foundation.Rect(wide ? width + 20 : 0, wide ? 0 : left.DesiredSize.Height + 20, width, right.DesiredSize.Height));
        return size;
    }
}
