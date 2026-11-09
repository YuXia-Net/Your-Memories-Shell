using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MemoryShell;

public sealed partial class TerminalView
{
    private const double PaddingX = 8;
    private const double PaddingY = 6;
    private static readonly FontFamily TerminalFont = new("Consolas");
    private static readonly int[] DarkColors =
    [
        0x111b2a, 0xee778a, 0x9ae8b0, 0xf6d087, 0x79b5fc, 0xc9a9f6, 0x70dfeb, 0xd4e7f7,
        0x7c91a9, 0xff95a5, 0xb9f6c7, 0xffe0a9, 0xa8ceff, 0xe0c5ff, 0xa5f2f7, 0xf8fbff
    ];
    private static readonly int[] LightColors =
    [
        0x172336, 0x9c2335, 0x146039, 0x745200, 0x154ba0, 0x702994, 0x006077, 0x4a617d,
        0x526580, 0xb82e48, 0x246d2d, 0x7a4d00, 0x245eb2, 0x803fa0, 0x166a79, 0x26364d
    ];
    private static readonly int[] WindowsDarkColors =
    [
        0x0c0c0c, 0xc50f1f, 0x13a10e, 0xc19c00, 0x3b78ff, 0x881798, 0x3a96dd, 0xcccccc,
        0x767676, 0xe74856, 0x16c60c, 0xf9f1a5, 0x3b78ff, 0xb4009e, 0x61d6d6, 0xf2f2f2
    ];
    private static readonly int[] ContrastColors =
    [
        0x000000, 0xff5d68, 0x62ff71, 0xffff62, 0x69afff, 0xff86ff, 0x75ffff, 0xffffff,
        0x929292, 0xff8690, 0xa0ffa8, 0xffffa2, 0x9dceff, 0xffb2ff, 0xb0ffff, 0xffffff
    ];

    private readonly record struct CellPosition(int Row, int Column);
    private readonly record struct ColumnRange(int Start, int End)
    {
        public bool Includes(int column) => column >= Start && column < End;
    }

    private sealed class PaintedRow
    {
        public readonly Canvas Layer = new() { IsHitTestVisible = false, UseLayoutRounding = true };
        public TerminalBuffer.Line? Line;
        public int LineVersion;
        public int StyleVersion;
        public ColumnRange Selection;
        public ColumnRange Match;
    }

    private readonly List<PaintedRow> paintedRows = [];
    private readonly Dictionary<int, SolidColorBrush> brushes = [];
    private readonly TextBlock measurement = new() { FontFamily = TerminalFont, IsTextScaleFactorEnabled = false };
    private readonly Border wallpaperLayer = new() { IsHitTestVisible = false };
    private readonly Border cursor = new() { IsHitTestVisible = false, Opacity = .65, Visibility = Visibility.Collapsed };
    private double rasterScale = 1;
    private double cellWidth = 8;
    private double cellHeight = 17;
    private int measuredFontSize;
    private bool fixedWidthFont = true;
    private FontFamily renderFont = TerminalFont;
    private WorkspaceAppearance workspaceVisuals = WorkspaceAppearance.Default;
    private int[]? ansiOverride;
    private int paintVersion;
    private int backgroundColor;
    private int foregroundColor;
    private int accentColor;
    private int selectionColor;
    private int matchColor;
    private bool reverseScreen;
    private CellPosition? selectionStart;
    private CellPosition? selectionEnd;
    private CellPosition? matchStart;
    private CellPosition? matchEnd;
    private int matchTextIndex;
    private string previousQuery = "";

