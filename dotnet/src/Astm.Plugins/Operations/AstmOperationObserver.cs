using System.Text;
using Astm.Abstractions.Tracing;
using Astm.Plugins;
using Astm.Protocol.Lis01;
using Astm.Protocol.Lis02;

namespace Astm.Tracing;

/// <summary>Incremental, read-only LIS01/LIS02 operation observation. Feed both directions in trace sequence order.
/// It never writes, opens a port, owns timers or requests control. Current is safe to read concurrently with Feed.</summary>
public sealed class AstmOperationObserver
{
    private readonly object _gate = new();
    private readonly Guid _traceId;
    private readonly Encoding _encoding;
    private readonly IAstmOperationPolicy? _policy;
    private readonly int _maxTransmissionBytes;
    private readonly bool _receiverEotAcknowledges;
    private Lis01Observer _link = new() { ValidateRestrictedCharacters = true };
    private TraceOperationSnapshot _current;
    private TraceOperationState _operation = TraceOperationState.Unknown;
    private bool? _linkInProgress;
    private bool _invalid, _ended, _established, _duplicateAckPending;
    private long? _nextSequence;
    private long _lastSequence = -1, _revision;
    private DateTimeOffset? _lastObservedAt;
    private string _code = "ASTM-OP-NOT-SYNCHRONIZED";
    private int _bufferedBytes, _expectedFrame = 1, _lastAccepted = -1;
    private Lis01Role? _sender;

