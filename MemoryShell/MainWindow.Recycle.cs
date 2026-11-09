using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private async void ShowDeletedConnections_Click(object sender, RoutedEventArgs e)
    {
        if (database is null)
            return;

        try
        {
            var entries = store.DeletedConnections.OrderByDescending(item => item.DeletedAt).ToArray();
            if (entries.Length == 0)
            {
                Status.Text = L10n.Get("S4DB5DCEF330A");
                return;
            }

            var list = new ListView
            {
                ItemsSource = entries.Select(item =>
                    $"{DeviceName(item.Connection.Profile)}  ·  {item.DeletedAt.LocalDateTime:yyyy-MM-dd HH:mm}").ToArray(),
                MinWidth = 350, MaxHeight = 380
            };
            list.SelectedIndex = 0;
            var dialog = Dialog(L10n.Get("S3E3ACE919506"), list, L10n.Get("SE0534B8A4E46"));
            dialog.SecondaryButtonText = L10n.Get("S4E01A4D26A03");
            list.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
                dialog.IsSecondaryButtonEnabled = list.SelectedIndex >= 0;
            };
            var result = await dialog.ShowAsync();
            if (list.SelectedIndex < 0 || result == ContentDialogResult.None)
                return;

            var entry = entries[list.SelectedIndex];
            var restore = result == ContentDialogResult.Primary;
            if (!restore && await Dialog(L10n.Get("SF32E10B41BA7"),
                L10n.Format("SD3F2E0DF2E95", DeviceName(entry.Connection.Profile)), L10n.Get("S4E01A4D26A03"))
                .ShowAsync() != ContentDialogResult.Primary)
                return;

            var index = store.DeletedConnections.IndexOf(entry);
            if (index < 0)
                return;
            store.DeletedConnections.RemoveAt(index);
            if (restore)
                store.Connections.Add(entry.Connection);
            try { SaveWorkspace(); }
            catch
            {
                if (restore)
                    store.Connections.Remove(entry.Connection);
                store.DeletedConnections.Insert(index, entry);
                throw;
            }

            if (!restore)
            {
                if (entry.Connection.PrivateKey is { } key)
                    CryptographicOperations.ZeroMemory(key);
                if (entry.Connection.Profile.Jump?.PrivateKey is { } jump)
                    CryptographicOperations.ZeroMemory(jump);
            }
            RefreshProfiles();
            RefreshRecentMenu();
            Status.Text = restore ? L10n.Get("S1A97AFFA40A8") : L10n.Get("S317575A0E278");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}

