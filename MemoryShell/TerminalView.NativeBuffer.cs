using System.Globalization;
using System.Text;

namespace MemoryShell;

internal sealed class TerminalBuffer
{
    private const int HistoryCellBudget = 1_000_000;
    [Flags]
    internal enum Decoration { None = 0, Bold = 1, Faint = 2, Italic = 4, Underline = 8, Reverse = 16, Hidden = 32, Strike = 64 }
    internal readonly record struct Style(int Foreground = -1, int Background = -1, Decoration Flags = Decoration.None);
    internal readonly record struct Cell(int Glyph, int StyleId, string? Tail = null);

    internal sealed class Line(int width)
    {
        private const int GlyphBits = 21;
        private const uint GlyphMask = (1u << GlyphBits) - 1;
        private uint[]? archivedCells;

        public Cell[] Cells = new Cell[width];
        public int CellCount => archivedCells?.Length ?? Cells.Length;
        public int Version { get; private set; }
        public bool Wrapped { get; set; }

        public void Changed() => Version++;

        public Cell GetCell(int column)
        {
            if (archivedCells is not { } packed)
                return Cells[column];
            var value = packed[column];
            return new Cell((int)(value & GlyphMask) - 1, (int)(value >> GlyphBits));
        }

        public void Archive()
        {
            var length = Array.FindLastIndex(Cells, cell => cell != default) + 1;
            var content = Cells.AsSpan(0, length);
            foreach (var cell in content)
            {
                if (cell.Tail is not null || cell.StyleId >= 1 << (32 - GlyphBits))
                {
                    Array.Resize(ref Cells, length);
                    return;
                }
            }

            // History rows are immutable. A Unicode code point and the usual style IDs fit in one word.
            if (length > 0)
            {
                var packed = new uint[length];
                for (var x = 0; x < length; x++)
                {
                    var cell = content[x];
                    packed[x] = ((uint)cell.StyleId << GlyphBits) | (uint)(cell.Glyph + 1);
                }
                archivedCells = packed;
            }
            Cells = [];
        }

        public string ReadText(int start = 0, int end = int.MaxValue)
        {
            var first = Math.Max(0, start);
            var limit = Math.Min(end, CellCount);
            if (first >= limit)
                return "";
            var text = new StringBuilder(limit - first);
            for (var i = first; i < limit; i++)
            {
                var cell = GetCell(i);
                if (cell.Glyph < 0)
                    continue;
                AppendCell(text, cell);
            }
            return text.ToString().TrimEnd(' ');
        }

        public int ColumnAt(int index)
        {
            for (var x = 0; x < CellCount; x++)
            {
                var cell = GetCell(x);
                if (cell.Glyph < 0)
                    continue;
                if (index <= 0)
                    return x;
                index -= (cell.Glyph > 0xffff ? 2 : 1) + (cell.Tail?.Length ?? 0);
            }
            return CellCount;
        }
    }

    private enum State { Ground, Escape, Csi, Osc, OscEnd, Ignore, IgnoreEnd, Charset }
    private sealed class Screen(int width, int height)
    {
        public Line[] Lines = CreateLines(width, height);
        public int X, Y, SavedX, SavedY, Top;
        public int Bottom = height - 1;
        public bool WrapPending;
        public Style SavedStyle = DefaultStyle;
    }

    private static readonly Style DefaultStyle = new(-1, -1);
    private readonly List<Line?> history = [];
    private readonly List<Style> styles = [DefaultStyle];
    private readonly Dictionary<Style, int> styleIds = new() { [DefaultStyle] = 0 };
    private readonly StringBuilder control = new();
    private Screen primary = new(80, 24);
    private Screen? alternate;
    private Screen active;
    private State parser;
    private Style currentStyle = DefaultStyle;
    private int styleId;
    private int historyStart;
    private int historyCells;
    private int historyLimit = 10000;
    private bool useLineGraphics;
    private bool charsetIsG1;
    private bool graphicsG0;
    private bool graphicsG1;
    private bool autoWrap = true;
    private bool insertMode;
    private bool originMode;
    private bool reverseScreen;
    private bool focusEvents;
    private bool newlineMode;
    private bool[] tabStops = DefaultTabs(80);

