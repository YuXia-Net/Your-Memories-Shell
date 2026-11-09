using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace MemoryShell.Services;

internal sealed record ZmodemOffer(string Name, uint? Size);

internal sealed class ZmodemTransfer
{
    private const int RetryLimit = 8;
    private const int SendBlockSize = 1024;
    private const int SendWindow = 128 * 1024;
    private const uint ReceiveCapabilities = (0x01u | 0x02u | 0x20u) << 24;
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly CancellationTokenSource cancellation;
    private readonly ZmodemWire wire;

    public ZmodemTransfer(Func<ReadOnlyMemory<byte>, CancellationToken, Task> send, CancellationToken lifetime)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        wire = new ZmodemWire(input.Reader, send);
    }

    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public ValueTask FeedAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        input.Writer.WriteAsync(bytes.ToArray(), ct);

    public async Task RunAsync(ZHeader start,
        Func<CancellationToken, Task<bool>> confirmReceive,
        Func<ZmodemOffer, CancellationToken, Task<string?>> chooseDestination,
        Func<CancellationToken, Task<IReadOnlyList<string>>> chooseSources,
        Action<string> status,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> terminal,
        CancellationToken lifetime)
    {
        var cleanFinish = false;
        var ct = cancellation.Token;
        try
        {
            if (start.Frame == ZFrame.RequestInit)
            {
                if (!await confirmReceive(ct).ConfigureAwait(false))
                    throw new OperationCanceledException();
                await ReceiveAsync(chooseDestination, status, ct).ConfigureAwait(false);
            }
            else
            {
                var sources = await chooseSources(ct).ConfigureAwait(false);
                if (sources.Count == 0)
                    throw new OperationCanceledException();
                await SendAsync(start, sources, status, ct).ConfigureAwait(false);
            }
            cleanFinish = true;
            status(L10n.Get("SEB1E731EA63F"));
        }
        catch (OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                await AbortWireAsync(lifetime).ConfigureAwait(false);
                status(L10n.Get("S13F313DCFB65"));
            }
        }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested)
            {
                await AbortWireAsync(lifetime).ConfigureAwait(false);
                status($"ZMODEM · {ex.Message}");
            }
        }
        finally
        {
            input.Writer.TryComplete();
            try
            {
                if (cleanFinish && !lifetime.IsCancellationRequested)
                    foreach (var tail in wire.Remaining())
                        await terminal(tail, lifetime).ConfigureAwait(false);
            }
            finally { cancellation.Dispose(); }
        }
    }

    private async Task AbortWireAsync(CancellationToken lifetime)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        limit.CancelAfter(TimeSpan.FromSeconds(2));
        try { await wire.SendCancelAsync(limit.Token).ConfigureAwait(false); }
        catch (Exception) { /* The session may have disconnected already. */ }
    }

    private async Task ReceiveAsync(Func<ZmodemOffer, CancellationToken, Task<string?>> chooseDestination,
        Action<string> status, CancellationToken ct)
    {
        var packetBuffer = new byte[65541];
        PendingDownload? file = null;
        var failures = 0;
        uint? lastCompleted = null;
        var nextProgress = Stopwatch.GetTimestamp();
        await wire.SendHexAsync(ZFrame.ReceiveInit, ReceiveCapabilities, ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                try
                {
                    var header = await TimedHeaderAsync(ct).ConfigureAwait(false);
                    switch (header.Frame)
                    {
                        case ZFrame.RequestInit when file is null:
                            await wire.SendHexAsync(ZFrame.ReceiveInit, ReceiveCapabilities, ct).ConfigureAwait(false);
                            break;
                        case ZFrame.SenderInit when file is null:
                            var initialization = await TimedPacketAsync(packetBuffer, header.Crc32, ct).ConfigureAwait(false);
                            ValidatePacket(initialization, ZmodemWire.Wait);
                            if ((header.Flags & 0x80) != 0)
                                throw new IOException(L10n.Get("S4F835F76D3FE"));
                            await wire.SendHexAsync(ZFrame.Ack, 0, ct).ConfigureAwait(false);
                            break;
                        case ZFrame.File when file is null:
                            var metadata = await TimedPacketAsync(packetBuffer, header.Crc32, ct).ConfigureAwait(false);
                            ValidatePacket(metadata, ZmodemWire.Wait);
                            var offer = ParseOffer(metadata, header);
                            var destination = offer is null ? null : await chooseDestination(offer, ct).ConfigureAwait(false);
                            if (destination is null)
                            {
                                await wire.SendHexAsync(ZFrame.Skip, 0, ct).ConfigureAwait(false);
                                break;
                            }
                            file = new PendingDownload(destination, offer!.Size);
                            status(L10n.Format("SC30F261AD33C", offer.Name));
                            await wire.SendHexAsync(ZFrame.Position, 0, ct).ConfigureAwait(false);
                            break;
                        case ZFrame.File when file is not null:
                            // Retry the initial receive offset.
                            ValidatePacket(await TimedPacketAsync(packetBuffer, header.Crc32, ct).ConfigureAwait(false), ZmodemWire.Wait);
                            await wire.SendHexAsync(ZFrame.Position, file.Position, ct).ConfigureAwait(false);
                            break;
                        case ZFrame.Data when file is not null:
                            if (header.Value != file.Position)
                                throw new InvalidDataException(L10n.Get("S6B07C5469B9E"));
                            await ReceiveDataAsync(file, packetBuffer, header.Crc32, ReportReceiveProgress, ct).ConfigureAwait(false);
                            break;
                        case ZFrame.Eof when file is not null:
                            if (header.Value != file.Position)
                                throw new InvalidDataException(L10n.Get("SCC9A3367DF94"));
                            file.Commit();
                            status(L10n.Format("S15DEE4F90702", Path.GetFileName(file.Destination)));
                            lastCompleted = file.Position;
                            file.Dispose();
                            file = null;
                            await wire.SendHexAsync(ZFrame.ReceiveInit, ReceiveCapabilities, ct).ConfigureAwait(false);
                            break;
                        case ZFrame.Eof when lastCompleted == header.Value:
                            await wire.SendHexAsync(ZFrame.ReceiveInit, ReceiveCapabilities, ct).ConfigureAwait(false);
                            break;
                        case ZFrame.Finish when file is null:
                            await wire.SendHexAsync(ZFrame.Finish, 0, ct).ConfigureAwait(false);
                            await TimedFinishMarkerAsync(ct).ConfigureAwait(false);
                            return;
                        case ZFrame.Abort or ZFrame.Cancel or ZFrame.FileError:
                            throw new OperationCanceledException(L10n.Get("S160FF2E8F905"));
                        case ZFrame.Challenge:
                            await wire.SendHexAsync(ZFrame.Ack, header.Value, ct).ConfigureAwait(false);
                            break;
                        case ZFrame.FreeCount:
                            await wire.SendHexAsync(ZFrame.Ack, 0, ct).ConfigureAwait(false);
                            break;
                        default:
                            throw new InvalidDataException(L10n.Get("S0D0FAC42BF4E"));
                    }
                    failures = 0;
                }
                catch (Exception ex) when ((ex is InvalidDataException or TimeoutException) && ++failures <= RetryLimit)
                {
                    // Advance only after CRC verification.
                    if (file is null)
                        await wire.SendHexAsync(ZFrame.Nak, 0, ct).ConfigureAwait(false);
                    else
                        await wire.SendHexAsync(ZFrame.Position, file.Position, ct).ConfigureAwait(false);
                }
            }
        }
        finally { file?.Dispose(); }

        void ReportReceiveProgress(PendingDownload current)
        {
            if (Stopwatch.GetElapsedTime(nextProgress) < TimeSpan.FromMilliseconds(400))
                return;
            nextProgress = Stopwatch.GetTimestamp();
            status(L10n.Format("SB1A079D05D17", Path.GetFileName(current.Destination), FormatProgress(current.Position, current.Expected)));
        }
    }

    private async Task ReceiveDataAsync(PendingDownload file, byte[] buffer, bool crc32,
        Action<PendingDownload> progress, CancellationToken ct)
    {
        while (true)
        {
            var packet = await TimedPacketAsync(buffer, crc32, ct).ConfigureAwait(false);
            ValidatePacket(packet);
            await file.AppendAsync(packet.Buffer.AsMemory(0, packet.Length), ct).ConfigureAwait(false);
            progress(file);
            if (packet.Ending is ZmodemWire.Query or ZmodemWire.Wait)
                await wire.SendHexAsync(ZFrame.Ack, file.Position, ct).ConfigureAwait(false);
            if (packet.Ending is ZmodemWire.End or ZmodemWire.Wait)
                return;
        }
    }

    private static void ValidatePacket(ZPacket packet, byte? ending = null)
    {
        if (!packet.Valid || (ending.HasValue && packet.Ending != ending.Value))
            throw new InvalidDataException(L10n.Get("S612107E7AFBC"));
    }

    private static ZmodemOffer? ParseOffer(ZPacket packet, ZHeader header)
    {
        // Accept binary files without transfer-time conversion.
        if ((header.Value & 0x0000ffff) != 0 || header.Flags > 3 || header.Flags == 2)
            return null;
        var data = packet.Buffer.AsSpan(0, packet.Length);
        var separator = data.IndexOf((byte)0);
        if (separator <= 0 || separator > 1024)
            return null;

        string name;
        try { name = StrictUtf8.GetString(data[..separator]); }
        catch (DecoderFallbackException) { return null; }

        if (!IsSafeFileName(name))
            return null;

        var details = data[(separator + 1)..];
        var end = details.IndexOfAny((byte)0, (byte)' ');
        var sizeField = end >= 0 ? details[..end] : details;
        if (sizeField.IsEmpty)
            return new ZmodemOffer(name, null);
        return uint.TryParse(Encoding.ASCII.GetString(sizeField), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? new ZmodemOffer(name, size) : null;
    }

    private async Task SendAsync(ZHeader receiver, IReadOnlyList<string> sources, Action<string> status, CancellationToken ct)
    {
        foreach (var path in sources)
        {
            var name = Path.GetFileName(path);
            if (!IsSafeFileName(name))
                throw new IOException(L10n.Get("S14AE3C992608"));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                SendWindow, FileOptions.Asynchronous);
            if (stream.Length > uint.MaxValue)
                throw new IOException(L10n.Get("S048EF7ECA980"));

            var size = (uint)stream.Length;
            if ((receiver.Flags & 0x80) != 0)
                throw new IOException(L10n.Get("S17196B65498F"));
            var crc32 = (receiver.Flags & 0x20) != 0;
            var receiverWindow = (int)(receiver.Value & 0xffff);
            var window = receiverWindow == 0 ? SendWindow : Math.Min(SendWindow, receiverWindow);
            var metadata = Encoding.UTF8.GetBytes(name + "\0" + size.ToString(CultureInfo.InvariantCulture) + "\0");
            status(L10n.Format("S730EC82BD35B", name));
            var response = await OfferAsync(stream, size, metadata, crc32, ct).ConfigureAwait(false);
            if (response.Frame == ZFrame.Skip)
            {
                status(L10n.Format("SFB4FE9690F24", name));
                continue;
            }

            receiver = await SendFileDataAsync(stream, size, response.Value, window, crc32,
                confirmed => status(L10n.Format("S35DEA53CF780", name, FormatProgress(confirmed, size))), ct).ConfigureAwait(false);
            status(receiver.Frame == ZFrame.Skip ? L10n.Format("SFB4FE9690F24", name) : L10n.Format("S11606CFB6622", name));
            if (receiver.Frame == ZFrame.Skip)
                receiver = new ZHeader(ZFrame.ReceiveInit, (crc32 ? 0x20u : 0u) << 24 | (uint)receiverWindow);
        }

        for (var attempt = 0; attempt < RetryLimit; attempt++)
        {
            await wire.SendHexAsync(ZFrame.Finish, 0, ct).ConfigureAwait(false);
            var reply = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (reply.Frame == ZFrame.Finish)
            {
                await wire.SendFinishMarkerAsync(ct).ConfigureAwait(false);
                return;
            }
        }
        throw new IOException(L10n.Get("SE4A25912A345"));
    }

    private async Task<ZHeader> OfferAsync(FileStream stream, uint size, byte[] metadata, bool crc32, CancellationToken ct)
    {
        for (var attempt = 0; attempt < RetryLimit; attempt++)
        {
            const uint binaryProtectExisting = 0x01070000;
            await wire.SendBinaryAsync(ZFrame.File, binaryProtectExisting, crc32, ct).ConfigureAwait(false);
            await wire.SendPacketAsync(metadata, ZmodemWire.Wait, crc32, ct).ConfigureAwait(false);
            var checksumQueries = 0;
            // Discard repeated ZRINIT while the picker is open.
            for (var frames = 0; frames < 32; frames++)
            {
                var reply = await ReadReplyAsync(ct).ConfigureAwait(false);
                if (reply.Frame is ZFrame.Position or ZFrame.Skip)
                    return reply;
                if (reply.Frame == ZFrame.Nak)
                    break;
                if (reply.Frame != ZFrame.Crc)
                    continue;
                if (++checksumQueries > 2 || reply.Value > size)
                    throw new InvalidDataException(L10n.Get("S5EC8EE2EB329"));
                var crc = await ZmodemWire.ComputeFileCrcAsync(stream, reply.Value == 0 ? size : reply.Value, ct).ConfigureAwait(false);
                await wire.SendHexAsync(ZFrame.Crc, crc, ct).ConfigureAwait(false);
            }
        }
        throw new IOException(L10n.Get("SC929B4CD6248"));
    }

    private async Task<ZHeader> SendFileDataAsync(FileStream source, uint size, uint position, int window,
        bool crc32, Action<uint> progress, CancellationToken ct)
    {
        var block = new byte[Math.Min(window, SendBlockSize)];
        var errors = 0;
        var recovery = true;
        var nextProgress = Stopwatch.GetTimestamp();
        while (errors <= RetryLimit)
        {
            if (position > size)
                throw new InvalidDataException(L10n.Get("SE2D80158C0CA"));

            if (position == size)
            {
                await wire.SendBinaryAsync(ZFrame.Eof, size, crc32, ct).ConfigureAwait(false);
                var end = await ReadReplyAsync(ct).ConfigureAwait(false);
                if (end.Frame is ZFrame.ReceiveInit or ZFrame.Skip)
                    return end;
                if (end.Frame == ZFrame.Position)
                    position = end.Value;
                errors++;
                recovery = true;
                continue;
            }

            source.Position = position;
            await wire.SendBinaryAsync(ZFrame.Data, position, crc32, ct).ConfigureAwait(false);
            var sent = position;
            var remainingWindow = recovery ? block.Length : window;
            byte ending;
            do
            {
                var count = (int)Math.Min(Math.Min((uint)block.Length, size - sent), (uint)remainingWindow);
                await source.ReadExactlyAsync(block.AsMemory(0, count), ct).ConfigureAwait(false);
                sent += (uint)count;
                remainingWindow -= count;
                // Require acknowledgment after recovery.
                ending = remainingWindow == 0 || sent == size ? ZmodemWire.Wait : ZmodemWire.Continue;
                await wire.SendPacketAsync(block.AsMemory(0, count), ending, crc32, ct).ConfigureAwait(false);
            } while (ending == ZmodemWire.Continue);

            var reply = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (reply.Frame == ZFrame.Ack && reply.Value == sent)
            {
                position = sent;
                errors = 0;
                recovery = false;
                if (Stopwatch.GetElapsedTime(nextProgress) >= TimeSpan.FromMilliseconds(400))
                {
                    nextProgress = Stopwatch.GetTimestamp();
                    progress(position);
                }
            }
            else if (reply.Frame == ZFrame.Skip)
                return reply;
            else
            {
                if (reply.Frame == ZFrame.Position)
                    position = reply.Value;
                errors++;
                recovery = true;
            }
        }
        throw new IOException(L10n.Get("SDBE67AC6DE37"));
    }

    private async Task<ZHeader> ReadReplyAsync(CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < RetryLimit; i++)
            {
                var response = await TimedHeaderAsync(ct).ConfigureAwait(false);
                if (response.Frame is ZFrame.Abort or ZFrame.Cancel or ZFrame.FileError)
                    throw new OperationCanceledException();
                if (response.Frame != ZFrame.Challenge)
                    return response;
                await wire.SendHexAsync(ZFrame.Ack, response.Value, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or TimeoutException)
        {
            return new ZHeader(ZFrame.Nak, 0);
        }
        return new ZHeader(ZFrame.Nak, 0);
    }

    private Task<ZHeader> TimedHeaderAsync(CancellationToken ct) =>
        WithTimeoutAsync(wire.ReadHeaderAsync, ct);

    private Task<ZPacket> TimedPacketAsync(byte[] buffer, bool crc32, CancellationToken ct) =>
        WithTimeoutAsync(token => wire.ReadPacketAsync(buffer, crc32, token), ct);

    private async Task TimedFinishMarkerAsync(CancellationToken ct) =>
        await WithTimeoutAsync(async token => { await wire.ReadFinishMarkerAsync(token).ConfigureAwait(false); return true; }, ct)
            .ConfigureAwait(false);

    private static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReceiveTimeout);
        try { return await read(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(L10n.Get("SDAC1903C1B40"), ex);
        }
    }

    private static string FormatProgress(uint position, uint? size) => size is > 0
        ? $"{(position * 100d / size.Value):0.#}%"
        : $"{position / 1024d:0.#} KiB";

    internal static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240 || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Any(character => char.IsControl(character) ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format) ||
            name != name.Trim() || name.EndsWith('.'))
            return false;

        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
              "123456789¹²³".Contains(stem[3]));
    }

    private sealed class PendingDownload : IDisposable
    {
        private readonly string stage;
        private readonly FileStream output;
        private readonly uint? expected;
        private bool committed;

        public PendingDownload(string destination, uint? expected)
        {
            Destination = destination;
            this.expected = expected;
            var parent = Path.GetDirectoryName(destination) ?? throw new IOException(L10n.Get("S11DF9B345A1F"));
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException(L10n.Get("S6D4B39F8E24C"));
            stage = Path.Combine(parent, $".memoryshell-zmodem-{Guid.NewGuid():N}.partial");
            output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous);
        }

        public string Destination { get; }
        public uint Position { get; private set; }
        public uint? Expected => expected;

        public async Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if ((ulong)Position + (ulong)data.Length > (expected ?? uint.MaxValue))
                throw new IOException(L10n.Get("SD8F6AC501C91"));
            await output.WriteAsync(data, ct).ConfigureAwait(false);
            Position += (uint)data.Length;
        }

        public void Commit()
        {
            if (expected is uint size && size != Position)
                throw new IOException(L10n.Get("SFB6072E08429"));
            output.Flush(flushToDisk: true);
            output.Dispose();
            File.Move(stage, Destination, overwrite: false);
            committed = true;
        }

        public void Dispose()
        {
            output.Dispose();
            if (!committed)
            {
                try { File.Delete(stage); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}




