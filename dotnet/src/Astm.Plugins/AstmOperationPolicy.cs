using Astm.Abstractions.Tracing;
using Astm.Protocol.Lis02;

namespace Astm.Plugins;

public sealed record AstmOperationDecision(TraceOperationState State, string Code);

public sealed record InstrumentOperationProfile(string Id, string Scope, IReadOnlyList<string> Evidence);

/// <summary>Optional capability of an instrument plugin. The host explicitly selects the operation scope;
/// an instrument's message dialect alone cannot select a business boundary.</summary>
public interface IInstrumentOperationPolicyProvider
{
    IReadOnlyList<InstrumentOperationProfile> OperationProfiles(string instrumentProfileId);
    IAstmOperationPolicy CreateOperationPolicy(string instrumentProfileId, string operationProfileId);
}

/// <summary>One instance per observed trace. Called only for complete LIS02 messages carried by an explicitly
/// acknowledged LIS01 transmission. The plugin owns correlation and the definition of a business operation.</summary>
public interface IAstmOperationPolicy
{
    string Id { get; }
    IReadOnlyList<string> Evidence { get; }
    AstmOperationDecision OnTransmission(TraceDirection direction, IReadOnlyList<Lis02Message> messages, TraceRef trace);
    void Reset();
}

/// <summary>Explicit opt-in scope: each unsolicited results message is one operation, not an entire analyzer workflow.
/// A query keeps this policy pending: unrelated results cannot prove its answer. Use a vendor policy for multi-turn cycles.</summary>
public sealed class AstmResultsMessageOperationPolicy : IAstmOperationPolicy
{
    private bool _queryPending;
    private bool _unsupported;
    public string Id => "astm.results-message";
    public IReadOnlyList<string> Evidence { get; } =
        ["CLSI LIS01-A2 explicit frame acknowledgement and termination", "CLSI LIS02-A2 H/R/L structure",
         "EDC #414: selected scope is one unsolicited results message; no assertion about a wider business cycle"];

    public AstmOperationDecision OnTransmission(TraceDirection direction, IReadOnlyList<Lis02Message> messages, TraceRef trace)
    {
        if (messages.Any(m => m.OfType('Q').Any())) _queryPending = true;
        if (_unsupported) return new(TraceOperationState.Unknown, "ASTM-OP-WORKFLOW-UNSUPPORTED");
        if (_queryPending) return new(TraceOperationState.InProgress, "ASTM-OP-QUERY-PENDING");
        var results = direction == TraceDirection.Received && messages.Count > 0 && messages.All(m =>
            m.Header is not null && m.IsTerminated && m.OfType('R').Any() &&
            m.Records.All(r => r.Type is 'H' or 'P' or 'O' or 'R' or 'C' or 'M' or 'L'));
        _unsupported = !results;
        return results ? new(TraceOperationState.Idle, "ASTM-OP-RESULTS-COMPLETE")
            : new(TraceOperationState.Unknown, "ASTM-OP-WORKFLOW-UNSUPPORTED");
    }

    public void Reset() { _queryPending = false; _unsupported = false; }
}
