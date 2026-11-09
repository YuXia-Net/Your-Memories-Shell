using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MemoryShell;

internal enum WorkspaceSkin { Memory, WindowsLight, WindowsDark }

internal sealed record WorkspaceAppearance
{
    public static WorkspaceAppearance Default { get; } = new();

    public WorkspaceSkin Skin { get; init; } = WorkspaceSkin.WindowsLight;
    public bool LogoOnLeft { get; init; } = true;
    public string? TerminalFont { get; init; }
    public string? InterfaceFont { get; init; }
    public int? TerminalFontSize { get; init; }
    public int? CommandFontSize { get; init; }
    public int? Scrollback { get; init; }
    public bool? CursorBlink { get; init; }
    public string? BaseColor { get; init; }
    public string? PanelColor { get; init; }
    public string? TextColor { get; init; }
    public string? AccentColor { get; init; }
    public string? TerminalBackground { get; init; }
    public string? TerminalForeground { get; init; }
    public string? TerminalCursor { get; init; }
    public string? TerminalSelection { get; init; }
    public string? AnsiColors { get; init; }
    public string? Wallpaper { get; init; }
    public int WallpaperOpacity { get; init; } = 25;
    public Stretch WallpaperStretch { get; init; } = Stretch.UniformToFill;

    public TerminalAppearance ResolveTerminal(TerminalAppearance? legacy)
    {
        var defaults = legacy ?? TerminalAppearance.Default;
        var theme = Skin switch
        {
            WorkspaceSkin.WindowsLight => TerminalTheme.Light,
            WorkspaceSkin.WindowsDark => TerminalTheme.WindowsDark,
            _ => TerminalTheme.MemoryDark
        };
        return defaults with
        {
            Theme = theme,
            FontSize = TerminalFontSize ?? defaults.FontSize,
            Scrollback = Scrollback ?? defaults.Scrollback,
            CursorBlink = CursorBlink ?? defaults.CursorBlink
        };
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Skin) || !Enum.IsDefined(WallpaperStretch) ||
            TerminalFontSize is < 9 or > 32 || CommandFontSize is < 10 or > 24 ||
            Scrollback is < 100 or > 50000 || WallpaperOpacity is < 0 or > 100)
            throw new InvalidDataException(L10n.Get("SA6974A1B936C"));
        foreach (var font in new[] { TerminalFont, InterfaceFont })
            if (font is not null && (font.Length is 0 or > 100 || font.Any(char.IsControl) ||
                font.Contains('#') || font.Contains(':') || font.Contains('/') || font.Contains('\\')))
                throw new InvalidDataException(L10n.Get("SBE65903A6112"));
        foreach (var color in new[] { BaseColor, PanelColor, TextColor, AccentColor, TerminalBackground, TerminalForeground, TerminalCursor, TerminalSelection })
            if (color is not null && ColorValue(color) is null)
                throw new InvalidDataException(L10n.Get("SD31D5B23BB6B"));
        if (AnsiColors is not null && ReadAnsiColors() is null)
            throw new InvalidDataException(L10n.Get("S4D60F1C2B546"));
        if (Wallpaper is not null && (Wallpaper.Length > 128 || Wallpaper != Path.GetFileName(Wallpaper) ||
            Wallpaper.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetExtension(Wallpaper).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp")))
            throw new InvalidDataException(L10n.Get("SE0F3E2BE2642"));
    }

    public int[]? ReadAnsiColors()
    {
        if (AnsiColors is null)
            return null;
        var colors = AnsiColors.Split(',', StringSplitOptions.TrimEntries).Select(ColorValue).ToArray();
        return colors.Length == 16 && colors.All(color => color.HasValue)
            ? colors.Select(color => color.GetValueOrDefault()).ToArray() : null;
    }

    public static int? ColorValue(string? value) =>
        value is { Length: 7 } && value[0] == '#' &&
        int.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result) ? result : null;
}

internal sealed class WorkspaceAppearanceFile
{
    private const int MaxFileBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static string SettingsPath => Path.Combine(WorkspaceStore.CustomFolder, "Appearance.json");

