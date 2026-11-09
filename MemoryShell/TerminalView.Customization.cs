using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

namespace MemoryShell;

public sealed partial class TerminalView
{
    internal void ApplyWorkspaceAppearance(WorkspaceAppearance settings, BitmapImage? wallpaper, TerminalAppearance? previous)
    {
        workspaceVisuals = settings;
        ansiOverride = settings.ReadAnsiColors();
        var fontName = settings.TerminalFont ?? "Consolas";
        if (renderFont.Source != fontName)
        {
            renderFont = new FontFamily(fontName);
            measurement.FontFamily = input.FontFamily = renderFont;
            measuredFontSize = 0;
        }
        wallpaperLayer.Opacity = settings.WallpaperOpacity / 100d;
        wallpaperLayer.Background = wallpaper is null ? null : new ImageBrush
        {
            ImageSource = wallpaper,
            Stretch = settings.WallpaperStretch
        };
        var appearance = settings.ResolveTerminal(previous);
        if (new AccessibilitySettings().HighContrast)
            appearance = appearance with { Theme = TerminalTheme.HighContrast };
        SetAppearance(appearance);
    }
}

internal sealed partial class SessionPane
{
    internal void ApplyWorkspaceAppearance(WorkspaceAppearance settings, BitmapImage? wallpaper)
    {
        command.FontFamily = new FontFamily(settings.TerminalFont ?? "Consolas");
        command.FontSize = settings.CommandFontSize ?? 13;
        terminal.ApplyWorkspaceAppearance(settings, wallpaper, store.Appearance);
    }
}
