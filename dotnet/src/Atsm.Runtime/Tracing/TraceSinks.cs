using System.Text.Json;
using Atsm.Abstractions.Tracing;

namespace Atsm.Runtime.Tracing;

/// <summary>Shared sequence and clock bookkeeping for trace sinks.</summary>
public abstract class TraceSinkBase : ITraceSink
{
    private readonly TimeProvider _clock;
    private readonly long _start;
    private readonly Lock _gate = new();
    private long _sequence;

    protected TraceSinkBase(Guid traceId, TimeProvider? clock)
    {
        TraceId = traceId;
        _clock = clock ?? TimeProvider.System;
        _start = _clock.GetTimestamp();
    }

    public Guid TraceId { get; }

    public long Write(TraceDirection direction, ReadOnlySpan<byte> data, string? code = null, string? detail = null)
    {
        lock (_gate)
        {
            var entry = new TraceEntry(_sequence++, _clock.GetElapsedTime(_start), _clock.GetUtcNow(), direction, data.ToArray(), code, detail);
            Append(entry);
            return entry.Sequence;
        }
    }

    /// <summary>Called under the sink lock, in sequence order.</summary>
    protected abstract void Append(TraceEntry entry);
}

public sealed class MemoryTraceSink(Guid? traceId = null, TimeProvider? clock = null)
    : TraceSinkBase(traceId ?? Guid.CreateVersion7(), clock)
{
    private readonly List<TraceEntry> _entries = [];

    public IReadOnlyList<TraceEntry> Snapshot()
    {
        lock (_entries)
            return _entries.ToArray();
    }

    protected override void Append(TraceEntry entry)
    {
        lock (_entries)
            _entries.Add(entry);
    }
}

/// <summary>
/// ATSM trace format v1 (<c>atsm-trace/1</c>): JSON Lines. The first line is a header; every following line
/// is one entry with bytes in base64. Gaps are never filled: a lost range is an <c>Event</c> entry.
/// </summary>
public sealed class JsonLinesTraceWriter : TraceSinkBase, IDisposable
{
    public const string Format = "atsm-trace/1";

    private readonly StreamWriter _writer;

    public JsonLinesTraceWriter(Stream stream, TraceHeader header, TimeProvider? clock = null)
        : base(header.TraceId, clock)
    {
        _writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _writer.WriteLine(JsonSerializer.Serialize(header with { Format = Format }, TraceJson.Options));
    }

    public void Dispose() => _writer.Dispose();

    protected override void Append(TraceEntry entry) =>
        _writer.WriteLine(JsonSerializer.Serialize(TraceLine.From(entry), TraceJson.Options));
}

public sealed record TraceHeader(
    Guid TraceId,
    string Source,
    string? InstrumentId,
    string? Profile,
    DateTimeOffset StartedAt,
    IReadOnlyDictionary<string, string>? Properties = null,
    string Format = JsonLinesTraceWriter.Format,
    string? ImportedFrom = null,
    IReadOnlyList<string>? Limitations = null);

/// <summary>Serialized form of one entry.</summary>
public sealed record TraceLine(long Seq, double Ms, DateTimeOffset Utc, string Dir, string? Data, string? Code, string? Detail)
{
    public static TraceLine From(TraceEntry entry) => new(
        entry.Sequence,
        entry.Elapsed.TotalMilliseconds,
        entry.Utc,
        entry.Direction switch { TraceDirection.Received => "rx", TraceDirection.Transmitted => "tx", _ => "ev" },
        entry.Data.IsEmpty ? null : Convert.ToBase64String(entry.Data.Span),
        entry.Code,
        entry.Detail);

    public TraceEntry ToEntry() => new(
        Seq,
        TimeSpan.FromMilliseconds(Ms),
        Utc,
        Dir switch { "rx" => TraceDirection.Received, "tx" => TraceDirection.Transmitted, _ => TraceDirection.Event },
        Data is null ? ReadOnlyMemory<byte>.Empty : Convert.FromBase64String(Data),
        Code,
        Detail);
}

public static class TraceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class TraceFile
{
    public static (TraceHeader Header, List<TraceEntry> Entries) Read(TextReader reader)
    {
        var first = reader.ReadLine() ?? throw new InvalidDataException("Empty trace.");
        var header = JsonSerializer.Deserialize<TraceHeader>(first, TraceJson.Options)
            ?? throw new InvalidDataException("Invalid trace header.");
        if (header.Format != JsonLinesTraceWriter.Format)
            throw new InvalidDataException($"Unsupported trace format '{header.Format}'.");
        var entries = new List<TraceEntry>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            var parsed = JsonSerializer.Deserialize<TraceLine>(line, TraceJson.Options)
                ?? throw new InvalidDataException($"Invalid trace line {entries.Count + 2}.");
            entries.Add(parsed.ToEntry());
        }
        return (header, entries);
    }

    public static void Write(TextWriter writer, TraceHeader header, IEnumerable<TraceEntry> entries)
    {
        writer.Write(JsonSerializer.Serialize(header with { Format = JsonLinesTraceWriter.Format }, TraceJson.Options));
        writer.Write('\n');
        foreach (var entry in entries)
        {
            writer.Write(JsonSerializer.Serialize(TraceLine.From(entry), TraceJson.Options));
            writer.Write('\n');
        }
    }
}
