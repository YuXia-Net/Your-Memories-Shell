using System.Net;
using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MemoryShell;

internal sealed class JumpEditor : StackPanel
{
    private SshJumpSettings? original;
    private readonly KeyWalletPicker wallet;
    private readonly CheckBox enabled = new() { Content = L10n.Get("S151398B54CD6") };
    private readonly TextBox host = new() { Header = L10n.Get("S5E20B52F38F4") };
    private readonly TextBox port = new() { Header = L10n.Get("SE71AC32B544B") };
    private readonly TextBox user = new() { Header = L10n.Get("S01B6FCF6A966") };
    private readonly CheckBox useKey = new() { Content = L10n.Get("S1F6B392A4ED6") };
    private readonly TextBox path = new() { Header = L10n.Get("S6DBAA3B83FC4") };
    private readonly CheckBox clearSecret = new() { Content = L10n.Get("ClearSavedSecret") };
    private readonly PasswordBox password = new() { PasswordRevealMode = PasswordRevealMode.Hidden };

    public bool IsRouteEnabled => enabled.IsChecked == true;

    public JumpEditor(SshJumpSettings? original, nint hwnd, List<SavedJumpPreset> presets, Action saveWorkspace, IEnumerable<SavedPrivateKey>? keys = null)
    {
        wallet = new KeyWalletPicker(keys ?? []);
        this.original = original;
        enabled.IsChecked = original is not null;
        host.Text = original?.Host ?? "";
        port.Text = (original?.Port ?? 22).ToString();
        user.Text = original?.Username ?? "";
        useKey.IsChecked = original?.UsesPrivateKey == true || original?.PrivateKey is not null;
        path.PlaceholderText = original?.PrivateKey is null ? L10n.Get("SED5B2AB22F6C") : L10n.Get("SEF7D5375BC4B");
        var select = new Button { Content = L10n.Get("S863ED953C34B") };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        select.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                if (await picker.PickSingleFileAsync() is { } file)
                    path.Text = file.Path;
                error.Text = "";
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };

        Spacing = 8;
        Children.Add(enabled);
        var endpoint = new Grid { ColumnSpacing = 8 };
        endpoint.ColumnDefinitions.Add(new ColumnDefinition());
        endpoint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        Grid.SetColumn(port, 1);
        endpoint.Children.Add(host);
        endpoint.Children.Add(port);
        var fields = new StackPanel { Spacing = 8, Children = { endpoint, user, useKey, wallet, path, select, password, clearSecret, error } };
        Children.Add(fields);
        var match = original is null ? null : presets.FirstOrDefault(x =>
            x.Settings.Host == original.Host && x.Settings.Port == original.Port && x.Settings.Username == original.Username &&
            x.Settings.Password == original.Password && x.Settings.UsesPrivateKey == original.UsesPrivateKey &&
            (x.Settings.PrivateKey ?? []).SequenceEqual(original.PrivateKey ?? []));
        var presetSelector = new PresetSelector<SavedJumpPreset>(presets, x => x.Id, x => x.Name,
            (id, name) => new(id, name, Read(true) ?? throw new ArgumentException(L10n.Get("S06FCB9826093"))),
            chosen =>
            {
                wallet.SelectedIndex = 0;
                this.original = chosen.Settings;
                host.Text = chosen.Settings.Host; port.Text = chosen.Settings.Port.ToString(); user.Text = chosen.Settings.Username;
                useKey.IsChecked = chosen.Settings.UsesPrivateKey || chosen.Settings.PrivateKey is not null;
                path.Text = ""; password.Password = "";
                path.PlaceholderText = chosen.Settings.PrivateKey is null ? L10n.Get("SED5B2AB22F6C") : L10n.Get("SEF7D5375BC4B");
                Update();
            }, saveWorkspace, match?.Id);
        fields.Children.Insert(0, presetSelector);
        fields.Children.Add(presetSelector.Editor);
        void Update()
        {
            fields.Visibility = enabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            wallet.Visibility = useKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            select.Visibility = path.Visibility = useKey.IsChecked == true && wallet.SelectedKey is null ? Visibility.Visible : Visibility.Collapsed;
            password.Header = useKey.IsChecked == true ? L10n.Get("S069B9FBB176C") : L10n.Get("SC7A9BF9BEEA1");
            password.PlaceholderText = !string.IsNullOrEmpty(FindSavedPassword(NormalizeHost(host.Text), ReadPort(), user.Text.Trim(), KeyPath))
                ? L10n.Get("SAADCE4DEC186") : "";
        }
        clearSecret.Checked += (_, _) => { password.Password = ""; Update(); };
        clearSecret.Unchecked += (_, _) => Update();
        wallet.SelectionChanged += (_, _) => { password.Password = ""; Update(); };
        enabled.Checked += (_, _) => Update();
        enabled.Unchecked += (_, _) => Update();
        useKey.Checked += (_, _) => Update();
        useKey.Unchecked += (_, _) => Update();
        host.TextChanged += (_, _) => Update();
        port.TextChanged += (_, _) => Update();
        user.TextChanged += (_, _) => Update();
        path.TextChanged += (_, _) => Update();
        Update();
    }

    public SshJumpSettings? Read(bool requireKey)
    {
        if (enabled.IsChecked != true)
            return null;

        var address = NormalizeHost(host.Text);
        var number = ReadPort();
        var username = user.Text.Trim();
        if ((Uri.CheckHostName(address) == UriHostNameType.Unknown && !IPAddress.TryParse(address, out _)) ||
            username.Length == 0 || number is < 1 or > 65535)
            throw new ArgumentException(L10n.Get("S06FCB9826093"));

        var privateKeyPath = KeyPath;
        byte[]? key = null;
        if (useKey.IsChecked == true)
        {
            if (wallet.SelectedKey is { } savedKey) key = savedKey.PrivateKey.ToArray();
            else if (privateKeyPath.Length == 0)
            {
                key = original?.PrivateKey?.ToArray();
                if (key is null && requireKey)
                    throw new ArgumentException(L10n.Get("S37B7C6E66C5D"));
            }
            else
                key = WorkspaceStore.ImportPrivateKey(privateKeyPath);
        }

        var passphrase = clearSecret.IsChecked == true ? "" : password.Password.Length > 0 ? password.Password : FindSavedPassword(address, number, username, privateKeyPath);
        if (key is not null)
        {
            try { PrivateKeyValidation.Validate(key, passphrase); }
            catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); throw; }
        }
        return new SshJumpSettings
        {
            Host = address, Port = number, Username = username, PrivateKey = key, UsesPrivateKey = useKey.IsChecked == true,
            Password = passphrase
        };
    }

    private string KeyPath => wallet.SelectedKey is null ? path.Text.Trim().Trim('"') : "";

    private int ReadPort() => int.TryParse(port.Text.Trim(), out var number) ? number : 0;

    private string? FindSavedPassword(string address, int number, string username, string keyPath)
    {
        if (clearSecret.IsChecked == true) return null;
        var usingKey = useKey.IsChecked == true;
        if (usingKey && wallet.SelectedKey is { } selected) return selected.Passphrase;
        if (original is null || !string.Equals(original.Host, address, StringComparison.OrdinalIgnoreCase) ||
            original.Port != number || original.Username != username ||
            usingKey != (original.UsesPrivateKey || original.PrivateKey is not null))
            return null;

        return usingKey && (keyPath.Length != 0 || original.PrivateKey is null) ? null : original.Password;
    }

    private static string NormalizeHost(string value)
    {
        var address = value.Trim();
        return address.StartsWith('[') && address.EndsWith(']') ? address[1..^1] : address;
    }
}



