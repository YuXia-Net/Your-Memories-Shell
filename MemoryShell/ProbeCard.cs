using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace MemoryShell;

internal sealed class ProbeCard : Grid
{
    internal readonly TextBlock NameText = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    internal readonly TextBlock GroupText = new() { FontSize = 11, Foreground = WorkspaceTheme.Brush("ShellMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock status = new() { Text = L10n.Get("MonitorWaiting"), FontSize = 11 };
    private readonly Ellipse statusDot = new() { Width = 7, Height = 7, Fill = WorkspaceTheme.Brush("ShellMutedBrush") };
    private readonly Image logo = new() { Width = 18, Height = 18 };
    private readonly Gauge cpu = new("CPU"), memory = new("RAM"), swap = new("SWAP"), disk = new("/");
    private readonly StackPanel gauges = new() { Spacing = 2 };
    private readonly StackPanel volumes = new() { Spacing = 4 };
    private readonly Dictionary<string, Gauge> volumeGauges = [];
    private readonly TextBlock system = new() { FontSize = 11, Foreground = WorkspaceTheme.Brush("ShellMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock down = new() { FontSize = 11 }, up = new() { FontSize = 11 };
    private readonly Canvas graph = new() { MinHeight = 18, MaxHeight = 72, VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false };
    private readonly Polyline receiveLine = new() { Stroke = WorkspaceTheme.Brush("ShellAccentBrush"), StrokeThickness = 1.5 };
    private readonly Polyline sendLine = new() { Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 144, 120)), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 } };
    private readonly Line baseline = new() { Stroke = WorkspaceTheme.Brush("ShellLineBrush"), StrokeThickness = 1 };
    private readonly Queue<(double Down, double Up)> history = new();
    private readonly Grid network = new() { RowSpacing = 2 };
    private readonly Grid rates = new() { ColumnSpacing = 6 };
    private string distro = "", tooltip = "", statusTip = "";
    private int visibleVolumes;
    internal bool Online { get; private set; }
    internal int ColumnSpan = 1, RowSpan = 1;

    internal ProbeCard(Action connect, Action edit)
    {
        Padding = new Thickness(8); CornerRadius = new CornerRadius(6); RowSpacing = 3;
        Background = WorkspaceTheme.Brush("ShellCardBrush"); BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"); BorderThickness = new Thickness(1);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new Grid { ColumnSpacing = 5 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(logo); SetColumn(NameText, 1); title.Children.Add(NameText);
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { statusDot, status } };
        SetColumn(state, 2); title.Children.Add(state); Children.Add(title);
        SetRow(GroupText, 1); Children.Add(GroupText);
        gauges.Children.Add(cpu); gauges.Children.Add(memory); gauges.Children.Add(swap); gauges.Children.Add(disk);
        SetRow(gauges, 2); Children.Add(gauges);
        network.RowDefinitions.Add(new RowDefinition()); network.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        graph.Children.Add(baseline); graph.Children.Add(receiveLine); graph.Children.Add(sendLine); network.Children.Add(graph);
        rates.ColumnDefinitions.Add(new ColumnDefinition()); rates.ColumnDefinitions.Add(new ColumnDefinition());
        down.Foreground = receiveLine.Stroke; up.Foreground = sendLine.Stroke;
        down.TextTrimming = up.TextTrimming = TextTrimming.CharacterEllipsis;
        rates.Children.Add(down); SetColumn(up, 1); rates.Children.Add(up); SetRow(rates, 1); network.Children.Add(rates);
        graph.SizeChanged += (_, _) => DrawNetwork(); SetRow(network, 3); Children.Add(network);
        SetRow(volumes, 4); Children.Add(volumes); SetRow(system, 5); Children.Add(system);
        var menu = new MenuFlyout();
        var connectItem = new MenuFlyoutItem { Text = L10n.Get("MonitorConnect") }; connectItem.Click += (_, _) => connect();
        var editItem = new MenuFlyoutItem { Text = L10n.Get("MonitorEdit") }; editItem.Click += (_, _) => edit();
        var info = new MenuFlyoutItem { Text = L10n.Get("ProbeDetails") };
        info.Click += (_, _) => new Flyout { Content = new ScrollViewer { MaxHeight = 480, Content = new TextBlock { Text = NameText.Text + "\n" + statusTip + "\n" + tooltip, MaxWidth = 360, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } } }.ShowAt(this);
        menu.Items.Add(connectItem); menu.Items.Add(editItem); menu.Items.Add(info); ContextFlyout = menu;
        DoubleTapped += (_, args) => { args.Handled = true; connect(); };
    }

