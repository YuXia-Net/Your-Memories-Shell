using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private async void KeyWallet_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowKeyWalletAsync(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async Task ShowKeyWalletAsync()
    {
        while (database is { } active && !closed)
        {
            string? action = null;
            SavedPrivateKey? chosen = null;
            var cards = new StackPanel { Spacing = 12, MinWidth = 350 };
            var dialog = Dialog(L10n.Get("KeyWallet"), new ScrollViewer { Content = cards, MaxHeight = 500 }, L10n.Get("KeyAdd"));
            dialog.CloseButtonText = L10n.Get("S3FD47EDCE45B");
            if (store.KeyWallet.Count == 0) cards.Children.Add(new TextBlock { Text = L10n.Get("KeyEmpty"), Margin = new Thickness(0, 20, 0, 20) });
            foreach (var key in store.KeyWallet.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var (id, label) in new[] { ("view", "KeyViewCopy"), ("rename", "KeyRename"), ("delete", "S2F9DAA828907") })
                {
                    var button = new Button { Content = L10n.Get(label) };
                    button.Click += (_, _) => { action = id; chosen = key; dialog.Hide(); };
                    actions.Children.Add(button);
                }
                cards.Children.Add(new Border
                {
                    Padding = new Thickness(18), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                    BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"),
                    Background = WorkspaceTheme.Brush("ShellCardBrush"),
                    Child = new StackPanel { Spacing = 14, Children = {
                        new FontIcon { Glyph = "\uE192", HorizontalAlignment = HorizontalAlignment.Left },
                        new TextBlock { Text = key.Name, FontSize = 20, TextWrapping = TextWrapping.Wrap }, actions } }
                });
            }
            var result = await dialog.ShowAsync();
            if (database != active || closed) return;
            if (result == ContentDialogResult.Primary) { await AddWalletKeyAsync(); continue; }
            if (chosen is null) return;
            if (action == "view")
            {
                if (!await VerifyMasterAsync(L10n.Get("KeyViewCopy"))) continue;
                var value = Encoding.UTF8.GetString(chosen.PrivateKey);
                var field = CredentialField.Create(L10n.Get("SE7D212076D9E"), value, true);
                var pass = CredentialField.Create(L10n.Get("S0550C993E3F9"), chosen.Passphrase ?? "", false);
                var panel = new StackPanel { Spacing = 10, MinWidth = 350, Children = { field, CredentialField.Copy(value, true), pass, CredentialField.Copy(chosen.Passphrase ?? "") } };
                var view = Dialog(chosen.Name, new ScrollViewer { Content = panel, MaxHeight = 480 }, "");
                try { await view.ShowAsync(); } finally { field.Text = pass.Text = ""; panel.Children.Clear(); }
            }
            else if (action == "rename")
            {
                var name = new TextBox { Header = L10n.Get("KeyName"), Text = chosen.Name };
                var error = ErrorText();
                var edit = Dialog(L10n.Get("KeyRename"), new StackPanel { Spacing = 8, Children = { name, error } }, L10n.Get("SA3030BF8F16D"));
                edit.PrimaryButtonClick += (_, args) =>
                {
                    try { ValidateWalletName(name.Text, chosen.Id); CommitWalletKey(chosen, chosen with { Name = name.Text.Trim() }); }
                    catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; }
                };
                await edit.ShowAsync();
            }
            else if (action == "delete")
            {
                if (await Dialog(L10n.Get("S2F9DAA828907"), L10n.Format("KeyDeleteHint", chosen.Name), L10n.Get("S2F9DAA828907")).ShowAsync() == ContentDialogResult.Primary)
                {
                    CommitWalletKey(chosen, null);
                    CryptographicOperations.ZeroMemory(chosen.PrivateKey);
                }
            }
        }
    }

    private void ValidateWalletName(string value, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(L10n.Get("KeyNameRequired"));
        if (store.KeyWallet.Any(k => k.Id != id && k.Name.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(L10n.Get("S5A8780CDDE5E"));
    }

    private void CommitWalletKey(SavedPrivateKey? previous, SavedPrivateKey? next)
    {
        var index = previous is null ? store.KeyWallet.Count : store.KeyWallet.IndexOf(previous);
        if (previous is null) store.KeyWallet.Add(next!);
        else if (next is null) store.KeyWallet.RemoveAt(index);
        else store.KeyWallet[index] = next;
        try { SaveWorkspace(); }
        catch
        {
            if (previous is null) store.KeyWallet.RemoveAt(index);
            else if (next is null) store.KeyWallet.Insert(index, previous);
            else store.KeyWallet[index] = previous;
            throw;
        }
    }

    private async Task AddWalletKeyAsync()
    {
        var name = new TextBox { Header = L10n.Get("KeyName") };
        var key = new TextBox { Header = L10n.Get("SE7D212076D9E"), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 180 };
        var password = new PasswordBox { Header = L10n.Get("S0550C993E3F9"), PasswordRevealMode = PasswordRevealMode.Hidden };
        var import = new Button { Content = L10n.Get("KeyImport") };
        var error = ErrorText();
        import.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                if (await picker.PickSingleFileAsync() is not { } file) return;
                var bytes = WorkspaceStore.ImportPrivateKey(file.Path);
                try { key.Text = new UTF8Encoding(false, true).GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(file.Name);
                error.Text = "";
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var fields = new StackPanel { Spacing = 10, MinWidth = 350, Children = { name, import, key, password, error } };
        var dialog = Dialog(L10n.Get("KeyAdd"), new ScrollViewer { Content = fields, MaxHeight = 500 }, L10n.Get("SA3030BF8F16D"));
        dialog.PrimaryButtonClick += (_, args) =>
        {
            byte[]? bytes = null;
            try
            {
                ValidateWalletName(name.Text);
                bytes = Encoding.UTF8.GetBytes(key.Text.Replace("\r\n", "\n").Replace('\r', '\n'));
                if (bytes.Length is 0 or > 1024 * 1024) throw new ArgumentException(L10n.Get("SC1AA9DD1DF4B"));
                PrivateKeyValidation.Validate(bytes, password.Password);
                CommitWalletKey(null, new SavedPrivateKey(Guid.NewGuid(), name.Text.Trim(), bytes, password.Password));
                bytes = null;
            }
            catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; }
            finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
        };
        try { await dialog.ShowAsync(); } finally { key.Text = password.Password = ""; }
    }
}
