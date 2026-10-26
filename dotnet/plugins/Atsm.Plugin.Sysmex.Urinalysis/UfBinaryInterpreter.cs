using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Plugins.Tracing;

namespace Atsm.Plugin.Sysmex.Urinalysis;

internal static class UfBinaryInterpreter
{
    internal const string ProfileId = "sysmex.uf.binary", VendorId = "sysmex.uf.binary";
    internal static InstrumentTraceProfile Profile { get; } = new(ProfileId, "UF · Binario", []);
    private static readonly BinaryFrameLayout Envelope = new(45, 41, 4, BinaryByteOrder.LittleEndian, 3)
        { RequiredBytes = [new(0, [2]), new(36, [2, 1, 0, 0, 0])] };
    private static readonly BinaryFrameLayout SubEnvelope = new(9, 5, 4, BinaryByteOrder.LittleEndian, 3)
        { RequiredBytes = [new(0, [2, 1, 0, 0, 0])] };
    internal static IReadOnlyDictionary<string, ProtocolText> Vocabulary { get; } = new Dictionary<string, string>
    {
        ["kind.UF_ACTIVITY"] = "Aviso del equipo", ["kind.UF_NOTICE"] = "Aviso del equipo", ["kind.UF_ALERTS"] = "Avisos del equipo",
        ["kind.UF_DATA"] = "Datos del equipo", ["kind.UF_BINARY"] = "Bloque binario",
        ["uf.binary"] = "Bloque binario · {0}", ["uf.data"] = "Datos del equipo · {0} bytes", ["uf.notice"] = "{0}",
        ["uf.header"] = "Cabecera del equipo", ["uf.identityChanged"] = "Cambio de identificador: {0} → {1}", ["uf.value.Kind"] = "Aviso del equipo", ["uf.state.Notice"] = "Aviso",
        ["uf.need.DBS_LANG_01746"] = "Chequeo de fondo requerido", ["uf.need.DBS_LANG_02215"] = "Limpieza requerida",
        ["uf.state.Need"] = "Requiere atención", ["uf.unit.None"] = "—",
        ["finding.ATSM-UF-CONTROL-DBS_LANG_01746"] = "Chequeo de fondo requerido",
        ["finding.ATSM-UF-CONTROL-DBS_LANG_02215"] = "Limpieza requerida",
    }.ToDictionary(p => p.Key, p => new ProtocolText(p.Key, p.Value, ""));
    internal sealed record Notice(string Code, string Label, string Qualifier = "", string Context = "");
    internal sealed record Block(int Offset, long First, long Last, TimeSpan At, TraceDirection Direction, byte[] Data, string Kind,
        IReadOnlyDictionary<string, string> Fields, IReadOnlyList<Notice> Notices);
    internal static IReadOnlyList<Block> Decode(IReadOnlyList<TraceEntry> entries)
    {
        var blocks = new List<Block>();
        var buffers = new Dictionary<TraceDirection, (MemoryStream Data, List<(int Offset, TraceEntry Entry)> Reads)>();
        void Flush()
        {
            foreach (var (direction, value) in buffers)
            {
                var data = value.Data.ToArray(); var starts = value.Reads.Select(r => r.Offset).ToArray();
                TraceEntry At(int offset) { var i = Array.BinarySearch(starts, offset); return value.Reads[i < 0 ? ~i - 1 : i].Entry; }
                for (var i = 0; i < data.Length; i++)
                {
                    var length = 0; var kind = ""; var fields = new Dictionary<string, string>(); var notices = new List<Notice>();
                    // Fixed outer header and a length-delimited sub-envelope. ETX inside the payload is data.
                    if (BinaryWireParser.TryReadFrame(data.AsSpan(i), Envelope, out var envelope)
                        && CString(data.AsSpan(i + 1, 20)) is { Length: > 0 } identity && CString(data.AsSpan(i + 21, 12)) is { Length: > 0 } secondary)
                    {
                            var size = envelope.PayloadLength; length = envelope.TotalLength; var type = data[i + 33] | data[i + 34] << 8 | data[i + 35] << 16;
                            fields["device.identifier"] = identity; fields["device.secondary"] = secondary; fields["message.type"] = type.ToString(CultureInfo.InvariantCulture);
                            fields["payload.length"] = size.ToString(CultureInfo.InvariantCulture);
                            kind = ReadPayload(data.AsSpan(i + envelope.PayloadOffset, size), type, fields, notices);
                    }
                    if (length == 0 && BinaryWireParser.TryReadFrame(data.AsSpan(i), SubEnvelope, out var sub)
                        && sub.PayloadLength is 102 or 1500 or 376 or 262)
                    {
                            length = sub.TotalLength;
                            kind = ReadPayload(data.AsSpan(i + sub.PayloadOffset, sub.PayloadLength), sub.PayloadLength switch { 1500 => 6, 376 => 11, 262 => 12, _ => 2 }, fields, notices);
                    }
                    // The monitor can begin inside an envelope: retain recognizable isolated records.
                    if (length == 0 && i + 266 <= data.Length && data[i] == 1 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 265] == 3
                        && ReadNeed(data.AsSpan(i + 3, 262), fields, notices)) { length = 266; kind = "UF_ACTIVITY"; }
                    if (length == 0 && i + 13 <= data.Length && data[i] == 2 && data.AsSpan(i + 1, 5).IndexOfAnyExcept((byte)0) < 0
                        && data[i + 8] is 90 or 100 && data.AsSpan(i + 9, 3).IndexOfAnyExcept((byte)0) < 0 && data[i + 12] == 3)
                    {
                        length = 13; kind = "UF_BINARY";
                        fields["variant.offset8.u32le"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 8, 4)).ToString(CultureInfo.InvariantCulture);
                        fields["field.offset6.u16le"] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 6, 2)).ToString(CultureInfo.InvariantCulture);
                    }
                    if (length == 0) continue;
                    var first = At(i); var last = At(i + length - 1);
                    blocks.Add(new(i, first.Sequence, last.Sequence, last.Elapsed, direction, data.AsSpan(i, length).ToArray(), kind, fields, notices)); i += length - 1;
                }
                value.Data.Dispose();
            }
            buffers.Clear();
        }
        foreach (var entry in entries)
        {
            if (entry.Direction == TraceDirection.Event) { Flush(); continue; }
            if (entry.Data.Length == 0) continue;
            if (!buffers.TryGetValue(entry.Direction, out var value)) buffers[entry.Direction] = value = (new(), []);
            value.Reads.Add(((int)value.Data.Length, entry)); value.Data.Write(entry.Data.Span);
        }
        Flush(); return blocks.OrderBy(b => b.Last).ThenBy(b => b.Offset).ToArray();
    }
    private static string ReadPayload(ReadOnlySpan<byte> payload, int type, Dictionary<string, string> fields, List<Notice> notices)
    {
        if (type == 12 && payload.Length == 262 && ReadNeed(payload, fields, notices)) return "UF_ACTIVITY";
        if (type == 11 && payload.Length == 376 && CString(payload.Slice(62, 200)) is { Length: > 0 } label
            && CString(payload.Slice(262, 114)) is { } resource && Resource(resource))
        { notices.Add(new(resource[..14], label, resource[14..])); fields["reported.label"] = label; fields["reported.resource"] = resource; return "UF_NOTICE"; }
        if (type == 6 && payload.Length == 1500)
        {
            for (var slot = 0; slot < 10; slot++)
            {
                var record = payload.Slice(slot * 150, 150);
                if (CString(record.Slice(10, 100)) is not { Length: > 0 } text) continue;
                var code = "0x" + BinaryPrimitives.ReadUInt32LittleEndian(record).ToString("X8", CultureInfo.InvariantCulture);
                notices.Add(new(code, text)); fields[$"notice.{slot}.code"] = code; fields[$"notice.{slot}.text"] = text;
            }
            if (notices.Count > 0) return "UF_ALERTS";
        }
        return "UF_DATA";
    }
    private static bool ReadNeed(ReadOnlySpan<byte> payload, Dictionary<string, string> fields, List<Notice> notices)
    {
        if (CString(payload.Slice(34, 50)) is not { Length: > 0 } label || CString(payload.Slice(84, 64)) is not { } resource || !Resource(resource)
            || CString(payload.Slice(148, 114)) is not { } context || !Resource(context)) return false;
        fields["reported.label"] = label; fields["reported.resource"] = resource; fields["reported.context"] = context;
        fields["resource.key"] = resource[..14]; fields["reported.qualifier"] = resource[14..]; notices.Add(new(resource[..14], label, resource[14..], context)); return true;
    }
    private static string? CString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0); if (end < 0 || bytes[end..].IndexOfAnyExcept((byte)0) >= 0) return null;
        var text = bytes[..end]; if (text.IndexOfAnyInRange((byte)0, (byte)31) >= 0 || text.Contains((byte)127)) return null;
        try { return new UTF8Encoding(false, true).GetString(text).TrimEnd(); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(text).TrimEnd(); }
    }
    private static bool Resource(string text) => text.Length >= 14 && text.StartsWith("DBS_LANG_", StringComparison.Ordinal)
        && text.AsSpan(9, 5).IndexOfAnyExceptInRange('0', '9') < 0;
    private static bool KnownNeed(Notice notice) => notice.Qualifier == "Necesidad" && notice.Context == "DBS_LANG_01745" && Vocabulary.ContainsKey("uf.need." + notice.Code);
    private static ProtocolExplanation Meaning(Notice notice) => KnownNeed(notice) ? new(Vocabulary["uf.need." + notice.Code], [])
        : new(Vocabulary["uf.notice"], [notice.Label + (notice.Qualifier.Length > 0 ? " · " + notice.Qualifier : "")]);
    internal static bool Recognizes(IReadOnlyList<TraceSegment> segments) => Decode(segments.Select((s, i) =>
        new TraceEntry(i, TimeSpan.Zero, s.Timestamp ?? DateTimeOffset.UnixEpoch, s.Direction, s.Data)).ToArray()).Any(b => b.Fields.ContainsKey("device.identifier") || b.Notices.Count > 0);
    internal static TraceInterpretation Interpret(IReadOnlyList<TraceEntry> entries, InstrumentRef instrument, Guid traceId)
    {
        var messages = new List<InstrumentTraceMessage>(); var events = new List<InstrumentTraceEvent>(); var observations = new List<InstrumentObservation>(); var diagnostics = new List<AtsmDiagnostic>();
        var source = entries.ToDictionary(e => e.Sequence);
        var identifiers = new Dictionary<TraceDirection, string>();
        foreach (var block in Decode(entries))
        {
            var text = block.Notices.Count == 1 ? Meaning(block.Notices[0]) : block.Notices.Count > 1
                ? new ProtocolExplanation(Vocabulary["uf.notice"], [string.Join(" · ", block.Notices.Select(n => n.Label))])
                : block.Kind == "UF_BINARY" ? new(Vocabulary["uf.binary"], [block.Fields["variant.offset8.u32le"]])
                : new(Vocabulary["uf.data"], [block.Fields.GetValueOrDefault("payload.length", block.Data.Length.ToString(CultureInfo.InvariantCulture))]);
            messages.Add(new(block.First, block.Last, block.Direction, block.Kind, Convert.ToHexString(block.Data), text, block.Fields));
            events.Add(new(ProtocolEventLevel.Message, block.First, block.Last, block.At, block.Direction, "uf.binary." + block.Kind, text));
            if (block.Fields.TryGetValue("device.identifier", out var identifier))
            {
                var changed = identifiers.TryGetValue(block.Direction, out var previous) && previous != identifier;
                events.Add(new(ProtocolEventLevel.Link, block.First, block.First, source[block.First].Elapsed, block.Direction,
                    changed ? "uf.identity-changed" : "uf.header", changed ? new(Vocabulary["uf.identityChanged"], [previous!, identifier]) : new(Vocabulary["uf.header"], [])));
                identifiers[block.Direction] = identifier;
            }
            for (var slot = 0; slot < block.Notices.Count; slot++)
            {
                var notice = block.Notices[slot]; var need = KnownNeed(notice); var meaning = Meaning(notice);
                var system = CodeSystems.Vendor(VendorId); var kind = need ? "need" : "notice";
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{traceId}|{block.Direction}|{block.First}|{block.Offset}|{slot}|{notice.Code}"))[..16]);
                var metadata = new Dictionary<string, string>(block.Fields) { ["notice.code"] = notice.Code, ["notice.label"] = notice.Label };
                observations.Add(new(id, instrument, new(""), CodeSet.Of(new(notice.Code, notice.Label, system)),
                    new(need ? notice.Qualifier : notice.Label, Coded: new(notice.Code, null, system + ":" + kind)), null, [new(notice.Code, null, system + ":" + kind)],
                    notice.Qualifier, InstrumentStatusMeaning.Unknown, "DEVICE_NOTICE", null, null, null, [], [], new(metadata), new(traceId, block.First, block.Last)));
                var code = need ? "ATSM-UF-CONTROL-" + notice.Code : "ATSM-UF-DEVICE-NOTICE";
                diagnostics.Add(new(code, AtsmSeverity.Information, string.Format(CultureInfo.InvariantCulture, meaning.Text.Fallback, meaning.Arguments.Cast<object>().ToArray()), "entry:" + block.Last));
            }
        }
        return new(messages, events.OrderBy(e => e.LastSequence).ThenBy(e => e.FirstSequence).ToArray(), observations, diagnostics);
    }
}
