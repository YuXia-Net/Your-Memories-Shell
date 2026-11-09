using System.Net;
using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MemoryShell;

internal sealed class JumpEditor : StackPanel
{
    private SshJumpSettings? original;
    private readonly CheckBox enabled = new() { Content = L10n.Get("S151398B54CD6") };
    private readonly TextBox host = new() { Header = L10n.Get("S5E20B52F38F4") };
    private readonly TextBox port = new() { Header = L10n.Get("SE71AC32B544B") };
    private readonly TextBox user = new() { Header = L10n.Get("S01B6FCF6A966") };
    private readonly CheckBox useKey = new() { Content = L10n.Get("S1F6B392A4ED6") };
    private readonly TextBox path = new() { Header = L10n.Get("S6DBAA3B83FC4") };
    private readonly PasswordBox password = new() { PasswordRevealMode = PasswordRevealMode.Hidden };

    public bool IsRouteEnabled => enabled.IsChecked == true;

    public JumpEditor(SshJumpSettings? original, nint hwnd, List<SavedJumpPreset> presets, Action saveWorkspace)
    {
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
        var fields = new StackPanel { Spacing = 8, Children = { endpoint, user, useKey, path, select, password, error } };
        Children.Add(fields);
        var presetPicker = new ComboBox { Header = L10n.Get("JumpPreset"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var presetName = new TextBox { Header = L10n.Get("S5FE110AA6D1D") };
        var savePreset = new Button { Content = L10n.Get("SavePresetApply") };
        var removePreset = new Button { Content = L10n.Get("S2F9DAA828907"), IsEnabled = false };
        var selectionChanging = false;
        void Refill(Guid? selected)
        {
            selectionChanging = true;
            presetPicker.Items.Clear();
            presetPicker.Items.Add(new ComboBoxItem { Content = L10n.Get("S6E382D9250F8") });
            foreach (var item in presets) presetPicker.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Id });
            presetPicker.SelectedItem = presetPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected)) ?? presetPicker.Items[0];
            selectionChanging = false;
        }
        fields.Children.Insert(0, presetPicker);
        fields.Children.Add(presetName);
        fields.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { savePreset, removePreset } });
        presetPicker.SelectionChanged += (_, _) =>
        {
            if (selectionChanging) return;
            var chosen = presetPicker.SelectedItem is ComboBoxItem { Tag: Guid id } ? presets.Find(item => item.Id == id) : null;
            removePreset.IsEnabled = chosen is not null;
            presetName.Text = chosen?.Name ?? "";
            if (chosen is null) return;
            this.original = chosen.Settings;
            host.Text = chosen.Settings.Host; port.Text = chosen.Settings.Port.ToString(); user.Text = chosen.Settings.Username;
            useKey.IsChecked = chosen.Settings.UsesPrivateKey || chosen.Settings.PrivateKey is not null;
            path.Text = ""; password.Password = "";
            path.PlaceholderText = chosen.Settings.PrivateKey is null ? L10n.Get("SED5B2AB22F6C") : L10n.Get("SEF7D5375BC4B");
            Update();
        };
        savePreset.Click += (_, _) =>
        {
            try
            {
                var name = presetName.Text.Trim();
                if (name.Length == 0) throw new ArgumentException(L10n.Get("SCAB9417058BF"));
                var selectedId = (presetPicker.SelectedItem as ComboBoxItem)?.Tag as Guid?;
                if (presets.Any(item => item.Id != selectedId && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(L10n.Get("S5A8780CDDE5E"));
                var settings = Read(true) ?? throw new ArgumentException(L10n.Get("S06FCB9826093"));
                var previous = presets.Find(item => item.Id == selectedId);
                var next = new SavedJumpPreset(previous?.Id ?? Guid.NewGuid(), name, settings with { PrivateKey = settings.PrivateKey?.ToArray() });
                var index = previous is null ? presets.Count : presets.IndexOf(previous);
                if (previous is null) presets.Add(next); else presets[index] = next;
                try { saveWorkspace(); }
                catch { if (previous is null) presets.Remove(next); else presets[index] = previous; throw; }
                this.original = next.Settings; path.Text = ""; password.Password = "";
                Refill(next.Id); removePreset.IsEnabled = true; error.Text = ""; Update();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        removePreset.Click += (_, _) =>
        {
            if (presetPicker.SelectedItem is not ComboBoxItem { Tag: Guid id } || presets.Find(item => item.Id == id) is not { } chosen) return;
            var index = presets.IndexOf(chosen); presets.RemoveAt(index);
            try { saveWorkspace(); Refill(null); presetName.Text = ""; removePreset.IsEnabled = false; error.Text = ""; }
            catch (Exception ex) { presets.Insert(index, chosen); error.Text = ex.Message; }
        };
        Refill(null);
        void Update()
        {
            fields.Visibility = enabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            select.Visibility = path.Visibility = useKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            password.Header = useKey.IsChecked == true ? L10n.Get("S069B9FBB176C") : L10n.Get("SC7A9BF9BEEA1");
            password.PlaceholderText = !string.IsNullOrEmpty(FindSavedPassword(NormalizeHost(host.Text), ReadPort(), user.Text.Trim(), KeyPath))
                ? L10n.Get("SAADCE4DEC186") : "";
        }
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
            if (privateKeyPath.Length == 0)
            {
                key = original?.PrivateKey?.ToArray();
                if (key is null && requireKey)
                    throw new ArgumentException(L10n.Get("S37B7C6E66C5D"));
            }
            else
                key = WorkspaceStore.ImportPrivateKey(privateKeyPath);
        }

        return new SshJumpSettings
        {
            Host = address, Port = number, Username = username, PrivateKey = key, UsesPrivateKey = useKey.IsChecked == true,
            Password = password.Password.Length > 0 ? password.Password : FindSavedPassword(address, number, username, privateKeyPath)
        };
    }

    private string KeyPath => path.Text.Trim().Trim('"');

    private int ReadPort() => int.TryParse(port.Text.Trim(), out var number) ? number : 0;

    private string? FindSavedPassword(string address, int number, string username, string keyPath)
    {
        var usingKey = useKey.IsChecked == true;
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