    internal void Configure(double width, double height)
    {
        Width = width; Height = height;
        GroupText.Visibility = string.IsNullOrWhiteSpace(GroupText.Text) ? Visibility.Collapsed : Visibility.Visible;
        status.Visibility = width >= 240 ? Visibility.Visible : Visibility.Collapsed;
        system.Visibility = height >= 230 ? Visibility.Visible : Visibility.Collapsed;
        volumes.Visibility = height >= 280 ? Visibility.Visible : Visibility.Collapsed;
        rates.Visibility = height >= 168 ? Visibility.Visible : Visibility.Collapsed;
        visibleVolumes = Math.Clamp((int)(height - 260) / 20, 0, 4);
        UpdateVolumeVisibility();
        NameText.FontSize = width >= 240 ? 14 : 12;
        foreach (var gauge in new[] { cpu, memory, swap, disk }) gauge.SetSize(height >= 260);
    }

    internal void SetStatus(string text, bool online)
    {
        Online = online;
        status.Text = online ? L10n.Get("MonitorOnline") : text == L10n.Get("MonitorPaused") || text == L10n.Get("MonitorConnecting") || text == L10n.Get("MonitorWaiting") ? text : L10n.Get("MonitorOffline");
        statusTip = text;
        statusDot.Fill = new SolidColorBrush(online ? Windows.UI.Color.FromArgb(255, 20, 150, 84) : status.Text == L10n.Get("MonitorOffline") ? Windows.UI.Color.FromArgb(255, 204, 66, 66) : Windows.UI.Color.FromArgb(255, 128, 128, 128));
        ToolTipService.SetToolTip(statusDot, text); AutomationProperties.SetName(statusDot, text);
        gauges.Opacity = network.Opacity = volumes.Opacity = online ? 1 : .4;
        ToolTipService.SetToolTip(this, text + "\n" + tooltip);
    }

    internal void Present(LinuxMetrics metrics)
    {
        var next = DistroLogo.Name(metrics.System);
        if (distro != next) { distro = next; logo.Source = DistroLogo.Source(next); }
        ToolTipService.SetToolTip(logo, metrics.System);
        cpu.Update(metrics.CpuPercent, "CPU");
        memory.Update(metrics.MemoryPercent, L10n.Get("MonitorMemory") + "  " + metrics.Memory);
        swap.Update(metrics.SwapPercent, L10n.Get("MonitorSwap") + "  " + metrics.Swap);
        var disks = metrics.Disks.Where(item => !item.Mount.StartsWith("/snap/", StringComparison.Ordinal)).ToArray();
        var root = disks.FirstOrDefault(item => item.Mount == "/") ?? disks.FirstOrDefault();
        disk.Label = root?.Mount ?? "/";
        disk.Update(root?.Percent, root is null ? "—" : root.Mount + "  " + root.Used + " / " + root.Total);
        var extra = disks.Where(item => !ReferenceEquals(item, root)).Take(4).ToArray();
        foreach (var mount in volumeGauges.Keys.Where(key => !extra.Any(item => item.Mount == key)).ToArray())
        { volumes.Children.Remove(volumeGauges[mount]); volumeGauges.Remove(mount); }
        foreach (var volume in extra)
        {
            if (!volumeGauges.TryGetValue(volume.Mount, out var gauge))
            { gauge = new Gauge(volume.Mount); volumeGauges.Add(volume.Mount, gauge); volumes.Children.Add(gauge); }
            gauge.Update(volume.Percent, volume.Mount + "  " + volume.Used + " / " + volume.Total);
        }
        UpdateVolumeVisibility();
        var interfaces = metrics.Network.Interfaces;
        double? received = interfaces.Any(item => item.Received.HasValue) ? interfaces.Sum(item => item.Received ?? 0) : null;
        double? sent = interfaces.Any(item => item.Sent.HasValue) ? interfaces.Sum(item => item.Sent ?? 0) : null;
        var receiveText = received is { } rx ? LinuxMonitor.FormatBytes(rx) + "/s" : "—";
        var sendText = sent is { } tx ? LinuxMonitor.FormatBytes(tx) + "/s" : "—";
        down.Text = "↓ " + receiveText; up.Text = "↑ " + sendText;
        if (received.HasValue || sent.HasValue)
        {
            history.Enqueue((received ?? 0, sent ?? 0)); while (history.Count > 40) history.Dequeue(); DrawNetwork();
        }
        system.Text = metrics.System + " · " + metrics.Cores + " CPU · " + metrics.TotalMemory;
        tooltip = "CPU " + (metrics.CpuPercent is { } value ? $"{value:0.#}%" : "—") + "\n"
            + L10n.Format("MonitorSystem", metrics.System, metrics.Cores, metrics.TotalMemory) + "\n"
            + L10n.Get("MonitorMemory") + "  " + metrics.Memory + "\n" + L10n.Get("MonitorSwap") + "  " + metrics.Swap + "\n"
            + L10n.Format("MonitorLoad", metrics.Load, metrics.Uptime) + "\n"
            + L10n.Format("MonitorNetwork", receiveText, sendText, LinuxMonitor.FormatBytes(interfaces.Sum(item => (double)item.ReceivedBytes)), LinuxMonitor.FormatBytes(interfaces.Sum(item => (double)item.SentBytes))) + "\n"
            + string.Join("\n", disks.Select(volume => volume.Mount + "  " + volume.Used + " / " + volume.Total))
            + "\n" + L10n.Format("MonitorUpdated", DateTime.Now.ToString("HH:mm:ss"));
        ToolTipService.SetToolTip(network, L10n.Format("ProbeNetworkScale", LinuxMonitor.FormatBytes(NetworkMaximum()) + "/s"));
        SetStatus(L10n.Get("MonitorOnline"), true);
    }

