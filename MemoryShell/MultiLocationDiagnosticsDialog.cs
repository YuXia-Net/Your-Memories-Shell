using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed record NetworkProbeResult(string Status, double? Milliseconds = null);
internal sealed record NetworkProbePoint(string Name,
    Func<string, int, CancellationToken, Task<NetworkProbeResult>> Probe);

internal static class MultiLocationDiagnosticsDialog
{
    private const int MaximumRemotePoints = 12;
    private const int ParallelProbes = 4;

    public static async Task ShowAsync(XamlRoot root, IReadOnlyList<NetworkProbePoint> points,
        string? initialHost = null, int initialPort = 22)
    {
        var host = new TextBox { Header = L10n.Get("S9CD63D897DD4"), Text = initialHost ?? "" };
        var port = new TextBox { Header = L10n.Get("S1D6FE422C4BF"), Text = initialPort.ToString(CultureInfo.InvariantCulture) };
        var instructions = new TextBlock
        {
            Text = L10n.Get("SCC09378A7744"),
            TextWrapping = TextWrapping.Wrap
        };
        var choices = new StackPanel { Spacing = 4 };
        var selected = new List<(NetworkProbePoint Point, CheckBox Choice)>();
        var available = points.Take(MaximumRemotePoints).Prepend(new NetworkProbePoint(L10n.Get("S8A94C4A1CDBD"), ProbeLocalAsync));
        foreach (var point in available)
        {
            var choice = new CheckBox
            {
                Content = new TextBlock { Text = point.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330 },
                IsChecked = true
            };
            choices.Children.Add(choice);
            selected.Add((point, choice));
        }
        if (points.Count > MaximumRemotePoints)
            choices.Children.Add(new TextBlock { Text = L10n.Format("S75A3DA4A7854", MaximumRemotePoints) });
        if (points.Count == 0)
            choices.Children.Add(new TextBlock { Text = L10n.Get("S4FFE2D410008") });

        var runButton = new Button { Content = L10n.Get("S62AE26051296") };
        var cancelButton = new Button { Content = L10n.Get("SCA4D973C0B00"), IsEnabled = false };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { runButton, cancelButton }
        };
        var results = new TextBox
        {
            Header = L10n.Get("S24601DA8BF9C"), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 120, MaxHeight = 225
        };
        var body = new StackPanel
        {
            Spacing = 9, MinWidth = 340,
            Children =
            {
                instructions, host, port, new ScrollViewer { Content = choices, MaxHeight = 150 }, buttons, results
            }
        };
        var dialog = new QueuedContentDialog
        {
            XamlRoot = root, Title = L10n.Get("S68BDF6B92DF0"), RequestedTheme = (root.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Content = new ScrollViewer { Content = body, MaxHeight = 510 }, CloseButtonText = L10n.Get("S3FD47EDCE45B")
        };

        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? active = null;
        runButton.Click += async (_, _) =>
        {
            if (active is not null)
                return;
            var target = NormalizeHost(host.Text);
            if (target is null || !int.TryParse(port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var endpoint) || endpoint is < 1 or > 65535)
            {
                results.Text = L10n.Get("S1877547F4350");
                return;
            }
            var probes = selected.Where(item => item.Choice.IsChecked == true).Select(item => item.Point).ToArray();
            if (probes.Length == 0)
            {
                results.Text = L10n.Get("S37EA867D3B0F");
                return;
            }

            using var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            using var queue = new SemaphoreSlim(ParallelProbes);
            run.CancelAfter(TimeSpan.FromSeconds(25));
            active = run;
            runButton.IsEnabled = host.IsEnabled = port.IsEnabled = false;
            cancelButton.IsEnabled = true;
            results.Text = $"{target}  ·  TCP {endpoint}{Environment.NewLine}";
            try
            {
                await Task.WhenAll(probes.Select(async point =>
                {
                    await queue.WaitAsync(run.Token);
                    try
                    {
                        NetworkProbeResult result;
                        try { result = await point.Probe(target, endpoint, run.Token); }
                        catch (Exception ex) when (!run.IsCancellationRequested)
                        {
                            var detail = ex.Message.Split('\r', '\n')[0];
                            result = new NetworkProbeResult(L10n.Format("S4A2045B75824", detail[..Math.Min(detail.Length, 160)]));
                        }
                        if (!lifetime.IsCancellationRequested)
                        {
                            var duration = result.Milliseconds is double ms ? $" · {ms:0.#} ms" : "";
                            results.Text += $"{point.Name}  —  {result.Status}{duration}{Environment.NewLine}";
                        }
                    }
                    finally { queue.Release(); }
                }));
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
                if (!lifetime.IsCancellationRequested)
                    results.Text += L10n.Get("S2CBAC834C7AB");
            }
            catch (Exception ex)
            {
                if (!lifetime.IsCancellationRequested)
                    results.Text += L10n.Format("S903D52C85797", ex.Message);
            }
            finally
            {
                active = null;
                if (!lifetime.IsCancellationRequested)
                {
                    runButton.IsEnabled = host.IsEnabled = port.IsEnabled = true;
                    cancelButton.IsEnabled = false;
                }
            }
        };
        cancelButton.Click += (_, _) => active?.Cancel();
        try { await dialog.ShowAsync(); }
        finally { lifetime.Cancel(); }
    }

    internal static string? NormalizeHost(string input)
    {
        var value = input.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
            value = value[1..^1];
        if (IPAddress.TryParse(value, out var address))
            return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0 ? null : address.ToString();
        if (value.Length is 0 or > 254)
            return null;
        try
        {
            var ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(value);
            return Uri.CheckHostName(ascii) == UriHostNameType.Dns &&
                   ascii.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ? ascii : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static async Task<NetworkProbeResult> ProbeLocalAsync(string host, int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(host, port, timeout.Token);
            return new NetworkProbeResult(L10n.Get("S6043B97C2EC8"), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new NetworkProbeResult(L10n.Get("SAE5EAD4D8907"));
        }
        catch (SocketException ex)
        {
            return new NetworkProbeResult(ex.SocketErrorCode == SocketError.ConnectionRefused
                ? L10n.Get("S3E13B80BEB4B") : L10n.Format("S4A95A6F971BD", ex.Message));
        }
    }
}
