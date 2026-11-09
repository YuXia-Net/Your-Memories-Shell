using System.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace MemoryShell;

public sealed partial class TerminalView
{
    private readonly TextBox input = new()
    {
        Opacity = 0,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        FontFamily = TerminalFont,
        IsTabStop = true,
        IsTextPredictionEnabled = false,
        IsSpellCheckEnabled = false,
        AcceptsReturn = false,
        UseLayoutRounding = true
    };
    private bool composing;
    private bool drainingInput;
    private bool inputQueued;
    private uint? selectingPointer;
    private int? terminalButton;
    private CellPosition? lastMouseCell;
    private Flyout? selectionActions;

    private void InitializeInput()
    {
        AutomationProperties.SetName(input, L10n.Get("S9166592BFB40"));
        Canvas.SetZIndex(cursor, 1);
        Canvas.SetZIndex(input, 2);
        viewport.Children.Add(cursor);
        viewport.Children.Add(input);

        input.PreviewKeyDown += OnInputKeyDown;
        input.Loaded += (_, _) =>
        {
            if (focusPending)
                FocusTerminal();
        };
        input.TextChanged += (_, _) => QueueInputDrain();
        input.TextCompositionStarted += (_, _) =>
        {
            composing = true;
            input.Opacity = 1;
            UpdateBlinkTimer();
            PlaceCursor();
        };
        input.TextCompositionEnded += (_, _) =>
        {
            composing = false;
            input.Opacity = 0;
            UpdateBlinkTimer();
            PlaceCursor();
            QueueInputDrain();
        };
        input.GotFocus += (_, _) =>
        {
            UpdateBlinkTimer();
            terminal.ReportFocus(true);
            PlaceCursor();
        };
        input.LostFocus += (_, _) =>
        {
            UpdateBlinkTimer();
            terminal.ReportFocus(false);
            PlaceCursor();
        };

        viewport.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PointerPress), handledEventsToo: true);
        viewport.PointerMoved += PointerMove;
        viewport.PointerReleased += PointerRelease;
        viewport.PointerCaptureLost += (_, _) =>
        {
            selectingPointer = null;
            terminalButton = null;
            lastMouseCell = null;
        };
        viewport.PointerWheelChanged += ScrollWheel;
        viewport.DoubleTapped += (_, args) =>
        {
            if (terminal.MouseTracking != 0 && !Down(VirtualKey.Shift))
                return;
            var cell = PositionAt(args.GetPosition(viewport));
            var line = terminal.GetLine(cell.Row);
            var index = Math.Min(cell.Column, line.CellCount - 1);
            if (index < 0) return;
            var start = index;
            var end = index + 1;
            static bool IsWord(TerminalBuffer.Cell item) =>
                item.Glyph < 0 || item.Glyph > 0 && (item.Glyph > 127 || char.IsLetterOrDigit((char)item.Glyph) || item.Glyph == '_');
            if (IsWord(line.GetCell(index)))
            {
                while (start > 0 && IsWord(line.GetCell(start - 1))) start--;
                while (end < line.CellCount && IsWord(line.GetCell(end))) end++;
            }
            selectionStart = cell with { Column = start };
            selectionEnd = cell with { Column = end };
            RequestRedraw();
            ShowSelectionActions(args.GetPosition(viewport));
        };
        viewport.ContextFlyout = CreateTerminalMenu();
    }

    private MenuFlyout CreateTerminalMenu()
    {
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = L10n.Get("S60E432026426") };
        copy.Click += (_, _) => CopySelection();
        var paste = new MenuFlyoutItem { Text = L10n.Get("S335179267471") };
        paste.Click += async (_, _) => await PasteAsync();
        var find = new MenuFlyoutItem { Text = L10n.Get("S7E8D195DE11B") };
        find.Click += (_, _) => ShowSearch();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(find);
        menu.Items.Add(new MenuFlyoutSeparator());
        var clearHistory = new MenuFlyoutItem { Text = L10n.Get("S86CC361B5122") };
        clearHistory.Click += (_, _) =>
        {
            terminal.ClearHistory();
            selectionStart = selectionEnd = null;
            ClearSearch();
            ScrollTo(0);
            RequestRedraw();
        };
        menu.Items.Add(clearHistory);
        return menu;
    }

    private void QueueInputDrain()
    {
        if (composing || closed || drainingInput || inputQueued)
            return;
        inputQueued = DispatcherQueue.TryEnqueue(() =>
        {
            inputQueued = false;
            DrainTextInput();
        });
    }

    private void DrainTextInput()
    {
        if (composing || closed || drainingInput || input.Text.Length == 0)
            return;

        var text = input.Text;
        drainingInput = true;
        try { input.Text = ""; }
        finally { drainingInput = false; }
        SendInput(text);
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (composing || closed)
            return;

        var key = args.Key;
        var shift = Down(VirtualKey.Shift);
        var control = Down(VirtualKey.Control);
        var alt = Down(VirtualKey.Menu);
        if (control && alt && Down(VirtualKey.RightMenu))
            return; // Let the input method handle AltGr.

        if (control && shift)
        {
            switch (key)
            {
                case VirtualKey.C: args.Handled = true; CopySelection(); return;
                case VirtualKey.V: args.Handled = true; _ = PasteAsync(); return;
                case VirtualKey.F: args.Handled = true; ShowSearch(); return;
            }
        }
        if (shift && !control && !alt && key is VirtualKey.PageUp or VirtualKey.PageDown)
        {
            args.Handled = true;
            ScrollTo(viewTop + (key == VirtualKey.PageUp ? -Rows : Rows));
            return;
        }
        if (shift && !control && !alt && key == VirtualKey.Insert)
        {
            args.Handled = true;
            _ = PasteAsync();
            return;
        }

        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);
        string Arrow(char final) => modifier != 1 ? $"\x1b[1;{modifier}{final}" :
            terminal.ApplicationCursor ? $"\x1bO{final}" : $"\x1b[{final}";
        string Function(char final) => modifier == 1 ? $"\x1bO{final}" : $"\x1b[1;{modifier}{final}";
        string Tilde(int number) => modifier == 1 ? $"\x1b[{number}~" : $"\x1b[{number};{modifier}~";

        string? sequence = key switch
        {
            VirtualKey.Enter => "\r",
            VirtualKey.Back => control ? "\x08" : "\x7f",
            VirtualKey.Escape => "\x1b",
            VirtualKey.Tab => shift ? "\x1b[Z" : "\t",
            VirtualKey.Up => Arrow('A'),
            VirtualKey.Down => Arrow('B'),
            VirtualKey.Right => Arrow('C'),
            VirtualKey.Left => Arrow('D'),
            VirtualKey.Home => Arrow('H'),
            VirtualKey.End => Arrow('F'),
            VirtualKey.Insert => Tilde(2),
            VirtualKey.Delete => Tilde(3),
            VirtualKey.PageUp => Tilde(5),
            VirtualKey.PageDown => Tilde(6),
            VirtualKey.F1 => Function('P'),
            VirtualKey.F2 => Function('Q'),
            VirtualKey.F3 => Function('R'),
            VirtualKey.F4 => Function('S'),
            VirtualKey.F5 => Tilde(15),
            VirtualKey.F6 => Tilde(17),
            VirtualKey.F7 => Tilde(18),
            VirtualKey.F8 => Tilde(19),
            VirtualKey.F9 => Tilde(20),
            VirtualKey.F10 => Tilde(21),
            VirtualKey.F11 => Tilde(23),
            VirtualKey.F12 => Tilde(24),
            _ => null
        };
        var raw = (int)key;
        if (sequence is null && terminal.ApplicationKeypad && modifier == 1)
        {
            if (raw >= (int)VirtualKey.NumberPad0 && raw <= (int)VirtualKey.NumberPad9)
                sequence = $"\x1bO{(char)('p' + raw - (int)VirtualKey.NumberPad0)}";
            else
                sequence = key switch
                {
                    VirtualKey.Decimal => "\x1bOn",
                    VirtualKey.Add => "\x1bOk",
                    VirtualKey.Subtract => "\x1bOm",
                    VirtualKey.Multiply => "\x1bOj",
                    VirtualKey.Divide => "\x1bOo",
                    _ => null
                };
        }
        if (sequence is null && control)
        {
            if (raw >= (int)VirtualKey.A && raw <= (int)VirtualKey.Z)
                sequence = ((char)(raw - (int)VirtualKey.A + 1)).ToString();
            else
                sequence = raw switch
                {
                    (int)VirtualKey.Space or (int)VirtualKey.Number2 => "\0",
                    0xdb or (int)VirtualKey.Number3 => "\x1b",
                    0xdc or (int)VirtualKey.Number4 => "\x1c",
                    0xdd or (int)VirtualKey.Number5 => "\x1d",
                    (int)VirtualKey.Number6 => "\x1e",
                    0xbd or (int)VirtualKey.Number7 => "\x1f",
                    (int)VirtualKey.Number8 => "\x7f",
                    _ => null
                };
        }
        else if (sequence is null && alt && raw >= (int)VirtualKey.A && raw <= (int)VirtualKey.Z)
        {
            sequence = ((char)((shift ? 'A' : 'a') + raw - (int)VirtualKey.A)).ToString();
        }

        if (sequence is null)
            return;
        args.Handled = true;
        if (alt && !sequence.StartsWith('\x1b'))
            sequence = "\x1b" + sequence;
        DrainTextInput();
        SendInput(sequence);
    }

    private void SendInput(string value, bool scrollToEnd = true)
    {
        if (closed || value.Length == 0)
            return;
        selectionActions?.Hide();
        if (scrollToEnd && viewTop != terminal.HistoryCount)
            ScrollTo(terminal.HistoryCount);
        blinkOn = true;
        PlaceCursor();
        InputReceived?.Invoke(value);
    }

    internal async Task PasteAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return;
            var text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text) || closed)
                return;
            FocusTerminal();
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', '\r');
            if (terminal.BracketedPaste)
                text = "\x1b[200~" + text.Replace("\x1b", "", StringComparison.Ordinal) + "\x1b[201~";
            SendInput(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be owned by another process.
        }
    }

    internal void CopySelection()
    {
        if (selectionStart is not { } start || selectionEnd is not { } end || start == end)
            return;
        if (Compare(start, end) > 0)
            (start, end) = (end, start);

        var value = new StringBuilder();
        for (var row = start.Row; row <= end.Row; row++)
        {
            var line = terminal.GetLine(row);
            var range = GetRange(start, end, row);
            var first = range.Start;
            if (first > 0 && first < line.CellCount && line.GetCell(first).Glyph < 0)
                first--;
            value.Append(line.ReadText(first, range.End));
            if (row < end.Row && !line.Wrapped)
                value.AppendLine();
        }

        try
        {
            var data = new DataPackage();
            data.SetText(value.ToString());
            Clipboard.SetContent(data);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be owned by another process.
        }
    }

    private CellPosition PositionAt(Windows.Foundation.Point position, bool boundary = false)
    {
        var row = Math.Clamp((int)Math.Floor((position.Y - PaddingY) / cellHeight), 0, Rows - 1);
        var x = (position.X - PaddingX) / cellWidth;
        var column = Math.Clamp((int)(boundary ? Math.Round(x) : Math.Floor(x)), 0, boundary ? Columns : Columns - 1);
        return new CellPosition(row + viewTop, column);
    }

    private bool RemoteMouse => terminal.MouseTracking != 0 && viewTop == terminal.HistoryCount && !Down(VirtualKey.Shift);

    private void PointerPress(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(viewport);
        selectionActions?.Hide();
        FocusTerminal(FocusState.Pointer);
        if (RemoteMouse)
        {
            var button = point.Properties.IsLeftButtonPressed ? 0 : point.Properties.IsMiddleButtonPressed ? 1 :
                point.Properties.IsRightButtonPressed ? 2 : -1;
            if (button < 0)
                return;
            terminalButton = button;
            lastMouseCell = PositionAt(point.Position);
            SendMouse(button, lastMouseCell.Value, release: false);
            viewport.CapturePointer(args.Pointer);
            args.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        selectingPointer = args.Pointer.PointerId;
        selectionStart = selectionEnd = PositionAt(point.Position, boundary: true);
        viewport.CapturePointer(args.Pointer);
        RequestRedraw();
        args.Handled = true;
    }

    private void PointerMove(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(viewport);
        if (selectingPointer == args.Pointer.PointerId && selectionStart is not null)
        {
            if (point.Position.Y < 0) ScrollTo(viewTop - 1, schedule: false);
            else if (point.Position.Y > viewport.ActualHeight) ScrollTo(viewTop + 1, schedule: false);
            selectionEnd = PositionAt(point.Position, boundary: true);
            RequestRedraw();
            args.Handled = true;
            return;
        }

        if (RemoteMouse && (terminal.MouseTracking == 1003 || terminal.MouseTracking == 1002 && terminalButton is not null))
        {
            var cell = PositionAt(point.Position);
            if (lastMouseCell != cell)
            {
                lastMouseCell = cell;
                SendMouse(32 + (terminalButton ?? 3), cell, release: false);
            }
            args.Handled = true;
        }
    }

    private void PointerRelease(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(viewport);
        if (selectingPointer == args.Pointer.PointerId)
        {
            selectionEnd = PositionAt(point.Position, boundary: true);
            selectingPointer = null;
            viewport.ReleasePointerCapture(args.Pointer);
            if (input.FocusState == FocusState.Unfocused)
                FocusTerminal(FocusState.Pointer);
            RequestRedraw();
            args.Handled = true;
            ShowSelectionActions(point.Position);
        }
        else if (terminalButton is int button)
        {
            terminalButton = null;
            if (terminal.MouseTracking > 9)
                SendMouse(button, PositionAt(point.Position), release: true);
            viewport.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        }
    }

    private void ScrollWheel(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(viewport);
        if (point.Properties.IsHorizontalMouseWheel)
            return;
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
            return;
        selectionActions?.Hide();
        if (RemoteMouse)
            SendMouse(delta > 0 ? 64 : 65, PositionAt(point.Position), release: false);
        else
            ScrollTo(viewTop - Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 120) * 3);
        args.Handled = true;
    }

    private void SendMouse(int button, CellPosition cell, bool release)
    {
        var column = cell.Column + 1;
        var row = cell.Row - viewTop + 1;
        var modifiers = (Down(VirtualKey.Shift) ? 4 : 0) | (Down(VirtualKey.Menu) ? 8 : 0) | (Down(VirtualKey.Control) ? 16 : 0);
        if (terminal.SgrMouse)
            SendInput($"\x1b[<{button | modifiers};{column};{row}{(release ? 'm' : 'M')}", scrollToEnd: false);
        else if (column < 96 && row < 96)
            SendInput($"\x1b[M{(char)(32 + ((release ? 3 : button) | modifiers))}{(char)(32 + column)}{(char)(32 + row)}", scrollToEnd: false);
    }

    private void ClearSelection()
    {
        selectionActions?.Hide();
        selectionStart = selectionEnd = null;
        selectingPointer = null;
        RequestRedraw();
    }

    private void ShowSelectionActions(Windows.Foundation.Point position)
    {
        if (selectionStart is null || selectionEnd is null || selectionStart == selectionEnd || viewport.XamlRoot is null)
            return;
        selectionActions ??= BuildSelectionActions();
        selectionActions.ShowAt(viewport, new FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point(Math.Max(0, position.X), Math.Max(0, position.Y - 38)),
            ShowMode = FlyoutShowMode.Transient
        });
    }

    private Flyout BuildSelectionActions()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var flyout = new Flyout { Content = buttons };
        flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter));
        flyout.FlyoutPresenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3)));
        Add(Symbol.Copy, L10n.Get("S4D7EAACC6707"), () => { CopySelection(); FocusTerminal(); return Task.CompletedTask; });
        Add(Symbol.Paste, L10n.Get("SDFC1AAD74612"), PasteAsync);
        Add(Symbol.Find, L10n.Get("S7E8D195DE11B"), () => { ShowSearch(); return Task.CompletedTask; });
        return flyout;

        void Add(Symbol icon, string label, Func<Task> action)
        {
            var button = new Button
            {
                Content = new SymbolIcon(icon), Padding = new Thickness(2), MinWidth = 29, MinHeight = 27
            };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            button.Click += async (_, _) => { flyout.Hide(); await action(); };
            buttons.Children.Add(button);
        }
    }
}