    public WorkspaceAppearance Current { get; init; } = WorkspaceAppearance.Default;
    public Dictionary<string, WorkspaceAppearance> Presets { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static WorkspaceAppearanceFile Load()
    {
        if (!File.Exists(SettingsPath))
            return new WorkspaceAppearanceFile();
        using var file = File.OpenRead(SettingsPath);
        if (file.Length > MaxFileBytes)
            throw new InvalidDataException(L10n.Get("S038AA8076298"));
        var settings = JsonSerializer.Deserialize<WorkspaceAppearanceFile>(file, Options)
            ?? throw new InvalidDataException(L10n.Get("S9EB3BF76ACBC"));
        if (settings.Current is null || settings.Presets is null || settings.Presets.Count > 50)
            throw new InvalidDataException(L10n.Get("SA21D10E9C777"));
        settings.Current.Validate();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, appearance) in settings.Presets)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || name.Any(char.IsControl) ||
                appearance is null || !names.Add(name))
                throw new InvalidDataException(L10n.Get("S10B69928DAA6"));
            appearance.Validate();
        }
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(WorkspaceStore.CustomFolder);
        var temporary = Path.Combine(WorkspaceStore.CustomFolder, $"appearance-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, this, Options);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

internal static class WorkspaceTheme
{
    private static readonly string[] Keys =
    [
        "ShellBaseBrush", "ShellToolbarBrush", "ShellPaneBrush", "ShellCardBrush", "ShellLineBrush",
        "ShellTextBrush", "ShellMutedBrush", "ShellAccentBrush", "ShellActionBrush", "ShellActionTextBrush", "ShellErrorBrush"
    ];
    private static readonly int[] Memory = [0x0C121E, 0x18253A, 0x131E30, 0x1D2D46, 0x30435A, 0xEEF4FF, 0xA8B7CD, 0x83D6FF, 0x8FD8FF, 0x0C263C, 0xFFABA0];
    private static readonly int[] Light = [0xF8F8F8, 0xF0F0F0, 0xFFFFFF, 0xF5F5F5, 0xE0E0E0, 0x1B1B1B, 0x525252, 0x005FB8, 0x005FB8, 0xFFFFFF, 0x9C2528];
    private static readonly int[] Dark = [0x202020, 0x262626, 0x242424, 0x2B2B2B, 0x383838, 0xF5F5F5, 0xBDBDBD, 0x60CDFF, 0x60CDFF, 0x071C24, 0xFFABA0];
    private static readonly Dictionary<string, SolidColorBrush> LiveBrushes = new(StringComparer.Ordinal);

    public static SolidColorBrush Brush(string key)
    {
        if (!LiveBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(ToColor(Memory[Array.IndexOf(Keys, key)]));
            LiveBrushes.Add(key, brush);
        }
        return brush;
    }

    public static Color ToColor(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static void Apply(WorkspaceAppearance appearance)
    {
        var light = appearance.Skin == WorkspaceSkin.WindowsLight;
        var palette = (int[])(appearance.Skin switch
        {
            WorkspaceSkin.WindowsLight => Light,
            WorkspaceSkin.WindowsDark => Dark,
            _ => Memory
        }).Clone();
        if (WorkspaceAppearance.ColorValue(appearance.BaseColor) is int background)
            palette[0] = background;
        if (WorkspaceAppearance.ColorValue(appearance.PanelColor) is int panel)
            palette[1] = palette[2] = palette[3] = panel;
        if (WorkspaceAppearance.ColorValue(appearance.TextColor) is int text)
            palette[5] = palette[6] = text;
        if (WorkspaceAppearance.ColorValue(appearance.AccentColor) is int accent)
        {
            palette[7] = palette[8] = accent;
            palette[9] = ReadableText(accent);
        }

        var theme = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[light ? "Light" : "Dark"];
        var contrast = new AccessibilitySettings().HighContrast;
        var system = contrast ? new UISettings() : null;
        for (var i = 0; i < Keys.Length; i++)
        {
            var key = Keys[i];
            var color = ToColor(palette[i]);
            if (theme[key] is SolidColorBrush resource)
                resource.Color = color;
            Brush(key).Color = system is null ? color : system.GetColorValue(i is 0 or 1 or 2 or 3 or 9
                ? UIColorType.Background : UIColorType.Foreground);
        }
    }

    private static int ReadableText(int rgb)
    {
        static double Linear(int channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        var luminance = .2126 * Linear((rgb >> 16) & 255) + .7152 * Linear((rgb >> 8) & 255) + .0722 * Linear(rgb & 255);
        return luminance > .179 ? 0 : 0xFFFFFF;
    }
}