    private double NetworkMaximum() => Math.Max(1, history.Count == 0 ? 1 : history.Max(item => Math.Max(item.Down, item.Up)));

    private void UpdateVolumeVisibility()
    {
        var index = 0;
        foreach (var child in volumes.Children) child.Visibility = index++ < visibleVolumes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawNetwork()
    {
        var receivePoints = new PointCollection(); var sendPoints = new PointCollection();
        var max = NetworkMaximum(); var height = Math.Max(0, graph.ActualHeight - 2); var width = Math.Max(0, graph.ActualWidth - 2);
        receiveLine.Width = sendLine.Width = Math.Max(0, graph.ActualWidth);
        receiveLine.Height = sendLine.Height = Math.Max(0, graph.ActualHeight);
        baseline.X2 = width; baseline.Y1 = baseline.Y2 = height + 1;
        var index = 40 - history.Count;
        foreach (var sample in history)
        {
            var x = index++ * width / 39;
            receivePoints.Add(new Point(x, height - sample.Down / max * height + 1));
            sendPoints.Add(new Point(x, height - sample.Up / max * height + 1));
        }
        receiveLine.Points = receivePoints; sendLine.Points = sendPoints;
    }

    private sealed class Gauge : Grid
    {
        private readonly TextBlock label = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock value = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 100, Height = 6, MinHeight = 6, VerticalAlignment = VerticalAlignment.Center };
        internal string Label { set => label.Text = value; }

        internal Gauge(string name)
        {
            ColumnSpacing = 5; MinHeight = 15; label.Text = name;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            ColumnDefinitions.Add(new ColumnDefinition()); ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
            Children.Add(label); SetColumn(bar, 1); Children.Add(bar); SetColumn(value, 2); Children.Add(value);
        }

        internal void SetSize(bool large) { MinHeight = large ? 27 : 15; bar.Height = bar.MinHeight = large ? 10 : 6; }

        internal void Update(double? percent, string description)
        {
            var amount = percent.HasValue && double.IsFinite(percent.Value) ? Math.Clamp(percent.Value, 0, 100) : (double?)null;
            bar.Value = amount ?? 0; value.Text = amount is { } number ? $"{number:0}%" : "—";
            bar.Opacity = amount.HasValue ? 1 : .35;
            bar.Foreground = amount >= 95 ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 66, 66)) :
                amount >= 80 ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 194, 127, 0)) : WorkspaceTheme.Brush("ShellAccentBrush");
            var text = description + " · " + value.Text;
            ToolTipService.SetToolTip(this, text); AutomationProperties.SetName(bar, text);
        }
    }
}
