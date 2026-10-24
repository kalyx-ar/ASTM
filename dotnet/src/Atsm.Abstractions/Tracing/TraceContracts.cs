namespace Atsm.Abstractions.Tracing;

public enum TraceDirection { Received, Transmitted, Event }

/// <param name="Elapsed">Monotonic time since the trace started; comparable only within the same trace.</param>
public readonly record struct TraceEntry(
    long Sequence,
    TimeSpan Elapsed,
    DateTimeOffset Utc,
    TraceDirection Direction,
    ReadOnlyMemory<byte> Data,
    string? Code = null,
    string? Detail = null);

/// <summary>Range of entries of one trace that supports a derived fact.</summary>
public sealed record TraceRef(Guid TraceId, long FirstSequence, long LastSequence)
{
    public static readonly TraceRef None = new(Guid.Empty, -1, -1);
}

public interface ITraceSink
{
    Guid TraceId { get; }

    /// <summary>The orchestrating application must prepare storage before starting communications.
    /// Throw if capture is unavailable. ATSM never creates a fallback file or opens a database.</summary>
    void EnsureReady();

    /// <summary>Records an entry and returns its sequence number. Failures must propagate; the host
    /// defines the durability of a successful write and owns storage, encryption and retention.</summary>
    long Write(TraceDirection direction, ReadOnlySpan<byte> data, string? code = null, string? detail = null);
}
