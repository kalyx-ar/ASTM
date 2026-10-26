using System.Globalization;
using System.Text;

namespace Atsm.Plugins.Tracing;

public enum BinaryByteOrder { LittleEndian, BigEndian }
public sealed record BinaryBytePattern(int Offset, byte[] Bytes);
public sealed record BinaryFrameLayout(int PayloadOffset, int LengthOffset, int LengthBytes, BinaryByteOrder ByteOrder, byte EndMarker)
{
    public IReadOnlyList<BinaryBytePattern> RequiredBytes { get; init; } = [];
    public int MaxPayloadBytes { get; init; } = 16 * 1024 * 1024;
}
public readonly record struct BinaryFrameBounds(int PayloadOffset, int PayloadLength, int TotalLength);

/// <summary>Neutral byte/hex normalization and declarative framing. Payload semantics belong to the dialect.</summary>
public static class BinaryWireParser
{
    public static bool TryReadUnsigned(ReadOnlySpan<byte> source, int offset, int width, BinaryByteOrder byteOrder, out uint value)
    {
        value = 0;
        if (width is < 1 or > 4 || offset < 0 || offset > source.Length - width) return false;
        var field = source.Slice(offset, width);
        for (var i = 0; i < width; i++) value = (value << 8) | field[byteOrder == BinaryByteOrder.BigEndian ? i : width - i - 1];
        return true;
    }

    public static bool TryReadFrame(ReadOnlySpan<byte> source, BinaryFrameLayout layout, out BinaryFrameBounds frame)
    {
        frame = default;
        if (layout.PayloadOffset < 0 || layout.LengthOffset < 0 || layout.LengthBytes is not (1 or 2 or 4)
            || (long)layout.LengthOffset + layout.LengthBytes > layout.PayloadOffset) throw new ArgumentException("Invalid binary frame layout.", nameof(layout));
        if (source.Length <= layout.PayloadOffset) return false;
        foreach (var pattern in layout.RequiredBytes)
            if (pattern.Offset < 0 || pattern.Offset > source.Length - pattern.Bytes.Length || !source.Slice(pattern.Offset, pattern.Bytes.Length).SequenceEqual(pattern.Bytes)) return false;
        if (!TryReadUnsigned(source, layout.LengthOffset, layout.LengthBytes, layout.ByteOrder, out var length)) return false;
        if (length > layout.MaxPayloadBytes || length > source.Length - layout.PayloadOffset - 1 || source[layout.PayloadOffset + (int)length] != layout.EndMarker) return false;
        frame = new(layout.PayloadOffset, (int)length, layout.PayloadOffset + (int)length + 1); return true;
    }

    public static byte[] ParseHex(ReadOnlySpan<byte> text) => TryParseHex(text, out var bytes, allowCompact: true) ? bytes : throw new FormatException("Invalid hexadecimal byte representation.");
    public static bool TryParseHex(ReadOnlySpan<byte> source, out byte[] bytes, bool allowCompact = false)
    {
        bytes = [];
        if (source.StartsWith(new byte[] { 239, 187, 191 })) source = source[3..];
        if (source.IndexOfAnyExceptInRange((byte)0, (byte)127) >= 0) return false;
        var text = Encoding.ASCII.GetString(source).Trim(); if (text.Length == 0) return false;
        var result = new List<byte>(); var notation = text[0] is '<' or '['; var explicitEncoding = notation || text.Contains("0x", StringComparison.OrdinalIgnoreCase) || text.Contains("\\x", StringComparison.OrdinalIgnoreCase);
        if (!allowCompact && !explicitEncoding && !text.Any(c => char.IsWhiteSpace(c) || c is ',' or ';' or '-')) return false;
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i]) || !notation && text[i] is ',' or ';' or '-') { i++; continue; }
            if (text[i] is '<' or '[')
            {
                var end = text.IndexOf(text[i] == '<' ? '>' : ']', i + 1); if (end < 0) return false;
                var token = text[(i + 1)..end].ToUpperInvariant();
                if (text[i] == '<' && token.Length == 2 && byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var angleHex)) result.Add(angleHex);
                else if (AsciiControlTokens.Bracketed.TryGetValue("[" + token + "]", out var control)) result.Add(control);
                else if (token.Length == 2 && byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) result.Add(value);
                else return false;
                i = end + 1; continue;
            }
            if (notation) { result.Add((byte)text[i++]); continue; }
            if (i + 2 <= text.Length && (text.AsSpan(i, 2).Equals("0x", StringComparison.OrdinalIgnoreCase) || text.AsSpan(i, 2).Equals("\\x", StringComparison.OrdinalIgnoreCase))) i += 2;
            if (i + 2 > text.Length || !byte.TryParse(text.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return false;
            result.Add(hex); i += 2;
        }
        bytes = result.ToArray(); return bytes.Length > 0 && (allowCompact || explicitEncoding || bytes.Length > 1);
    }
}
