using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Urinalysis;

/// <summary>Passive partial reading of trace-derived records; not an active UF control implementation.</summary>
internal static class UfControlInterpreter
{
    internal const string ProfileId = "sysmex.uf.control.partial", VendorId = "sysmex.uf.control";
    internal const string Evidence = "Captura local UF-5000Control: disposición fija de avisos con recursos y bloques recurrentes de 13 bytes; estructura derivada de traza. Campos, integridad y roles físicos sin confirmar. Lectura pasiva, sin órdenes activas ni homologación de hardware.";
    internal static InstrumentTraceProfile Profile { get; } = new(ProfileId, "UF · Control binario (lectura parcial)", [Evidence]);
    internal static IReadOnlyDictionary<string, ProtocolText> Vocabulary { get; } = CreateVocabulary();
    private static IReadOnlyDictionary<string, ProtocolText> CreateVocabulary()
    {
        var result = new Dictionary<string, ProtocolText>();
        foreach (var (key, value) in new (string, string)[]
        {
            ("kind.UF_ACTIVITY", "Aviso del equipo"), ("kind.UF_BINARY", "Bloque binario"),
            ("uf.binary", "Bloque binario recurrente · variante {0}"), ("uf.notice", "Aviso del equipo: {0}"),
            ("uf.need.DBS_LANG_01746", "Chequeo de fondo requerido"), ("uf.need.DBS_LANG_02215", "Limpieza requerida"),
            ("uf.value.Kind", "Aviso de mantenimiento"), ("uf.state.Need", "Requiere atención"), ("uf.unit.None", "—"),
            ("finding.ATSM-UF-CONTROL-PARTIAL", "Interpretación parcial del control UF; hay campos y bloques pendientes de identificar."),
            ("finding.ATSM-UF-CONTROL-DBS_LANG_01746", "Chequeo de fondo requerido"),
            ("finding.ATSM-UF-CONTROL-DBS_LANG_02215", "Limpieza requerida"),
        }) result.Add(key, new(key, value, Evidence));
        return result;
    }
    internal sealed record Block(int Offset, long First, long Last, TimeSpan At, TraceDirection Direction, byte[] Data, string Kind,
        IReadOnlyDictionary<string, string> Fields);
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
                    var length = 0; var kind = ""; var fields = new Dictionary<string, string>();
                    if (i + 266 <= data.Length && data[i] == 1 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 265] == 3
                        && CString(data.AsSpan(i + 87, 64)) is { } resource && Resource(resource)
                        && CString(data.AsSpan(i + 151, 114)) is { } context && Resource(context)
                        && CString(data.AsSpan(i + 37, 50)) is { Length: > 0 } label)
                    {
                        length = 266; kind = "UF_ACTIVITY";
                        fields["reported.label"] = label; fields["reported.resource"] = resource; fields["reported.context"] = context;
                        fields["resource.key"] = resource[..14]; fields["reported.qualifier"] = resource[14..];
                    }
                    else if (i + 13 <= data.Length && data[i] == 2 && data.AsSpan(i + 1, 5).IndexOfAnyExcept((byte)0) < 0
                        && data[i + 8] is 90 or 100 && data.AsSpan(i + 9, 3).IndexOfAnyExcept((byte)0) < 0 && data[i + 12] == 3)
                    {
                        length = 13; kind = "UF_BINARY";
                        fields["variant.offset8.u32le"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 8, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        fields["field.offset6.u16le"] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 6, 2)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (length == 0) continue;
                    fields["interpretation"] = "trace-derived; partial"; fields["integrity"] = "not established";
                    var first = At(i); var last = At(i + length - 1);
                    blocks.Add(new(i, first.Sequence, last.Sequence, last.Elapsed, direction, data.AsSpan(i, length).ToArray(), kind, fields));
                    i += length - 1;
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
        Flush();
        return blocks.OrderBy(b => b.Last).ThenBy(b => b.Offset).ToArray();
    }
    private static string? CString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0); if (end < 0 || bytes[end..].IndexOfAnyExcept((byte)0) >= 0) return null;
        var text = bytes[..end];
        if (text.IndexOfAnyExceptInRange((byte)32, (byte)126) >= 0) return null;
        return Encoding.ASCII.GetString(text).TrimEnd();
    }
    private static bool Resource(string text) => text.Length >= 14 && text.StartsWith("DBS_LANG_", StringComparison.Ordinal)
        && text.AsSpan(9, 5).IndexOfAnyExceptInRange('0', '9') < 0;
    internal static bool Recognizes(IReadOnlyList<TraceSegment> segments) => Decode(segments.Select((s, i) =>
        new TraceEntry(i, TimeSpan.Zero, s.Timestamp ?? DateTimeOffset.UnixEpoch, s.Direction, s.Data)).ToArray()).Any(b => b.Kind == "UF_ACTIVITY");
    internal static TraceInterpretation Interpret(IReadOnlyList<TraceEntry> entries, InstrumentRef instrument, Guid traceId)
    {
        var messages = new List<InstrumentTraceMessage>(); var events = new List<InstrumentTraceEvent>();
        var observations = new List<InstrumentObservation>(); var diagnostics = new List<AtsmDiagnostic>();
        foreach (var block in Decode(entries))
        {
            var knownNeed = block.Kind == "UF_ACTIVITY" && block.Fields["reported.qualifier"] == "Necesidad"
                && block.Fields["reported.context"] == "DBS_LANG_01745"
                && Vocabulary.ContainsKey("uf.need." + block.Fields["resource.key"]);
            var text = knownNeed ? new ProtocolExplanation(Vocabulary["uf.need." + block.Fields["resource.key"]], [])
                : block.Kind == "UF_ACTIVITY" ? new(Vocabulary["uf.notice"], [block.Fields["reported.label"]])
                : new(Vocabulary["uf.binary"], [block.Fields["variant.offset8.u32le"]]);
            messages.Add(new(block.First, block.Last, block.Direction, block.Kind, Convert.ToHexString(block.Data), text, block.Fields));
            events.Add(new(ProtocolEventLevel.Message, block.First, block.Last, block.At, block.Direction, "uf.control." + block.Kind, text));
            if (!knownNeed) continue;
            var code = block.Fields["resource.key"]; var system = CodeSystems.Vendor(VendorId);
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{traceId}|{block.Direction}|{block.First}|{block.Offset}|{code}"))[..16]);
            observations.Add(new(id, instrument, new(""), CodeSet.Of(new(code, block.Fields["reported.label"], system)),
                new(block.Fields["reported.qualifier"], Coded: new(code, null, system + ":need")), null, [new(code, null, system + ":need")],
                "Necesidad", InstrumentStatusMeaning.Unknown, "MAINTENANCE_NOTICE", null, null, null, [], [], new(block.Fields), new(traceId, block.First, block.Last)));
            diagnostics.Add(new("ATSM-UF-CONTROL-" + code, AtsmSeverity.Information, text.Text.Fallback, "entry:" + block.Last));
        }
        diagnostics.Add(new("ATSM-UF-CONTROL-PARTIAL", AtsmSeverity.Information, Vocabulary["finding.ATSM-UF-CONTROL-PARTIAL"].Fallback));
        return new(messages, events, observations, diagnostics);
    }
}
