using System.Text;
using Astm.Abstractions.Tracing;

namespace Astm.Plugin.Sysmex.Urinalysis;

public enum Uc3500CheckMode { Auto, None, Bcc, Sum, Sum2 }

internal sealed record UcPacket(long First, long Last, TimeSpan Elapsed, TraceDirection Direction,
    string Text, string Check, bool Valid);

/// <summary>Passive half-duplex reconstruction. ETX and its checksum may arrive in different reads.
/// Manual 0.0.5 §6.1.1.3-.5; the sum of Table 6-17 field widths is 446, despite the stated 441-byte limit.</summary>
internal sealed class Uc3500WireDecoder(Uc3500CheckMode mode)
{
    private sealed class Buffer
    {
        public readonly List<byte> Payload = [];
        public readonly List<byte> Check = [];
        public bool Framed, Ended, Cr, Invalid;
        public long First, Last;
        public TimeSpan Elapsed;
        public void Clear() { Payload.Clear(); Check.Clear(); Framed = Ended = Cr = Invalid = false; }
    }
    private readonly Dictionary<TraceDirection, Buffer> buffers = new()
    { [TraceDirection.Received] = new(), [TraceDirection.Transmitted] = new() };
    public List<UcPacket> Packets { get; } = [];
    public List<(TraceEntry Entry, byte Value)> Controls { get; } = [];
    public List<(long Sequence, string Code)> Errors { get; } = [];

    public void Feed(TraceEntry entry)
    {
        if (entry.Direction == TraceDirection.Event || entry.Data.IsEmpty) return;
        // A response on the opposite wire proves the previous frame's checksum has ended.
        foreach (var other in buffers.Where(p => p.Key != entry.Direction && p.Value.Ended)) Emit(other.Value, other.Key);
        var buffer = buffers[entry.Direction];
        foreach (var value in entry.Data.Span) Byte(buffer, value, entry);
    }
    public void Complete()
    {
        foreach (var (direction, buffer) in buffers)
            if (buffer.Ended) Emit(buffer, direction);
            else if (buffer.Framed || buffer.Payload.Count > 0)
            { Errors.Add((buffer.Last, "INCOMPLETE")); EmitIncomplete(buffer, direction); }
    }
    private void Byte(Buffer b, byte value, TraceEntry entry)
    {
        if (b.Ended)
        {
            if (mode == Uc3500CheckMode.Auto)
            {
                if (b.Check.Count == 0 && value == 2 && Xor(b.Payload) != 2 && Sum(b.Payload) != 2)
                    Emit(b, entry.Direction); // Delimited frame without checksum, followed by another frame.
                else if (b.Check.Count == 1 && (b.Check[0] == Xor(b.Payload) || b.Check[0] == Sum(b.Payload) || !IsHex(b.Check[0]) || !IsHex(value)))
                    Emit(b, entry.Direction);
            }
            if (b.Ended)
            {
                b.Check.Add(value); b.Last = entry.Sequence; b.Elapsed = entry.Elapsed;
                var expected = mode == Uc3500CheckMode.Sum2 || mode == Uc3500CheckMode.Auto ? 2 : 1;
                if (b.Check.Count >= expected) Emit(b, entry.Direction);
                return;
            }
        }
        if (!b.Framed && b.Payload.Count == 0 && value is 4 or 6 or 21)
        { Controls.Add((entry, value)); return; }
        if (value == 2)
        {
            if (b.Framed || b.Payload.Count > 0) { Errors.Add((entry.Sequence, "RESTART")); EmitIncomplete(b, entry.Direction); }
            b.Clear(); b.Framed = true; b.First = entry.Sequence; b.Last = entry.Sequence; b.Elapsed = entry.Elapsed; return;
        }
        if (!b.Framed && b.Payload.Count == 0 && value is 10 or 13) return;
        if (!b.Framed && b.Payload.Count == 0) b.First = entry.Sequence;
        b.Last = entry.Sequence; b.Elapsed = entry.Elapsed;
        if (b.Framed && value == 3)
        { b.Ended = true; if (mode == Uc3500CheckMode.None) Emit(b, entry.Direction); return; }
        if (!b.Framed && value == 10 && b.Cr)
        { b.Payload.RemoveAt(b.Payload.Count - 1); Emit(b, entry.Direction); return; }
        b.Cr = value == 13;
        b.Invalid |= value < 32 && value != 13 || value > 126;
        b.Payload.Add(value);
        if (b.Payload.Count > 446 && !(b.Payload.Count == 447 && !b.Framed && value == 13))
        { Errors.Add((entry.Sequence, "OVERSIZE")); EmitIncomplete(b, entry.Direction); }
    }
    private void EmitIncomplete(Buffer b, TraceDirection direction)
    {
        if (b.Payload.Count > 0) Packets.Add(new(b.First, b.Last, b.Elapsed, direction, Encoding.Latin1.GetString(b.Payload.ToArray()), "incomplete", false));
        b.Clear();
    }
    private void Emit(Buffer b, TraceDirection direction)
    {
        var check = b.Check.ToArray();
        var kind = !b.Framed ? "CRLF" : check.Length switch
        {
            0 when mode is Uc3500CheckMode.None or Uc3500CheckMode.Auto => "NONE",
            1 when check[0] == Xor(b.Payload) && mode is Uc3500CheckMode.Bcc or Uc3500CheckMode.Auto => "BCC",
            1 when check[0] == Sum(b.Payload) && mode is Uc3500CheckMode.Sum or Uc3500CheckMode.Auto => "SUM",
            2 when Encoding.ASCII.GetString(check) == Sum(b.Payload).ToString("X2") && mode is Uc3500CheckMode.Sum2 or Uc3500CheckMode.Auto => "SUM2",
            _ => "INVALID",
        };
        Packets.Add(new(b.First, b.Last, b.Elapsed, direction, Encoding.Latin1.GetString(b.Payload.ToArray()), kind, !b.Invalid && kind != "INVALID"));
        b.Clear();
    }
    private static byte Xor(List<byte> payload) { byte value = 3; foreach (var c in payload) value ^= c; return value; }
    private static byte Sum(List<byte> payload) { var value = 3; foreach (var c in payload) value += c; return (byte)value; }
    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F';
}
