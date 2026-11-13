using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MemoryShell;

internal static class NetworkDiagnosticsDialog
{
    private const int MaximumHops = 24;
    private const int MaximumAddresses = 4;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly byte[] ProbeData = new byte[32];

    public static async Task ShowAsync(XamlRoot root, string? initialHost = null, int? initialPort = null)
    {
        var host = new TextBox { Header = L10n.Get("S9DE86A249F69"), Text = initialHost ?? "" };
        var port = new TextBox { Header = L10n.Get("S67310768E77B"), Text = initialPort?.ToString() ?? "" };
        var runButton = new Button { Content = L10n.Get("S5CE51775A064") };
        var stopButton = new Button { Content = L10n.Get("SCA4D973C0B00"), IsEnabled = false };
        var results = new TextBox
        {
            Header = L10n.Get("S058714E528A8"),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            MinHeight = 220,
            MaxHeight = 300
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { runButton, stopButton } };
        var body = new StackPanel { Spacing = 10, MinWidth = 350, Children = { host, port, buttons, results } };
        var dialog = new QueuedContentDialog
        {
            XamlRoot = root,
            Title = L10n.Get("SDAA335840030"),
            RequestedTheme = (root.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Content = new ScrollViewer { Content = body, MaxHeight = 500 },
            CloseButtonText = L10n.Get("S3FD47EDCE45B")
        };

        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? currentRun = null;
        runButton.Click += async (_, _) =>
        {
            if (currentRun is not null)
                return;

            var target = host.Text.Trim();
            if (target.StartsWith('[') && target.EndsWith(']'))
                target = target[1..^1];
            if (Uri.CheckHostName(target) == UriHostNameType.Unknown && !IPAddress.TryParse(target, out _))
            {
                results.Text = L10n.Get("SACB8493920A0");
                return;
            }
            int? targetPort = null;
            if (!string.IsNullOrWhiteSpace(port.Text))
            {
                if (!int.TryParse(port.Text.Trim(), out var number) || number is < 1 or > 65535)
                {
                    results.Text = L10n.Get("S5A1EF7F8F893");
                    return;
                }
                targetPort = number;
            }

            using var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            run.CancelAfter(TimeSpan.FromSeconds(35));
            currentRun = run;
            runButton.IsEnabled = host.IsEnabled = port.IsEnabled = false;
            stopButton.IsEnabled = true;
            results.Text = "";
            void Append(string line)
            {
                if (!lifetime.IsCancellationRequested)
                    results.Text += line + Environment.NewLine;
            }

            try { await DiagnoseAsync(target, targetPort, Append, run.Token); }
            catch (OperationCanceledException) when (run.IsCancellationRequested) { Append(L10n.Get("S78CD3806248E")); }
            catch (Exception ex) { Append(L10n.Format("S8A2A24DC83A4", ex.Message)); }
            finally
            {
                currentRun = null;
                if (!lifetime.IsCancellationRequested)
                {
                    runButton.IsEnabled = host.IsEnabled = port.IsEnabled = true;
                    stopButton.IsEnabled = false;
                }
            }
        };
        stopButton.Click += (_, _) => currentRun?.Cancel();
        try { await dialog.ShowAsync(); }
        finally { lifetime.Cancel(); }
    }

    private static async Task DiagnoseAsync(string host, int? port, Action<string> report, CancellationToken ct)
    {
        report(L10n.Format("S53E57C5C13A2", host));
        var resolved = (await Dns.GetHostAddressesAsync(host, ct))
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct().ToArray();
        if (resolved.Length == 0)
        {
            report(L10n.Get("S2F8B700C2D8B"));
            return;
        }

        var addresses = resolved.Take(MaximumAddresses).ToArray();
        foreach (var address in addresses)
            report($"DNS {(address.AddressFamily == AddressFamily.InterNetwork ? "A   " : "AAAA")}  {address}");
        if (resolved.Length > addresses.Length)
            report(L10n.Format("S06C4095375E3", addresses.Length, resolved.Length));

        IPAddress? firstReachable = null;
        using var ping = new Ping();
        report("");
        report("ICMP Ping");
        foreach (var address in addresses)
        {
            try
            {
                var reply = await ping.SendPingAsync(address, PingTimeout, ProbeData, cancellationToken: ct);
                if (reply.Status == IPStatus.Success)
                {
                    firstReachable ??= address;
                    report($"  {address}    {reply.RoundtripTime} ms");
                }
                else
                    report($"  {address}    {Describe(reply.Status)}");
            }
            catch (PingException ex) { report($"  {address}    {ex.GetBaseException().Message}"); }
        }

        var route = firstReachable ?? addresses[0];
        if (port is int endpoint)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var tcp = new TcpClient(route.AddressFamily);
            var time = Stopwatch.StartNew();
            try
            {
                await tcp.ConnectAsync(route, endpoint, timeout.Token);
                report(L10n.Format("SFCE591B12A95", route, endpoint, time.Elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                report(L10n.Format("SFC346A26546E", route, endpoint));
            }
            catch (SocketException ex) { report(L10n.Format("SA6867DF7429F", route, endpoint, ex.Message)); }
        }

        report("");
        report(L10n.Format("S33B72C29D80B", route, MaximumHops));
        for (var ttl = 1; ttl <= MaximumHops; ttl++)
        {
            try
            {
                var reply = await ping.SendPingAsync(route, PingTimeout, ProbeData, new PingOptions(ttl, false), ct);
                if (reply.Status == IPStatus.TimedOut)
                    report($"  {ttl,2}    *");
                else
                    report($"  {ttl,2}    {reply.Address}    {Describe(reply.Status)}");
                if (reply.Status == IPStatus.Success)
                    return;
            }
            catch (PingException ex)
            {
                report(L10n.Format("S69181F45369A", ex.GetBaseException().Message));
                return;
            }
        }
        report(L10n.Get("SB36C2D5C71F4"));
    }

    private static string Describe(IPStatus status) => status switch
    {
        IPStatus.Success => L10n.Get("S434C6AD59D72"),
        IPStatus.TimedOut => L10n.Get("SE512CF016F96"),
        IPStatus.TtlExpired => L10n.Get("SFE4E2DFF1832"),
        IPStatus.DestinationHostUnreachable => L10n.Get("S977C7C73F771"),
        IPStatus.DestinationNetworkUnreachable => L10n.Get("S314FABD4FDC3"),
        _ => status.ToString()
    };
}
