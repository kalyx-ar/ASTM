using System.Globalization;
using Astm.Plugins;
using Astm.Plugins.Tracing;

namespace Astm.Plugin.Sysmex.Urinalysis;

/// <summary>UF field layout. Counters/levels are decoded values, not assigned reagent names or physical units.</summary>
internal static class UfBinaryFields
{
    private static uint Number(ReadOnlySpan<byte> data, int offset, int width)
    {
        if (!BinaryWireParser.TryReadUnsigned(data, offset, width, BinaryByteOrder.LittleEndian, out var value))
            throw new ArgumentOutOfRangeException(nameof(offset));
        return value;
    }
    private static string Decimal(uint value) => value.ToString(CultureInfo.InvariantCulture);
    private static InstrumentTraceField Field(int offset, int length, string code, string value, string text, params string[] args) =>
        new(offset, length, code, value, new(UfBinaryInterpreter.Vocabulary[text], args));
    internal static bool IsCounterLayout(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 102) return false;
        for (var i = 0; i < 7; i++) if (Number(payload, 18 + i * 12, 4) != i + 1) return false;
        return true;
    }
    internal static IReadOnlyList<InstrumentTraceField> Counters(ReadOnlySpan<byte> payload)
    {
        var fields = new List<InstrumentTraceField>();
        void Add(int offset, int width, string code, string value, string text, params string[] args) => fields.Add(Field(offset, width, code, value, text, args));
        var state = Decimal(Number(payload, 0, 2)); Add(0, 2, "status.code", state, "uf.field.state", state);
        for (var i = 1; i <= 2; i++)
        {
            var value = Decimal(Number(payload, i * 2, 2)); Add(i * 2, 2, "status.indicator." + i, value, "uf.field.indicator", i.ToString(), value);
        }
        // Unknown fields stay explicit; zero does not prove that a field is reserved or absent.
        var unknown = Convert.ToHexString(payload.Slice(6, 8)); Add(6, 8, "status.unknown.6", unknown, "uf.field.unknown", "6", unknown);
        var level = Decimal(Number(payload, 14, 4)); Add(14, 4, "status.level", level, "uf.field.level", level);
        for (var i = 1; i <= 7; i++)
        {
            var offset = 18 + (i - 1) * 12; var id = i.ToString(CultureInfo.InvariantCulture);
            Add(offset, 4, $"counter.{i}.id", id, "uf.field.slot", id);
            var flag = Decimal(Number(payload, offset + 4, 2)); Add(offset + 4, 2, $"counter.{i}.unknown", flag, "uf.field.unknown", (offset + 4).ToString(), flag);
            var count = Decimal(Number(payload, offset + 6, 2)); Add(offset + 6, 2, $"counter.{i}.value", count, "uf.field.counter", id, count);
            var amount = Decimal(Number(payload, offset + 8, 4)); Add(offset + 8, 4, $"counter.{i}.level", amount, "uf.field.counterLevel", id, amount);
        }
        return fields;
    }
    internal static IReadOnlyList<InstrumentTraceField> Read(ReadOnlySpan<byte> packet, int offset, int size, string kind, bool outer, IReadOnlyList<ProtocolExplanation> notices)
    {
        var fields = new List<InstrumentTraceField>();
        if (outer)
        {
            fields.Add(Field(0, 1, "envelope.start", "02", "uf.field.start"));
            fields.Add(Field(1, 32, "device.header", Convert.ToHexString(packet.Slice(1, 32)), "uf.header"));
            var type = Decimal(Number(packet, 33, 3)); fields.Add(Field(33, 3, "message.type", type, "uf.field.type", type));
        }
        fields.Add(Field(offset - 9, 1, "content.start", "02", "uf.field.start"));
        var tag = Decimal(Number(packet, offset - 8, 4)); fields.Add(Field(offset - 8, 4, "content.tag", tag, "uf.field.unknown", (offset - 8).ToString(), tag));
        fields.Add(Field(offset - 4, 4, "payload.length", size.ToString(), "uf.field.length", size.ToString()));
        if (kind == "UF_COUNTERS") fields.AddRange(Counters(packet.Slice(offset, size)).Select(f => f with { Offset = f.Offset + offset }));
        else if (notices.Count > 0)
        {
            // A partial read of a notice still belongs to that notice, not to an ASCII link command.
            foreach (var notice in notices) fields.Add(new(offset, size, "notice", "", notice));
        }
        else fields.Add(Field(offset, size, "payload", Convert.ToHexString(packet.Slice(offset, size)), "uf.field.payload"));
        fields.Add(Field(offset + size, 1, "envelope.end", "03", "uf.field.end"));
        return fields;
    }
}
