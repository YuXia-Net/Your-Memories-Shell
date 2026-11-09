using System.Buffers.Binary;
using System.Threading.Channels;

namespace MemoryShell.Services;

// Wire values and byte order follow Chuck Forsberg's ZMODEM protocol description.
internal enum ZFrame : byte
{
    RequestInit, ReceiveInit, SenderInit, Ack, File, Skip, Nak, Abort, Finish, Position,
    Data, Eof, FileError, Crc, Challenge, Complete, Cancel, FreeCount, Command, Stderr
}

internal readonly record struct ZHeader(ZFrame Frame, uint Value, bool Crc32 = false)
{
    public byte Flags => (byte)(Value >> 24);
}

internal enum ZStartState { Invalid, Incomplete, Complete }

internal readonly record struct ZPacket(byte[] Buffer, int Length, byte Ending, bool Valid);

internal sealed class ZmodemWire(
    ChannelReader<byte[]> input,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task> send)
{
    internal const byte Pad = 0x2a;
    internal const byte Escape = 0x18;
    internal const byte Hex = (byte)'B';
    internal const byte End = (byte)'h';
    internal const byte Continue = (byte)'i';
    internal const byte Query = (byte)'j';
    internal const byte Wait = (byte)'k';
    private const int MaxSubpacket = 65536;
    private const int MaxResyncBytes = 4 * 1024 * 1024;
    private static readonly uint[] Crc32Table = MakeCrc32Table();

    private byte[] current = [];
    private int offset;
    private int cancelBytes;
    private int pushback = -1;

    public Task SendHexAsync(ZFrame frame, uint value, CancellationToken ct) =>
        send(EncodeHeader(frame, value, hex: true, crc32: false), ct);

    public Task SendBinaryAsync(ZFrame frame, uint value, bool crc32, CancellationToken ct) =>
        send(EncodeHeader(frame, value, hex: false, crc32), ct);

    public Task SendPacketAsync(ReadOnlyMemory<byte> bytes, byte ending, bool crc32, CancellationToken ct) =>
        send(EncodePacket(bytes.Span, ending, crc32), ct);

    public Task SendFinishMarkerAsync(CancellationToken ct) => send(new byte[] { (byte)'O', (byte)'O' }, ct);

    public Task SendCancelAsync(CancellationToken ct) =>
        send(new byte[] { Escape, Escape, Escape, Escape, Escape, Escape, Escape, Escape, 8, 8, 8, 8, 8, 8, 8, 8 }, ct);

    public static ZStartState ReadStart(ReadOnlySpan<byte> data, out ZHeader header, out int consumed)
    {
        header = default;
        consumed = 0;
        if (data.IsEmpty || data[0] != Pad)
            return ZStartState.Invalid;
        if (data.Length == 1)
            return ZStartState.Incomplete;

        var index = data[1] == Pad ? 2 : 1;
        if (index == data.Length)
            return ZStartState.Incomplete;
        if (data[index++] != Escape)
            return ZStartState.Invalid;
        if (index == data.Length)
            return ZStartState.Incomplete;

        var encoding = data[index++];
        if (encoding != (byte)'A' && encoding != Hex && encoding != (byte)'C')
            return ZStartState.Invalid;

        var crc32 = encoding == (byte)'C';
        Span<byte> raw = stackalloc byte[9];
        var length = crc32 ? 9 : 7;
        for (var i = 0; i < length; i++)
        {
            if (index == data.Length)
                return ZStartState.Incomplete;
            var value = data[index++];
            if (encoding == Hex)
            {
                var high = HexDigit(value);
                if (high < 0)
                    return ZStartState.Invalid;
                if (index == data.Length)
                    return ZStartState.Incomplete;
                var low = HexDigit(data[index++]);
                if (low < 0)
                    return ZStartState.Invalid;
                value = (byte)((high << 4) | low);
            }
            else if (value == Escape)
            {
                if (index == data.Length)
                    return ZStartState.Incomplete;
                var decoded = DecodeEscaped(data[index++]);
                if (decoded < 0)
                    return ZStartState.Invalid;
                value = (byte)decoded;
            }
            if (i == 0 && value > (byte)ZFrame.ReceiveInit)
                return ZStartState.Invalid;
            raw[i] = value;
        }
        if (!CheckCrc(raw, 5, crc32))
            return ZStartState.Invalid;

        header = new ZHeader((ZFrame)raw[0], BinaryPrimitives.ReadUInt32LittleEndian(raw[1..]), crc32);
        consumed = index;
        return ZStartState.Complete;
    }

    public async Task<ZHeader> ReadHeaderAsync(CancellationToken ct)
    {
        var raw = new byte[9];
        for (var count = 0; count < MaxResyncBytes; count++)
        {
            if (await ReadByteAsync(ct).ConfigureAwait(false) != Pad)
                continue;

            byte code;
            do { code = await ReadByteAsync(ct).ConfigureAwait(false); }
            while (code == Pad && ++count < MaxResyncBytes);

            if (code != Escape)
                continue;
            var encoding = await ReadByteAsync(ct).ConfigureAwait(false);
            if (encoding != (byte)'A' && encoding != Hex && encoding != (byte)'C')
                continue;
            var crc32 = encoding == (byte)'C';
            var length = crc32 ? 9 : 7;

            if (encoding == Hex)
            {
                for (var i = 0; i < length; i++)
                {
                    var high = HexDigit(await ReadByteAsync(ct).ConfigureAwait(false));
                    var low = HexDigit(await ReadByteAsync(ct).ConfigureAwait(false));
                    if (high < 0 || low < 0)
                        throw new InvalidDataException(L10n.Get("SDCE27AC6C4E5"));
                    raw[i] = (byte)((high << 4) | low);
                }
                await ReadHexEndingAsync(ct).ConfigureAwait(false);
            }
            else
            {
                for (var i = 0; i < length; i++)
                    raw[i] = (byte)await ReadEscapedAsync(ct).ConfigureAwait(false);
            }

            if (!CheckCrc(raw, 5, crc32))
                throw new InvalidDataException(L10n.Get("S526F66DC9876"));
            return new ZHeader((ZFrame)raw[0], BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(1, 4)), crc32);
        }
        throw new InvalidDataException(L10n.Get("S2B689267562B"));
    }

    public async Task<ZPacket> ReadPacketAsync(byte[] buffer, bool crc32, CancellationToken ct)
    {
        var length = 0;
        while (length <= MaxSubpacket)
        {
            var value = await ReadEscapedAsync(ct, allowEnding: true).ConfigureAwait(false);
            if (value >= 256)
            {
                var ending = (byte)value;
                buffer[length] = ending;
                var crcBytes = crc32 ? 4 : 2;
                for (var i = 0; i < crcBytes; i++)
                    buffer[length + 1 + i] = (byte)await ReadEscapedAsync(ct).ConfigureAwait(false);
                return new ZPacket(buffer, length, ending, CheckCrc(buffer, length + 1, crc32));
            }
            if (length == MaxSubpacket)
                break;
            buffer[length++] = (byte)value;
        }
        throw new InvalidDataException(L10n.Get("SFF80BCFF6352"));
    }

    public async Task ReadFinishMarkerAsync(CancellationToken ct)
    {
        var first = await ReadNonFlowByteAsync(ct).ConfigureAwait(false);
        if (first != (byte)'O')
        {
            pushback = first;
            throw new InvalidDataException(L10n.Get("SAC55E7F195D5"));
        }
        var second = await ReadNonFlowByteAsync(ct).ConfigureAwait(false);
        if (second != (byte)'O')
        {
            pushback = second;
            throw new InvalidDataException(L10n.Get("S10FF7826741F"));
        }
    }

    public IEnumerable<ReadOnlyMemory<byte>> Remaining()
    {
        if (pushback >= 0)
            yield return new byte[] { (byte)pushback };
        if (offset < current.Length)
            yield return current.AsMemory(offset);
        while (input.TryRead(out var remainder))
            yield return remainder;
    }

    private async ValueTask ReadHexEndingAsync(CancellationToken ct)
    {
        var first = await ReadByteAsync(ct).ConfigureAwait(false);
        if ((first & 0x7f) == '\r')
        {
            var second = await ReadByteAsync(ct).ConfigureAwait(false);
            if ((second & 0x7f) != '\n')
                pushback = second;
        }
        else if ((first & 0x7f) != '\n')
            pushback = first;
    }

    private async ValueTask<int> ReadEscapedAsync(CancellationToken ct, bool allowEnding = false)
    {
        var value = await ReadNonFlowByteAsync(ct).ConfigureAwait(false);
        if (value != Escape)
            return value;
        var next = await ReadNonFlowByteAsync(ct).ConfigureAwait(false);
        if (allowEnding && next is >= End and <= Wait)
            return 256 + next;
        var decoded = DecodeEscaped(next);
        return decoded >= 0 ? decoded : throw new InvalidDataException(L10n.Get("SAC94310CAF9E"));
    }

    private static int DecodeEscaped(byte value) => value switch
    {
        (byte)'l' => 0x7f,
        (byte)'m' => 0xff,
        _ when (value & 0x60) == 0x40 => value ^ 0x40,
        _ => -1
    };

    private async ValueTask<byte> ReadNonFlowByteAsync(CancellationToken ct)
    {
        byte value;
        do { value = await ReadByteAsync(ct).ConfigureAwait(false); }
        while ((value & 0x7f) is 0x11 or 0x13);
        return value;
    }

    private async ValueTask<byte> ReadByteAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pushback >= 0)
        {
            var value = (byte)pushback;
            pushback = -1;
            return value;
        }
        if (offset == current.Length)
        {
            current = await input.ReadAsync(ct).ConfigureAwait(false);
            offset = 0;
        }
        var next = current[offset++];
        cancelBytes = next == Escape ? cancelBytes + 1 : 0;
        if (cancelBytes >= 5)
            throw new OperationCanceledException(L10n.Get("S160FF2E8F905"));
        return next;
    }

    private static byte[] EncodeHeader(ZFrame frame, uint value, bool hex, bool crc32)
    {
        Span<byte> raw = stackalloc byte[9];
        raw[0] = (byte)frame;
        BinaryPrimitives.WriteUInt32LittleEndian(raw[1..], value);
        WriteCrc(raw[5..], raw[..5], crc32);
        var result = new List<byte>(24) { Pad };
        if (hex)
        {
            result.Add(Pad);
            result.Add(Escape);
            result.Add(Hex);
            const string digits = "0123456789abcdef";
            foreach (var item in raw[..7])
            {
                result.Add((byte)digits[item >> 4]);
                result.Add((byte)digits[item & 0xf]);
            }
            result.Add(13);
            result.Add(10);
            if (frame is not (ZFrame.Ack or ZFrame.Finish))
                result.Add(0x11);
        }
        else
        {
            result.Add(Escape);
            result.Add(crc32 ? (byte)'C' : (byte)'A');
            foreach (var item in raw[..(crc32 ? 9 : 7)])
                AppendEscaped(result, item);
        }
        return result.ToArray();
    }

    private static ReadOnlyMemory<byte> EncodePacket(ReadOnlySpan<byte> bytes, byte ending, bool crc32)
    {
        var output = new byte[bytes.Length * 2 + 12];
        var length = 0;
        foreach (var item in bytes)
            WriteEscaped(output, ref length, item);
        output[length++] = Escape;
        output[length++] = ending;
        Span<byte> checksum = stackalloc byte[4];
        WriteCrc(checksum, bytes, crc32, ending);
        foreach (var item in checksum[..(crc32 ? 4 : 2)])
            WriteEscaped(output, ref length, item);
        if (ending == Wait)
            output[length++] = 0x11;
        return output.AsMemory(0, length);
    }

    private static void WriteEscaped(byte[] result, ref int offset, byte value)
    {
        if ((value & 0x60) == 0)
        {
            result[offset++] = Escape;
            value ^= 0x40;
        }
        result[offset++] = value;
    }

    private static void AppendEscaped(List<byte> result, byte value)
    {
        if ((value & 0x60) == 0)
        {
            result.Add(Escape);
            result.Add((byte)(value ^ 0x40));
        }
        else
            result.Add(value);
    }

    private static int HexDigit(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1
    };

    private static bool CheckCrc(byte[] frame, int length, bool crc32) =>
        CheckCrc(frame.AsSpan(), length, crc32);

    private static bool CheckCrc(ReadOnlySpan<byte> frame, int length, bool crc32)
    {
        Span<byte> checksum = stackalloc byte[4];
        WriteCrc(checksum, frame[..length], crc32);
        var size = crc32 ? 4 : 2;
        return checksum[..size].SequenceEqual(frame.Slice(length, size));
    }

    private static void WriteCrc(Span<byte> destination, ReadOnlySpan<byte> bytes, bool crc32, byte? last = null)
    {
        if (crc32)
        {
            var crc = uint.MaxValue;
            foreach (var value in bytes)
                crc = Crc32Table[(crc ^ value) & 255] ^ (crc >> 8);
            if (last is byte ending)
                crc = Crc32Table[(crc ^ ending) & 255] ^ (crc >> 8);
            BinaryPrimitives.WriteUInt32LittleEndian(destination, ~crc);
            return;
        }

        ushort crc16 = 0;
        foreach (var value in bytes)
            crc16 = AddCrc16(crc16, value);
        if (last is byte final)
            crc16 = AddCrc16(crc16, final);
        BinaryPrimitives.WriteUInt16BigEndian(destination, crc16);
    }

    private static ushort AddCrc16(ushort crc, byte value)
    {
        var result = (crc ^ (value << 8));
        for (var bit = 0; bit < 8; bit++)
            result = (result << 1) ^ ((result & 0x8000) != 0 ? 0x1021 : 0);
        return (ushort)result;
    }

    public static async Task<uint> ComputeFileCrcAsync(Stream source, uint length, CancellationToken ct)
    {
        source.Position = 0;
        var remaining = length;
        var buffer = new byte[65536];
        var crc = uint.MaxValue;
        while (remaining > 0)
        {
            var count = (int)Math.Min((uint)buffer.Length, remaining);
            await source.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
                crc = Crc32Table[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
            remaining -= (uint)count;
        }
        return ~crc;
    }

    private static uint[] MakeCrc32Table()
    {
        var table = new uint[256];
        for (uint item = 0; item < table.Length; item++)
        {
            var crc = item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            table[item] = crc;
        }
        return table;
    }
}


