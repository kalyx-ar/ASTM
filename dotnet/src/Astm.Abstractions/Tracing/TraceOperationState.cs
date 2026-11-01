namespace Astm.Abstractions.Tracing;

public enum TraceOperationState { Unknown, InProgress, Idle }

/// <summary>Immutable, payload-free state of one observed trace/generation. Unknown is never a declaration of idle.</summary>
public sealed record TraceOperationSnapshot(Guid TraceId, string? PolicyId, TraceOperationState State,
    bool? LinkInProgress, long LastSequence, long Revision, DateTimeOffset? LastObservedAt, string Code, bool IsObserving)
{
    public bool? OperationInProgress => State switch
    {
        TraceOperationState.InProgress => true,
        TraceOperationState.Idle => false,
        _ => null,
    };

    /// <summary>A current declaration for the selected policy, not a guarantee about other business workflows or devices.</summary>
    public bool NoPendingOperations => IsObserving && State == TraceOperationState.Idle && LinkInProgress == false;
}
