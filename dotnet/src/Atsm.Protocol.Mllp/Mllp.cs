namespace Atsm.Protocol.Mllp;

/// <summary>MLLP block: <c>VT payload FS CR</c>.</summary>
public static class MllpFraming
{
    public const byte StartBlock = 0x0B;
    public const byte EndBlock = 0x1C;
    public const byte CarriageReturn = 0x0D;

    public static byte[] Wrap(ReadOnlySpan<byte> payload)
    {
        var block = new byte[payload.Length + 3];
        block[0] = StartBlock;
        payload.CopyTo(block.AsSpan(1));
        block[^2] = EndBlock;
        block[^1] = CarriageReturn;
        return block;
    }
}

public enum MllpEventKind { Message, Noise, Overflow, MalformedEnd }

public readonly record struct MllpEvent(MllpEventKind Kind, byte[]? Payload, int ByteCount);

/// <summary>
/// Incremental MLLP decoder. Accepts any split of the TCP stream and several blocks per read.
/// The size limit is checked before the buffer grows past it.
/// </summary>
public sealed class MllpDecoder(int maxMessageBytes = 1024 * 1024)
{
    private readonly List<byte> _buffer = [];
    private bool _inBlock;
    private bool _sawEnd;
    private int _noise;
    private bool _overflowed;

    public void Feed(ReadOnlySpan<byte> data, List<MllpEvent> events)
    {
        foreach (var value in data)
        {
            if (!_inBlock)
            {
                if (value == MllpFraming.StartBlock)
                {
                    FlushNoise(events);
                    _inBlock = true;
                    _sawEnd = false;
                    _overflowed = false;
                    _buffer.Clear();
                }
                else
                {
                    _noise++;
                }
                continue;
            }

            if (_sawEnd)
            {
                _inBlock = false;
                _sawEnd = false;
                if (value == MllpFraming.CarriageReturn)
                {
                    if (!_overflowed)
                        events.Add(new(MllpEventKind.Message, [.. _buffer], _buffer.Count));
                }
                else
                {
                    events.Add(new(MllpEventKind.MalformedEnd, null, _buffer.Count));
                    if (value == MllpFraming.StartBlock)
                    {
                        _inBlock = true;
                        _overflowed = false;
                    }
                }
                _buffer.Clear();
                continue;
            }

            if (value == MllpFraming.EndBlock)
            {
                _sawEnd = true;
                continue;
            }
            if (value == MllpFraming.StartBlock)
            {
                events.Add(new(MllpEventKind.MalformedEnd, null, _buffer.Count));
                _buffer.Clear();
                _overflowed = false;
                continue;
            }
            if (_overflowed)
                continue;
            if (_buffer.Count >= maxMessageBytes)
            {
                _overflowed = true;
                events.Add(new(MllpEventKind.Overflow, null, _buffer.Count));
                _buffer.Clear();
                continue;
            }
            _buffer.Add(value);
        }
        FlushNoise(events);
    }

    private void FlushNoise(List<MllpEvent> events)
    {
        if (_noise == 0)
            return;
        events.Add(new(MllpEventKind.Noise, null, _noise));
        _noise = 0;
    }
}
