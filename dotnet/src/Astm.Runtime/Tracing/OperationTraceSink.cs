using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;

namespace Astm.Runtime.Tracing;

/// <summary>Custody first, ordered observation second. A failed capture/decoder invalidates observation and propagates.</summary>
public sealed class OperationTraceSink(ITraceSink custody, IMedicalOperationObserver observer, TimeProvider? clock = null,
    Action<Astm.Operations.OperationSnapshot>? changed = null) : ITraceSink
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly long _start = (clock ?? TimeProvider.System).GetTimestamp();
    public Guid TraceId => custody.TraceId;
    public void EnsureReady() => custody.EnsureReady();
    public long Write(TraceDirection direction, ReadOnlySpan<byte> data, string? code = null, string? detail = null)
    {
        lock (_gate)
        {
            try
            {
                var sequence = custody.Write(direction, data, code, detail);
                observer.Feed(new(sequence, _clock.GetElapsedTime(_start), _clock.GetUtcNow(), direction, data.ToArray(), code, detail));
                changed?.Invoke(observer.Current);
                return sequence;
            }
            catch { observer.Invalidate(); throw; }
        }
    }
}
