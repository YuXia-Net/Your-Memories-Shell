using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private enum ConflictChoice { Skip, Replace, Merge, Rename }
    private sealed class FileBatch
    {
        public int Completed, Skipped;
        public List<string> Errors { get; } = [];
        public Dictionary<string, ConflictChoice> Choices { get; } = [];
        public async Task Run(string name, Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Errors.Add(name + ": " + ex.Message); }
        }
        public string Summary => L10n.Format("BatchSummary", Completed, Skipped, Errors.Count);
    }

    private async Task<ConflictChoice> ResolveConflictAsync(string name, bool sourceDirectory, bool targetDirectory, FileBatch batch)
    {
        var kind = $"{sourceDirectory}:{targetDirectory}";
        if (batch.Choices.TryGetValue(kind, out var remembered)) return remembered;
        var choices = new ExplicitComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        void Add(ConflictChoice choice, string label) => choices.Items.Add(new ComboBoxItem { Content = L10n.Get(label), Tag = choice });
        Add(ConflictChoice.Skip, "ConflictSkip"); Add(ConflictChoice.Rename, "ConflictRename");
        if (!sourceDirectory && !targetDirectory) Add(ConflictChoice.Replace, "ConflictReplace");
        if (sourceDirectory && targetDirectory) Add(ConflictChoice.Merge, "ConflictMerge");
        choices.SelectedIndex = 0;
        var applyAll = new CheckBox { Content = L10n.Get("ConflictApplyAll") };
        var dialog = Dialog(L10n.Get("ConflictTitle"), new StackPanel { Spacing = 12, Children = {
            new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, choices, applyAll } }, L10n.Get("ConflictContinue"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) throw new OperationCanceledException(cancellation);
        var selected = (ConflictChoice)((ComboBoxItem)choices.SelectedItem).Tag;
        if (applyAll.IsChecked == true) batch.Choices[kind] = selected;
        return selected;
    }

    private static string NumberedName(string name, bool directory, int number)
    {
        var extension = directory ? "" : Path.GetExtension(name);
        return name[..(name.Length - extension.Length)] + $" ({number})" + extension;
    }

    private async Task UploadBatchItemAsync(SshSession client, string local, string remote, FileBatch batch, CancellationToken ct, int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 64) throw new IOException(L10n.Get("TransferDepthExceeded"));
        var attributes = File.GetAttributes(local);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(L10n.Get("TransferLinkUnsupported"));
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var existing = await client.FindEntryAsync(remote, ct);
        var replace = false;
        if (existing is not null)
        {
            if (existing.IsSymbolicLink) throw new IOException(L10n.Get("TransferLinkUnsupported"));
            var choice = await ResolveConflictAsync(remote, isDirectory, existing.IsDirectory, batch);
            if (choice == ConflictChoice.Skip) { batch.Skipped++; return; }
            if (choice == ConflictChoice.Rename)
            {
                var parent = ParentPath(remote); var name = Path.GetFileName(local); var suffix = 2;
                do { remote = RemotePath(parent, NumberedName(name, isDirectory, suffix++)); }
                while (await client.FindEntryAsync(remote, ct) is not null);
                existing = null;
            }
            replace = choice == ConflictChoice.Replace;
        }
        if (!isDirectory)
        {
            await client.UploadAsync(local, remote, TransferProgress(L10n.Get("S9E07E3C0532D"), Path.GetFileName(local), new FileInfo(local).Length), ct, replace);
            batch.Completed++;
        }
        else
        {
            if (existing is null) await client.CreateDirectoryAsync(remote, ct);
            foreach (var child in Directory.EnumerateFileSystemEntries(local))
            {
                var target = RemotePath(remote, Path.GetFileName(child));
                await batch.Run(child, () => UploadBatchItemAsync(client, child, target, batch, ct, depth + 1));
            }
        }
    }

    private async Task DownloadBatchItemAsync(SshSession client, RemoteEntry entry, string destination, FileBatch batch, CancellationToken ct, int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 64) throw new IOException(L10n.Get("TransferDepthExceeded"));
        SshSession.ValidateLocalName(entry.Name);
        if (entry.IsSymbolicLink) throw new IOException(L10n.Get("TransferLinkUnsupported"));
        var replace = false;
        if (Path.Exists(destination))
        {
            var attributes = File.GetAttributes(destination);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(L10n.Get("TransferLinkUnsupported"));
            var choice = await ResolveConflictAsync(destination, entry.IsDirectory, Directory.Exists(destination), batch);
            if (choice == ConflictChoice.Skip) { batch.Skipped++; return; }
            if (choice == ConflictChoice.Rename)
            {
                var parent = Path.GetDirectoryName(destination)!; var suffix = 2;
                do { destination = Path.Combine(parent, NumberedName(entry.Name, entry.IsDirectory, suffix++)); }
                while (Path.Exists(destination));
            }
            replace = choice == ConflictChoice.Replace;
        }
        if (!entry.IsDirectory)
        {
            await client.DownloadAsync(entry.FullPath, destination, TransferProgress(L10n.Get("S4673A2306165"), entry.Name, entry.Size), ct, replace);
            batch.Completed++;
        }
        else
        {
            Directory.CreateDirectory(destination);
            foreach (var child in await client.ListDirectoryAsync(entry.FullPath, ct))
            {
                var target = Path.Combine(destination, child.Name);
                await batch.Run(child.FullPath, () => DownloadBatchItemAsync(client, child, target, batch, ct, depth + 1));
            }
        }
    }

    private void FinishBatch(FileBatch batch)
    {
        fileStatus.Text = batch.Summary;
        if (batch.Errors.Count > 0) throw new IOException(batch.Summary + "\n" + string.Join("\n", batch.Errors));
    }

    private RemoteEntry[] SelectedFiles => files.SelectedItems.OfType<RemoteFileRow>().Select(row => row.Entry).ToArray();
}