    public TerminalBuffer() => active = primary;
    public event Action<string>? DirectoryReported;
    public event Action<string>? Reply;
    public int Columns { get; private set; } = 80;
    public int Rows { get; private set; } = 24;
    public bool Alternate => ReferenceEquals(active, alternate);
    public int HistoryCount => Alternate ? 0 : history.Count - historyStart;
    public long DroppedHistory { get; private set; }
    public int CursorColumn => active.X;
    public int CursorRow => active.Y;
    public bool CursorVisible { get; private set; } = true;
    public int CursorShape { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool ApplicationCursor { get; private set; }
    public bool ApplicationKeypad { get; private set; }
    public int MouseTracking { get; private set; }
    public bool SgrMouse { get; private set; }
    public bool ReverseScreen => reverseScreen;

    public Line GetLine(int index) => index < HistoryCount ? history[historyStart + index]! : active.Lines[index - HistoryCount];
    internal void KeepHistoryFrom(TerminalBuffer previous)
    {
        for (var row = previous.historyStart; row < previous.history.Count; row++) Copy(previous.history[row]!);
        CopyScreen(previous.primary);
        if (previous.Alternate) CopyScreen(previous.active);

        void CopyScreen(Screen screen)
        {
            var count = screen.Lines.Length;
            while (count > 0 && screen.Lines[count - 1].ReadText().Length == 0) count--;
            for (var row = 0; row < count; row++) Copy(screen.Lines[row]);
        }
        void Copy(Line source)
        {
            var copy = new Line(source.CellCount) { Wrapped = source.Wrapped };
            for (var col = 0; col < source.CellCount; col++)
            {
                var cell = source.GetCell(col);
                var style = previous.GetStyle(cell.StyleId);
                if (!styleIds.TryGetValue(style, out var id)) { id = styles.Count; styles.Add(style); styleIds.Add(style, id); }
                copy.Cells[col] = cell with { StyleId = id };
            }
            AddHistory(copy);
        }
    }
    public Style GetStyle(int id) => styles[id];
    public void ReportFocus(bool focused)
    {
        if (focusEvents)
            Reply?.Invoke(focused ? "\x1b[I" : "\x1b[O");
    }

    public static void AppendCell(StringBuilder text, Cell cell)
    {
        var rune = new Rune(cell.Glyph > 0 ? cell.Glyph : ' ');
        Span<char> utf16 = stackalloc char[2];
        text.Append(utf16[..rune.EncodeToUtf16(utf16)]);
        text.Append(cell.Tail);
    }

    public void SetHistoryLimit(int value)
    {
        historyLimit = value;
        TrimHistory();
    }

    public void Resize(int columns, int rows)
    {
        if (Columns == columns && Rows == rows)
            return;

        ResizeScreen(primary, columns, rows, keepHistory: true);
        if (alternate is not null)
            ResizeScreen(alternate, columns, rows, keepHistory: false);
        var previousTabs = tabStops;
        tabStops = DefaultTabs(columns);
        Array.Copy(previousTabs, tabStops, Math.Min(previousTabs.Length, columns));
        Columns = columns;
        Rows = rows;
    }

    private void ResizeScreen(Screen screen, int columns, int rows, bool keepHistory)
    {
        var removed = Math.Max(0, screen.Y - rows + 1);
        if (keepHistory)
        {
            for (var y = 0; y < removed; y++)
                AddHistory(screen.Lines[y]);
        }

        var lines = new Line[rows];
        for (var y = 0; y < rows; y++)
        {
            if (y + removed >= screen.Lines.Length)
            {
                lines[y] = new Line(columns);
                continue;
            }
            var line = screen.Lines[y + removed];
            if (line.Cells.Length != columns)
            {
                Array.Resize(ref line.Cells, columns);
                if (columns > 0 && line.Cells[^1].Glyph > 0 && GlyphWidth(new Rune(line.Cells[^1].Glyph)) == 2)
                    line.Cells[^1] = default;
                line.Changed();
            }
            lines[y] = line;
        }

        screen.Lines = lines;
        screen.X = Math.Clamp(screen.X, 0, columns - 1);
        screen.Y = Math.Clamp(screen.Y - removed, 0, rows - 1);
        screen.SavedX = Math.Clamp(screen.SavedX, 0, columns - 1);
        screen.SavedY = Math.Clamp(screen.SavedY - removed, 0, rows - 1);
        screen.Top = 0;
        screen.Bottom = rows - 1;
        screen.WrapPending = false;
    }

    public void Feed(ReadOnlySpan<char> input)
    {
        foreach (var rune in input.EnumerateRunes())
        {
            var code = rune.Value;
            if (code is 0x18 or 0x1a)
            {
                parser = State.Ground;
                control.Clear();
                continue;
            }

            switch (parser)
            {
                case State.Ground:
                    if (code == 0x1b) parser = State.Escape;
                    else if (code == 0x9b) Begin(State.Csi);
                    else if (code == 0x9d) Begin(State.Osc);
                    else if (code is 0x90 or 0x98 or 0x9e or 0x9f) Begin(State.Ignore);
                    else if (code < 0x20 || code is >= 0x7f and <= 0x9f) Control(code);
                    else Print(rune);
                    break;
                case State.Escape:
                    Escape(code);
                    break;
                case State.Csi:
                    if (code is >= 0x40 and <= 0x7e)
                    {
                        var arguments = control.ToString();
                        parser = State.Ground;
                        control.Clear();
                        DispatchCsi((char)code, arguments);
                    }
                    else if (code == 0x1b) Begin(State.Escape);
                    else if (code < 0x20) Control(code);
                    else if (code <= 0x3f && control.Length < 128) control.Append((char)code);
                    else Begin(State.Ground);
                    break;
                case State.Osc:
                    if (code is 0x07 or 0x9c) CompleteOsc();
                    else if (code == 0x1b) parser = State.OscEnd;
                    else if (code >= 0x20 && control.Length < 8192) control.Append(rune.ToString());
                    break;
                case State.OscEnd:
                    if (code == '\\') CompleteOsc();
                    else Begin(State.Ground);
                    break;
                case State.Ignore:
                    if (code == 0x1b) parser = State.IgnoreEnd;
                    else if (code == 0x9c) Begin(State.Ground);
                    break;
                case State.IgnoreEnd:
                    parser = code == '\\' ? State.Ground : State.Ignore;
                    break;
                case State.Charset:
                    if (charsetIsG1) graphicsG1 = code == '0';
                    else graphicsG0 = code == '0';
                    parser = State.Ground;
                    break;
            }
        }
    }

    private void Begin(State state)
    {
        control.Clear();
        parser = state;
    }

    private void Control(int code)
    {
        switch (code)
        {
            case '\b': Move(active.X - 1, active.Y); break;
            case '\t': Tab(1, 1); break;
            case '\n':
            case '\v':
            case '\f':
            case 0x84:
                if (newlineMode) active.X = 0;
                Index();
                break;
            case '\r': Move(0, active.Y); break;
            case 0x0e: useLineGraphics = true; break;
            case 0x0f: useLineGraphics = false; break;
            case 0x85: active.X = 0; Index(); break;
            case 0x88: tabStops[active.X] = true; break;
            case 0x8d: ReverseIndex(); break;
        }
    }

    private void Escape(int code)
    {
        parser = State.Ground;
        switch (code)
        {
            case 0x1b: Begin(State.Escape); break;
            case '[': Begin(State.Csi); break;
            case ']': Begin(State.Osc); break;
            case 'P':
            case 'X':
            case '^':
            case '_': Begin(State.Ignore); break;
            case '(':
            case ')':
                charsetIsG1 = code == ')';
                parser = State.Charset;
                break;
            case '%':
            case '#':
            case '*':
            case '+': parser = State.Charset; break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': Index(); break;
            case 'E': active.X = 0; Index(); break;
            case 'M': ReverseIndex(); break;
            case 'H': tabStops[active.X] = true; break;
            case '=': ApplicationKeypad = true; break;
            case '>': ApplicationKeypad = false; break;
            case 'Z': Reply?.Invoke("\x1b[?1;2c"); break;
            case 'c': Reset(); break;
        }
    }

    private void CompleteOsc()
    {
        var text = control.ToString();
        Begin(State.Ground);
        if (!text.StartsWith("7;file://", StringComparison.Ordinal))
            return;
        var separator = text.IndexOf('/', 9);
        if (separator < 0)
            return;
        var escaped = text[separator..];
        if (escaped.Contains('?') || escaped.Contains('#'))
            return;
        var path = Uri.UnescapeDataString(escaped);
        if (path.Length <= 4096 && path[0] == '/' && !path.Any(char.IsControl))
            DirectoryReported?.Invoke(path);
    }

    private void DispatchCsi(char action, string args)
    {
        var prefix = args.Length > 0 && args[0] is '?' or '>' or '!' or '<' or '=' ? args[0] : '\0';
        if (prefix != '\0')
            args = args[1..];
        var intermediates = args.TrimEnd(' ', '"', '$', '\'').Length != args.Length;
        args = args.TrimEnd(' ', '"', '$', '\'');
        var values = args.Split(';');
        int Read(int index, int fallback = 1) => index < values.Length &&
            int.TryParse(values[index], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? Math.Min(n, 100000) : fallback;
        int Count(int index = 0) => Math.Max(1, Read(index));

        if (action is 'h' or 'l')
        {
            for (var i = 0; i < values.Length; i++)
                SetMode(prefix, Read(i, 0), action == 'h');
            return;
        }

        if (prefix != '\0' && action is not ('c' or 'n' or 'J' or 'K'))
            return;

        var minRow = originMode ? active.Top : 0;
        var maxRow = originMode ? active.Bottom : Rows - 1;
        switch (action)
        {
            case 'A': Move(active.X, Math.Max(active.Y - Count(), active.Y >= active.Top ? active.Top : 0)); break;
            case 'B':
            case 'e': Move(active.X, Math.Min(active.Y + Count(), active.Y <= active.Bottom ? active.Bottom : Rows - 1)); break;
            case 'C':
            case 'a': Move(active.X + Count(), active.Y); break;
            case 'D': Move(active.X - Count(), active.Y); break;
            case 'E': Move(0, Math.Min(maxRow, active.Y + Count())); break;
            case 'F': Move(0, Math.Max(minRow, active.Y - Count())); break;
            case 'G':
            case '|': Move(Count() - 1, active.Y); break;
            case 'd': Move(active.X, Math.Clamp(minRow + Count() - 1, minRow, maxRow)); break;
            case 'H':
            case 'f': Move(Count(1) - 1, Math.Clamp(minRow + Count() - 1, minRow, maxRow)); break;
            case 'I': Tab(Count(), 1); break;
            case 'Z': Tab(Count(), -1); break;
            case 'g':
                if (Read(0, 0) == 3) Array.Clear(tabStops);
                else tabStops[active.X] = false;
                break;
            case 'J': EraseDisplay(Read(0, 0)); break;
            case 'K':
                var mode = Read(0, 0);
                if (mode <= 2) EraseLine(active.Y, mode == 0 ? active.X : 0, mode == 1 ? active.X + 1 : Columns);
                active.WrapPending = false;
                break;
            case 'L':
                if (active.Y >= active.Top && active.Y <= active.Bottom) ScrollDown(active.Y, active.Bottom, Count());
                active.WrapPending = false;
                break;
            case 'M':
                if (active.Y >= active.Top && active.Y <= active.Bottom) ScrollUp(active.Y, active.Bottom, Count(), keepHistory: false);
                active.WrapPending = false;
                break;
            case 'S': ScrollUp(active.Top, active.Bottom, Count(), keepHistory: true); break;
            case 'T': ScrollDown(active.Top, active.Bottom, Count()); break;
            case '@': ShiftLine(Count()); break;
            case 'P': ShiftLine(-Count()); break;
            case 'X': EraseLine(active.Y, active.X, Math.Min(Columns, active.X + Count())); break;
            case 'm': ApplyStyle(values); break;
            case 'q' when intermediates: CursorShape = Math.Clamp(Read(0, 0), 0, 6); break;
            case 'r':
                var top = Count() - 1;
                var bottom = Read(1, Rows) - 1;
                if (top < bottom && bottom < Rows)
                {
                    active.Top = top;
                    active.Bottom = bottom;
                    Move(0, originMode ? top : 0);
                }
                break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'n':
                var query = Read(0, 0);
                if (query == 5 && prefix == '\0') Reply?.Invoke("\x1b[0n");
                if (query == 6) Reply?.Invoke($"\x1b[{(prefix == '?' ? "?" : "")}{active.Y + 1 - minRow};{active.X + 1}R");
                break;
            case 'c':
                Reply?.Invoke(prefix == '>' ? "\x1b[>0;115;0c" : "\x1b[?1;2c");
                break;
            case 't' when Read(0, 0) == 18:
                Reply?.Invoke($"\x1b[8;{Rows};{Columns}t");
                break;
        }
    }

    private void SetMode(char prefix, int mode, bool enabled)
    {
        if (prefix != '?')
        {
            if (mode == 4) insertMode = enabled;
            else if (mode == 20) newlineMode = enabled;
            return;
        }

        switch (mode)
        {
            case 1: ApplicationCursor = enabled; break;
            case 5: reverseScreen = enabled; break;
            case 6: originMode = enabled; Move(0, enabled ? active.Top : 0); break;
            case 7: autoWrap = enabled; break;
            case 25: CursorVisible = enabled; break;
            case 9:
            case 1000:
            case 1002:
            case 1003: MouseTracking = enabled ? mode : 0; break;
            case 1004: focusEvents = enabled; break;
            case 1006: SgrMouse = enabled; break;
            case 47:
            case 1047:
            case 1049:
                if (enabled && !Alternate)
                {
                    if (mode == 1049) SaveCursor();
                    if (mode != 47 || alternate is null) alternate = new Screen(Columns, Rows);
                    active = alternate;
                }
                else if (!enabled && Alternate)
                {
                    active = primary;
                    if (mode == 1049) RestoreCursor();
                }
                break;
            case 1048:
                if (enabled) SaveCursor();
                else RestoreCursor();
                break;
            case 2004: BracketedPaste = enabled; break;
        }
    }

    private void ApplyStyle(string[] parts)
    {
        static int Number(string value, int fallback = 0) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ? result : fallback;
        for (var i = 0; i < parts.Length; i++)
        {
            var segment = parts[i].Split(':');
            var code = Number(segment[0]);
            if (code is 38 or 48 or 58)
            {
                int ReadPart(int offset) => offset < parts.Length ? Number(parts[offset], -1) : -1;
                var kind = segment.Length > 1 ? Number(segment[1], -1) : ReadPart(++i);
                var color = -1;
                if (kind == 5)
                {
                    var index = segment.Length >= 3 ? Number(segment[2], -1) : ReadPart(++i);
                    if (index is >= 0 and <= 255) color = index;
                }
                else if (kind == 2)
                {
                    var red = segment.Length >= 5 ? Number(segment[^3], -1) : ReadPart(++i);
                    var green = segment.Length >= 5 ? Number(segment[^2], -1) : ReadPart(++i);
                    var blue = segment.Length >= 5 ? Number(segment[^1], -1) : ReadPart(++i);
                    if (red is >= 0 and <= 255 && green is >= 0 and <= 255 && blue is >= 0 and <= 255)
                        color = 0x1000000 | red << 16 | green << 8 | blue;
                }
                if (color >= 0)
                {
                    if (code == 38) currentStyle = currentStyle with { Foreground = color };
                    if (code == 48) currentStyle = currentStyle with { Background = color };
                }
                continue;
            }

            var flags = currentStyle.Flags;
            currentStyle = code switch
            {
                0 => DefaultStyle,
                1 => currentStyle with { Flags = flags | Decoration.Bold },
                2 => currentStyle with { Flags = flags | Decoration.Faint },
                3 => currentStyle with { Flags = flags | Decoration.Italic },
                4 or 21 => currentStyle with { Flags = flags | Decoration.Underline },
                7 => currentStyle with { Flags = flags | Decoration.Reverse },
                8 => currentStyle with { Flags = flags | Decoration.Hidden },
                9 => currentStyle with { Flags = flags | Decoration.Strike },
                22 => currentStyle with { Flags = flags & ~(Decoration.Bold | Decoration.Faint) },
                23 => currentStyle with { Flags = flags & ~Decoration.Italic },
                24 => currentStyle with { Flags = flags & ~Decoration.Underline },
                27 => currentStyle with { Flags = flags & ~Decoration.Reverse },
                28 => currentStyle with { Flags = flags & ~Decoration.Hidden },
                29 => currentStyle with { Flags = flags & ~Decoration.Strike },
                >= 30 and <= 37 => currentStyle with { Foreground = code - 30 },
                39 => currentStyle with { Foreground = -1 },
                >= 40 and <= 47 => currentStyle with { Background = code - 40 },
                49 => currentStyle with { Background = -1 },
                >= 90 and <= 97 => currentStyle with { Foreground = code - 82 },
                >= 100 and <= 107 => currentStyle with { Background = code - 92 },
                _ => currentStyle
            };
        }
        styleId = GetStyleId(currentStyle);
    }

    private int GetStyleId(Style style)
    {
        if (styleIds.TryGetValue(style, out var index))
            return index;
        if (styles.Count >= 8192)
            return 0;
        index = styles.Count;
        styles.Add(style);
        styleIds.Add(style, index);
        return index;
    }

    private Cell Blank()
    {
        // Erased cells retain only their effective background color.
        var background = currentStyle.Flags.HasFlag(Decoration.Reverse) ? currentStyle.Foreground : currentStyle.Background;
        return new Cell(0, background < 0 ? 0 : GetStyleId(new Style(-1, background)));
    }

    private void Move(int x, int y)
    {
        active.X = Math.Clamp(x, 0, Columns - 1);
        active.Y = Math.Clamp(y, 0, Rows - 1);
        active.WrapPending = false;
    }

    private void SaveCursor()
    {
        active.SavedX = active.X;
        active.SavedY = active.Y;
        active.SavedStyle = currentStyle;
    }

    private void RestoreCursor()
    {
        Move(active.SavedX, active.SavedY);
        currentStyle = active.SavedStyle;
        styleId = GetStyleId(currentStyle);
    }

    private void Tab(int count, int direction)
    {
        var x = active.X;
        while (count-- > 0 && (direction > 0 ? x < Columns - 1 : x > 0))
        {
            do { x += direction; }
            while (x > 0 && x < Columns - 1 && !tabStops[x]);
        }
        Move(x, active.Y);
    }

    private void Index()
    {
        if (active.Y == active.Bottom) ScrollUp(active.Top, active.Bottom, 1, keepHistory: true);
        else active.Y = Math.Min(Rows - 1, active.Y + 1);
        active.WrapPending = false;
    }

    private void ReverseIndex()
    {
        if (active.Y == active.Top) ScrollDown(active.Top, active.Bottom, 1);
        else active.Y = Math.Max(0, active.Y - 1);
        active.WrapPending = false;
    }

    private void ScrollUp(int top, int bottom, int count, bool keepHistory)
    {
        count = Math.Min(count, bottom - top + 1);
        if (keepHistory && !Alternate && top == 0 && bottom == Rows - 1)
        {
            for (var y = top; y < top + count; y++)
                AddHistory(active.Lines[y]);
        }
        Array.Copy(active.Lines, top + count, active.Lines, top, bottom - top + 1 - count);
        for (var y = bottom - count + 1; y <= bottom; y++)
            active.Lines[y] = BlankLine();
    }

    private void ScrollDown(int top, int bottom, int count)
    {
        count = Math.Min(count, bottom - top + 1);
        Array.Copy(active.Lines, top, active.Lines, top + count, bottom - top + 1 - count);
        for (var y = top; y < top + count; y++)
            active.Lines[y] = BlankLine();
    }

    private void AddHistory(Line line)
    {
        line.Archive();
        history.Add(line);
        historyCells += line.CellCount;
        TrimHistory();
    }

    private void TrimHistory()
    {
        while (history.Count - historyStart > historyLimit || historyCells > HistoryCellBudget)
        {
            historyCells -= history[historyStart]!.CellCount;
            history[historyStart++] = null;
            DroppedHistory++;
        }
        if (historyStart >= 256 && historyStart * 2 >= history.Count)
        {
            history.RemoveRange(0, historyStart);
            historyStart = 0;
        }
    }

    public void ClearHistory()
    {
        DroppedHistory += history.Count - historyStart;
        history.Clear();
        historyStart = 0;
        historyCells = 0;
    }

    private Line BlankLine()
    {
        var line = new Line(Columns);
        var blank = Blank();
        if (blank != default) Array.Fill(line.Cells, blank);
        return line;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(active.Y, active.X, Columns);
                for (var y = active.Y + 1; y < Rows; y++) active.Lines[y] = BlankLine();
                break;
            case 1:
                for (var y = 0; y < active.Y; y++) active.Lines[y] = BlankLine();
                EraseLine(active.Y, 0, active.X + 1);
                break;
            case 2:
                for (var y = 0; y < Rows; y++) active.Lines[y] = BlankLine();
                break;
            case 3:
                if (!Alternate) ClearHistory();
                break;
        }
        active.WrapPending = false;
    }

    private void EraseLine(int row, int start, int end)
    {
        var line = active.Lines[row];
        if (start > 0 && line.Cells[start].Glyph < 0) start--;
        if (end < Columns && line.Cells[end].Glyph < 0) end++;
        Array.Fill(line.Cells, Blank(), start, end - start);
        line.Wrapped = false;
        line.Changed();
    }

    private void ShiftLine(int count)
    {
        var line = active.Lines[active.Y];
        var x = active.X;
        var available = Columns - x;
        count = Math.Clamp(count, -available, available);
        if (line.Cells[x].Glyph < 0) ClearGlyph(line, x);
        if (count > 0)
        {
            Array.Copy(line.Cells, x, line.Cells, x + count, available - count);
            Array.Fill(line.Cells, Blank(), x, count);
        }
        else if (count < 0)
        {
            if (x - count < Columns && line.Cells[x - count].Glyph < 0) ClearGlyph(line, x - count);
            Array.Copy(line.Cells, x - count, line.Cells, x, available + count);
            Array.Fill(line.Cells, Blank(), Columns + count, -count);
        }
        if (line.Cells[^1].Glyph > 0 && GlyphWidth(new Rune(line.Cells[^1].Glyph)) == 2)
            line.Cells[^1] = Blank();
        line.Changed();
        active.WrapPending = false;
    }

    private void ClearGlyph(Line line, int x)
    {
        if (line.Cells[x].Glyph < 0 && x > 0)
            line.Cells[x - 1] = Blank();
        else if (x + 1 < Columns && line.Cells[x + 1].Glyph < 0)
            line.Cells[x + 1] = Blank();
        line.Cells[x] = Blank();
    }

    private void Print(Rune rune)
    {
        if ((useLineGraphics ? graphicsG1 : graphicsG0) && rune.Value is >= 0x60 and <= 0x7e)
            rune = new Rune("◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·"[rune.Value - 0x60]);
        var width = GlyphWidth(rune);
        var line = active.Lines[active.Y];
        var previousX = active.WrapPending ? active.X : active.X - 1;
        while (previousX > 0 && line.Cells[previousX].Glyph < 0) previousX--;
        if (previousX >= 0 && (width == 0 || line.Cells[previousX].Tail?.EndsWith('\u200d') == true))
        {
            var cell = line.Cells[previousX];
            if (cell.Glyph > 0 && (cell.Tail?.Length ?? 0) < 64)
            {
                line.Cells[previousX] = cell with { Tail = cell.Tail + rune };
                line.Changed();
            }
            return;
        }
        if (width == 0) return;

        if (active.WrapPending || (width == 2 && active.X == Columns - 1))
        {
            if (autoWrap)
            {
                line.Wrapped = true;
                active.X = 0;
                Index();
                line = active.Lines[active.Y];
            }
            else if (width == 2)
            {
                return;
            }
            active.WrapPending = false;
        }

        if (insertMode) ShiftLine(width);
        ClearGlyph(line, active.X);
        if (width == 2) ClearGlyph(line, active.X + 1);
        line.Cells[active.X] = new Cell(rune.Value, styleId);
        if (width == 2) line.Cells[active.X + 1] = new Cell(-1, styleId);
        line.Changed();
        active.WrapPending = active.X + width >= Columns && autoWrap;
        active.X = Math.Min(Columns - 1, active.X + width);
    }

    private void Reset()
    {
        primary = new Screen(Columns, Rows);
        alternate = null;
        active = primary;
        ClearHistory();
        currentStyle = DefaultStyle;
        styleId = 0;
        originMode = insertMode = useLineGraphics = graphicsG0 = graphicsG1 = false;
        reverseScreen = focusEvents = newlineMode = false;
        BracketedPaste = ApplicationCursor = ApplicationKeypad = SgrMouse = false;
        CursorVisible = autoWrap = true;
        CursorShape = MouseTracking = 0;
        tabStops = DefaultTabs(Columns);
    }

    private static Line[] CreateLines(int width, int height)
    {
        var lines = new Line[height];
        for (var y = 0; y < height; y++)
            lines[y] = new Line(width);
        return lines;
    }

    private static bool[] DefaultTabs(int width)
    {
        var tabs = new bool[width];
        for (var x = 8; x < width; x += 8)
            tabs[x] = true;
        return tabs;
    }

    private static int GlyphWidth(Rune rune)
    {
        var n = rune.Value;
        if (n == 0x200d || n is >= 0x1f3fb and <= 0x1f3ff ||
            Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return 0;
        return n is >= 0x1100 and <= 0x115f or 0x2329 or 0x232a or
            >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff or
            >= 0xfe10 and <= 0xfe19 or >= 0xfe30 and <= 0xfe6f or >= 0xff00 and <= 0xff60 or
            >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1faff or >= 0x20000 and <= 0x3fffd ? 2 : 1;
    }
}

