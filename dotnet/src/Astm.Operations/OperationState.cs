namespace Astm.Operations;

public enum OperationState { Pending, Running, Completed, Failed, Cancelled, Unknown }
public enum OperationEvidence { None, Transport, MessageAcknowledged, ApplicationAccepted, DeviceReported, ContentValidated }
public sealed record OperationReceipt(Guid OperationId, string Purpose, OperationState State, OperationCompletionScope Scope,
    OperationEvidence Evidence, bool RequiresChannel, long Sequence, string Code);
public sealed record OperationSnapshot(long Revision, bool IsObserving, bool CoverageComplete, bool LinkInProgress,
    IReadOnlyList<OperationReceipt> Operations)
{
    public bool? OperationInProgress => Operations.Any(o => o.State is OperationState.Pending or OperationState.Running) ? true
        : !IsObserving || !CoverageComplete || Operations.Any(o => o.State == OperationState.Unknown) ? null : false;
    public bool? ChannelOperationInProgress => LinkInProgress || Operations.Any(o => o.RequiresChannel && o.State is OperationState.Pending or OperationState.Running) ? true
        : !IsObserving || !CoverageComplete || Operations.Any(o => o.RequiresChannel && o.State == OperationState.Unknown) ? null : false;
    public bool NoPendingOperations => ChannelOperationInProgress == false;
}

/// <summary>Thread-safe ledger of evidence, not a protocol interpreter. Unknown/EOF never turn into idle.</summary>
public sealed class OperationTracker(int maxTrackedOperations = 4096)
{
    private readonly int _limit = maxTrackedOperations > 0 ? maxTrackedOperations : throw new ArgumentOutOfRangeException(nameof(maxTrackedOperations));
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OperationReceipt> _operations = [];
    private long _revision;
    private bool _observing = true, _complete, _link;
    public OperationSnapshot Current { get { lock (_gate) return new(_revision, _observing, _complete, _link, Array.AsReadOnly(_operations.Values.ToArray())); } }
    public void Record(OperationReceipt receipt)
    {
        if (receipt.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(receipt.Purpose) || receipt.Sequence < 0 ||
            !Enum.IsDefined(receipt.State) || !Enum.IsDefined(receipt.Scope) || !Enum.IsDefined(receipt.Evidence)) throw new ArgumentException("operation.receipt.invalid");
        lock (_gate)
        {
            if (!_observing) throw new InvalidOperationException("operation.observation.ended");
            if (!_operations.ContainsKey(receipt.OperationId) && _operations.Count >= _limit)
            { Invalidate(); throw new InvalidOperationException("operation.observation.capacity"); }
            if (_operations.TryGetValue(receipt.OperationId, out var previous) && receipt.Sequence < previous.Sequence) throw new InvalidOperationException("operation.event.stale");
            _operations[receipt.OperationId] = receipt; _revision++;
            var terminal = _operations.Values.Where(o => o.State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled).OrderBy(o => o.Sequence).ToArray();
            foreach (var old in terminal.Take(Math.Max(0, terminal.Length - 64))) _operations.Remove(old.OperationId);
        }
    }
    public void SetLink(bool active) { lock (_gate) { _link = active; _revision++; } }
    public void Synchronize() { lock (_gate) { if (_observing) { _complete = true; _revision++; } } }
    public void Invalidate()
    {
        lock (_gate)
        {
            _complete = false;
            foreach (var pair in _operations.ToArray()) if (pair.Value.State is OperationState.Pending or OperationState.Running)
                _operations[pair.Key] = pair.Value with { State = OperationState.Unknown, Code = "operation.observation.gap" };
            _revision++;
        }
    }
    public void Complete() { lock (_gate) { Invalidate(); _observing = false; _link = false; _revision++; } }
}
