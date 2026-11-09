using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private readonly ProgressBar transferBar = new() { Minimum = 0, Maximum = 100 };
    private readonly TextBlock transferTitle = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock transferDetail = new() { FontSize = 11, Opacity = .78, TextTrimming = TextTrimming.CharacterEllipsis };
    private Border? transferCard;
    private long transferDisplayVersion;
    private long progressStarted;
    private long progressOrigin = -1;

    private UIElement BuildTransferCard()
    {
        var labels = new StackPanel { Spacing = 4, Children = { transferTitle, transferDetail, transferBar } };
        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(labels);
        cancelTransfer.VerticalAlignment = VerticalAlignment.Center;
        cancelTransfer.Click += (_, _) =>
        {
            if (transferCancellation is { } running)
            {
                cancelTransfer.IsEnabled = false;
                running.Cancel();
            }
            else if (transferCard is not null)
            {
                transferCard.Visibility = Visibility.Collapsed;
            }
        };
        Grid.SetColumn(cancelTransfer, 1);
        layout.Children.Add(cancelTransfer);
        AutomationProperties.SetName(transferBar, L10n.Get("S20A454BE6071"));

        transferCard = new Border
        {
            Child = layout, Visibility = Visibility.Collapsed, Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), Background = WorkspaceTheme.Brush("ShellCardBrush")
        };
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            transferCard.Transitions = new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 8 } };
        return transferCard;
    }

    private static SolidColorBrush TransferBrush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));

    private void BeginTransferDisplay()
    {
        transferItemNames.Clear();
        transferItemCount = 0;
        currentTransferText.Text = L10n.Get("SAF20B81BD1FD");
        if (transferCard is null)
            return;
        transferTitle.Text = L10n.Get("S393E18EED6CB");
        transferDetail.Text = L10n.Get("S06F4872BFC56");
        transferBar.Foreground = WorkspaceTheme.Brush("ShellAccentBrush");
        transferBar.Value = 0;
        transferBar.IsIndeterminate = true;
        transferCard.Visibility = Visibility.Visible;
        cancelTransfer.Content = L10n.Get("S2CD0F3BE8738");
        cancelTransfer.IsEnabled = true;
        cancelTransfer.Visibility = Visibility.Visible;
    }

    private long SetTransferItem(string action, string name, long? total)
    {
        transferItemCount++;
        if (transferItemNames.Count < 3) transferItemNames.Add($"{action} · {name}");
        progressStarted = Stopwatch.GetTimestamp();
        progressOrigin = -1;
        transferTitle.Text = $"{action} · {name}";
        transferBar.Value = 0;
        transferBar.IsIndeterminate = total is null;
        transferDetail.Text = total is long length ? FormatSize(length) : L10n.Get("SCB25C9CBE9E8");
        return ++transferDisplayVersion;
    }

    private void ShowTransferProgress(long version, long bytes, long? total, int? files = null)
    {
        if (transferCancellation is null || disposed || version != transferDisplayVersion)
            return;

        if (progressOrigin < 0)
            progressOrigin = bytes;
        var elapsed = Stopwatch.GetElapsedTime(progressStarted).TotalSeconds;
        var speed = elapsed >= .4 ? $"  ·  {FormatSize((long)(Math.Max(0, bytes - progressOrigin) / elapsed))}/s" : "";
        var count = files is int fileCount ? L10n.Format("SFCE7C72D4716", fileCount) : "";
        if (total is long length)
        {
            var percentage = length == 0 ? 100 : Math.Clamp(bytes * 100d / length, 0, 100);
            transferBar.Value = percentage;
            var rate = elapsed > 0 ? Math.Max(0, bytes - progressOrigin) / elapsed : 0;
            var remaining = rate > 0 && elapsed >= 2 ? Math.Max(0, length - bytes) / rate : double.NaN;
            var eta = double.IsFinite(remaining) && remaining <= 86400 ? L10n.Format("S4BA0F7344EAE", TimeSpan.FromSeconds(remaining)) : "";
            transferDetail.Text = $"{percentage:0}%  ·  {FormatSize(bytes)} / {FormatSize(length)}{speed}{eta}";
        }
        else
        {
            transferDetail.Text = $"{FormatSize(bytes)}{count}{speed}";
        }
        currentTransferText.Text = transferTitle.Text + "  " + transferDetail.Text;
    }

    private void FinishTransferDisplay(bool complete, string title)
    {
        transferDisplayVersion++;
        if (disposed || transferCard is null)
            return;
        cancelTransfer.IsEnabled = true;
        cancelTransfer.Content = L10n.Get("S3FD47EDCE45B");
        transferBar.IsIndeterminate = false;
        if (complete)
        {
            transferBar.Value = 100;
            transferBar.Foreground = TransferBrush(97, 226, 172);
        }
        else
        {
            transferBar.Foreground = TransferBrush(255, 176, 121);
        }
        transferTitle.Text = title;
        transferDetail.Text = fileStatus.Text;
        SaveTransferRecord(title);
    }
}