    private void ApplyPalette()
    {
        (backgroundColor, foregroundColor, accentColor, selectionColor, matchColor) = appearance.Theme switch
        {
            TerminalTheme.Light => (0xf2f7fe, 0x12243a, 0x2056ad, 0xc6dbfc, 0xffe0a1),
            TerminalTheme.HighContrast => (0x000000, 0xffffff, 0x65f9ff, 0x25448f, 0x7d4800),
            TerminalTheme.WindowsDark => (0x0c0c0c, 0xcccccc, 0x60cdff, 0x234c70, 0x5e4a21),
            _ => (0x101a29, 0xd8eafb, 0x55dafa, 0x25496c, 0x674c32)
        };
        var highContrast = appearance.Theme == TerminalTheme.HighContrast;
        if (!highContrast)
        {
            backgroundColor = WorkspaceAppearance.ColorValue(workspaceVisuals.TerminalBackground) ?? backgroundColor;
            foregroundColor = WorkspaceAppearance.ColorValue(workspaceVisuals.TerminalForeground) ?? foregroundColor;
            accentColor = WorkspaceAppearance.ColorValue(workspaceVisuals.TerminalCursor) ?? accentColor;
            selectionColor = WorkspaceAppearance.ColorValue(workspaceVisuals.TerminalSelection) ?? selectionColor;
        }
        wallpaperLayer.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        paintVersion++;
        brushes.Clear();
        UpdateViewportBrush();
        cursor.Background = Brush(accentColor);
        input.Foreground = Brush(foregroundColor);
        input.Background = Brush(backgroundColor);
    }

    private void UpdateViewportBrush()
    {
        viewport.Background = Brush(reverseScreen ? foregroundColor : backgroundColor);
    }

