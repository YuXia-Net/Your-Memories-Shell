namespace MemoryShell;

internal sealed partial class SessionPane
{
    private Task DownloadArchiveAsync()
    {
        if (SelectedFile is not { IsDirectory: true, IsSymbolicLink: false } entry)
        {
            fileStatus.Text = L10n.Get("S8D659ED7C2C6");
            return Task.CompletedTask;
        }

        return FileActionAsync(async () =>
        {
            if (!await ConfirmAsync(L10n.Get("SA6F914426720"), L10n.Get("S71876E0FFD92"), L10n.Get("S7C9691192F1B")))
                return;
            if (await ChooseSftpDownloadFolderAsync() is not { } folder)
                return;

            var name = entry.Name + ".tar.gz";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException(L10n.Get("SB01A951D5325"));
            await TransferAsync(async (client, ct) =>
            {
                await client.DownloadDirectoryArchiveAsync(entry, Path.Combine(folder, name),
                    TransferProgress(L10n.Get("SA6F914426720"), name), ct);
                fileStatus.Text = L10n.Format("S9F570D2D8F99", name);
            });
        });
    }
}
