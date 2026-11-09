using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MemoryShell;

internal sealed class TerminalSettingsLayout : Panel
{
    private readonly Grid previewArea = new() { RowSpacing = 8, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border previewFrame;
    private readonly ScrollViewer controls;
    private readonly Button expand = new() { Content = L10n.Get("ExpandPreview"), HorizontalAlignment = HorizontalAlignment.Right };
    private bool expanded;

    public TerminalSettingsLayout(UIElement preview, UIElement fields)
    {
        previewArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid();
        heading.Children.Add(new TextBlock { Text = L10n.Get("TerminalPreview"), VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        heading.Children.Add(expand); previewArea.Children.Add(heading);
        previewFrame = new Border { Child = preview, BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetRow(previewFrame, 1); previewArea.Children.Add(previewFrame); Children.Add(previewArea);
        controls = new ScrollViewer { Content = fields, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 8, 0) };
        Children.Add(controls);
        expand.Click += (_, _) =>
        {
            expanded = !expanded;
            expand.Content = L10n.Get(expanded ? "RestorePreview" : "ExpandPreview");
            controls.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            InvalidateMeasure();
        };
    }

    private (Rect Preview, Rect Controls, double FrameWidth) Layout(Size size)
    {
        var wide = size.Width >= 780 && !expanded;
        var right = wide ? Math.Clamp(size.Width * .34, 280, 380) : 0;
        var left = wide ? size.Width - right - 18 : size.Width;
        var maxHeight = Math.Max(0, size.Height - 44) * (wide || expanded ? 1 : .48);
        var frameWidth = Math.Max(0, Math.Min(left, maxHeight * 16 / 9));
        var previewHeight = Math.Min(size.Height, frameWidth * 9 / 16 + 44);
        return (new Rect(0, 0, left, previewHeight),
            wide ? new Rect(left + 18, 0, right, size.Height) : new Rect(0, previewHeight + 12, size.Width, Math.Max(0, size.Height - previewHeight - 12)), frameWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 800,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 500);
        var areas = Layout(size);
        previewFrame.Width = areas.FrameWidth; previewFrame.Height = areas.FrameWidth * 9 / 16;
        previewArea.Measure(new Size(areas.Preview.Width, areas.Preview.Height));
        if (!expanded) controls.Measure(new Size(areas.Controls.Width, areas.Controls.Height));
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var areas = Layout(finalSize);
        previewArea.Arrange(areas.Preview);
        if (!expanded) controls.Arrange(areas.Controls);
        return finalSize;
    }
}
