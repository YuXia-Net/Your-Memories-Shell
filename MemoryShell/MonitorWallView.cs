using System.Security.Cryptography;
using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MemoryShell;

internal sealed class MonitorWallView : Grid, IDisposable
{
    private readonly Func<SavedConnection[]> connections;
    private readonly Func<SavedHostKey[]> hostKeys;
    private readonly Func<ConnectionProfile, string> displayName;
    private readonly Func<ConnectionProfile, string, string> displayText;
    private readonly Dictionary<Guid, ProbeCard> screens = [];
    private readonly Dictionary<Guid, Worker> workers = [];
    private readonly Grid wall = new() { ColumnSpacing = 6, RowSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer scroll;
    private ProbeLayoutPreferences layout;
    private readonly Action<ProbeLayoutPreferences> saveLayout;
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock empty = new() { Text = L10n.Get("MonitorWallEmpty"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) };
    private readonly TextBox filter = new() { PlaceholderText = L10n.Get("MonitorFilter"), Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ToggleButton pause = new() { Content = L10n.Get("MonitorPause") };
    private readonly ComboBox interval = new() { Width = 100 };
    private readonly Button fullScreen = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private CancellationTokenSource? lifetime;
    private bool active, running, disposed;
    private int seconds = 5;
    public event Action<Guid>? ConnectRequested;
    public event Action<Guid>? EditRequested;
    public event Action? FullScreenRequested;
    public event Action? BackRequested;

    public MonitorWallView(Func<SavedConnection[]> connections, Func<SavedHostKey[]> hostKeys, Func<ConnectionProfile, string> displayName,
        Func<ConnectionProfile, string, string> displayText, ProbeLayoutPreferences preferences, Action<ProbeLayoutPreferences> saveLayout)
    {
        this.connections = connections; this.hostKeys = hostKeys; this.displayName = displayName; this.displayText = displayText;
        layout = preferences.Normalize();
        this.saveLayout = saveLayout;
        Background = WorkspaceTheme.Brush("ShellBaseBrush");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var heading = new TextBlock { Text = L10n.Get("MonitorWallTitle"), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var back = new Button { Content = new FontIcon { Glyph = "\uE72B", FontSize = 16 }, Width = 36, Height = 36, MinWidth = 0 };
        ToolTipService.SetToolTip(back, L10n.Get("BackToWorkspace"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(back, L10n.Get("BackToWorkspace"));
        back.Click += (_, _) => BackRequested?.Invoke();
        var refresh = new Button { Content = L10n.Get("MonitorRefresh") };
        fullScreen.Content = L10n.Get("MonitorFullScreen");
        var controls = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemHeight = 40, ItemWidth = 110, MaximumRowsOrColumns = 8 };
        controls.Children.Add(pause); controls.Children.Add(refresh); controls.Children.Add(interval); controls.Children.Add(fullScreen);
        VariableSizedWrapGrid.SetColumnSpan(filter, 2); controls.Children.Add(filter);
        var gridButton = new DropDownButton { Content = L10n.Get("ProbeGrid") };
        var presets = new ComboBox { Header = L10n.Get("ProbeGrid"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var size in new[] { (4, 4), (8, 8), (6, 16) }) presets.Items.Add(new ComboBoxItem { Content = $"{size.Item1} × {size.Item2}", Tag = size });
        var columnsInput = new NumberBox { Header = L10n.Get("ProbeColumns"), Minimum = 1, Maximum = 32, Value = layout.Columns, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var rowsInput = new NumberBox { Header = L10n.Get("ProbeRows"), Minimum = 1, Maximum = 32, Value = layout.Rows, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        presets.SelectionChanged += (_, _) => { if (presets.SelectedItem is ComboBoxItem { Tag: ValueTuple<int, int> size }) { columnsInput.Value = size.Item1; rowsInput.Value = size.Item2; } };
        var apply = new Button { Content = L10n.Get("SA3030BF8F16D") };
        var gridError = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var flyout = new Flyout { Content = new StackPanel { Width = 230, Spacing = 8, Children = { presets, columnsInput, rowsInput,
            new TextBlock { Text = L10n.Get("ProbeGridNote"), TextWrapping = TextWrapping.Wrap, FontSize = 12 }, gridError, apply } } };
        gridButton.Flyout = flyout; controls.Children.Add(gridButton);
        apply.Click += (_, _) =>
        {
            if (!double.IsFinite(columnsInput.Value) || !double.IsFinite(rowsInput.Value)) return;
            try { ApplyLayout(new ProbeLayoutPreferences((int)columnsInput.Value, (int)rowsInput.Value)); flyout.Hide(); }
            catch (Exception ex) { gridError.Text = ex.Message; }
        };
        foreach (var value in new[] { 5, 10, 30 }) interval.Items.Add(new ComboBoxItem { Content = $"{value} s", Tag = value });
        interval.SelectedIndex = 0;
        ToolTipService.SetToolTip(interval, L10n.Get("MonitorInterval"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(interval, L10n.Get("MonitorInterval"));
        summary.Foreground = WorkspaceTheme.Brush("ShellMutedBrush");
        var header = new StackPanel { Spacing = 12, Margin = new Thickness(24, 16, 24, 16), Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { back, heading } }, summary,
            new Border { Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), Background = WorkspaceTheme.Brush("ShellCardBrush"), Child = controls } } };
        Children.Add(header);
        var body = new Grid { Children = { wall, empty } };
        scroll = new ScrollViewer { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 0, 24, 24) };
        SetRow(scroll, 1); Children.Add(scroll);
        scroll.SizeChanged += (_, _) => ArrangeScreens();
        filter.TextChanged += (_, _) => ArrangeScreens();
        interval.SelectionChanged += (_, _) => { seconds = (int)((ComboBoxItem)interval.SelectedItem).Tag; Wake(); };
        pause.Click += (_, _) => { pause.Content = L10n.Get(pause.IsChecked == true ? "MonitorResume" : "MonitorPause"); UpdateRun(); };
        refresh.Click += (_, _) => { foreach (var worker in workers.Values) worker.RetryAt = default; Wake(); };
        fullScreen.Click += (_, _) => FullScreenRequested?.Invoke();
        Reconcile();
    }

    public void SetActive(bool value) { active = value; UpdateRun(); }
    public void SetFullScreen(bool value) => fullScreen.Content = L10n.Get(value ? "ExitProbeFullScreen" : "MonitorFullScreen");
    internal void ApplyLayout(ProbeLayoutPreferences next) { next = next.Normalize(); saveLayout(next); layout = next; ArrangeScreens(); }
    public void RefreshConnections() { Reconcile(); foreach (var worker in workers.Values) if (worker.RetryAt == DateTimeOffset.MaxValue) worker.RetryAt = default; Wake(); }
    private void Wake() { if (!disposed && wake.CurrentCount == 0) wake.Release(); }

    private void UpdateRun()
    {
        if (disposed || !active || pause.IsChecked == true)
        {
            lifetime?.Cancel();
            foreach (var screen in screens.Values) screen.SetStatus(L10n.Get("MonitorPaused"), false);
            UpdateSummary();
        }
        else if (!running) Run();
    }

    private void Reconcile()
    {
        var changed = false;
        var selected = connections().Where(item => item.Profile.IncludeInMonitor).ToArray();
        var ids = selected.Select(item => item.Profile.Id).ToHashSet();
        foreach (var id in screens.Keys.Where(id => !ids.Contains(id)).ToArray())
        { wall.Children.Remove(screens[id]); screens.Remove(id); changed = true; }
        foreach (var saved in selected)
        {
            if (!screens.TryGetValue(saved.Profile.Id, out var screen))
            {
                var id = saved.Profile.Id;
                screen = new ProbeCard(() => ConnectRequested?.Invoke(id), () => EditRequested?.Invoke(id));
                screens.Add(id, screen); wall.Children.Add(screen);
                changed = true;
            }
            var name = displayName(saved.Profile); var tags = displayText(saved.Profile, saved.Profile.MonitorTags);
            var spanX = Math.Clamp(saved.Profile.MonitorColumnSpan, 1, 32); var spanY = Math.Clamp(saved.Profile.MonitorRowSpan, 1, 32);
            changed |= screen.NameText.Text != name || screen.GroupText.Text != tags || screen.ColumnSpan != spanX || screen.RowSpan != spanY;
            screen.NameText.Text = name; screen.GroupText.Text = tags;
            screen.ColumnSpan = spanX; screen.RowSpan = spanY;
        }
        if (changed) ArrangeScreens();
        UpdateSummary();
    }

    private void ArrangeScreens()
    {
        var columns = layout.Columns;
        var cellWidth = Math.Max(144, (Math.Max(0, scroll.ActualWidth - 48) - (columns - 1) * 6) / columns);
        var cellHeight = Math.Max(144, (Math.Max(0, scroll.ActualHeight - 24) - (layout.Rows - 1) * 6) / layout.Rows);
        if (wall.ColumnDefinitions.Count != columns)
        {
            wall.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++) wall.ColumnDefinitions.Add(new ColumnDefinition());
        }
        foreach (var column in wall.ColumnDefinitions) column.Width = new GridLength(cellWidth);
        var occupied = new HashSet<(int Row, int Column)>();
        var usedRows = layout.Rows;
        var index = 0;
        foreach (var screen in screens.Values)
        {
            var visible = (screen.NameText.Text + " " + screen.GroupText.Text).Contains(filter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
            screen.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) continue;
            var spanX = Math.Min(screen.ColumnSpan, columns); var spanY = screen.RowSpan;
            var row = 0; var column = 0;
            bool Fits(int r, int c) { for (var y = 0; y < spanY; y++) for (var x = 0; x < spanX; x++) if (occupied.Contains((r + y, c + x))) return false; return true; }
            while (true) { if (column + spanX <= columns && Fits(row, column)) break; column++; if (column >= columns) { row++; column = 0; } }
            for (var y = 0; y < spanY; y++) for (var x = 0; x < spanX; x++) occupied.Add((row + y, column + x));
            SetRow(screen, row); SetColumn(screen, column); SetColumnSpan(screen, spanX); SetRowSpan(screen, spanY);
            screen.Configure(cellWidth * spanX + 6 * (spanX - 1), cellHeight * spanY + 6 * (spanY - 1));
            usedRows = Math.Max(usedRows, row + spanY); index++;
        }
        var rows = usedRows;
        while (wall.RowDefinitions.Count < rows) wall.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (wall.RowDefinitions.Count > rows) wall.RowDefinitions.RemoveAt(wall.RowDefinitions.Count - 1);
        foreach (var row in wall.RowDefinitions) row.Height = new GridLength(cellHeight);
        empty.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Text = L10n.Get(screens.Count == 0 ? "MonitorWallEmpty" : "MonitorNoResults");
    }

    private void UpdateSummary() => summary.Text = L10n.Format("MonitorSummary", screens.Count,
        screens.Values.Count(item => item.Online), screens.Values.Count(item => !item.Online));

    private async void Run()
    {
        running = true;
        using var cancellation = new CancellationTokenSource();
        lifetime = cancellation;
        var token = cancellation.Token;
        using var gate = new SemaphoreSlim(4);
        try
        {
            while (!token.IsCancellationRequested)
            {
                Reconcile();
                var selected = connections().Where(item => item.Profile.IncludeInMonitor).ToArray();
                var ids = selected.Select(item => item.Profile.Id).ToHashSet();
                foreach (var id in workers.Keys.Where(id => !ids.Contains(id)).ToArray())
                { workers[id].Dispose(); workers.Remove(id); }
                var trusted = hostKeys();
                await Task.WhenAll(selected.Select(async saved =>
                {
                    await gate.WaitAsync(token);
                    try { await SampleAsync(saved, trusted, token); }
                    finally { gate.Release(); }
                }));
                UpdateSummary();
                await wake.WaitAsync(TimeSpan.FromSeconds(seconds), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) { summary.Text = ex.Message; pause.IsChecked = true; pause.Content = L10n.Get("MonitorResume"); } }
        finally
        {
            foreach (var worker in workers.Values) worker.Dispose();
            workers.Clear(); lifetime = null; running = false;
            if (!disposed && active && pause.IsChecked != true) Run();
        }
    }

    private async Task SampleAsync(SavedConnection saved, SavedHostKey[] trusted, CancellationToken token)
    {
        if (!screens.TryGetValue(saved.Profile.Id, out var screen)) return;
        if (workers.TryGetValue(saved.Profile.Id, out var worker) && !ReferenceEquals(worker.Source, saved))
        { worker.Dispose(); workers.Remove(saved.Profile.Id); worker = null; }
        worker ??= workers[saved.Profile.Id] = new Worker(saved);
        if (DateTimeOffset.UtcNow < worker.RetryAt) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            if ((!saved.Profile.RememberCredentials && string.IsNullOrEmpty(saved.Profile.KeyPath)) || (saved.Secret is null && saved.PrivateKey is null && string.IsNullOrEmpty(saved.Profile.KeyPath)))
                throw new InvalidOperationException(L10n.Get("MonitorNeedsCredentials"));
            if (!trusted.Any(key => key.Host.Equals(saved.Profile.Host, StringComparison.OrdinalIgnoreCase) && key.Port == saved.Profile.Port) ||
                (saved.Profile.Jump is { } route && !trusted.Any(key => key.Host.Equals(route.Host, StringComparison.OrdinalIgnoreCase) && key.Port == route.Port)))
                throw new InvalidOperationException(L10n.Get("MonitorNeedsTrust"));
            if (worker.Connection is null)
            {
                screen.SetStatus(L10n.Get("MonitorConnecting"), false);
                var copy = saved with { PrivateKey = saved.PrivateKey?.ToArray(), Profile = saved.Profile with
                { Jump = saved.Profile.Jump is { } jump ? jump with { PrivateKey = jump.PrivateKey?.ToArray() } : null } };
                try
                {
                    worker.Connection = await SshSession.MonitorConnection.OpenAsync(copy, (identity, _) =>
                    {
                        var accepted = trusted.Any(key => key.Host.Equals(identity.Host, StringComparison.OrdinalIgnoreCase) &&
                            key.Port == identity.Port && key.Algorithm == identity.Algorithm && key.Fingerprint == identity.Fingerprint);
                        if (!accepted) throw new InvalidOperationException(L10n.Get("MonitorNeedsTrust"));
                        return Task.FromResult(true);
                    }, timeout.Token);
                }
                finally
                {
                    if (copy.PrivateKey is { } key) CryptographicOperations.ZeroMemory(key);
                    if (copy.Profile.Jump?.PrivateKey is { } jumpKey) CryptographicOperations.ZeroMemory(jumpKey);
                }
                worker.Monitor = new LinuxMonitor(worker.Connection.ExecuteAsync, includeProcesses: false);
            }
            var metrics = await worker.Monitor!.ReadAsync(timeout.Token);
            token.ThrowIfCancellationRequested();
            if (metrics.TotalMemory == "—") throw new InvalidOperationException(L10n.Get("MonitorUnsupported"));
            if (screens.TryGetValue(saved.Profile.Id, out var current) && ReferenceEquals(current, screen)) screen.Present(metrics);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var failure = SshConnectionErrors.Classify(ex, saved.Profile, saved.PrivateKey is not null);
            var retry = ex is OperationCanceledException || (ex is not InvalidOperationException && failure.Retry == SshRetryAction.Reconnect);
            worker.Dispose(); worker.RetryAt = retry ? DateTimeOffset.UtcNow.AddSeconds(30) : DateTimeOffset.MaxValue;
            if (!disposed && !token.IsCancellationRequested)
                screen.SetStatus(displayText(saved.Profile, ex is OperationCanceledException ? L10n.Get("MonitorTimeout") :
                    ex is InvalidOperationException ? ex.Message : failure.Message), false);
        }
    }

    public void Dispose() { if (disposed) return; disposed = true; active = false; lifetime?.Cancel(); }

    private sealed class Worker(SavedConnection source) : IDisposable
    {
        public SavedConnection Source { get; } = source;
        public SshSession.MonitorConnection? Connection;
        public LinuxMonitor? Monitor;
        public DateTimeOffset RetryAt;
        public void Dispose() { Connection?.Dispose(); Connection = null; Monitor = null; }
    }

}
