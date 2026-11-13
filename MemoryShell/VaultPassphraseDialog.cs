using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal static class VaultPassphraseDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root, VaultDatabase database)
    {
        var current = new PasswordBox { Header = L10n.Get("S94D8B07A55B8") };
        var replacement = new PasswordBox { Header = L10n.Get("S669D85A6F7F4") };
        var confirmation = new PasswordBox { Header = L10n.Get("S59EED7B02BB2") };
        var hint = new TextBlock
        {
            Text = L10n.Get("S0D4D9941B38B"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = .75
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var fields = new StackPanel { Spacing = 9, MinWidth = 340, Children = { current, replacement, confirmation, hint, error } };
        var dialog = new QueuedContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (root.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = L10n.Get("SC172031D5348"),
            Content = fields,
            PrimaryButtonText = L10n.Get("S90C28258BC4E"),
            CloseButtonText = L10n.Get("S2CD0F3BE8738"),
            DefaultButton = ContentDialogButton.Primary
        };

        var saving = false;
        var changed = false;
        dialog.Closing += (_, args) =>
        {
            if (saving)
                args.Cancel = true;
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var oldPassphrase = current.Password;
            var newPassphrase = replacement.Password;
            if (string.IsNullOrEmpty(oldPassphrase) || string.IsNullOrEmpty(newPassphrase))
            {
                error.Text = L10n.Get("SF68B498325FB");
                args.Cancel = true;
                return;
            }

            if (!string.Equals(newPassphrase, confirmation.Password, StringComparison.Ordinal) ||
                string.Equals(newPassphrase, oldPassphrase, StringComparison.Ordinal))
            {
                error.Text = L10n.Get("S61834D82B08A");
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            saving = true;
            dialog.IsPrimaryButtonEnabled = current.IsEnabled = replacement.IsEnabled = confirmation.IsEnabled = false;
            error.Text = L10n.Get("S71D1EB4C0919");
            try
            {
                await Task.Run(() => database.ChangePassphrase(oldPassphrase, newPassphrase));
                changed = true;
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                error.Text = ex.Message;
            }
            finally
            {
                saving = false;
                dialog.IsPrimaryButtonEnabled = current.IsEnabled = replacement.IsEnabled = confirmation.IsEnabled = true;
                deferral.Complete();
            }
        };

        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && changed; }
        finally { current.Password = replacement.Password = confirmation.Password = ""; }
    }
}
