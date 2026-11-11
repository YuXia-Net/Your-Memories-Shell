using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MemoryShell;

internal sealed class ToolbarLayoutPicker : Grid
{
    private readonly RadioButtons choices = new();
    public int SelectedIndex { get => choices.SelectedIndex; set => choices.SelectedIndex = value; }
    public event EventHandler? SelectionChanged;

    public ToolbarLayoutPicker(bool logoOnLeft, bool compact = false)
    {
        if (!compact) choices.Header = L10n.Get("TopBarLayout");
        choices.MaxColumns = 2;
        Children.Add(choices);
        choices.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        SizeChanged += (_, args) => choices.MaxColumns = args.NewSize.Width < (compact ? 370 : 520) ? 1 : 2;
        foreach (var left in new[] { true, false })
        {
            var preview = new Grid { MinWidth = compact ? 145 : 210, Height = compact ? 42 : 54, Padding = new Thickness(compact ? 8 : 12),
                Background = WorkspaceTheme.Brush("ShellToolbarBrush"), CornerRadius = new CornerRadius(5), ColumnSpacing = compact ? 12 : 24 };
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            preview.ColumnDefinitions.Add(new ColumnDefinition());
            var logo = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/MemoryShell.png")), Width = 26, Height = 26 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = compact ? 8 : 14, VerticalAlignment = VerticalAlignment.Center };
            foreach (var symbol in new[] { Symbol.Add, Symbol.Folder, Symbol.Setting })
                buttons.Children.Add(new SymbolIcon(symbol) { Foreground = WorkspaceTheme.Brush("ShellTextBrush") });
            Grid.SetColumn(logo, left ? 0 : 1); Grid.SetColumn(buttons, left ? 1 : 0);
            logo.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            buttons.HorizontalAlignment = left ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            preview.Children.Add(logo); preview.Children.Add(buttons);
            choices.Items.Add(new RadioButton { Content = new StackPanel { Spacing = 8, Children =
            { preview, new TextBlock { Text = L10n.Get(left ? "LogoLeft" : "LogoRight"), TextWrapping = TextWrapping.Wrap } } } });
        }
        SelectedIndex = logoOnLeft ? 0 : 1;
    }
}
