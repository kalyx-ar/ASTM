using static Astm.Protocol.Lis01.ControlCharacters;

namespace Astm.Protocol.Lis01;

public enum Lis01FrameError { None, Structure, FrameNumber, Checksum, RestrictedCharacter }

/// <param name="Text">Frame text between the frame number and ETB/ETX. Also set for <see cref="Lis01FrameError.Checksum"/>, for diagnosis only.</param>
public readonly record struct Lis01ParsedFrame(Lis01FrameError Error, int FrameNumber, ReadOnlyMemory<byte> Text, bool IsFinal)
{
    public bool IsValid => Error == Lis01FrameError.None;
}

/// <summary>Frame layout: <c>STX FN text (ETB|ETX) C1 C2 CR LF</c>.</summary>
public static class Lis01Frame
{
    public const int MaxFrameLength = 247;
    public const int Overhead = 7;
    public const int MaxTextLength = MaxFrameLength - Overhead;

    /// <summary>Modulo-256 sum of the bytes from the frame number through ETB/ETX inclusive.</summary>
    public static byte Checksum(ReadOnlySpan<byte> frameNumberThroughTerminator)
    {
        var sum = 0;
        foreach (var value in frameNumberThroughTerminator)
            sum += value;
        return (byte)sum;
    }

    public static byte[] Build(int frameNumber, ReadOnlySpan<byte> text, bool isFinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameNumber, 7);
        var frame = new byte[text.Length + Overhead];
        frame[0] = Stx;
        frame[1] = (byte)('0' + frameNumber);
        text.CopyTo(frame.AsSpan(2));
        var terminator = 2 + text.Length;
        frame[terminator] = isFinal ? Etx : Etb;
        var checksum = Checksum(frame.AsSpan(1, terminator));
        frame[terminator + 1] = Hex(checksum >> 4);
        frame[terminator + 2] = Hex(checksum & 0x0F);
        frame[terminator + 3] = Cr;
        frame[terminator + 4] = Lf;
        return frame;
    }

    /// <summary>
    /// Splits each message into frames of at most <paramref name="maxTextLength"/> text bytes.
    /// Intermediate frames end with ETB and the last frame of every message with ETX. Numbering starts at 1, modulo 8.
    /// </summary>
    public static List<byte[]> BuildTransmission(IReadOnlyList<ReadOnlyMemory<byte>> messages, int maxTextLength = MaxTextLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTextLength, 1);
        if (messages.Count == 0)
            throw new ArgumentException("A transmission needs at least one message.", nameof(messages));
        var frames = new List<byte[]>();
        var frameNumber = 1;
        foreach (var message in messages)
        {
            if (message.IsEmpty)
                throw new ArgumentException("LIS01 messages cannot be empty.", nameof(messages));
            var span = message.Span;
            for (var i = 0; i < span.Length; i++)
            {
                if (IsRestricted(span[i]))
                    throw new ArgumentException($"Restricted character 0x{span[i]:X2} at offset {i}.", nameof(messages));
            }
            for (var offset = 0; offset < span.Length; offset += maxTextLength)
            {
                var length = Math.Min(maxTextLength, span.Length - offset);
                frames.Add(Build(frameNumber, span.Slice(offset, length), offset + length == span.Length));
                frameNumber = (frameNumber + 1) % 8;
            }
        }
        return frames;
    }

    /// <summary>Parses a frame without its leading STX: <c>FN text (ETB|ETX) C1 C2 CR LF</c>.</summary>
    public static Lis01ParsedFrame Parse(ReadOnlyMemory<byte> afterStx, bool acceptLowercaseChecksum = true, bool strictText = true)
    {
        var span = afterStx.Span;
        if (span.Length < 6 || span[^2] != Cr || span[^1] != Lf)
            return new(Lis01FrameError.Structure, -1, default, false);
        var terminatorIndex = span.Length - 5;
        var terminator = span[terminatorIndex];
        if (terminator is not (Etx or Etb))
            return new(Lis01FrameError.Structure, -1, default, false);
        var frameNumber = span[0] - '0';
        if (frameNumber is < 0 or > 7)
            return new(Lis01FrameError.FrameNumber, -1, default, false);
        var text = afterStx.Slice(1, terminatorIndex - 1);
        if (strictText)
        {
            foreach (var value in text.Span)
            {
                if (IsRestricted(value))
                    return new(Lis01FrameError.RestrictedCharacter, frameNumber, default, false);
            }
        }
        var expected = Checksum(span[..(terminatorIndex + 1)]);
        if (!TryHex(span[terminatorIndex + 1], acceptLowercaseChecksum, out var high)
            || !TryHex(span[terminatorIndex + 2], acceptLowercaseChecksum, out var low)
            || (high << 4 | low) != expected)
            return new(Lis01FrameError.Checksum, frameNumber, text, terminator == Etx);
        return new(Lis01FrameError.None, frameNumber, text, terminator == Etx);
    }

    private static byte Hex(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    private static bool TryHex(byte value, bool acceptLowercase, out int nibble)
    {
        nibble = value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - '0',
            >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
            >= (byte)'a' and <= (byte)'f' when acceptLowercase => value - 'a' + 10,
            _ => -1,
        };
        return nibble >= 0;
    }
}
