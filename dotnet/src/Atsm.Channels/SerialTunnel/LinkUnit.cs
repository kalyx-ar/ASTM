using System.Buffers.Binary;

namespace Atsm.Channels.SerialTunnel;

public enum LinkType : byte
{
    Hello = 0x01,
    Capabilities = 0x02,
    Acquire = 0x10,
    Queued = 0x11,
    Granted = 0x12,
    Denied = 0x13,
    Renew = 0x14,
    Release = 0x15,
    Revoked = 0x16,
    RequestHandover = 0x17,
    ConfirmTransfer = 0x18,
    HandoverAsked = 0x19,
    HandoverAnswer = 0x1A,
    HandoverResult = 0x1B,
    KillSwitch = 0x1C,
    Contest = 0x1D,
    SecurityResult = 0x1E,
    SerialData = 0x20,
    WriteStatus = 0x21,
    PortEvent = 0x22,
    SecurityEvent = 0x23,
    GetStatus = 0x40,
    GetConfig = 0x41,
    SetConfig = 0x42,
    SetTls = 0x43,
    ListDevices = 0x44,
    RevokePort = 0x45,
    ReopenPort = 0x46,
    ReadEvents = 0x47,
    Restart = 0x48,
    UnblockController = 0x49,
    AdminEvent = 0x4E,
    AdminResult = 0x4F,
    Heartbeat = 0x7E,
    Error = 0x7F,
}

/// <summary>One RemoteUSB Link v1 unit (spec/link-v1.md, spec/admin-v1.md): 12-byte big-endian header and payload.</summary>
public sealed record LinkUnit(LinkType Type, ushort Flags, ushort Channel, byte[] Payload)
{
    public const int HeaderSize = 12;
    public const ushort CborFlag = 0x0001;
    public const ushort CriticalFlag = 0x8000;
    public const int SerialDataHeader = 20;

    public bool IsCbor => (Flags & CborFlag) != 0;

    public static LinkUnit Control(LinkType type, ushort channel, SortedDictionary<ulong, object?> map, bool critical = false) =>
        new(type, (ushort)(CborFlag | (critical ? CriticalFlag : 0)), channel, LinkCbor.Encode(map));

    public static LinkUnit Serial(ushort channel, ulong sequence, ulong offset, uint generation, ReadOnlySpan<byte> data)
    {
        var payload = new byte[SerialDataHeader + data.Length];
        BinaryPrimitives.WriteUInt64BigEndian(payload, sequence);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(8), offset);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), generation);
        data.CopyTo(payload.AsSpan(SerialDataHeader));
        return new LinkUnit(LinkType.SerialData, 0, channel, payload);
    }

    public byte[] Encode()
    {
        var bytes = new byte[HeaderSize + Payload.Length];
        bytes[0] = 0x41;
        bytes[1] = 0x4C;
        bytes[2] = 1;
        bytes[3] = (byte)Type;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), Flags);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), Channel);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)Payload.Length);
        Payload.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    public SortedDictionary<ulong, object?>? Map() =>
        IsCbor && LinkCbor.TryDecode(Payload, out var value) ? value as SortedDictionary<ulong, object?> : null;

    public (ulong Sequence, ulong Offset, uint Generation, ReadOnlyMemory<byte> Data)? SerialPayload()
    {
        if (Type != LinkType.SerialData || IsCbor || Payload.Length < SerialDataHeader)
            return null;
        return (BinaryPrimitives.ReadUInt64BigEndian(Payload),
            BinaryPrimitives.ReadUInt64BigEndian(Payload.AsSpan(8)),
            BinaryPrimitives.ReadUInt32BigEndian(Payload.AsSpan(16)),
            Payload.AsMemory(SerialDataHeader));
    }
}

public enum LinkDecodeError { None, BadMagic, BadVersion, TooLarge }

/// <summary>Incremental decoder that validates the length before buffering the payload.</summary>
public sealed class LinkDecoder(int maxUnitBytes = 64 * 1024)
{
    private readonly List<byte> _buffer = [];

    public LinkDecodeError Error { get; private set; }

    public LinkDecodeError Feed(ReadOnlySpan<byte> data, List<LinkUnit> output)
    {
        if (Error != LinkDecodeError.None)
            return Error;
        _buffer.AddRange(data);
        var position = 0;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer);
        while (span.Length - position >= LinkUnit.HeaderSize)
        {
            var header = span.Slice(position, LinkUnit.HeaderSize);
            if (header[0] != 0x41 || header[1] != 0x4C)
                return Error = LinkDecodeError.BadMagic;
            if (header[2] != 1)
                return Error = LinkDecodeError.BadVersion;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
            if (length > (uint)maxUnitBytes)
                return Error = LinkDecodeError.TooLarge;
            if (span.Length - position < LinkUnit.HeaderSize + (int)length)
                break;
            output.Add(new LinkUnit(
                (LinkType)header[3],
                BinaryPrimitives.ReadUInt16BigEndian(header[4..]),
                BinaryPrimitives.ReadUInt16BigEndian(header[6..]),
                span.Slice(position + LinkUnit.HeaderSize, (int)length).ToArray()));
            position += LinkUnit.HeaderSize + (int)length;
        }
        _buffer.RemoveRange(0, position);
        return LinkDecodeError.None;
    }
}
