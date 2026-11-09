using System.Net;
using System.Text;
using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class ProxyEditor : StackPanel
{
    private readonly List<SavedProxyPreset> presets;
    private readonly Action saveWorkspace;
    private readonly bool hideEndpoint;
    private readonly CheckBox enabled = new() { Content = L10n.Get("S10743551FBD1") };
    private readonly ComboBox picker = new() { Header = L10n.Get("S18C3E368A059"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox kind = new() { Header = L10n.Get("SAB2F31F30ACF"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox host = new() { Header = L10n.Get("SE87D9F23A3F5"), PlaceholderText = "proxy.example.com" };
    private readonly TextBox port = new() { Header = L10n.Get("SE71AC32B544B") };
    private readonly TextBox user = new() { Header = L10n.Get("S63527366AABC") };
    private readonly PasswordBox password = new() { Header = L10n.Get("S760D8FF16E6A"), PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly CheckBox keepPassword = new() { Content = L10n.Get("S0853A9DCE853") };
    private readonly TextBlock routeHint = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
    private readonly ContentControl parameters = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private SshProxySettings? passwordSource;
    private SshProxyKind previousKind;
    private bool updatingPicker;
    private bool loadingSettings;

    public bool IsRouteEnabled => enabled.IsChecked == true;

    public ProxyEditor(SshProxySettings? original, List<SavedProxyPreset> presets, Action saveWorkspace, bool hideEndpoint)
    {
        this.presets = presets;
        this.saveWorkspace = saveWorkspace;
        this.hideEndpoint = hideEndpoint;
        enabled.IsChecked = original is not null;
        Spacing = 8;
        foreach (var option in new[] { "HTTP CONNECT", "SOCKS4", "SOCKS5" })
            kind.Items.Add(option);
        var manage = new Button { Content = L10n.Get("SavePresetApply"), VerticalAlignment = VerticalAlignment.Bottom, Flyout = BuildManageFlyout() };
        var selection = new Grid { ColumnSpacing = 8 };
        selection.ColumnDefinitions.Add(new ColumnDefinition());
        selection.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(manage, 1);
        selection.Children.Add(picker);
        selection.Children.Add(manage);
        var endpoint = new Grid { ColumnSpacing = 8 };
        endpoint.ColumnDefinitions.Add(new ColumnDefinition());
        endpoint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        Grid.SetColumn(port, 1);
        endpoint.Children.Add(host);
        endpoint.Children.Add(port);
        parameters.Content = new StackPanel { Spacing = 8, Children = { kind, endpoint, user, password, keepPassword } };
        var fields = new StackPanel { Spacing = 8, Children = { selection, parameters, routeHint } };
        Children.Add(enabled);
        Children.Add(fields);

        void ShowFields()
        {
            fields.Visibility = IsRouteEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        picker.SelectionChanged += (_, _) =>
        {
            if (updatingPicker)
                return;
            if (SelectedPreset is { } preset)
                LoadSettings(preset.Settings);

            RefreshDetails();
        };
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
        port.TextChanged += (_, _) => RefreshDetails();
        user.TextChanged += (_, _) => RefreshDetails();
        keepPassword.Checked += (_, _) => RefreshDetails();
        keepPassword.Unchecked += (_, _) => RefreshDetails();
        enabled.Checked += (_, _) => ShowFields();
        enabled.Unchecked += (_, _) => ShowFields();

        // Match the saved credentials before selecting a preset.
        var match = original is null ? null : presets.FirstOrDefault(item => SameSettings(item.Settings, original));
        RefillPresets(match?.Id);
        LoadSettings(original);

        ShowFields();
    }

    public SshProxySettings? Read()
    {
        if (!IsRouteEnabled)
            return null;

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

    private Flyout BuildManageFlyout()
    {
        var label = new TextBox { Header = L10n.Get("S5FE110AA6D1D"), PlaceholderText = L10n.Get("S415DF34B0C25") };
        var description = new TextBlock
        {
            Text = L10n.Get("SC5E4EDAB941B"),
            TextWrapping = TextWrapping.Wrap, Opacity = .76
        };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var add = new Button { Content = L10n.Get("SavePresetApply") };
        var update = new Button { Content = L10n.Get("S3055A035F0EB") };
        var remove = new Button { Content = L10n.Get("S2F9DAA828907") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, update, remove } };
        var flyout = new Flyout
        {
            Content = new StackPanel { Width = 250, Spacing = 8, Children = { description, label, actions, message } }
        };

        flyout.Opening += (_, _) =>
        {
            var selected = SelectedPreset;
            label.Text = selected?.Name ?? "";
            update.IsEnabled = remove.IsEnabled = selected is not null;
            message.Text = "";
        };
        add.Click += (_, _) => SavePreset(null);
        update.Click += (_, _) =>
        {
            if (SelectedPreset is { } current)
                SavePreset(current);
        };
        remove.Click += (_, _) =>
        {
            if (SelectedPreset is not { } current)
                return;
            try
            {
                CommitPresetChange(current, null);
                RefillPresets(null);

                RefreshDetails();
                flyout.Hide();
            }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        return flyout;

        void SavePreset(SavedProxyPreset? current)
        {
            try
            {
                var name = label.Text.Trim();
                if (name.Length == 0)
                    throw new ArgumentException(L10n.Get("SCAB9417058BF"));
                if (presets.Any(item => item.Id != current?.Id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(L10n.Get("S5A8780CDDE5E"));
                var settings = Read() ?? throw new ArgumentException(L10n.Get("S96CF933E1F62"));
                var saved = new SavedProxyPreset(current?.Id ?? Guid.NewGuid(), name, settings);
                CommitPresetChange(current, saved);
                RefillPresets(saved.Id);
                LoadSettings(saved.Settings);

                flyout.Hide();
            }
            catch (Exception ex) { message.Text = ex.Message; }
        }
    }

    private void CommitPresetChange(SavedProxyPreset? current, SavedProxyPreset? next)
    {
        var index = current is null ? presets.Count : presets.IndexOf(current);
        if (current is null)
            presets.Add(next!);
        else if (next is null)
            presets.RemoveAt(index);
        else
            presets[index] = next;
        try { saveWorkspace(); }
        catch
        {
            if (current is null)
                presets.RemoveAt(index);
            else if (next is null)
                presets.Insert(index, current);
            else
                presets[index] = current;
            throw;
        }
    }

    private SavedProxyPreset? SelectedPreset => picker.SelectedItem is ComboBoxItem { Tag: Guid id }
        ? presets.Find(item => item.Id == id) : null;

    private void RefillPresets(Guid? selected)
    {
        updatingPicker = true;
        try
        {
            var manual = new ComboBoxItem { Content = L10n.Get("S6E382D9250F8") };
            ComboBoxItem selection = manual;
            picker.Items.Clear();
            picker.Items.Add(manual);
            foreach (var preset in presets.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var settings = preset.Settings;
                var label = hideEndpoint ? MainWindow.HideEndpoint(preset.Name, settings.Host, settings.Port, settings.Username) : preset.Name;
                var item = new ComboBoxItem { Content = label, Tag = preset.Id };
                picker.Items.Add(item);
                if (preset.Id == selected)
                    selection = item;
            }
            picker.SelectedItem = selection;
        }
        finally { updatingPicker = false; }
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
        routeHint.Text = SelectedPreset is null ? L10n.Get("S166C1C306C31") : L10n.Get("S90D476D76D8A");
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