    private void MeasureCells()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        if (measuredFontSize == appearance.FontSize && Math.Abs(rasterScale - scale) < .0001)
            return;
        rasterScale = scale > 0 ? scale : 1;
        measuredFontSize = appearance.FontSize;
        const int sampleColumns = 40;
        measurement.FontSize = measuredFontSize;
        measurement.Text = new string('M', sampleColumns);
        measurement.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        cellWidth = Math.Max(1, measurement.DesiredSize.Width / sampleColumns);
        cellHeight = Math.Max(1, Math.Ceiling(measurement.DesiredSize.Height * rasterScale) / rasterScale);
        measurement.Text = new string('i', sampleColumns);
        measurement.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        fixedWidthFont = Math.Abs(cellWidth - measurement.DesiredSize.Width / sampleColumns) < .2;
        input.FontSize = measuredFontSize;
        paintVersion++;
    }

    private double Pixel(double value) => Math.Round(value * rasterScale) / rasterScale;

    private SolidColorBrush Brush(int rgb)
    {
        if (brushes.TryGetValue(rgb, out var cached))
            return cached;
        var brush = new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        if (brushes.Count < 512)
            brushes.Add(rgb, brush);
        return brush;
    }

    private int Palette(int index)
    {
        if ((index & 0x1000000) != 0) return index & 0xffffff;
        if (index < 16)
        {
            var colors = appearance.Theme switch
            {
                TerminalTheme.Light => ansiOverride ?? LightColors,
                TerminalTheme.HighContrast => ContrastColors,
                TerminalTheme.WindowsDark => ansiOverride ?? WindowsDarkColors,
                _ => ansiOverride ?? DarkColors
            };
            return colors[index];
        }
        if (index >= 232)
        {
            var shade = 8 + (index - 232) * 10;
            return shade * 0x010101;
        }
        static int Channel(int value) => value == 0 ? 0 : 55 + value * 40;
        index -= 16;
        return Channel(index / 36) << 16 | Channel(index / 6 % 6) << 8 | Channel(index % 6);
    }

    private void Render()
    {
        if (closed || viewport.ActualWidth < 1 || viewport.ActualHeight < 1)
            return;
        if (terminal.ReverseScreen != reverseScreen)
        {
            reverseScreen = terminal.ReverseScreen;
            paintVersion++;
            UpdateViewportBrush();
        }

        while (paintedRows.Count < Rows)
        {
            var row = new PaintedRow();
            paintedRows.Add(row);
            viewport.Children.Insert(paintedRows.Count, row.Layer);
        }
        while (paintedRows.Count > Rows)
        {
            var row = paintedRows[^1];
            viewport.Children.Remove(row.Layer);
            paintedRows.RemoveAt(paintedRows.Count - 1);
        }

        for (var y = 0; y < Rows; y++)
        {
            var index = y + viewTop;
            var line = terminal.GetLine(index);
            var row = paintedRows[y];
            var selection = GetRange(selectionStart, selectionEnd, index);
            var match = GetRange(matchStart, matchEnd, index);
            Canvas.SetTop(row.Layer, Pixel(PaddingY + y * cellHeight));
            if (!ReferenceEquals(row.Line, line) || row.LineVersion != line.Version || row.StyleVersion != paintVersion ||
                row.Selection != selection || row.Match != match)
            {
                DrawLine(row.Layer, line, selection, match);
                row.Line = line;
                row.LineVersion = line.Version;
                row.StyleVersion = paintVersion;
                row.Selection = selection;
                row.Match = match;
            }
        }
        PlaceCursor();
    }

    private void DrawLine(Canvas layer, TerminalBuffer.Line line, ColumnRange selection, ColumnRange match)
    {
        layer.Children.Clear();
        var limit = Math.Min(line.CellCount, Columns);
        var visibleColumns = Math.Min(Columns, Math.Max(limit, Math.Max(selection.End, match.End)));
        var text = new StringBuilder(visibleColumns);
        for (var x = 0; x < visibleColumns;)
        {
            var cell = x < limit ? line.GetCell(x) : default;
            if (cell.Glyph < 0)
            {
                x++;
                continue;
            }
            var start = x;
            var selected = selection.Includes(x) || (selection.Includes(x + 1) && x + 1 < limit && line.GetCell(x + 1).Glyph < 0);
            var matched = match.Includes(x) || (match.Includes(x + 1) && x + 1 < limit && line.GetCell(x + 1).Glyph < 0);
            var canGroup = fixedWidthFont && cell.Glyph <= 0x7e && cell.Tail is null;
            text.Clear();
            do
            {
                TerminalBuffer.AppendCell(text, cell);
                x++;
                if (x < limit && line.GetCell(x).Glyph < 0)
                {
                    x++;
                    break;
                }
                if (!canGroup || x >= visibleColumns || selection.Includes(x) != selected || match.Includes(x) != matched)
                    break;

                var next = x < limit ? line.GetCell(x) : default;
                if (next.StyleId != cell.StyleId || next.Glyph is < 0 or > 0x7e || next.Tail is not null)
                    break;
                cell = next;
            } while (true);

            var style = terminal.GetStyle(cell.StyleId);
            var flags = style.Flags;
            var defaultBack = reverseScreen ? foregroundColor : backgroundColor;
            var background = style.Background < 0 ? defaultBack : Palette(style.Background);
            var foreground = style.Foreground < 0 ? (reverseScreen ? backgroundColor : foregroundColor) : Palette(style.Foreground);
            if ((flags & TerminalBuffer.Decoration.Reverse) != 0)
                (foreground, background) = (background, foreground);
            if (matched) background = matchColor;
            if (selected) background = selectionColor;
            var left = Pixel(PaddingX + start * cellWidth);

            if (background != defaultBack)
            {
                var stripe = new Border
                {
                    Background = Brush(background),
                    Width = Pixel(PaddingX + x * cellWidth) - left,
                    Height = cellHeight
                };
                Canvas.SetLeft(stripe, left);
                layer.Children.Add(stripe);
            }

            if ((flags & TerminalBuffer.Decoration.Hidden) != 0)
                continue;
            var glyphs = text.ToString().TrimEnd(' ');
            var glyphWidth = Pixel(PaddingX + x * cellWidth) - left;
            if ((flags & TerminalBuffer.Decoration.Underline) != 0)
                DrawRule(layer, left, cellHeight - 2 / rasterScale, glyphWidth, foreground);
            if ((flags & TerminalBuffer.Decoration.Strike) != 0)
                DrawRule(layer, left, Pixel(cellHeight / 2), glyphWidth, foreground);
            if (glyphs.Length == 0)
                continue;

            var span = new TextBlock
            {
                Text = glyphs,
                FontFamily = renderFont,
                MaxWidth = fixedWidthFont ? double.PositiveInfinity : glyphWidth,
                FontSize = appearance.FontSize,
                Foreground = Brush(foreground),
                FontWeight = (flags & TerminalBuffer.Decoration.Bold) != 0 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = (flags & TerminalBuffer.Decoration.Italic) != 0 ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                Opacity = (flags & TerminalBuffer.Decoration.Faint) != 0 ? .6 : 1,
                TextWrapping = TextWrapping.NoWrap,
                IsTextScaleFactorEnabled = false,
                UseLayoutRounding = true
            };
            Canvas.SetLeft(span, left);
            layer.Children.Add(span);
        }
    }

    private void DrawRule(Canvas layer, double left, double top, double width, int rgb)
    {
        var rule = new Border { Background = Brush(rgb), Width = width, Height = 1 / rasterScale };
        Canvas.SetLeft(rule, left);
        Canvas.SetTop(rule, top);
        layer.Children.Add(rule);
    }

    private void PlaceCursor()
    {
        var cursorRow = terminal.HistoryCount + terminal.CursorRow - viewTop;
        var cursorColumn = terminal.CursorColumn;
        var focused = input.FocusState != FocusState.Unfocused;
        var hasCursor = cursorRow >= 0 && cursorRow < Rows && viewTop == terminal.HistoryCount;
        var left = Pixel(PaddingX + cursorColumn * cellWidth);
        var top = Pixel(PaddingY + cursorRow * cellHeight);
        if (hasCursor)
        {
            Canvas.SetLeft(input, left);
            Canvas.SetTop(input, top);
            input.Width = composing ? Math.Max(cellWidth, viewport.ActualWidth - left) : cellWidth;
            input.Height = cellHeight;
        }

        var useBlink = terminal.CursorShape is not (2 or 4 or 6) && appearance.CursorBlink;
        cursor.Visibility = hasCursor && focused && terminal.CursorVisible && !composing && (!useBlink || blinkOn)
            ? Visibility.Visible : Visibility.Collapsed;
        if (cursor.Visibility != Visibility.Visible)
            return;

        var underline = terminal.CursorShape is 3 or 4;
        var bar = terminal.CursorShape is 5 or 6;
        var stroke = Math.Max(1, Math.Round(rasterScale)) / rasterScale;
        cursor.Opacity = underline || bar ? 1 : .6;
        cursor.Width = bar ? stroke : Math.Max(stroke, Pixel(PaddingX + (cursorColumn + 1) * cellWidth) - left);
        cursor.Height = underline ? stroke : cellHeight;
        Canvas.SetLeft(cursor, left);
        Canvas.SetTop(cursor, underline ? top + cellHeight - stroke : top);
    }

    private ColumnRange GetRange(CellPosition? start, CellPosition? end, int row)
    {
        if (start is not { } a || end is not { } b)
            return default;
        if (Compare(a, b) > 0)
            (a, b) = (b, a);
        if (row < a.Row || row > b.Row)
            return default;
        return new ColumnRange(row == a.Row ? a.Column : 0, row == b.Row ? b.Column : Columns);
    }

    private static int Compare(CellPosition a, CellPosition b) =>
        a.Row == b.Row ? a.Column.CompareTo(b.Column) : a.Row.CompareTo(b.Row);

    private void Find(string term, bool backwards)
    {
        if (string.IsNullOrEmpty(term))
        {
            ClearSearch();
            return;
        }
        term = term.Length > 512 ? term[..512] : term;

        var total = terminal.HistoryCount + Rows;
        var previous = term == previousQuery ? matchStart : null;
        var initial = previous?.Row ?? (backwards ? Math.Min(viewTop + Rows - 1, total - 1) : viewTop);
        var direction = backwards ? -1 : 1;
        for (var offset = 0; offset <= total; offset++)
        {
            var row = (initial + direction * offset + total) % total;
            var line = terminal.GetLine(row);
            var text = line.ReadText();
            if (text.Length == 0)
                continue;
            var index = -1;
            if (backwards)
            {
                var end = offset == 0 && previous is not null ? Math.Min(matchTextIndex - 1, text.Length - 1) : text.Length - 1;
                if (end >= 0)
                    index = text.LastIndexOf(term, end, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var start = offset == 0 && previous is not null ? matchTextIndex + 1 : 0;
                if (start < text.Length)
                    index = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            }
            if (index < 0)
                continue;
            matchStart = new CellPosition(row, line.ColumnAt(index));
            matchEnd = new CellPosition(row, line.ColumnAt(index + term.Length));
            matchTextIndex = index;
            previousQuery = term;
            searchStatus.Text = L10n.Get("S47ED180BE4D6");
            if (row < viewTop || row >= viewTop + Rows)
                ScrollTo(row);
            else
                RequestRedraw();
            return;
        }
        ClearSearch();
        searchStatus.Text = L10n.Get("S7A54F3FC4716");
    }
}
