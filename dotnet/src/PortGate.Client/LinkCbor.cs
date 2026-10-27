using System.Buffers.Binary;
using System.Text;

namespace PortGate.Client;

/// <summary>
/// Deterministic CBOR subset of PortGate Link v1, mirroring PortGate/core/src/cbor.cpp: unsigned and negative integers,
/// byte and text strings, arrays, maps with unsigned keys in ascending order, booleans and null.
/// Values are <see cref="ulong"/>, <see cref="long"/> (negative only), <see cref="byte"/>[], <see cref="string"/>,
/// <see cref="bool"/>, null, <see cref="List{T}"/> of object and <see cref="SortedDictionary{TKey,TValue}"/> keyed by ulong.
/// </summary>
public static class LinkCbor
{
    public const int MaxDepth = 8;
    public const int MaxItems = 4096;

    public static byte[] Encode(object? value)
    {
        var output = new List<byte>(64);
        Write(value, output);
        return [.. output];
    }

    public static SortedDictionary<ulong, object?> Map(params (ulong Key, object? Value)[] entries)
    {
        var map = new SortedDictionary<ulong, object?>();
        foreach (var (key, value) in entries)
            map.Add(key, value);
        return map;
    }

    /// <summary>Decodes exactly one item spanning the input; null on malformed, unsupported or oversized data.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out object? value)
    {
        var reader = new Reader(data);
        try
        {
            value = reader.Item(0);
            return reader.AtEnd;
        }
        catch (FormatException)
        {
            value = null;
            return false;
        }
    }

    private static void Write(object? value, List<byte> output)
    {
        switch (value)
        {
            case null: output.Add(0xF6); break;
            case bool b: output.Add(b ? (byte)0xF5 : (byte)0xF4); break;
            case ulong u: Head(0, u, output); break;
            case uint u: Head(0, u, output); break;
            case int i when i >= 0: Head(0, (ulong)i, output); break;
            case int i: Head(1, (ulong)(-1L - i), output); break;
            case long l when l >= 0: Head(0, (ulong)l, output); break;
            case long l: Head(1, (ulong)(-1 - l), output); break;
            case byte[] bytes:
                Head(2, (ulong)bytes.Length, output);
                output.AddRange(bytes);
                break;
            case string text:
                var utf8 = Encoding.UTF8.GetBytes(text);
                Head(3, (ulong)utf8.Length, output);
                output.AddRange(utf8);
                break;
            case SortedDictionary<ulong, object?> map:
                Head(5, (ulong)map.Count, output);
                foreach (var (key, item) in map)
                {
                    Head(0, key, output);
                    Write(item, output);
                }
                break;
            case IReadOnlyList<object?> list:
                Head(4, (ulong)list.Count, output);
                foreach (var item in list)
                    Write(item, output);
                break;
            default:
                throw new ArgumentException($"Type {value.GetType()} is not part of the Link v1 CBOR subset.", nameof(value));
        }
    }

    private static void Head(byte major, ulong argument, List<byte> output)
    {
        var m = (byte)(major << 5);
        Span<byte> buffer = stackalloc byte[8];
        if (argument < 24)
        {
            output.Add((byte)(m | (byte)argument));
        }
        else if (argument <= byte.MaxValue)
        {
            output.Add((byte)(m | 24));
            output.Add((byte)argument);
        }
        else if (argument <= ushort.MaxValue)
        {
            output.Add((byte)(m | 25));
            BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)argument);
            output.AddRange(buffer[..2]);
        }
        else if (argument <= uint.MaxValue)
        {
            output.Add((byte)(m | 26));
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)argument);
            output.AddRange(buffer[..4]);
        }
        else
        {
            output.Add((byte)(m | 27));
            BinaryPrimitives.WriteUInt64BigEndian(buffer, argument);
            output.AddRange(buffer);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        private int _items;

        public readonly bool AtEnd => _position == _data.Length;

        public object? Item(int depth)
        {
            if (depth > MaxDepth || ++_items > MaxItems || _position >= _data.Length)
                throw new FormatException();
            var initial = _data[_position++];
            var major = initial >> 5;
            var info = initial & 0x1F;
            if (major == 7)
            {
                return info switch
                {
                    20 => false,
                    21 => true,
                    22 => null,
                    _ => throw new FormatException(),
                };
            }
            var argument = Argument(info);
            switch (major)
            {
                case 0:
                    return argument;
                case 1:
                    if (argument > long.MaxValue)
                        throw new FormatException();
                    return -1 - (long)argument;
                case 2:
                case 3:
                    if (argument > (ulong)(_data.Length - _position))
                        throw new FormatException();
                    var slice = _data.Slice(_position, (int)argument);
                    _position += (int)argument;
                    return major == 2 ? slice.ToArray() : Encoding.UTF8.GetString(slice);
                case 4:
                    if (argument > MaxItems)
                        throw new FormatException();
                    var list = new List<object?>((int)argument);
                    for (ulong i = 0; i < argument; i++)
                        list.Add(Item(depth + 1));
                    return list;
                case 5:
                    if (argument > MaxItems)
                        throw new FormatException();
                    var map = new SortedDictionary<ulong, object?>();
                    for (ulong i = 0; i < argument; i++)
                    {
                        if (Item(depth + 1) is not ulong key || !map.TryAdd(key, Item(depth + 1)))
                            throw new FormatException();
                    }
                    return map;
                default:
                    throw new FormatException();
            }
        }

        private ulong Argument(int info)
        {
            if (info < 24)
                return (ulong)info;
            var length = info switch { 24 => 1, 25 => 2, 26 => 4, 27 => 8, _ => throw new FormatException() };
            if (length > _data.Length - _position)
                throw new FormatException();
            ulong value = 0;
            for (var i = 0; i < length; i++)
                value = value << 8 | _data[_position++];
            return value;
        }
    }
}
