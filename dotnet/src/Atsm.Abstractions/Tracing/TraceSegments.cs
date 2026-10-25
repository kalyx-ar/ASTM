namespace Atsm.Abstractions.Tracing;

/// <summary>What a segment carries: bytes as they travel on the link, application text without link framing, or an event.</summary>
public enum SegmentContent { Wire, Message, Event }

/// <summary>
/// Pure content extracted from any source (capture, vendor log, file). <see cref="Direction"/> is always from the
/// host's point of view: <see cref="TraceDirection.Received"/> means instrument → host.
/// </summary>
public sealed record TraceSegment(
    DateTimeOffset? Timestamp,
    TraceDirection Direction,
    SegmentContent Content,
    ReadOnlyMemory<byte> Data,
    string? Code = null,
    string? Detail = null);

/// <summary>Which side wrote the log. Vendor logs written by the instrument see "received" as host → instrument.</summary>
public enum RecordedBy { Host, Instrument }

public sealed record LogAdapterOptions(RecordedBy RecordedBy = RecordedBy.Host)
{
    /// <summary>Offset of the timestamps written in the log, which usually have none.</summary>
    public TimeSpan UtcOffset { get; init; }

    /// <summary>Optional adapter-specific selection, e.g. one instrument of a multi-instrument log.</summary>
    public string? Filter { get; init; }
}

public sealed record LogExtraction(
    IReadOnlyList<TraceSegment> Segments,
    int Skipped,
    IReadOnlyList<string> Findings,
    string? InstrumentHint = null);

/// <summary>Turns one kind of log into pure segments. Plugins provide adapters for the logs of their model series.</summary>
public interface ITraceLogAdapter
{
    string Id { get; }

    string Description { get; }

    IReadOnlyList<string> Limitations { get; }

    LogExtraction Extract(ReadOnlySpan<byte> log, LogAdapterOptions options);
}

public interface ITraceLogAdapterProvider
{
    IReadOnlyList<ITraceLogAdapter> LogAdapters { get; }
}

/// <summary>Optional content signature. An extension alone never proves a recorder format.</summary>
public interface ITraceLogProbe
{
    bool Recognizes(ReadOnlySpan<byte> content);
}
