using Atsm.Abstractions.Model;

namespace Atsm.Abstractions.Hosting;

public enum IngestState { Accepted, Duplicate, Rejected, Deferred }

public sealed record IngestOutcome(IngestState State, string? Code = null, string? PreviousReference = null)
{
    public static readonly IngestOutcome Accepted = new(IngestState.Accepted);
}

public enum QueryAnswerState { Orders, NoInformation, Unavailable }

public sealed record OrderQueryAnswer(QueryAnswerState State, IReadOnlyList<WorkOrder> Orders)
{
    public static readonly OrderQueryAnswer NoInformation = new(QueryAnswerState.NoInformation, []);
    public static readonly OrderQueryAnswer Unavailable = new(QueryAnswerState.Unavailable, []);
}

public enum OrderDeliveryState { Sent, Rejected, Uncertain }

public sealed record OrderDeliveryOutcome(OrderDeliveryState State, string? Code = null);

public sealed record CodeMapping(string VendorCode, IReadOnlyList<CodedValue> Codes, CodedValue? Unit = null);

/// <summary>Versioned mapping (LIVD or local equivalences) the host publishes for one instrument model.</summary>
public sealed record CodeMappingSet(string Version, string VendorSystem, IReadOnlyList<CodeMapping> Mappings);

/// <summary>
/// Required workflow port for hosting ATSM. Optional quality read/resource ports remain separate.
/// Implementations live in the host repository.
/// </summary>
public interface ILabWorkflowHost
{
    /// <summary>Synchronous host query. <paramref name="deadline"/> carries the instrument's response budget.</summary>
    ValueTask<OrderQueryAnswer> AnswerQueryAsync(OrderQuery query, CancellationToken deadline);

    /// <summary>Return <see cref="IngestState.Accepted"/> only once the entire batch, including quality
    /// context and resource references, is durably stored. A batch may contain references only;
    /// acceptance of a reference does not mean the external content has been fetched.</summary>
    ValueTask<IngestOutcome> IngestAsync(InstrumentObservationBatch batch, CancellationToken cancellationToken);

    IAsyncEnumerable<WorkOrder> PendingOrdersAsync(InstrumentRef instrument, CancellationToken cancellationToken);

    ValueTask AcknowledgeOrderAsync(string orderId, OrderDeliveryOutcome outcome, CancellationToken cancellationToken);

    ValueTask<CodeMappingSet?> MappingsAsync(InstrumentRef instrument, CancellationToken cancellationToken);
}
