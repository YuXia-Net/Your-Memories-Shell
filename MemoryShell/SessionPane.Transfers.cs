using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private readonly Button cancelTransfer = new() { Content = L10n.Get("SCE29BE0644D0"), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? transferCancellation;

    private Task UploadAsync() => FileActionAsync(async () =>
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var picked = await picker.PickMultipleFilesAsync();
        if (picked.Count > 0)
            await TransferAsync((client, ct) => UploadItemsAsync(client, picked, ct));
    });

    private Task UploadFolderAsync() => FileActionAsync(async () =>
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        if (await picker.PickSingleFolderAsync() is { } folder)
            await TransferAsync((client, ct) => UploadItemsAsync(client, new[] { folder }, ct));
    });

    private Task UploadDroppedAsync(IEnumerable<IStorageItem> picked) =>
        FileActionAsync(() => TransferAsync((client, ct) => UploadItemsAsync(client, picked, ct)));

    private async Task UploadItemsAsync(SshSession client, IEnumerable<IStorageItem> items, CancellationToken ct)
    {
        var transferred = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            switch (item)
            {
                case StorageFile file:
                    await client.UploadAsync(file.Path, RemotePath(directory, file.Name), TransferProgress(L10n.Get("S9E07E3C0532D"), file.Name, new FileInfo(file.Path).Length), ct);
                    break;
                case StorageFolder folder:
                    await client.UploadDirectoryAsync(folder.Path, RemotePath(directory, folder.Name), FolderProgress(L10n.Get("S9E07E3C0532D"), folder.Name), ct);
                    break;
                default:
                    continue;
            }
            transferred++;
        }

        await RefreshFilesAsync(directory);
        fileStatus.Text = L10n.Format("S7FDC2248D3C6", transferred);
    }

    private Task DownloadResumableAsync()
    {
        if (SelectedFile is not { IsDirectory: false, IsSymbolicLink: false } entry)
        {
            fileStatus.Text = L10n.Get("S6088C8A46B08");
            return Task.CompletedTask;
        }

        return FileActionAsync(async () =>
        {
            if (await ChooseSftpDownloadFolderAsync() is not { } folder)
                return;
            if (entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Name is "." or "..")
                throw new IOException(L10n.Get("S1498E8A5C0E6"));

            await TransferAsync(async (client, ct) =>
            {
                await client.DownloadResumableAsync(entry, Path.Combine(folder, entry.Name),
                    TransferProgress(L10n.Get("SD31450598CFE"), entry.Name, entry.Size), ct);
                fileStatus.Text = L10n.Format("S9F570D2D8F99", entry.Name);
            }, L10n.Get("S28E526C953C4"));
        });
    }

    private Task DownloadAsync()
    {
        if (SelectedFile is not { IsSymbolicLink: false } entry)
            return Task.CompletedTask;

        return FileActionAsync(async () =>
        {
            if (await ChooseSftpDownloadFolderAsync() is not { } target)
                return;
            if (entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Name.EndsWith('.') || entry.Name.EndsWith(' '))
                throw new IOException(L10n.Get("S81BE6FAD646E"));
            var destination = Path.Combine(target, entry.Name);
            if (!entry.IsDirectory && File.Exists(destination) &&
                !await ConfirmAsync(L10n.Get("SE4B715A60C85"), L10n.Format("SD426CD37066C", entry.Name), L10n.Get("S4CE4C98EB27E")))
                return;

            await TransferAsync(async (client, ct) =>
            {
                if (entry.IsDirectory)
                    await client.DownloadDirectoryAsync(entry, destination, FolderProgress(L10n.Get("S4673A2306165"), entry.Name), ct);
                else
                    await client.DownloadAsync(entry.FullPath, destination, TransferProgress(L10n.Get("S4673A2306165"), entry.Name, entry.Size), ct);
                fileStatus.Text = L10n.Format("S9F570D2D8F99", entry.Name);
            });
        });
    }

    private async Task<string?> ChooseSftpDownloadFolderAsync()
    {
        if (filePreferences.DownloadFolder is { } configured && Directory.Exists(configured))
            return configured;

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async Task TransferAsync(Func<SshSession, CancellationToken, Task> transfer, string? cancelled = null)
    {
        if (session is not { } client)
            return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        transferCancellation = operation;
        try
        {
            BeginTransferDisplay();
            await transfer(client, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            FinishTransferDisplay(true, L10n.Get("S305EEF7A84A9"));
            if (!disposed) _ = TransferNotifications.ShowCompletedAsync();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!disposed)
                fileStatus.Text = cancelled ?? L10n.Get("SE9C7910BBF8D");
            FinishTransferDisplay(false, L10n.Get("SE9C7910BBF8D"));
        }
        catch (Exception ex)
        {
            if (!disposed)
                fileStatus.Text = ex.Message;
            FinishTransferDisplay(false, L10n.Get("SE7FA8BF6D52C"));
        }
        finally
        {
            transferCancellation = null;
            ActivityChanged?.Invoke();
        }
    }

    private IProgress<long> TransferProgress(string action, string name, long? total = null)
    {
        var version = SetTransferItem(action, name, total);
        return CreateTransferReporter<long>(bytes => ShowTransferProgress(version, bytes, total));
    }

    private IProgress<DirectoryTransferProgress> FolderProgress(string action, string name)
    {
        var version = SetTransferItem(action, name, null);
        return CreateTransferReporter<DirectoryTransferProgress>(progress =>
            ShowTransferProgress(version, progress.BytesTransferred, null, progress.FilesTransferred));
    }

    private IProgress<T> CreateTransferReporter<T>(Action<T> callback)
    {
        var queue = DispatcherQueue;
        return new TransferReporter<T>(value => queue.TryEnqueue(() => callback(value)));
    }

    private sealed class TransferReporter<T>(Action<T> update) : IProgress<T>
    {
        private long previous;

        public void Report(T value)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var last = Volatile.Read(ref previous);
            if ((last == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds >= 100) &&
                Interlocked.CompareExchange(ref previous, now, last) == last)
                update(value);
        }
    }
}
