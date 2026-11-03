using Astm.Abstractions.Tracing;
using Astm.Formats.Medical.Model;
using Astm.Plugins;

namespace Astm.Plugin.Sysmex.Urinalysis;

internal sealed class Uc3500StreamReader(Uc3500Interpreter interpreter, Uc3500CheckMode mode,
    InstrumentRef instrument, Guid traceId, TimeZoneInfo zone) : IInstrumentTraceReader
{
    private readonly Uc3500WireDecoder _decoder = new(mode);
    private readonly Uc3500Interpreter.InterpretationState _state = new();
    private bool _learned = mode != Uc3500CheckMode.Auto;
    public TraceInterpretation Feed(TraceEntry entry) { _decoder.Feed(entry); return Take(); }
    public TraceInterpretation Complete() { _decoder.Complete(); return Take(); }
    private TraceInterpretation Take()
    {
        if (!_learned && _decoder.Packets.FirstOrDefault(p => p.Check is "BCC" or "SUM" or "SUM2") is { } first)
        {
            _decoder.UseMode(first.Check switch { "BCC" => Uc3500CheckMode.Bcc, "SUM" => Uc3500CheckMode.Sum, _ => Uc3500CheckMode.Sum2 });
            _learned = true;
        }
        var decoded = interpreter.InterpretDecoded(_decoder, _state, instrument, traceId, zone);
        _decoder.Packets.Clear(); _decoder.Controls.Clear(); _decoder.Errors.Clear();
        return decoded;
    }
}

internal sealed class UfStreamReader(InstrumentRef instrument, Guid traceId, int maxBytes) : IInstrumentTraceReader
{
    private readonly List<TraceEntry> _entries = [];
    private readonly Dictionary<long, int> _offsets = [];
    private int _bytes;
    public TraceInterpretation Feed(TraceEntry entry)
    {
        if (entry.Direction == TraceDirection.Event || entry.Data.IsEmpty) return new([], [], [], []);
        _entries.Add(entry with { Data = entry.Data.ToArray() }); _bytes += entry.Data.Length;
        if (_bytes > maxBytes || _entries.Count > maxBytes) throw new IOException("uf.stream.buffer-limit");
        return Take(false);
    }
    public TraceInterpretation Complete() => Take(true);
    private TraceInterpretation Take(bool complete)
    {
        var blocks = UfBinaryInterpreter.Decode(_entries, streaming: !complete);
        var decoded = UfBinaryInterpreter.InterpretBlocks(_entries, blocks.Select(block => block with
        { Offset = block.FirstByteOffset + _offsets.GetValueOrDefault(block.First),
            FirstByteOffset = block.FirstByteOffset + _offsets.GetValueOrDefault(block.First) }).ToArray(), instrument, traceId);
        foreach (var direction in new[] { TraceDirection.Received, TraceDirection.Transmitted })
        {
            var consumed = blocks.Where(b => b.Direction == direction).Select(b => b.Offset + b.Data.Length).DefaultIfEmpty(0).Max();
            for (var i = 0; i < _entries.Count && consumed > 0;)
            {
                var entry = _entries[i];
                if (entry.Direction != direction) { i++; continue; }
                var take = Math.Min(consumed, entry.Data.Length); consumed -= take; _bytes -= take;
                if (take == entry.Data.Length) { _entries.RemoveAt(i); _offsets.Remove(entry.Sequence); }
                else { _entries[i] = entry with { Data = entry.Data[take..] }; _offsets[entry.Sequence] = _offsets.GetValueOrDefault(entry.Sequence) + take; i++; }
            }
        }
        if (complete && _bytes > 0) return decoded with { Diagnostics = decoded.Diagnostics.Append(
            new("ASTM-UF-STREAM-INCOMPLETE", Astm.Abstractions.Diagnostics.AstmSeverity.Warning, "Incomplete or unrecognized tail; original retained in trace.")).ToArray() };
        return decoded;
    }
}
