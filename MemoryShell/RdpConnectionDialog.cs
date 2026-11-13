using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed record RdpProfile(Guid Id, string Name, string Host, int Port, string Username, bool Clipboard,
    string Gateway = "");

internal static class RdpConnectionDialog
{
    public static async Task<RdpProfile?> ShowAsync(XamlRoot root, RdpProfile? existing, bool save)
    {
        var name = new TextBox { Header = L10n.Get("S228C3AC9032F"), Text = existing?.Name ?? "" };
        var host = new TextBox { Header = L10n.Get("S317C133E7A87"), Text = existing?.Host ?? "", PlaceholderText = "server.example.com" };
        var port = new TextBox { Header = L10n.Get("SE71AC32B544B"), Text = (existing?.Port ?? 3389).ToString(CultureInfo.InvariantCulture) };
        var username = new TextBox { Header = L10n.Get("SFDCA866F3834"), Text = existing?.Username ?? "" };
        var gateway = new TextBox
        {
            Header = L10n.Get("S31AF06B744CC"), Text = existing?.Gateway ?? "",
            PlaceholderText = "gateway.example.com"
        };
        var clipboard = new CheckBox { Content = L10n.Get("S2D36596D7B61"), IsChecked = existing?.Clipboard ?? false };
        var note = new TextBlock
        {
            Text = L10n.Get("S7732A890ADCC"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = .75
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var fields = new StackPanel { Spacing = 9, MinWidth = 330 };
        if (save)
            fields.Children.Add(name);
        fields.Children.Add(host);
        fields.Children.Add(port);
        if (save)
            fields.Children.Add(username);
        fields.Children.Add(gateway);
        fields.Children.Add(clipboard);
        fields.Children.Add(note);
        fields.Children.Add(error);

        var dialog = new QueuedContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (root.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = save ? existing is null ? L10n.Get("SF2B5B3B87433") : L10n.Get("S38C609BDA27C") : L10n.Get("S73C0689EF392"),
            Content = new ScrollViewer { Content = fields, MaxHeight = 530 },
            PrimaryButtonText = save ? L10n.Get("SA3030BF8F16D") : L10n.Get("SA5574109F020"),
            CloseButtonText = L10n.Get("S2CD0F3BE8738"),
            DefaultButton = ContentDialogButton.Primary
        };
        RdpProfile? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!TryNormalizeHost(host.Text, out var address) ||
                !int.TryParse(port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) ||
                parsedPort is < 1 or > 65535)
            {
                args.Cancel = true;
                error.Text = L10n.Get("S00D47C95855E");
                return;
            }

            var gatewayHost = "";
            if (!string.IsNullOrWhiteSpace(gateway.Text) && !TryNormalizeHost(gateway.Text, out gatewayHost))
            {
                args.Cancel = true;
                error.Text = L10n.Get("SD2C424F3490E");
                return;
            }
            result = new RdpProfile(existing?.Id ?? Guid.NewGuid(),
                string.IsNullOrWhiteSpace(name.Text) ? address : name.Text.Trim(), address, parsedPort,
                username.Text.Trim(), clipboard.IsChecked == true, gatewayHost);
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    public static void Launch(RdpProfile profile)
    {
        if (!TryNormalizeHost(profile.Host, out var host) || profile.Port is < 1 or > 65535)
            throw new ArgumentException(L10n.Get("S4F8FD13946E1"), nameof(profile));
        var gateway = "";
        if (!string.IsNullOrWhiteSpace(profile.Gateway) && !TryNormalizeHost(profile.Gateway, out gateway))
            throw new ArgumentException(L10n.Get("S22791993A122"), nameof(profile));
        var useGateway = gateway.Length > 0;

        var program = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
        if (!File.Exists(program))
            throw new FileNotFoundException(L10n.Get("SB56B4F780843"), program);

        var address = host.Contains(':') ? $"[{host}]" : host;
        var destination = address + ":" + profile.Port.ToString(CultureInfo.InvariantCulture);
        var optionsPath = Path.Combine(Path.GetTempPath(), $"memoryshell-options-{Guid.NewGuid():N}.rdp");
        try
        {
            using (var output = new StreamWriter(new FileStream(optionsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None),
                Encoding.Unicode))
            {
                // Write session policy without credentials.
                output.WriteLine("prompt for credentials:i:1");
                output.WriteLine("enablecredsspsupport:i:1");
                output.WriteLine("authentication level:i:2");
                output.WriteLine("gatewayprofileusagemethod:i:1");
                output.WriteLine($"gatewayusagemethod:i:{(useGateway ? 1 : 0)}");
                output.WriteLine($"redirectclipboard:i:{(profile.Clipboard ? 1 : 0)}");
            }

            var settings = new ProcessStartInfo { FileName = program, UseShellExecute = false };
            settings.ArgumentList.Add(optionsPath);
            settings.ArgumentList.Add("/v:" + destination);
            if (useGateway)
                settings.ArgumentList.Add("/g:" + (gateway.Contains(':') ? $"[{gateway}]" : gateway));
            settings.ArgumentList.Add("/public");
            settings.ArgumentList.Add("/prompt");

            var desktop = Process.Start(settings) ?? throw new IOException(L10n.Get("S7103954C42B7"));
            _ = CleanupOptionsAsync(desktop, optionsPath);
        }
        catch
        {
            DeleteOptionsFile(optionsPath);
            throw;
        }
    }

    private static bool TryNormalizeHost(string input, out string host)
    {
        host = input.Trim();
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        if (IPAddress.TryParse(host, out var address))
        {
            host = address.ToString();
            return address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
        }

        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            return false;

        try
        {
            host = new IdnMapping().GetAscii(host);
            return host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
        }
        catch (ArgumentException) { return false; }
    }

    private static async Task CleanupOptionsAsync(Process desktop, string path)
    {
        try { await desktop.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        finally
        {
            desktop.Dispose();
            DeleteOptionsFile(path);
        }
    }

    private static void DeleteOptionsFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
