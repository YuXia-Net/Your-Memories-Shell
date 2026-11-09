using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private ListView transferHistory = null!;
    private readonly TextBlock currentTransferText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock transferEmpty = new() { Text = L10n.Get("S990F283D1B1D"), Opacity = .65, Margin = new Thickness(8) };
    private readonly List<string> transferItemNames = [];
    private int transferItemCount;
    private bool transferHistoryBuilt;

    private UIElement BuildTransferHistory()
    {
        transferHistory = new ListView { SelectionMode = ListViewSelectionMode.Single };
        transferHistoryBuilt = true;
        var panel = new Grid { Padding = new Thickness(8), RowSpacing = 6 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(currentTransferText);
        var cancel = Button(L10n.Get("SA988D3B8F402"), () => { transferCancellation?.Cancel(); return Task.CompletedTask; });
        SetColumn(cancel, 1);
        toolbar.Children.Add(cancel);
        currentTransferText.Text = transferCancellation is null ? L10n.Get("SC9534F6B7932") : transferTitle.Text + "  " + transferDetail.Text;
        SetRow(transferHistory, 1);
        SetRow(transferEmpty, 1);
        panel.Children.Add(toolbar);
        panel.Children.Add(transferHistory);
        panel.Children.Add(transferEmpty);
        RefreshTransferHistory();
        return panel;
    }

    private void RefreshTransferHistory()
    {
        if (!transferHistoryBuilt) return;
        transferHistory.Items.Clear();
        foreach (var item in store.Transfers.Where(item => item.ProfileId == profile.Id).Reverse().Take(100))
        {
            var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(4, 4, 4, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
            row.Children.Add(new TextBlock { Text = item.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss"), FontSize = 12, Opacity = .7 });
            var name = new TextBlock { Text = item.Items, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(name, item.Detail);
            SetColumn(name, 1);
            row.Children.Add(name);
            var result = new TextBlock { Text = item.Result, FontSize = 12 };
            SetColumn(result, 2);
            row.Children.Add(result);
            transferHistory.Items.Add(new ListViewItem { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        transferEmpty.Visibility = transferHistory.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveTransferRecord(string result)
    {
        var items = transferItemNames.Count == 0 ? L10n.Get("SF3C32E652F65") : string.Join("、", transferItemNames);
        if (transferItemCount > transferItemNames.Count) items += L10n.Format("S6D0705C4DBB0", transferItemCount);
        var entry = new TransferEntry(profile.Id, DateTimeOffset.Now, items, result, fileStatus.Text);
        var records = store.Transfers;
        records.Add(entry);
        var oldest = records.Count > 200 ? records[0] : null;
        if (oldest is not null) records.RemoveAt(0);
        try { saveWorkspace(); }
        catch (Exception ex)
        {
            records.Remove(entry);
            if (oldest is not null) records.Insert(0, oldest);
            SetStatus(L10n.Format("S7310FB94F9C4", ex.Message));
        }
        currentTransferText.Text = result + " · " + items;
        RefreshTransferHistory();
    }
}
