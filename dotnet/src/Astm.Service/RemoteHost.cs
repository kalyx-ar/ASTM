using System.Runtime.CompilerServices;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Hosting;
using Astm.Formats.Medical.Model;

namespace Astm.Service;

internal sealed class RemoteWorkflowHost(IServiceCallbacks peer, Guid sessionId, TimeSpan queryBudget, SessionCorrelation correlation) : ILabWorkflowHost
{
    public Guid? ReceptionId { get; set; }
    public async ValueTask<OrderQueryAnswer> AnswerQueryAsync(OrderQuery query, CancellationToken deadline)
    {
        try { return await peer.InvokeAsync<OrderQuery, OrderQueryAnswer>(sessionId, "workflow.query", query, deadline, DateTimeOffset.UtcNow + queryBudget,
            correlation with { ReceptionId = ReceptionId }).ConfigureAwait(false); }
        catch (ServiceException error) when (error.Code == "callback.timeout") { throw new OperationCanceledException("query.deadline", error, deadline); }
    }
    public ValueTask<IngestOutcome> IngestAsync(InstrumentObservationBatch batch, CancellationToken token) =>
        peer.InvokeAsync<InstrumentObservationBatch, IngestOutcome>(sessionId, "workflow.ingest", batch, token,
            correlation: correlation with { ReceptionId = ReceptionId });
    public async IAsyncEnumerable<WorkOrder> PendingOrdersAsync(InstrumentRef instrument, [EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; token.ThrowIfCancellationRequested(); yield break; }
    public ValueTask AcknowledgeOrderAsync(string id, OrderDeliveryOutcome outcome, CancellationToken token) =>
        throw new NotSupportedException("service.orders.explicit-operations-only");
    public ValueTask<CodeMappingSet?> MappingsAsync(InstrumentRef instrument, CancellationToken token) => ValueTask.FromResult<CodeMappingSet?>(null);
}

/// <summary>The synchronous runtime boundary only returns after the host acknowledged the exact entry.</summary>
internal sealed class RemoteTraceSink(IServiceCallbacks peer, Guid sessionId, Guid generation, Guid traceId,
    IMedicalOperationObserver observer, CancellationToken token, Action<long> progress, SessionCorrelation correlation) : ITraceSink
{
    private readonly object _gate = new();
    private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
    private long _sequence;
    public Guid TraceId { get; } = traceId != Guid.Empty ? traceId : throw new ArgumentException("trace.id.required");
    public void EnsureReady()
    {
        var ready = peer.InvokeAsync<TraceReadyRequest, TraceReadyReply>(sessionId, "trace.ready", new(TraceId, generation), token, correlation: correlation)
            .AsTask().GetAwaiter().GetResult();
        if (ready.TraceId != TraceId || !ready.Ready) throw new ServiceException("trace.not-ready");
    }
    public long Write(TraceDirection direction, ReadOnlySpan<byte> data, string? code = null, string? detail = null)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var entry = new TraceEntry(_sequence, System.Diagnostics.Stopwatch.GetElapsedTime(_started), DateTimeOffset.UtcNow,
                direction, data.ToArray(), code, detail);
            var reply = peer.InvokeAsync<TraceAppendRequest, TraceAppendReply>(sessionId, "trace.append", new(TraceId, generation, entry), token, correlation: correlation)
                .AsTask().GetAwaiter().GetResult();
            if (!reply.Stored || reply.TraceId != TraceId || reply.Sequence != entry.Sequence) throw new ServiceException("trace.not-stored");
            var recorded = _sequence++;
            progress(recorded);
            observer.Feed(entry);
            return recorded;
        }
    }
}
