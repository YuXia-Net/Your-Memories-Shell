using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace MemoryShell;

internal sealed class SessionStatusIndicators : StackPanel, IDisposable
{
    private readonly SolidColorBrush screen = new(Windows.UI.Color.FromArgb(255, 0, 120, 215));
    private readonly Grid first;
    private readonly Grid second;
    private readonly Grid center = new() { Width = 24, Height = 24, Opacity = 0 };
    private readonly ProgressRing progress = new() { Width = 24, Height = 24, MinWidth = 0, MinHeight = 0, IsActive = false };
    private readonly FontIcon done = new() { Glyph = "\uE73E", FontSize = 17, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform shift = new();
    private readonly Grid icons = new() { Width = 24, Height = 26 };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? blink;
    private Storyboard? movement;
    private CancellationTokenSource? completion;
    private long completionStamp;
    private bool disposed;
    internal bool Connected { get; private set; }
    internal bool Transferring { get; private set; }
    internal bool IsBlinking => blink?.IsRunning == true;
    internal bool WhitePhase { get; private set; }
    internal bool ShowingCompletion => done.Visibility == Visibility.Visible;
    internal double Progress => progress.Value;

    public SessionStatusIndicators()
    {
        Orientation = Orientation.Horizontal; Spacing = 7;
        VerticalAlignment = VerticalAlignment.Center;
        first = Computer(); second = Computer();
        first.RenderTransform = shift;
        second.Opacity = 0;
        first.HorizontalAlignment = second.HorizontalAlignment = center.HorizontalAlignment = HorizontalAlignment.Right;
        center.RenderTransform = new TranslateTransform { X = -35 };
        center.Children.Add(progress); center.Children.Add(done);
        icons.Children.Add(second); icons.Children.Add(center); icons.Children.Add(first);
        Children.Add(new TextBlock { Text = "SSH/SFTP", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = WorkspaceTheme.Brush("ShellMutedBrush") });
        Children.Add(icons);
        SetState(false, false);
    }

    private Grid Computer()
    {
        var grid = new Grid { Width = 24, Height = 24 };
        grid.Children.Add(new Border { Width = 23, Height = 17, VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1.3),
            BorderBrush = WorkspaceTheme.Brush("ShellMutedBrush"), Padding = new Thickness(2),
            Child = new Border { Background = screen, CornerRadius = new CornerRadius(.5) } });
        grid.Children.Add(new Rectangle { Width = 2, Height = 4, Margin = new Thickness(0, 16, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, Fill = WorkspaceTheme.Brush("ShellMutedBrush") });
        grid.Children.Add(new Rectangle { Width = 12, Height = 1.3, Margin = new Thickness(0, 21, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, Fill = WorkspaceTheme.Brush("ShellMutedBrush") });
        return grid;
    }

    internal void SetState(bool connected, bool transferring, double? percentage = null, long completed = 0)
    {
        if (disposed) return;
        Connected = connected;
        var description = "SSH/SFTP · " + L10n.Get(transferring ? "TransferIndicatorActive" : connected ? "S5BE0323E8ADC" : "S1F0AC6953E04");
        if (transferring && percentage is double value) description += $" · {value:0}%";
        AutomationProperties.SetName(this, description); ToolTipService.SetToolTip(this, description);
        progress.IsIndeterminate = percentage is null;
        progress.Value = percentage ?? 0;
        if (Transferring != transferring)
        {
            Transferring = transferring;
            completion?.Cancel(); completion?.Dispose(); completion = null;
            done.Visibility = Visibility.Collapsed;
            WhitePhase = false;
            if (transferring)
            {
                Expand(true); progress.Visibility = Visibility.Visible; progress.IsActive = true;
                blink ??= DispatcherQueue.CreateTimer();
                blink.Interval = TimeSpan.FromMilliseconds(500);
                blink.Tick -= Blink; blink.Tick += Blink; blink.Start();
            }
            else { blink?.Stop(); progress.IsActive = false; }
        }
        PaintScreen();
        if (transferring) { completionStamp = 0; return; }
        if (completed != 0 && completed != completionStamp)
        {
            completionStamp = completed;
            completion?.Cancel(); completion?.Dispose(); completion = new();
            Expand(true); progress.Visibility = Visibility.Collapsed; done.Visibility = Visibility.Visible;
            _ = FinishAsync(completion.Token);
        }
        else if (completed == 0)
        {
            completion?.Cancel(); completion?.Dispose(); completion = null;
            completionStamp = 0; done.Visibility = Visibility.Collapsed; Expand(false);
        }
    }

    private void Blink(object? sender, object args) { WhitePhase = !WhitePhase; PaintScreen(); }
    private void PaintScreen() => screen.Color = Transferring && WhitePhase ? Microsoft.UI.Colors.White :
        Connected || Transferring ? Windows.UI.Color.FromArgb(255, 0, 120, 215) : Windows.UI.Color.FromArgb(255, 130, 138, 148);

    private async Task FinishAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(850, token);
            if (!disposed) Expand(false);
            await Task.Delay(280, token);
            if (!disposed) done.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
    }

    private void Expand(bool expanded)
    {
        var x = expanded ? -70d : 0d; var opacity = expanded ? 1d : 0d;
        if (shift.X == x && center.Opacity == opacity) return;
        var fromX = shift.X; var fromCenter = center.Opacity; var fromSecond = second.Opacity; var fromWidth = icons.Width;
        movement?.Stop();
        shift.X = x; center.Opacity = second.Opacity = opacity;
        icons.Width = expanded ? 94 : 24;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        movement = new Storyboard();
        Animate(shift, "X", fromX, x); Animate(center, "Opacity", fromCenter, opacity); Animate(second, "Opacity", fromSecond, opacity);
        Animate(icons, "Width", fromWidth, icons.Width);
        movement.Begin();
    }

    private void Animate(DependencyObject target, string property, double from, double to)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(280),
            EnableDependentAnimation = property == "Width",
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
        movement!.Children.Add(animation);
    }

    public void Dispose()
    {
        disposed = true; completion?.Cancel(); completion?.Dispose(); completion = null;
        blink?.Stop(); if (blink is not null) blink.Tick -= Blink; blink = null;
        movement?.Stop(); movement = null; progress.IsActive = false;
    }
}

public sealed partial class MainWindow
{
    private readonly SessionStatusIndicators sessionIndicators = new();
    private void RefreshSessionIndicators()
    {
        var pane = Sessions?.SelectedItem is TabViewItem tab ? panes.GetValueOrDefault(tab) : null;
        sessionIndicators.SetState(pane?.IsConnected == true, pane?.IsTransferring == true,
            pane?.TransferPercentage, pane?.TransferCompletedAt ?? 0);
    }
}
