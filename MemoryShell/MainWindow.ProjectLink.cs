using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private static readonly Uri ProjectUri = new("https://github.com/YuXia-Net/Your-Memories-Shell");

    private static HyperlinkButton ProjectLink(Brush? foreground = null)
    {
        var link = new HyperlinkButton
        {
            NavigateUri = ProjectUri,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
            {
                new Viewbox { Child = GitHubPath(foreground), Width = 16, Height = 16 },
                new TextBlock { Text = "GitHub", Foreground = foreground ?? WorkspaceTheme.Brush("ShellTextBrush"), VerticalAlignment = VerticalAlignment.Center }
            } },
            Padding = new Thickness(5, 1, 5, 1), MinHeight = 22, MinWidth = 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, "GitHub");
        ToolTipService.SetToolTip(link, "GitHub · Your Memories Shell");
        return link;
    }
    private static Microsoft.UI.Xaml.Shapes.Path GitHubPath(Brush? foreground)
    {
        var path = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='M12,0 C5.37,0 0,5.37 0,12 C0,17.31 3.44,21.8 8.21,23.39 C8.81,23.5 9.03,23.13 9.03,22.81 L9.01,20.57 C5.67,21.29 4.97,19.16 4.97,19.16 C4.43,17.77 3.64,17.4 3.64,17.4 C2.55,16.66 3.72,16.67 3.72,16.67 C4.92,16.76 5.55,17.91 5.55,17.91 C6.62,19.75 8.36,19.22 9.05,18.91 C9.16,18.13 9.47,17.6 9.81,17.3 C7.14,17 4.34,15.96 4.34,11.37 C4.34,10.06 4.81,8.99 5.57,8.15 C5.45,7.85 5.04,6.63 5.69,4.98 C5.69,4.98 6.7,4.66 8.99,6.21 C9.95,5.94 10.98,5.81 12,5.8 C13.02,5.81 14.05,5.94 15.01,6.21 C17.3,4.66 18.3,4.98 18.3,4.98 C18.96,6.63 18.55,7.85 18.43,8.15 C19.2,8.99 19.66,10.06 19.66,11.37 C19.66,15.97 16.85,17 14.18,17.3 C14.61,17.67 15,18.4 15,19.52 L14.99,22.81 C14.99,23.13 15.21,23.51 15.82,23.39 C20.57,21.8 24,17.3 24,12 C24,5.37 18.63,0 12,0 Z' />");
        path.Fill = foreground ?? WorkspaceTheme.Brush("ShellTextBrush");
        return path;
    }

}
