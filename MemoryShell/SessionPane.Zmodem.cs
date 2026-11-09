using System.Threading.Channels;
using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private readonly SemaphoreSlim zmodemInput = new(1);
    private ZmodemTransfer? activeZmodem;
    private ContentDialog? zmodemDialog;
    private byte[] pendingZmodemPrefix = [];
    private long zmodemPrefixVersion;

    private bool IsZmodemBusy => Volatile.Read(ref activeZmodem) is not null;

    private void AttachZmodem()
    {
        terminal.InputReceived += text =>
        {
            if (text == "\u0003")
                Volatile.Read(ref activeZmodem)?.Cancel();
        };
    }

    private void CloseZmodem()
    {
        Volatile.Read(ref activeZmodem)?.Cancel();
        zmodemDialog?.Hide();
    }

    private async Task ReceiveSshBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await zmodemInput.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (Volatile.Read(ref activeZmodem) is { } active)
            {
                try
                {
                    await active.FeedAsync(bytes, ct).ConfigureAwait(false);
                    return;
                }
                catch (ChannelClosedException)
                {
                    // The protocol renders its trailing terminal output before accepting the next chunk.
                    await active.Completed.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            }

            Interlocked.Increment(ref zmodemPrefixVersion);
            if (pendingZmodemPrefix.Length > 0)
            {
                var combined = new byte[pendingZmodemPrefix.Length + bytes.Length];
                pendingZmodemPrefix.CopyTo(combined, 0);
                bytes.CopyTo(combined.AsMemory(pendingZmodemPrefix.Length));
                pendingZmodemPrefix = [];
                bytes = combined;
            }

            var marker = FindZmodemStart(bytes.Span);
            if (marker.PrefixAt < 0)
            {
                await terminal.WriteTerminalBytesAsync(bytes, ct).ConfigureAwait(false);
                return;
            }

            if (marker.PrefixAt > 0)
                await terminal.WriteTerminalBytesAsync(bytes[..marker.PrefixAt], ct).ConfigureAwait(false);
            if (marker.Header is not { } header)
            {
                pendingZmodemPrefix = bytes[marker.PrefixAt..].ToArray();
                _ = FlushZmodemPrefixAsync(Volatile.Read(ref zmodemPrefixVersion), ct);
                return;
            }

            if (session is not { } connected)
                return;
            var transfer = new ZmodemTransfer(connected.WriteTerminalBytesAsync, ct);
            Volatile.Write(ref activeZmodem, transfer);
            var tail = bytes[(marker.PrefixAt + marker.Length)..];
            if (!tail.IsEmpty)
                await transfer.FeedAsync(tail, ct).ConfigureAwait(false);
            _ = RunZmodemAsync(transfer, header, ct);
        }
        finally { zmodemInput.Release(); }
    }

    private static (int PrefixAt, ZHeader? Header, int Length) FindZmodemStart(ReadOnlySpan<byte> bytes)
    {
        for (var start = 0; start < bytes.Length; start++)
        {
            if (bytes[start] != ZmodemWire.Pad)
                continue;
            var state = ZmodemWire.ReadStart(bytes[start..], out var header, out var consumed);
            if (state == ZStartState.Incomplete)
                return (start, null, 0);
            if (state == ZStartState.Complete)
                return (start, header, consumed);
        }
        return (-1, null, 0);
    }

    private async Task FlushZmodemPrefixAsync(long version, CancellationToken ct)
    {
        try
        {
            // A typed asterisk can be an ordinary terminal character or a split handshake prefix.
            await Task.Delay(180, ct).ConfigureAwait(false);
            await zmodemInput.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (version != Volatile.Read(ref zmodemPrefixVersion) || pendingZmodemPrefix.Length == 0)
                    return;
                var prefix = pendingZmodemPrefix;
                pendingZmodemPrefix = [];
                await terminal.WriteTerminalBytesAsync(prefix, ct).ConfigureAwait(false);
            }
            finally { zmodemInput.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => { if (!disposed) SetStatus(ex.Message); });
        }
    }

    private async Task RunZmodemAsync(ZmodemTransfer transfer, ZHeader header, CancellationToken ct)
    {
        try
        {
            await transfer.RunAsync(header,
                token => InvokeZmodemUiAsync(ConfirmZmodemReceiveAsync, token),
                (offer, token) => InvokeZmodemUiAsync(t => SelectZmodemDestinationAsync(offer, t), token),
                token => InvokeZmodemUiAsync(SelectZmodemSourcesAsync, token),
                message => DispatcherQueue.TryEnqueue(() => { if (!disposed) SetStatus(message); }),
                terminal.WriteTerminalBytesAsync, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => { if (!disposed) SetStatus(ex.Message); });
        }
        finally
        {
            Interlocked.CompareExchange(ref activeZmodem, null, transfer);
            transfer.Completed.TrySetResult();
        }
    }

    private async Task<T> InvokeZmodemUiAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (disposed || XamlRoot is null)
                        throw new OperationCanceledException();
                    result.TrySetResult(await action(ct));
                }
                catch (OperationCanceledException) { result.TrySetCanceled(); }
                catch (Exception ex) { result.TrySetException(ex); }
            }))
            throw new OperationCanceledException();
        return await result.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private Task<bool> ConfirmZmodemReceiveAsync(CancellationToken ct) =>
        ShowZmodemDialogAsync(L10n.Get("SB11618715734"), L10n.Get("SB320AB042FB3"), L10n.Get("S7C9691192F1B"), ct);

    private async Task<string?> SelectZmodemDestinationAsync(ZmodemOffer offer, CancellationToken ct)
    {
        var size = offer.Size is uint bytes ? FormatSize(bytes) : L10n.Get("S4F8DD9AF425D");
        if (!await ShowZmodemDialogAsync(L10n.Get("SC4825C9C84FF"), $"{offer.Name}\n{size}", L10n.Get("S99A705BABC98"), ct))
            return null;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var selected = await picker.PickSingleFolderAsync();
        ct.ThrowIfCancellationRequested();
        if (selected is null)
            return null;
        var destination = Path.Combine(selected.Path, offer.Name);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            SetStatus(L10n.Format("S70A6B518CF04", offer.Name));
            return null;
        }
        return destination;
    }

    private async Task<IReadOnlyList<string>> SelectZmodemSourcesAsync(CancellationToken ct)
    {
        if (!await ShowZmodemDialogAsync(L10n.Get("S040325A072FB"), L10n.Get("S4F9F1BBCDD25"), L10n.Get("S822FB37DBA29"), ct))
            return [];
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        ct.ThrowIfCancellationRequested();
        if (files.Count > 128)
            throw new IOException(L10n.Get("S93FC22D65483"));
        return files.Select(file => file.Path).ToArray();
    }

    private async Task<bool> ShowZmodemDialogAsync(string title, string message, string primary, CancellationToken ct)
    {
        var dialog = Dialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, primary);
        zmodemDialog = dialog;
        using var close = ct.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(zmodemDialog, dialog))
                dialog.Hide();
        }));
        try
        {
            ct.ThrowIfCancellationRequested();
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            if (ReferenceEquals(zmodemDialog, dialog))
                zmodemDialog = null;
        }
    }
}

