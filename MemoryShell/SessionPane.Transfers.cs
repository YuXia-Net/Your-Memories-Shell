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
        var batch = new FileBatch();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await batch.Run(item.Name, () => UploadBatchItemAsync(client, item.Path, RemotePath(directory, item.Name), batch, ct));
        }
        await RefreshFilesAsync(directory);
        FinishBatch(batch);
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
        var selected = SelectedFiles;
        if (selected.Length == 0) return Task.CompletedTask;
        return FileActionAsync(async () =>
        {
            if (await ChooseSftpDownloadFolderAsync() is not { } target) return;
            if ((File.GetAttributes(target) & System.IO.FileAttributes.ReparsePoint) != 0)
                throw new IOException(L10n.Get("TransferLinkUnsupported"));
            await TransferAsync(async (client, ct) =>
            {
                var batch = new FileBatch();
                foreach (var entry in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    await batch.Run(entry.Name, () => DownloadBatchItemAsync(client, entry, Path.Combine(target, entry.Name), batch, ct));
                }
                FinishBatch(batch);
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
        catch (OperationCanceledException)
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
