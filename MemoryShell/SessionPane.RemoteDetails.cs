using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    public async Task ShowRemoteDetailsAsync(XamlRoot root)
    {
        if (session is not { } connected)
        {
            SetStatus(L10n.Get("S6F792CDE02C9"));
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var category = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 142 };
        foreach (var label in new[] { L10n.Get("S671B1E6796CA"), L10n.Get("S9140378734A8"), L10n.Get("S0CF08B6EA76C"), L10n.Get("S70A999F5B20F"), L10n.Get("S366ADA1D2FCF") })
            category.Items.Add(label);
        category.SelectedIndex = 0;
        var output = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"),
            MinHeight = 180, MaxHeight = Math.Clamp(root.Size.Height - 270, 180, 460), TextWrapping = TextWrapping.NoWrap
        };
        var refresh = new Button { Content = L10n.Get("SAEE887434131") };
        var width = Math.Clamp(root.Size.Width - 56, 360, 960);
        ScrollViewer.SetHorizontalScrollBarVisibility(output, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(output, ScrollBarVisibility.Auto);
        var layout = new Grid { Width = width - 54, ColumnSpacing = 14 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(category);
        var content = new StackPanel { Spacing = 8, Children = { refresh, output } };
        Grid.SetColumn(content, 1);
        layout.Children.Add(content);
        var dialog = Dialog(L10n.Get("S3C3A99753274"), layout, L10n.Get("S3FD47EDCE45B"));
        dialog.XamlRoot = root;
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(320, width);
        dialog.CloseButtonText = "";

        var loading = false;
        async Task LoadAsync()
        {
            if (loading)
                return;

            loading = true;
            category.IsEnabled = refresh.IsEnabled = false;
            output.Text = L10n.Get("S86B6D0D63062");
            try
            {
                var request = category.SelectedIndex switch
                {
                    1 => "LC_ALL=C ps -eo pid,ppid,user,stat,%cpu,%mem,rss,etime,comm --sort=-%cpu | head -n 201",
                    2 => "if command -v ss >/dev/null 2>&1; then LC_ALL=C ss -tunap | head -n 301; else printf 'ss is unavailable on this host.\n'; fi",
                    3 => "if command -v ip >/dev/null 2>&1; then LC_ALL=C ip -brief addr; LC_ALL=C ip route show; LC_ALL=C ip -6 route show; fi; head -n 40 /proc/net/dev 2>/dev/null",
                    4 => "if command -v journalctl >/dev/null 2>&1; then journalctl --no-pager -n 80 2>&1; elif [ -r /var/log/syslog ]; then tail -n 80 /var/log/syslog; elif [ -r /var/log/messages ]; then tail -n 80 /var/log/messages; else echo '当前账号没有可读的系统日志。'; fi",
                    _ => "uname -a; head -n 24 /etc/os-release 2>/dev/null; if command -v lscpu >/dev/null 2>&1; then LC_ALL=C lscpu | head -n 50; fi; LC_ALL=C df -hP | head -n 40"
                };
                var result = await connected.ExecuteAsync(request, lifetime.Token);
                if (!lifetime.IsCancellationRequested)
                    output.Text = MainWindow.MaskDeviceAddress(profile, result.Length > 80000 ? result[..80000] : result, store.HideDeviceAddress);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) output.Text = MainWindow.MaskDeviceAddress(profile, ex.Message, store.HideDeviceAddress); }
            finally
            {
                loading = false;
                if (!lifetime.IsCancellationRequested)
                    category.IsEnabled = refresh.IsEnabled = true;
            }
        }

        dialog.Opened += async (_, _) => await LoadAsync();
        category.SelectionChanged += async (_, _) => await LoadAsync();
        refresh.Click += async (_, _) => await LoadAsync();
        try { await dialog.ShowAsync(); }
        finally { lifetime.Cancel(); }
    }
}

