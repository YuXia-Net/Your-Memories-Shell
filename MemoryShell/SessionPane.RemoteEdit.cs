using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private Task EditRemoteTextAsync()
    {
        if (SelectedFile is not { IsDirectory: false, IsSymbolicLink: false } entry)
        {
            fileStatus.Text = L10n.Get("S41138C2D7FE1");
            return Task.CompletedTask;
        }

        return FileActionAsync(async () =>
        {
            var connected = session;
            if (connected is null)
                return;

            var original = await connected.ReadTextFileAsync(entry, cancellation);
            if (disposed || !ReferenceEquals(session, connected))
                return;

            var fileName = new TextBlock { Text = entry.FullPath, TextWrapping = TextWrapping.Wrap, Opacity = .75 };
            var input = new TextBox
            {
                Text = original.Text,
                AcceptsReturn = true,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                MinHeight = 240,
                MaxHeight = 380
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
            var initialText = NormalizeLineEndings(input.Text);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 8, Children = { fileName, input, error } };
            var dialog = Dialog(L10n.Get("S164E105A8017"), panel, L10n.Get("S52D6949C89A3"));
            var saved = false;
            WireRemoteDialog(dialog, error, async () =>
            {
                var edited = NormalizeLineEndings(input.Text);
                if (edited == initialText)
                    return;

                await connected.SaveTextFileAsync(original, RestoreLineEndings(edited, original.Text), cancellation);
                saved = true;
            });
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && saved && !disposed &&
                ReferenceEquals(session, connected))
            {
                await RefreshFilesAsync(directory);
                fileStatus.Text = L10n.Format("S478C2DF2AA75", entry.Name);
            }
        });
    }

    private Task ChangeRemotePermissionsAsync()
    {
        if (SelectedFile is not { IsSymbolicLink: false } entry || entry.UnixMode is null)
        {
            fileStatus.Text = L10n.Get("S72C35F624FEB");
            return Task.CompletedTask;
        }

        return FileActionAsync(async () =>
        {
            var connected = session;
            if (connected is null)
                return;

            var name = new TextBlock { Text = entry.FullPath, TextWrapping = TextWrapping.Wrap, Opacity = .75 };
            var input = new TextBox { Header = L10n.Get("SB77599AAEFB2"), Text = FormatRemotePermissions(entry.UnixMode), MaxLength = 4 };
            var hint = new TextBlock
            {
                Text = L10n.Get("SE3E13C61822F"),
                TextWrapping = TextWrapping.Wrap,
                Opacity = .75
            };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 8, Children = { name, input, hint, error } };
            var dialog = Dialog(L10n.Get("S20699BA62508"), panel, L10n.Get("S63C73C4730F4"));
            var saved = false;
            WireRemoteDialog(dialog, error, async () =>
            {
                var text = input.Text.Trim();
                if (text.Length is not (3 or 4) || text.Any(character => character is < '0' or > '7'))
                    throw new InvalidOperationException(L10n.Get("S250259EBD05F"));

                var mode = Convert.ToInt32(text, 8);
                if (mode == (entry.UnixMode.Value & 0xFFF))
                    return;
                await connected.ChangePermissionsAsync(entry, mode, cancellation);
                saved = true;
            });
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && saved && !disposed &&
                ReferenceEquals(session, connected))
            {
                await RefreshFilesAsync(directory);
                fileStatus.Text = L10n.Format("S8B109F17E018", entry.Name);
            }
        });
    }

    private static void WireRemoteDialog(ContentDialog dialog, TextBlock error, Func<Task> save)
    {
        var saving = false;
        dialog.Closing += (_, args) => args.Cancel = saving;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            saving = true;
            dialog.IsPrimaryButtonEnabled = false;
            error.Text = "";
            try { await save(); }
            catch (Exception ex)
            {
                args.Cancel = true;
                error.Text = ex.Message;
            }
            finally
            {
                saving = false;
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
    }

    private static string FormatRemotePermissions(int? unixMode) => unixMode is int value
        ? Convert.ToString(value & 0xFFF, 8).PadLeft(4, '0')
        : "—";

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string RestoreLineEndings(string text, string original)
    {
        if (original.Contains("\r\n", StringComparison.Ordinal))
            return text.Replace("\n", "\r\n");
        if (original.Contains('\r') && !original.Contains('\n'))
            return text.Replace('\n', '\r');
        return text;
    }
}