    public AstmOperationObserver(Guid traceId, Encoding encoding, IAstmOperationPolicy? policy = null,
        int maxTransmissionBytes = 1024 * 1024, bool receiverEotAcknowledges = false)
    {
        if (traceId == Guid.Empty) throw new ArgumentException("A trace identity is required.", nameof(traceId));
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTransmissionBytes);
        _traceId = traceId; _encoding = (Encoding)encoding.Clone(); _policy = policy;
        _maxTransmissionBytes = maxTransmissionBytes;
        _receiverEotAcknowledges = receiverEotAcknowledges;
        _current = Snapshot();
    }

    public TraceOperationSnapshot Current => Volatile.Read(ref _current);

    public TraceOperationSnapshot Feed(TraceEntry entry)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entry.Sequence);
        lock (_gate)
        {
            if (_ended) throw new InvalidOperationException("The observation ended; resynchronize or create a new observer.");
            if (_nextSequence is { } expected && entry.Sequence != expected) Poison("ASTM-OP-SEQUENCE-GAP");
            _nextSequence = entry.Sequence == long.MaxValue ? null : entry.Sequence + 1;
            _lastSequence = Math.Max(_lastSequence, entry.Sequence); _lastObservedAt = entry.Utc;
            try
            {
                if (_invalid || entry.Direction == TraceDirection.Event || entry.Data.IsEmpty) return Publish();
                var side = entry.Direction == TraceDirection.Received ? Lis01Role.Instrument : Lis01Role.Host;
                var observations = new List<Lis01Observation>();
                // A read can carry several complete exchanges, or just the first byte of a frame.
                for (var i = 0; i < entry.Data.Length && !_invalid; i++)
                {
                    if (++_bufferedBytes > _maxTransmissionBytes) { Poison("ASTM-OP-BUFFER-LIMIT"); break; }
                    if (_linkInProgress != true) _code = "ASTM-OP-TRANSFERRING";
                    _linkInProgress = true;
                    observations.Clear();
                    _link.Feed(side, entry.Data.Span.Slice(i, 1), entry.Sequence, observations);
                    foreach (var observation in observations)
                    {
                        OnObservation(observation, entry.Data.Span[i]);
                        if (_invalid) break;
                    }
                }
                return Publish();
            }
            catch
            {
                Poison("ASTM-OP-DECODER-FAILED"); Publish(); throw;
            }
        }
    }

    /// <summary>Capture loss, reconnect or external discontinuity. No subsequent bytes clear this latch automatically.</summary>
    public TraceOperationSnapshot Invalidate(string code = "ASTM-OP-OBSERVATION-GAP")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        lock (_gate) { Poison(code); return Publish(); }
    }

    /// <summary>The caller has resynchronized its protocol/context. This resets to Unknown, never directly to Idle.
    /// Use a new observer/trace identity when the source epoch or ownership generation changes.</summary>
    public TraceOperationSnapshot Resynchronize(long? nextSequence = null)
    {
        if (nextSequence is < 0) throw new ArgumentOutOfRangeException(nameof(nextSequence));
        lock (_gate)
        {
            Poison("ASTM-OP-NOT-SYNCHRONIZED"); _ended = false;
            _nextSequence = nextSequence;
            _lastSequence = nextSequence is { } next ? next - 1 : -1; _lastObservedAt = null;
            try { _policy?.Reset(); _invalid = false; }
            finally { Publish(); }
            return Current;
        }
    }

    /// <summary>EOF/disconnect does not prove completion of a protocol operation.</summary>
    public TraceOperationSnapshot Complete()
    {
        lock (_gate) { _ended = true; Poison("ASTM-OP-OBSERVATION-ENDED"); return Publish(); }
    }

    private void OnObservation(Lis01Observation observation, byte value)
    {
        switch (observation.Kind)
        {
            case Lis01ObservationKind.Enquiry:
                if (_established) { Poison("ASTM-OP-UNEXPECTED-ENQUIRY"); break; }
                _code = "ASTM-OP-ESTABLISHING"; break;
            case Lis01ObservationKind.Established:
                _established = true; _sender = observation.Side; _expectedFrame = 1; _lastAccepted = -1;
                _duplicateAckPending = false;
                _code = "ASTM-OP-TRANSFERRING"; break;
            case Lis01ObservationKind.Frame:
                if (!_established || observation.Side != _sender ||
                    (observation.FrameNumber != _expectedFrame && observation.FrameNumber != _lastAccepted) ||
                    (_duplicateAckPending && observation.FrameNumber != _lastAccepted))
                    Poison("ASTM-OP-UNEXPECTED-FRAME");
                else _duplicateAckPending = observation.FrameNumber == _lastAccepted;
                break;
            case Lis01ObservationKind.FrameAccepted:
                _lastAccepted = observation.FrameNumber; _expectedFrame = (_lastAccepted + 1) % 8; break;
            case Lis01ObservationKind.FrameRejected:
            case Lis01ObservationKind.Retransmission:
            case Lis01ObservationKind.Busy:
                _code = "ASTM-OP-RETRY-PENDING"; break;
            case Lis01ObservationKind.Contention:
                // A losing sender can still have a deferred operation; a different completed transmission cannot clear it.
                Poison("ASTM-OP-CONTENTION"); break;
            case Lis01ObservationKind.TransmissionEnd:
                EndTransmission(observation); break;
            case Lis01ObservationKind.InvalidFrame:
            case Lis01ObservationKind.AlteredByRecorder:
            case Lis01ObservationKind.ImplicitAcknowledge:
            case Lis01ObservationKind.ReceiverInterrupt:
                if (observation.Kind != Lis01ObservationKind.ReceiverInterrupt || !_receiverEotAcknowledges)
                    Poison("ASTM-OP-UNRELIABLE-EXCHANGE");
                break;
            case Lis01ObservationKind.Noise:
                // The general recorder reports the ACK of a duplicate accepted frame as noise. In this stricter
                // monitor it is expected only while that duplicate is outstanding and from the receiver.
                if (_duplicateAckPending && observation.Side != _sender && value == ControlCharacters.Ack)
                    _duplicateAckPending = false;
                else if (!(_duplicateAckPending && observation.Side != _sender && value == ControlCharacters.Nak))
                    Poison("ASTM-OP-UNRELIABLE-EXCHANGE");
                break;
        }
    }

    private void EndTransmission(Lis01Observation observation)
    {
        if (!_established || observation.Side != _sender || !observation.Complete || _duplicateAckPending)
        { Poison("ASTM-OP-INCOMPLETE-TRANSMISSION"); return; }
        _established = false; _sender = null; _bufferedBytes = 0; _linkInProgress = false;
        if (observation.Messages is not { Count: > 0 })
        { _code = "ASTM-OP-EMPTY-TRANSMISSION"; return; } // A link check cannot clear a pending business operation.
        var text = string.Concat(observation.Messages.Select(m => _encoding.GetString(m)));
        var parsed = Lis02Codec.Parse(text);
        if (parsed.Diagnostics.Count > 0 || parsed.Messages.Count == 0 || parsed.Messages.Any(m => m.Header is null || !m.IsTerminated))
        { Poison("ASTM-OP-INCOMPLETE-MESSAGE"); return; }
        var decision = _policy?.OnTransmission(observation.Side == Lis01Role.Instrument ? TraceDirection.Received : TraceDirection.Transmitted,
            parsed.Messages, new(_traceId, observation.FirstSequence, observation.Sequence));
        _operation = decision?.State ?? TraceOperationState.Unknown;
        _code = decision?.Code ?? "ASTM-OP-POLICY-REQUIRED";
    }

    private void Poison(string code)
    {
        _invalid = true; _operation = TraceOperationState.Unknown; _linkInProgress = null; _code = code;
        _link = new() { ValidateRestrictedCharacters = true };
        _established = false; _sender = null; _bufferedBytes = 0; _duplicateAckPending = false;
    }

    private TraceOperationSnapshot Snapshot() => new(_traceId, _policy?.Id, _invalid ? TraceOperationState.Unknown :
        _linkInProgress == true ? TraceOperationState.InProgress : _operation,
        _linkInProgress, _lastSequence, _revision, _lastObservedAt, _code, !_ended);

    private TraceOperationSnapshot Publish()
    {
        _revision++; var snapshot = Snapshot(); Volatile.Write(ref _current, snapshot); return snapshot;
    }
}
