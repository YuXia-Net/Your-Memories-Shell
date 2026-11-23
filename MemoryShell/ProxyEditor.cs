using System.Net;
using System.Text;
using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class ProxyEditor : StackPanel
{
    private readonly CheckBox enabled = new() { Content = L10n.Get("S10743551FBD1") };
    private readonly ExplicitComboBox kind = new() { Header = L10n.Get("SAB2F31F30ACF"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox host = new() { Header = L10n.Get("SE87D9F23A3F5"), PlaceholderText = "proxy.example.com" };
    private readonly TextBox port = new() { Header = L10n.Get("SE71AC32B544B") };
    private readonly TextBox user = new() { Header = L10n.Get("S63527366AABC") };
    private readonly PasswordBox password = new() { Header = L10n.Get("S760D8FF16E6A"), PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly CheckBox keepPassword = new() { Content = L10n.Get("S0853A9DCE853") };
    private readonly ContentControl parameters = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private SshProxySettings? passwordSource;
    private SshProxyKind previousKind;
    private bool loadingSettings;

    public bool IsRouteEnabled => enabled.IsChecked == true;

    public ProxyEditor(SshProxySettings? original, List<SavedProxyPreset> presets, Action saveWorkspace, bool hideEndpoint)
    {
        enabled.IsChecked = original is not null;
        Spacing = 8;
        foreach (var option in new[] { "HTTP CONNECT", "SOCKS4", "SOCKS5" })
            kind.Items.Add(option);
        var match = original is null ? null : presets.FirstOrDefault(item => SameSettings(item.Settings, original));
        var selection = new PresetSelector<SavedProxyPreset>(presets, x => x.Id, x => x.Name,
            (id, name) => new(id, name, Read() ?? throw new ArgumentException(L10n.Get("S96CF933E1F62"))),
            x => LoadSettings(x.Settings), saveWorkspace, match?.Id,
            x => hideEndpoint ? MainWindow.HideEndpoint(x.Name, x.Settings.Host, x.Settings.Port, x.Settings.Username) : x.Name);
        var endpoint = new Grid { ColumnSpacing = 8 };
        endpoint.ColumnDefinitions.Add(new ColumnDefinition());
        endpoint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        Grid.SetColumn(port, 1);
        endpoint.Children.Add(host);
        endpoint.Children.Add(port);
        parameters.Content = new StackPanel { Spacing = 8, Children = { kind, endpoint, user, password, keepPassword } };
        var fields = new StackPanel { Spacing = 8, Children = { selection, parameters, selection.Editor } };
        Children.Add(enabled);
        Children.Add(fields);

        void ShowFields()
        {
            fields.Visibility = IsRouteEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        kind.SelectionChanged += (_, _) =>
        {
            if (loadingSettings)
                return;
            var next = SelectedKind;
            if (port.Text.Trim() == DefaultPort(previousKind).ToString())
                port.Text = DefaultPort(next).ToString();
            previousKind = next;
            RefreshDetails();
        };
        host.TextChanged += (_, _) => RefreshDetails();
        host.LostFocus += (_, _) => ApplyPastedEndpoint();
        port.TextChanged += (_, _) => RefreshDetails();
        user.TextChanged += (_, _) => RefreshDetails();
        keepPassword.Checked += (_, _) => RefreshDetails();
        keepPassword.Unchecked += (_, _) => RefreshDetails();
        enabled.Checked += (_, _) => ShowFields();
        enabled.Unchecked += (_, _) => ShowFields();

        LoadSettings(original);

        ShowFields();
    }

    public SshProxySettings? Read()
    {
        if (!IsRouteEnabled)
            return null;

        ApplyPastedEndpoint();
        var address = NormalizeHost(host.Text);
        var number = ReadPortOrZero();
        if ((Uri.CheckHostName(address) == UriHostNameType.Unknown && !IPAddress.TryParse(address, out _)) ||
            number is < 1 or > 65535)
            throw new ArgumentException(L10n.Get("SE3F177FDE7F7"));

        var username = user.Text.Trim();
        var secret = SelectedKind == SshProxyKind.Socks4 ? "" : password.Password;
        if (secret.Length == 0 && SelectedKind != SshProxyKind.Socks4 &&
            keepPassword.IsChecked == true && MatchesPasswordSource(address, number, username))
            secret = passwordSource?.Password ?? "";

        if (username.Any(char.IsControl) || (SelectedKind == SshProxyKind.Http && username.Contains(':')))
            throw new ArgumentException(L10n.Get("S3831F280C591"));
        if (secret.Length > 0 && username.Length == 0)
            throw new ArgumentException(L10n.Get("S8BACB776456C"));
        if (SelectedKind == SshProxyKind.Socks5 &&
            (Encoding.UTF8.GetByteCount(username) > 255 || Encoding.UTF8.GetByteCount(secret) > 255))
            throw new ArgumentException(L10n.Get("S40C8BD13911C"));

        return new SshProxySettings { Kind = SelectedKind, Host = address, Port = number, Username = username, Password = secret };
    }

    private void ApplyPastedEndpoint()
    {
        var input = host.Text.Trim();
        if (IPAddress.TryParse(input.Trim('[', ']'), out _)) return;
        var scheme = input.IndexOf("://", StringComparison.Ordinal);
        var authority = scheme < 0 ? input : input[(scheme + 3)..].TrimEnd('/');
        if (authority.IndexOfAny(['/', '?', '#']) >= 0 || (scheme < 0 && authority.Contains('@'))) return;
        if (!Uri.TryCreate("tcp://" + authority, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return;
        if (scheme < 0 && uri.Port < 1) return;
        var protocol = scheme < 0 ? SelectedKind : input[..scheme].ToLowerInvariant() switch
        {
            "http" => SshProxyKind.Http,
            "socks4" => SshProxyKind.Socks4,
            "socks5" or "socks5h" => SshProxyKind.Socks5,
            _ => (SshProxyKind?)null
        };
        if (protocol is null) return;
        var credentials = uri.UserInfo.Split(':', 2);
        kind.SelectedIndex = (int)protocol.Value;
        host.Text = uri.IdnHost;
        if (uri.Port >= 0) port.Text = uri.Port.ToString();
        else port.Text = (protocol == SshProxyKind.Http ? 80 : DefaultPort(protocol.Value)).ToString();
        if (uri.UserInfo.Length > 0)
        {
            user.Text = Uri.UnescapeDataString(credentials[0]);
            password.Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "";
            keepPassword.IsChecked = false;
        }
    }

    private void LoadSettings(SshProxySettings? settings)
    {
        loadingSettings = true;
        try
        {
            passwordSource = settings;
            previousKind = settings?.Kind ?? SshProxyKind.Socks5;
            kind.SelectedIndex = (int)previousKind;
            host.Text = settings?.Host ?? "";
            port.Text = (settings?.Port ?? DefaultPort(previousKind)).ToString();
            user.Text = settings?.Username ?? "";
            password.Password = "";
            keepPassword.IsChecked = !string.IsNullOrEmpty(settings?.Password);
        }
        finally { loadingSettings = false; }
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        if (loadingSettings)
            return;
        var hasPassword = SelectedKind != SshProxyKind.Socks4;
        var hasSavedPassword = hasPassword && !string.IsNullOrEmpty(passwordSource?.Password) &&
            MatchesPasswordSource(NormalizeHost(host.Text), ReadPortOrZero(), user.Text.Trim());
        password.Visibility = hasPassword ? Visibility.Visible : Visibility.Collapsed;
        password.PlaceholderText = hasSavedPassword && keepPassword.IsChecked == true ? L10n.Get("SAADCE4DEC186") : "";
        keepPassword.Visibility = hasSavedPassword ? Visibility.Visible : Visibility.Collapsed;
        user.Header = hasPassword ? L10n.Get("S63527366AABC") : L10n.Get("S3AC1CAFF4D4E");
    }

    private static bool SameSettings(SshProxySettings first, SshProxySettings second) =>
        first.Kind == second.Kind && first.Port == second.Port &&
        string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
        (first.Username ?? "") == (second.Username ?? "") && (first.Password ?? "") == (second.Password ?? "");

    private SshProxyKind SelectedKind => kind.SelectedIndex < 0 ? SshProxyKind.Socks5 : (SshProxyKind)kind.SelectedIndex;
    private int ReadPortOrZero() => int.TryParse(port.Text.Trim(), out var number) ? number : 0;
    private static int DefaultPort(SshProxyKind protocol) => protocol == SshProxyKind.Http ? 8080 : 1080;

    private bool MatchesPasswordSource(string address, int number, string username) => passwordSource is { } source &&
        string.Equals(source.Host, address, StringComparison.OrdinalIgnoreCase) &&
        source.Port == number && (source.Username ?? "") == username && source.Kind == SelectedKind;

    private static string NormalizeHost(string value)
    {
        var address = value.Trim();
        return address.StartsWith('[') && address.EndsWith(']') ? address[1..^1] : address;
    }
}


