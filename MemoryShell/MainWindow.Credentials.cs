using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private async Task<bool> VerifyMasterAsync(string action, string? notice = null)
    {
        if (database is not { } active)
            return false;

        var password = new PasswordBox { Header = L10n.Get("SE88ECA7923F1") };
        var error = ErrorText();
        var fields = new StackPanel { Spacing = 8, MinWidth = 345 };
        if (notice is not null)
            fields.Children.Add(new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(password);
        fields.Children.Add(error);
        var dialog = Dialog(L10n.Get("S373D2EFC2707"), fields, action);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            using var deferral = new DialogDeferral(args.GetDeferral());
            var supplied = password.Password;
            try { await Task.Run(() => active.VerifyPassphrase(supplied)); }
            catch (Exception ex)
            {
                args.Cancel = true;
                error.Text = ex is CryptographicException ? L10n.Get("SD6AF9C40979B") : ex.Message;
            }
            finally { password.Password = ""; }
        };
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && database == active; }
        finally { password.Password = ""; }
    }

    private async void ExportConnectionsExcel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (database is not { } active)
                return;
            if (store.Connections.Count == 0 && store.RemoteDesktops.Count == 0 && store.KeyWallet.Count == 0)
            {
                Status.Text = L10n.Get("S0CCDB445DAD5");
                return;
            }
            if (!await VerifyMasterAsync(L10n.Get("S876B9541F817"),
                L10n.Get("S3B1A27F22141")))
                return;

            var picker = new FileSavePicker { SuggestedFileName = L10n.Get("S9EB52B6EABA9") };
            picker.FileTypeChoices.Add(L10n.Get("S6C0A599B3315"), new List<string> { ".xlsx" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            if (await picker.PickSaveFileAsync() is not { } file || database != active)
                return;

            using var workbook = ConnectionWorkbook.Capture(store);
            await Task.Run(() => workbook.Save(file.Path));
            Status.Text = L10n.Format("S4F31B843EC3A", file.Name);
        }
        catch (Exception ex) { Status.Text = L10n.Format("SE070135D82CD", ex.Message); }
    }
}
