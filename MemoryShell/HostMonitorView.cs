using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MemoryShell;

internal sealed class HostMonitorView : StackPanel
{
    private const int MaximumSamples = 36;
    private readonly SolidColorBrush card = WorkspaceTheme.Brush("ShellPaneBrush");
    private readonly SolidColorBrush border = WorkspaceTheme.Brush("ShellLineBrush");
    private readonly SolidColorBrush muted = WorkspaceTheme.Brush("ShellMutedBrush");
    private readonly SolidColorBrush blue = Brush(0x348BC7);
    private readonly SolidColorBrush mint = Brush(0x369E77);
    private readonly SolidColorBrush amber = Brush(0xCF963A);
    private readonly SolidColorBrush violet = Brush(0x987CCB);
    private readonly bool animations = new UISettings().AnimationsEnabled;
    private readonly TextBlock title = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock state = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock uptime = new() { FontSize = 12 };
    private readonly TextBlock load = new() { FontSize = 12 };
    private readonly StackPanel sections = new() { Spacing = 8 };
    private readonly Canvas chart = new() { Height = 76, IsHitTestVisible = false };
    private readonly Polyline receiveLine = new() { StrokeThickness = 1.8 };
    private readonly Polyline sendLine = new() { StrokeThickness = 1.8 };
    private readonly TextBlock receiveRate = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock sendRate = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock chartScale = new() { FontSize = 10 };
    private readonly ComboBox networkFilter = new() { MinHeight = 28, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    private bool sortProcessesByMemory;
    private readonly List<Line> chartGrid = [];
    private readonly TextBlock processEmpty = new() { Text = L10n.Get("S80A8C43A6BA9"), FontSize = 12, Opacity = .65 };
    private readonly TextBlock diskEmpty = new() { Text = L10n.Get("SE2DB740C604C"), FontSize = 12, Opacity = .65 };
    private readonly List<LinuxNetwork> history = [];
    private readonly StackPanel processRows = new() { Spacing = 4 };
    private readonly StackPanel diskRows = new() { Spacing = 6 };
    private readonly List<ProcessCells> processCells = [];
    private readonly List<Meter> diskMeters = [];
    private readonly Meter cpu;
    private readonly Meter memory;
    private readonly Meter swap;
    private object? currentSession;
    private LinuxMetrics? sample;
    private bool refreshingInterfaces;

    private sealed record ProcessCells(Grid Container, TextBlock Name, TextBlock Cpu, TextBlock Memory);

    public HostMonitorView()
    {
        if (new AccessibilitySettings().HighContrast)
        {
            var system = new UISettings();
            var background = system.GetColorValue(UIColorType.Background);
            var foreground = system.GetColorValue(UIColorType.Foreground);
            card.Color = background;
            border.Color = muted.Color = blue.Color = mint.Color = amber.Color = violet.Color = foreground;
        }
        Spacing = 8;
        Padding = new Thickness(1, 0, 2, 6);
        UseLayoutRounding = true;

        var header = new StackPanel { Spacing = 4 };
        title.Text = L10n.Get("SC797A5342DCF");
        state.Text = L10n.Get("S3D52574CE150");
        state.Foreground = muted;
        header.Children.Add(title);
        header.Children.Add(state);
        Children.Add(header);
        Children.Add(sections);
        sections.Visibility = Visibility.Visible;

        cpu = new Meter("CPU", blue, border, muted, animations);
        memory = new Meter(L10n.Get("S7D8F8C37EC78"), mint, border, muted, animations);
        swap = new Meter(L10n.Get("SC9C3D2D17549"), violet, border, muted, animations);
        var resources = new StackPanel { Spacing = 4 };
        resources.Children.Add(uptime);
        resources.Children.Add(load);
        resources.Children.Add(cpu.View);
        resources.Children.Add(memory.View);
        resources.Children.Add(swap.View);
        sections.Children.Add(Card(L10n.Get("SDAA6D02A55BC"), resources));

        AutomationProperties.SetName(networkFilter, L10n.Get("S17584DF6F9E2"));
        networkFilter.SelectionChanged += (_, _) =>
        {
            if (!refreshingInterfaces)
                RenderNetwork();
        };
        receiveLine.Stroke = blue;
        sendLine.Stroke = mint;
        sendLine.StrokeDashArray = new DoubleCollection { 3, 2 };
        for (var index = 0; index < 4; index++)
        {
            var line = new Line { Stroke = border, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 }, Opacity = .7 };
            chartGrid.Add(line);
            chart.Children.Add(line);
        }
        chart.Children.Add(receiveLine);
        chart.Children.Add(sendLine);
        chart.SizeChanged += (_, _) => RenderChart();
        chartScale.Foreground = muted;
        receiveRate.Foreground = blue;
        sendRate.Foreground = mint;
        var network = new StackPanel { Spacing = 5 };
        network.Children.Add(networkFilter);
        network.Children.Add(chartScale);
        network.Children.Add(chart);
        network.Children.Add(receiveRate);
        network.Children.Add(sendRate);
        var networkSection = Card(L10n.Get("S97B31B5D63F5"), network);

        var process = new StackPanel { Spacing = 4 };
        var heading = AddProcessRow(process);
        heading.Name.Text = L10n.Get("S928F87D4507B");
        heading.Cpu.Text = "CPU ↓";
        heading.Memory.Text = L10n.Get("S7D8F8C37EC78");
        heading.Container.Background = WorkspaceTheme.Brush("ShellToolbarBrush");
        void SortHeading(TextBlock label, bool byMemory, int column)
        {
            heading.Container.Children.Remove(label);
            var button = new Button { Content = label, Padding = new Thickness(0, 3, 0, 3), MinWidth = 0, MinHeight = 24,
                BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            ToolTipService.SetToolTip(button, byMemory ? L10n.Get("S21B984DE281B") : L10n.Get("S5F9D0952A2F3"));
            button.Click += (_, _) =>
            {
                sortProcessesByMemory = byMemory;
                heading.Cpu.Text = byMemory ? "CPU" : "CPU ↓";
                heading.Memory.Text = byMemory ? L10n.Get("S0177B719715F") : L10n.Get("S7D8F8C37EC78");
                RenderProcesses();
            };
            Grid.SetColumn(button, column);
            heading.Container.Children.Add(button);
        }
        SortHeading(heading.Memory, true, 0);
        SortHeading(heading.Cpu, false, 1);
        process.Children.Add(processEmpty);
        for (var index = 0; index < 8; index++)
            processCells.Add(AddProcessRow(processRows));
        process.Children.Add(processRows);
        sections.Children.Add(Card(L10n.Get("S9BFB43ADA1F9"), process));
        sections.Children.Add(networkSection);

        for (var index = 0; index < 8; index++)
        {
            var meter = new Meter("", amber, border, muted, animations);
            diskMeters.Add(meter);
            diskRows.Children.Add(meter.View);
        }
        diskRows.Children.Add(diskEmpty);
        sections.Children.Add(Card(L10n.Get("SDE7B72A3F852"), diskRows));
        ClearReadings();
    }

    public void Present(object? session, string name, LinuxMetrics? metrics, string message)
    {
        if (!ReferenceEquals(currentSession, session))
        {
            currentSession = session;
            sample = null;
            history.Clear();
            networkFilter.ItemsSource = null;
        }

        title.Text = session is null ? L10n.Get("SC797A5342DCF") : name;
        state.Text = metrics is null ? message : L10n.Get("S5BE0323E8ADC");
        state.Foreground = metrics is null ? muted : mint;
        if (metrics is null)
        {
            ClearReadings();
            return;
        }
        if (ReferenceEquals(sample, metrics))
            return;

        sample = metrics;
        uptime.Text = L10n.Get("S921DCCE85113") + metrics.Uptime;
        load.Text = L10n.Get("S08083BEEF550") + (metrics.Load.Length == 0 ? "—" : metrics.Load);
        cpu.Update("CPU", metrics.CpuPercent);
        memory.Update(L10n.Get("S7D8F8C37EC78"), metrics.MemoryPercent, metrics.Memory);
        swap.Update(L10n.Get("SC9C3D2D17549"), metrics.SwapPercent, metrics.Swap);
        history.Add(metrics.Network);
        if (history.Count > MaximumSamples)
            history.RemoveAt(0);
        UpdateNetworkChoices(metrics.Network);
        RenderNetwork();
        RenderProcesses();
        RenderDisks(metrics.Disks);
    }

    private void ClearReadings()
    {
        sample = null;
        history.Clear();
        uptime.Text = L10n.Get("S61116B1C5E6E");
        load.Text = L10n.Get("SA2351AE6FC9E");
        cpu.Update("CPU", null);
        memory.Update(L10n.Get("S7D8F8C37EC78"), null);
        swap.Update(L10n.Get("SC9C3D2D17549"), null);
        foreach (var row in processCells) row.Container.Visibility = Visibility.Collapsed;
        foreach (var meter in diskMeters) meter.View.Visibility = Visibility.Collapsed;
        processEmpty.Visibility = diskEmpty.Visibility = Visibility.Visible;
        RenderNetwork();
    }

    private Border Card(string caption, UIElement content)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = caption, FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = muted });
        body.Children.Add(content);
        return new Border { Background = card, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 8, 9, 9), Child = body };
    }

    private void UpdateNetworkChoices(LinuxNetwork network)
    {
        var options = network.Interfaces.Select(item => item.Name).Prepend(L10n.Get("SAA2E915C38FC")).ToArray();
        if (networkFilter.ItemsSource is string[] old && options.SequenceEqual(old))
            return;

        var selected = networkFilter.SelectedIndex > 0 ? networkFilter.SelectedItem as string : null;
        refreshingInterfaces = true;
        try
        {
            networkFilter.ItemsSource = options;
            networkFilter.SelectedIndex = selected is null ? 0 : Math.Max(0, Array.IndexOf(options, selected));
        }
        finally { refreshingInterfaces = false; }
    }

    private void RenderNetwork()
    {
        var rate = sample is null ? (Received: (double?)null, Sent: (double?)null) : Rate(sample.Network);
        receiveRate.Text = L10n.Get("SCEC617F04E3B") + FormatRate(rate.Received);
        sendRate.Text = L10n.Get("S43FBB5363E95") + FormatRate(rate.Sent);
        RenderChart();
    }

    private (double? Received, double? Sent) Rate(LinuxNetwork sample)
    {
        if (networkFilter.SelectedIndex > 0 && networkFilter.SelectedItem is string selected)
        {
            var item = sample.Interfaces.FirstOrDefault(item => item.Name == selected);
            return (item?.Received, item?.Sent);
        }

        double received = 0, sent = 0;
        var available = false;
        foreach (var item in sample.Interfaces)
        {
            if (item.Received is not double down || item.Sent is not double up)
                continue;
            received += down;
            sent += up;
            available = true;
        }
        return available ? (received, sent) : (null, null);
    }

    private void RenderChart()
    {
        var samples = history.Select(Rate).ToArray();
        var peak = samples.Select(rate => Math.Max(rate.Received ?? 0, rate.Sent ?? 0)).DefaultIfEmpty(0).Max();
        var scale = Math.Max(1024, Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(1, peak)))));
        chartScale.Text = samples.Any(item => item.Received is not null)
            ? L10n.Get("S14014B0FB9B5") + FormatRate(scale)
            : L10n.Get("S8AB33643C616");

        var width = chart.ActualWidth;
        var height = chart.ActualHeight;
        for (var index = 0; index < chartGrid.Count; index++)
        {
            chartGrid[index].X1 = 0; chartGrid[index].X2 = Math.Max(0, width);
            chartGrid[index].Y1 = chartGrid[index].Y2 = Math.Max(0, height - 1) * index / 3;
        }
        var receive = new PointCollection();
        var send = new PointCollection();
        if (width > 0 && height > 0)
        {
            // Use equal spacing for chart samples.
            var step = (width - 2) / (MaximumSamples - 1);
            for (var index = 0; index < samples.Length; index++)
            {
                var (down, up) = samples[index];
                if (down is null || up is null)
                    continue;
                var x = width - 1 - step * (samples.Length - 1 - index);
                receive.Add(new Point(x, height - 1 - down.Value / scale * (height - 2)));
                send.Add(new Point(x, height - 1 - up.Value / scale * (height - 2)));
            }
        }
        receiveLine.Points = receive;
        sendLine.Points = send;
    }

    private void RenderProcesses()
    {
        var items = sample?.Processes
            .OrderByDescending(process => sortProcessesByMemory ? process.Memory : process.Cpu)
            .Take(processCells.Count).ToArray() ?? [];
        for (var index = 0; index < processCells.Count; index++)
        {
            var row = processCells[index];
            row.Container.Visibility = index < items.Length ? Visibility.Visible : Visibility.Collapsed;
            if (index >= items.Length)
                continue;
            var item = items[index];
            row.Name.Text = $"{item.Name} · {item.Pid}";
            ToolTipService.SetToolTip(row.Name, row.Name.Text);
            row.Cpu.Text = Percent(item.Cpu);
            row.Memory.Text = Percent(item.Memory);
        }
        processRows.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        processEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ProcessCells AddProcessRow(StackPanel body)
    {
        var grid = new Grid { ColumnSpacing = 4, MinHeight = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var cpu = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        var memory = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(cpu, 1);
        Grid.SetColumn(memory, 0);
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);
        grid.Children.Add(cpu);
        grid.Children.Add(memory);
        body.Children.Add(grid);
        return new ProcessCells(grid, name, cpu, memory);
    }

    private void RenderDisks(IReadOnlyList<LinuxDisk> disks)
    {
        var items = disks.OrderByDescending(disk => disk.Mount == "/").ThenByDescending(disk => disk.Percent)
            .Take(diskMeters.Count).ToArray();
        diskEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < diskMeters.Count; index++)
        {
            var meter = diskMeters[index];
            meter.View.Visibility = index < items.Length ? Visibility.Visible : Visibility.Collapsed;
            if (index < items.Length)
            {
                var item = items[index];
                meter.Update(item.Mount, item.Percent, $"{item.Used} / {item.Total}");
            }
        }
    }

    private static string Percent(double? value) => value is double number ? $"{number:0.#}%" : "—";
    private static string FormatRate(double? value) => value is double number ? LinuxMonitor.FormatBytes(number) + "/s" : "—";

    private static SolidColorBrush Brush(int rgb) =>
        new(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    private sealed class Meter
    {
        private readonly TextBlock label = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock reading = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock detail = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ScaleTransform scale = new() { ScaleX = 0 };
        private readonly Rectangle progress = new() { Height = 5 };
        private readonly bool animate;
        private Storyboard? movement;
        public StackPanel View { get; } = new() { Spacing = 5 };

        public Meter(string name, Brush fill, Brush rail, Brush muted, bool animated)
        {
            label.Text = name;
            reading.Foreground = muted;
            detail.Foreground = muted;
            animate = animated;
            progress.Fill = fill;
            progress.RenderTransform = scale;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(label);
            Grid.SetColumn(reading, 1);
            row.Children.Add(reading);
            View.Children.Add(row);
            View.Children.Add(new Grid { Height = 5, Background = rail, Children = { progress } });
            View.Children.Add(detail);
        }

        public void Update(string name, double? value, string? detailText = null)
        {
            label.Text = name;
            ToolTipService.SetToolTip(View, detailText is null ? name : $"{name} · {detailText}");
            reading.Text = Percent(value);
            detail.Text = detailText ?? "";
            detail.Visibility = detailText is null ? Visibility.Collapsed : Visibility.Visible;
            var target = Math.Clamp(value ?? 0, 0, 100) / 100;
            var current = scale.ScaleX;
            movement?.Stop();
            scale.ScaleX = target;
            if (animate && Math.Abs(current - target) > .001)
            {
                var change = new DoubleAnimation { From = current, To = target,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)), EnableDependentAnimation = true };
                Storyboard.SetTarget(change, scale);
                Storyboard.SetTargetProperty(change, "ScaleX");
                movement = new Storyboard(); movement.Children.Add(change); movement.Begin();
            }
        }
    }

}
