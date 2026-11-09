using System.Buffers;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MemoryShell;

public enum TerminalTheme
{
    MemoryDark,
    Light,
    HighContrast,
    WindowsDark
}

public sealed record TerminalAppearance(
    TerminalTheme Theme = TerminalTheme.MemoryDark,
    int FontSize = 14,
    bool CursorBlink = true,
    int Scrollback = 10000)
{
    public static TerminalAppearance Default { get; } = new();
}

public sealed partial class TerminalView : Grid, IDisposable
{
    private readonly TerminalBuffer terminal = new();
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly Canvas viewport = new() { UseLayoutRounding = true };
    private readonly ScrollBar scroll = new() { Orientation = Orientation.Vertical, IsTabStop = false, SmallChange = 3 };
    private readonly Grid searchBar = new() { Visibility = Visibility.Collapsed, ColumnSpacing = 5, Padding = new Thickness(10, 5, 10, 5) };
    private readonly TextBox searchBox = new() { PlaceholderText = L10n.Get("S7E8D195DE11B"), MaxLength = 512 };
    private readonly TextBlock searchStatus = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = .75 };
    private readonly DispatcherQueueTimer redrawTimer;
    private readonly DispatcherQueueTimer blinkTimer;
    private XamlRoot? subscribedRoot;
    private TerminalAppearance appearance = TerminalAppearance.Default;
    private bool redrawQueued;
    private bool changingScroll;
    private bool focusPending;
    private bool blinkOn = true;
    private volatile bool closed;
    private int viewTop;

    public event Action<string>? InputReceived;
    public event Action<int, int>? TerminalResized;
    public event Action<string>? WorkingDirectoryChanged;

    public int Columns => terminal.Columns;
    public int Rows => terminal.Rows;
    public string? WorkingDirectory { get; private set; }

    public TerminalView()
    {
        UseLayoutRounding = true;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        BuildSearchBar();
        Children.Add(searchBar);

        var body = new Grid { UseLayoutRounding = true };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(viewport);
        body.Children.Add(scroll);
        SetColumn(scroll, 1);
        SetRow(body, 1);
        Children.Add(body);

        scroll.ValueChanged += (_, args) =>
        {
            if (!changingScroll)
                ScrollTo((int)Math.Round(args.NewValue));
        };
        viewport.SizeChanged += (_, args) =>
        {
            wallpaperLayer.Width = args.NewSize.Width;
            wallpaperLayer.Height = args.NewSize.Height;
            viewport.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height)
            };
            ResizeViewport();
        };

        redrawTimer = DispatcherQueue.CreateTimer();
        redrawTimer.Interval = TimeSpan.FromMilliseconds(16);
        redrawTimer.IsRepeating = false;
        redrawTimer.Tick += (_, _) =>
        {
            redrawQueued = false;
            Render();
        };
        blinkTimer = DispatcherQueue.CreateTimer();
        blinkTimer.Interval = TimeSpan.FromMilliseconds(550);
        blinkTimer.Tick += (_, _) =>
        {
            blinkOn = !blinkOn;
            PlaceCursor();
        };

        terminal.DirectoryReported += path =>
        {
            if (path == WorkingDirectory) return;
            WorkingDirectory = path;
            WorkingDirectoryChanged?.Invoke(path);
        };
        terminal.Reply += data => InputReceived?.Invoke(data);

        viewport.Children.Add(wallpaperLayer);
        InitializeInput();
        ApplyPalette();
        Loaded += TerminalLoaded;
        Unloaded += TerminalUnloaded;
    }

    public void Write(string data)
    {
        if (closed || data.Length == 0)
            return;
        if (DispatcherQueue.HasThreadAccess)
            Feed(data.AsSpan());
        else
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!closed) Feed(data.AsSpan());
            });
    }

    public Task WriteTerminalBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (closed || bytes.IsEmpty)
            return Task.CompletedTask;
        if (DispatcherQueue.HasThreadAccess)
        {
            Decode(bytes.Span);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!closed) Decode(bytes.Span);
                completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
            return Task.FromException(new InvalidOperationException(L10n.Get("SF9628BC21001")));
        // SSH retains the backing bytes until this dispatcher operation completes.
        return completion.Task;
    }

    private void Decode(ReadOnlySpan<byte> bytes)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
        try
        {
            var count = decoder.GetChars(bytes, buffer, flush: false);
            if (count > 0)
                Feed(buffer.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void Feed(ReadOnlySpan<char> data)
    {
        var atBottom = viewTop == terminal.HistoryCount;
        var alternate = terminal.Alternate;
        var oldDropped = terminal.DroppedHistory;
        terminal.Feed(data);
        if (alternate != terminal.Alternate)
        {
            ClearSelection();
            ClearSearch();
        }
        AdjustPositions(oldDropped);
        ScrollTo(atBottom || alternate != terminal.Alternate
            ? terminal.HistoryCount : viewTop, schedule: false);
        RequestRedraw();
    }

    public void FocusTerminal(FocusState focusState = FocusState.Programmatic)
    {
        if (closed)
            return;
        focusPending = !input.IsLoaded || input.XamlRoot is null || !input.Focus(focusState);
    }

    public void SetAppearance(TerminalAppearance settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        appearance = settings with
        {
            Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : TerminalTheme.MemoryDark,
            FontSize = Math.Clamp(settings.FontSize, 9, 32),
            Scrollback = Math.Clamp(settings.Scrollback, 100, 50000)
        };
        var atBottom = viewTop == terminal.HistoryCount;
        var oldDropped = terminal.DroppedHistory;
        terminal.SetHistoryLimit(appearance.Scrollback);
        AdjustPositions(oldDropped);
        ScrollTo(atBottom ? terminal.HistoryCount : viewTop, schedule: false);
        ApplyPalette();
        ResizeViewport();
        UpdateBlinkTimer();
        RequestRedraw();
    }

    private void TerminalLoaded(object sender, RoutedEventArgs args)
    {
        subscribedRoot = XamlRoot;
        if (subscribedRoot is not null)
            subscribedRoot.Changed += OnRootChanged;
        ResizeViewport();
        UpdateScrollBar();
        UpdateBlinkTimer();
        RequestRedraw();
        if (focusPending)
            FocusTerminal();
    }

    private void TerminalUnloaded(object sender, RoutedEventArgs args)
    {
        if (subscribedRoot is not null)
            subscribedRoot.Changed -= OnRootChanged;
        subscribedRoot = null;
        redrawTimer.Stop();
        redrawQueued = false;
        blinkTimer.Stop();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!closed && subscribedRoot is null)
            {
                foreach (var row in paintedRows)
                    viewport.Children.Remove(row.Layer);
                paintedRows.Clear();
            }
        });
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(rasterScale - sender.RasterizationScale) > .0001)
            ResizeViewport();
    }

    private void ResizeViewport()
    {
        if (closed)
            return;
        MeasureCells();
        if (viewport.ActualWidth < 1 || viewport.ActualHeight < 1)
            return;
        var columns = Math.Clamp((int)Math.Floor((viewport.ActualWidth - PaddingX * 2) / cellWidth), 2, 500);
        var rows = Math.Clamp((int)Math.Floor((viewport.ActualHeight - PaddingY * 2) / cellHeight), 1, 200);
        if (columns != Columns || rows != Rows)
        {
            var atBottom = viewTop == terminal.HistoryCount;
            var oldDropped = terminal.DroppedHistory;
            terminal.Resize(columns, rows);
            ClearSelection();
            ClearSearch();
            AdjustPositions(oldDropped);
            ScrollTo(atBottom ? terminal.HistoryCount : viewTop, schedule: false);
            TerminalResized?.Invoke(columns, rows);
        }
        RequestRedraw();
    }

    private void ScrollTo(int top, bool schedule = true)
    {
        viewTop = Math.Clamp(top, 0, terminal.HistoryCount);
        if (subscribedRoot is not null)
            UpdateScrollBar();
        UpdateBlinkTimer(resetPhase: false);
        if (schedule)
            RequestRedraw();
    }

    private void UpdateScrollBar()
    {
        changingScroll = true;
        try
        {
            scroll.Maximum = terminal.HistoryCount;
            scroll.Value = viewTop;
            scroll.ViewportSize = Rows;
            scroll.LargeChange = Rows;
            scroll.IsEnabled = terminal.HistoryCount > 0;
        }
        finally { changingScroll = false; }
    }

    private void AdjustPositions(long oldDropped)
    {
        var dropped = (int)Math.Min(int.MaxValue, terminal.DroppedHistory - oldDropped);
        if (dropped <= 0)
            return;
        viewTop = Math.Max(0, viewTop - dropped);
        Shift(ref selectionStart, dropped);
        Shift(ref selectionEnd, dropped);
        Shift(ref matchStart, dropped);
        Shift(ref matchEnd, dropped);

        static void Shift(ref CellPosition? position, int amount)
        {
            if (position is { } value)
                position = value.Row < amount ? null : value with { Row = value.Row - amount };
        }
    }

    private void RequestRedraw()
    {
        if (closed || redrawQueued || subscribedRoot is null)
            return;
        redrawQueued = true;
        redrawTimer.Start();
    }

    private void UpdateBlinkTimer(bool resetPhase = true)
    {
        if (resetPhase)
            blinkOn = true;
        var animate = !closed && subscribedRoot is not null && appearance.CursorBlink &&
            terminal.CursorVisible && terminal.CursorShape is not (2 or 4 or 6) &&
            viewTop == terminal.HistoryCount && !composing && input.FocusState != FocusState.Unfocused;
        if (animate)
        {
            if (!blinkTimer.IsRunning)
                blinkTimer.Start();
        }
        else if (blinkTimer.IsRunning)
        {
            blinkTimer.Stop();
            blinkOn = true;
        }
    }

    public void ShowSearch()
    {
        if (closed)
            return;
        searchBar.Visibility = Visibility.Visible;
        searchBox.Focus(FocusState.Programmatic);
        searchBox.SelectAll();
    }

    public void FindNext(string term) => Find(term, backwards: false);
    public void FindPrevious(string term) => Find(term, backwards: true);
    public void ClearSearch()
    {
        matchStart = matchEnd = null;
        previousQuery = "";
        searchStatus.Text = "";
        RequestRedraw();
    }

    private void BuildSearchBar()
    {
        searchBar.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 4; i++)
            searchBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchBar.Children.Add(searchBox);
        AddSearchButton(L10n.Get("SBD4B9718F1F0"), 1, () => FindPrevious(searchBox.Text));
        AddSearchButton(L10n.Get("SA898D7C3D78C"), 2, () => FindNext(searchBox.Text));
        SetColumn(searchStatus, 3);
        searchBar.Children.Add(searchStatus);
        AddSearchButton(L10n.Get("S3FD47EDCE45B"), 4, HideSearch);
        searchBar.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                HideSearch();
            }
            else if (e.Key == VirtualKey.Enter && searchBox.FocusState != FocusState.Unfocused)
            {
                e.Handled = true;
                FindNext(searchBox.Text);
            }
        };
        searchBox.TextChanged += (_, _) => ClearSearch();
        AutomationProperties.SetName(scroll, L10n.Get("S5EA7D7F11861"));
    }

    private void AddSearchButton(string text, int column, Action click)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => click();
        SetColumn(button, column);
        searchBar.Children.Add(button);
    }

    private void HideSearch()
    {
        ClearSearch();
        searchBar.Visibility = Visibility.Collapsed;
        FocusTerminal();
    }

    public void Dispose()
    {
        if (closed)
            return;
        closed = true;
        redrawTimer.Stop();
        blinkTimer.Stop();
        if (subscribedRoot is not null)
            subscribedRoot.Changed -= OnRootChanged;
        subscribedRoot = null;
        terminal.ClearHistory();
        viewport.Children.Clear();
        paintedRows.Clear();
    }
}


