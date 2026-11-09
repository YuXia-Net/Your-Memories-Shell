using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MemoryShell;

internal sealed class CloudSettingsEditor : Grid
{
    private sealed record Draft(string Endpoint, string Username, string Password, string Bucket, string Region, string Prefix);
    private readonly Dictionary<CloudProtocol, Draft> drafts = [];
    private CloudProtocol previousProtocol;
    private readonly ComboBox protocol = new()
    {
        Header = L10n.Get("S80277B305462"),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox endpoint = new();
    private readonly TextBox username = new();
    private readonly PasswordBox password = new();

    internal void Clear() { password.Password = ""; drafts.Clear(); }
    private readonly TextBox bucket = new() { Header = L10n.Get("S144FCB8679ED") };
    private readonly TextBox region = new() { Header = L10n.Get("S3382870BC4AD"), PlaceholderText = "us-east-1" };
    private readonly TextBox prefix = new() { Header = L10n.Get("SDDAD9FBA57C2"), PlaceholderText = "memoryshell" };
    private readonly ComboBox policy = new() { Header = L10n.Get("BackupPolicy"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox interval = new() { Header = L10n.Get("S2B965126EA38") };
    private readonly CheckBox enabled = new() { Content = L10n.Get("SB47A0E597497") };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, Opacity = .85 };
    private readonly TextBlock warning = new()
    {
        Text = L10n.Get("SCA72CFDA62AE"),
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange)
    };
    private readonly TextBlock error = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        Visibility = Visibility.Collapsed
    };

    public CloudSettingsEditor(CloudSettings? current)
    {
        MaxHeight = 525;
        MinWidth = 365;
        protocol.Items.Add(new ComboBoxItem { Content = "WebDAV（HTTPS）", Tag = (int)CloudProtocol.WebDav });
        protocol.Items.Add(new ComboBoxItem { Content = L10n.Get("S7CC3448F1B28"), Tag = (int)CloudProtocol.Ftps });
        protocol.Items.Add(new ComboBoxItem { Content = "FTP", Tag = (int)CloudProtocol.Ftp });
        protocol.Items.Add(new ComboBoxItem { Content = L10n.Get("S1C53C2FE2454"), Tag = (int)CloudProtocol.S3 });

        policy.Items.Add(L10n.Get("BackupInterval"));
        policy.Items.Add(L10n.Get("BackupOnChange"));
        policy.SelectedIndex = (int)(current?.Policy ?? CloudBackupPolicy.Interval);
        void UpdateSchedule()
        {
            policy.IsEnabled = enabled.IsChecked == true;
            interval.Visibility = policy.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            interval.IsEnabled = enabled.IsChecked == true;
        }
        policy.SelectionChanged += (_, _) => UpdateSchedule();
        enabled.Checked += (_, _) => UpdateSchedule();
        enabled.Unchecked += (_, _) => UpdateSchedule();
        var fields = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(0, 0, 8, 2),
            Children = { protocol, description, warning, endpoint, bucket, region, prefix, username, password,
                new TextBlock { Text = L10n.Get("SF39866B907E7"), TextWrapping = TextWrapping.Wrap, Opacity = .75, Margin = new Thickness(0, 10, 0, 2) },
                enabled, policy, interval, error }
        };
        Children.Add(new ScrollViewer
        {
            Content = fields,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        protocol.SelectedItem = protocol.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, (int)(current?.Protocol ?? CloudProtocol.WebDav)))
            ?? protocol.Items[0];
        endpoint.Text = current?.Endpoint ?? "";
        username.Text = current?.Username ?? "";
        password.Password = current?.Password ?? "";
        bucket.Text = current?.Bucket ?? "";
        region.Text = current?.Region ?? "";
        prefix.Text = current?.Prefix ?? "";
        interval.Text = (current?.IntervalMinutes ?? 60).ToString(CultureInfo.InvariantCulture);
        enabled.IsChecked = current?.Enabled ?? false;
        UpdateSchedule();
        previousProtocol = SelectedProtocol;
        protocol.SelectionChanged += (_, _) =>
        {
            drafts[previousProtocol] = new(endpoint.Text, username.Text, password.Password, bucket.Text, region.Text, prefix.Text);
            var next = drafts.GetValueOrDefault(SelectedProtocol);
            endpoint.Text = next?.Endpoint ?? "";
            username.Text = next?.Username ?? "";
            password.Password = next?.Password ?? "";
            bucket.Text = next?.Bucket ?? "";
            region.Text = next?.Region ?? "";
            prefix.Text = next?.Prefix ?? "";
            UpdateSchedule();
            previousProtocol = SelectedProtocol;
            ShowError("");
            UpdateFields();
        };
        UpdateFields();
    }

    private CloudProtocol SelectedProtocol => protocol.SelectedItem is ComboBoxItem { Tag: int index }
        ? (CloudProtocol)index : CloudProtocol.WebDav;

    public CloudSettings Read()
    {
        var minutes = 60;
        if (policy.SelectedIndex == 0 && !int.TryParse(interval.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
            throw new ArgumentException(L10n.Get("S499FEABFEA0C"));
        var settings = new CloudSettings(SelectedProtocol, endpoint.Text.Trim(), username.Text.Trim(), password.Password,
            bucket.Text.Trim(), region.Text.Trim(), prefix.Text.Trim(), minutes, enabled.IsChecked == true, (CloudBackupPolicy)policy.SelectedIndex);
        CloudBackup.Validate(settings);
        return settings;
    }

    public void ShowError(string message)
    {
        error.Text = message;
        error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (error.Visibility == Visibility.Visible)
            error.StartBringIntoView();
    }

    private void UpdateFields()
    {
        var selected = SelectedProtocol;
        var s3 = selected == CloudProtocol.S3;
        var ftp = selected is CloudProtocol.Ftp or CloudProtocol.Ftps;
        var s3Fields = s3 ? Visibility.Visible : Visibility.Collapsed;
        bucket.Visibility = region.Visibility = prefix.Visibility = s3Fields;
        warning.Visibility = selected == CloudProtocol.Ftp ? Visibility.Visible : Visibility.Collapsed;
        endpoint.Header = s3 ? L10n.Get("S320321B228D1") : L10n.Get("S50A99C494E05");
        endpoint.PlaceholderText = s3 ? "https://s3.us-east-1.amazonaws.com" :
            ftp ? "ftp://storage.example.com/backups/" : "https://storage.example.com/webdav/backups/";
        username.Header = s3 ? L10n.Get("SAC0379C70118") : L10n.Get("SE32EB104CC6E");
        password.Header = s3 ? L10n.Get("S81123C56D5D8") : L10n.Get("S90AC421F550E");
        description.Text = s3
            ? L10n.Get("S61E06ADD4274")
            : L10n.Get("SF8E0251733E9");
    }
}
