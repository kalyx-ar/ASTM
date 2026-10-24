using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;

namespace Atsm.Testing;

/// <summary>Host double: stores batches, answers queries from a dictionary keyed by specimen id.</summary>
public sealed class InMemoryLabHost : ILabWorkflowHost
{
    private readonly ConcurrentQueue<InstrumentObservationBatch> _batches = new();
    private readonly ConcurrentQueue<OrderQuery> _queries = new();
    private readonly ConcurrentQueue<(string OrderId, OrderDeliveryOutcome Outcome)> _acknowledged = new();

    public ConcurrentDictionary<string, List<WorkOrder>> OrdersBySpecimen { get; } = new();

    public ConcurrentQueue<WorkOrder> Pending { get; } = new();

    public TimeSpan QueryDelay { get; set; }

    public CodeMappingSet? Mappings { get; set; }

    public IReadOnlyList<InstrumentObservationBatch> Batches => _batches.ToArray();

    public IReadOnlyList<OrderQuery> Queries => _queries.ToArray();

    public IReadOnlyList<(string OrderId, OrderDeliveryOutcome Outcome)> Acknowledged => _acknowledged.ToArray();

    public event Action? Changed;

    public async ValueTask<OrderQueryAnswer> AnswerQueryAsync(OrderQuery query, CancellationToken deadline)
    {
        _queries.Enqueue(query);
        if (QueryDelay > TimeSpan.Zero)
            await Task.Delay(QueryDelay, deadline).ConfigureAwait(false);
        var orders = query.Specimens.SelectMany(s => OrdersBySpecimen.TryGetValue(s.SpecimenId, out var list) ? list : []).ToList();
        Changed?.Invoke();
        return orders.Count == 0 ? OrderQueryAnswer.NoInformation : new OrderQueryAnswer(QueryAnswerState.Orders, orders);
    }

    public ValueTask<IngestOutcome> IngestAsync(InstrumentObservationBatch batch, CancellationToken cancellationToken)
    {
        _batches.Enqueue(batch);
        Changed?.Invoke();
        return ValueTask.FromResult(IngestOutcome.Accepted);
    }

    public async IAsyncEnumerable<WorkOrder> PendingOrdersAsync(InstrumentRef instrument, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        while (Pending.TryDequeue(out var order))
            yield return order;
    }

    public ValueTask AcknowledgeOrderAsync(string orderId, OrderDeliveryOutcome outcome, CancellationToken cancellationToken)
    {
        _acknowledged.Enqueue((orderId, outcome));
        return ValueTask.CompletedTask;
    }

    public ValueTask<CodeMappingSet?> MappingsAsync(InstrumentRef instrument, CancellationToken cancellationToken) => ValueTask.FromResult(Mappings);
}
