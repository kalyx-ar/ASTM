using System.Security.Cryptography;
using System.Text;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Model;
using Astm.Operations;
using Astm.Protocol.Lis01;
using Astm.Protocol.Lis02;
using Astm.Protocol.Hl7v2;
using Astm.Protocol.Mllp;

namespace Astm.Plugins.Operations;

public static class MedicalOperationObservers
{
    public static IMedicalOperationObserver Create(IMedicalOperationProvider plugin, string profileId, MedicalOperationContext context, Guid traceId)
    {
        var adapter = plugin.CreateOperations(profileId, context);
        return adapter.Profile.Format switch
        {
            "lis02" => new AstmObserver(adapter, traceId),
            "hl7v2" => new Hl7Observer(adapter, traceId),
            _ => new ContentObserver(adapter, plugin as IInstrumentTracePlugin, context, traceId),
        };
    }
    internal static Guid Key(Guid traceId, string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(traceId + ":" + text)).AsSpan(0, 16));

    private abstract class Observer(Guid traceId) : IMedicalOperationObserver
    {
        protected readonly Guid TraceId = traceId != Guid.Empty ? traceId : throw new ArgumentException("trace.id");
        protected OperationTracker Tracker = new();
        private readonly object _gate = new();
        private long? _next;
        protected bool Invalid, Ended;
        public OperationSnapshot Current { get { lock (_gate) return Tracker.Current; } }
        public OperationSnapshot Feed(TraceEntry entry)
        {
            lock (_gate)
            {
                if (Ended) throw new InvalidOperationException("operation.observation.ended");
                if (entry.Sequence < 0 || _next is { } next && entry.Sequence != next) Invalidate();
                _next = entry.Sequence == long.MaxValue ? null : entry.Sequence + 1;
                if (Invalid) return Current;
                try { if (entry.Direction != TraceDirection.Event && !entry.Data.IsEmpty) OnEntry(entry); }
                catch { Invalidate(); throw; }
                return Current;
            }
        }
        public void Invalidate() { lock (_gate) { Invalid = true; Tracker.Invalidate(); } }
        public void Resynchronize(long? nextSequence = null)
        {
            if (nextSequence is < 0) throw new ArgumentOutOfRangeException(nameof(nextSequence));
            lock (_gate) { Tracker = new(); Invalid = Ended = false; _next = nextSequence; Reset(); }
        }
        public void Complete() { lock (_gate) { if (Ended) return; OnEnd(); Ended = true; Tracker.Complete(); } }
        protected abstract void OnEntry(TraceEntry entry);
        protected abstract void Reset();
        protected virtual void OnEnd() { }
        protected void Record(Guid id, string purpose, OperationState state, long sequence, string code,
            OperationEvidence evidence = OperationEvidence.MessageAcknowledged, bool channel = true,
            OperationCompletionScope scope = OperationCompletionScope.MessageExchange) =>
            Tracker.Record(new(id, purpose, state, scope, evidence, channel, sequence, code));
    }

    private sealed class AstmObserver : Observer, IAstmOperationPolicy
    {
        private readonly IMedicalOperations _adapter;
        private Astm.Tracing.AstmOperationObserver _link = null!;
        private readonly Dictionary<string, Guid> _queries = new(StringComparer.Ordinal);
        public AstmObserver(IMedicalOperations adapter, Guid traceId) : base(traceId) { _adapter = adapter; Reset(); }
        public string Id => _adapter.Profile.Id;
        public IReadOnlyList<string> Evidence => _adapter.Describe(Id)[0].Evidence;
        protected override void Reset()
        {
            _queries.Clear();
            _link = new(TraceId, Encoding.GetEncoding(_adapter.Profile.EncodingName), this,
                receiverEotAcknowledges: _adapter.Profile.Rules.ReceiverEotAcknowledges);
        }
        void IAstmOperationPolicy.Reset() => _queries.Clear();
        protected override void OnEntry(TraceEntry entry)
        {
            var state = _link.Feed(entry);
            Tracker.SetLink(state.LinkInProgress == true);
            // The link contract is authoritative. A list of diagnostic codes can miss new poison reasons.
            if (state.LinkInProgress is null) Invalidate();
        }
        public AstmOperationDecision OnTransmission(TraceDirection direction, IReadOnlyList<Lis02Message> messages, TraceRef trace)
        {
            var key = Key(TraceId, direction + ":" + trace.FirstSequence + ":" + trace.LastSequence);
            var rules = _adapter.Profile.Rules;
            if (direction == TraceDirection.Transmitted)
            {
                var answered = messages.SelectMany(m => m.OfType('O')).Select(o => o.Text(3).Trim()).ToHashSet(StringComparer.Ordinal);
                if (rules.NegativeAnswer == "Q.X")
                    foreach (var q in messages.SelectMany(m => m.OfType('Q')).Where(q => q.Field(13) == "X")) answered.Add(q.Text(3, 2).Trim());
                var negativeAll = rules.AllSpecimensQuery && messages.Any(m => m.OfType('L').Any(l => l.Field(3) == "I"));
                // LIS02 L.I has no specimen identifier. Only one outstanding query can be correlated safely.
                if (rules.NegativeAnswer == "L.I" && _queries.Count == 1 && messages.Any(m => m.OfType('L').Any(l => l.Field(3) == "I")))
                    answered.Add(_queries.Keys.Single());
                foreach (var pending in _queries.ToArray())
                    if (answered.Contains(pending.Key) || pending.Key == "*" && negativeAll)
                    { Record(pending.Value, MedicalPurposes.ReceiveQuery, OperationState.Completed, trace.LastSequence, "medical.query.answered"); _queries.Remove(pending.Key); }
                Record(key, MedicalPurposes.SubmitOrders, OperationState.Completed, trace.LastSequence, "medical.message.acknowledged");
            }
            else
            {
                var decoded = _adapter.Decode(Encoding.GetEncoding(_adapter.Profile.EncodingName).GetBytes(
                    string.Concat(messages.Select(m => Lis02Codec.Serialize(m.Records)))), trace, TimeZoneInfo.Utc);
                if (decoded.Diagnostics.Any(d => d.Severity == Astm.Abstractions.Diagnostics.AstmSeverity.Error || d.Code.EndsWith("QUERY-UNSUPPORTED", StringComparison.Ordinal) || d.Code.EndsWith("UNKNOWN-RECORD", StringComparison.Ordinal)))
                { Invalidate(); return new(TraceOperationState.Unknown, "medical.operation.unsupported"); }
                foreach (var query in decoded.Queries)
                {
                    foreach (var sample in query.Specimens.Count == 0 && rules.AllSpecimensQuery ? ["*"] : query.Specimens.Select(s => s.SpecimenId))
                    {
                        if (_queries.ContainsKey(sample)) { Invalidate(); continue; }
                        var id = Key(TraceId, "query:" + trace.FirstSequence + ":" + sample);
                        _queries[sample] = id; Record(id, MedicalPurposes.ReceiveQuery, OperationState.Pending, trace.LastSequence, "medical.query.pending");
                    }
                }
                if (rules.NegativeAnswer == "Q.X")
                    foreach (var q in messages.SelectMany(m => m.OfType('Q')).Where(q => q.Field(13) == "X"))
                        if (_queries.Remove(q.Text(3, 2).Trim(), out var cancelled))
                            Record(cancelled, MedicalPurposes.ReceiveQuery, OperationState.Cancelled, trace.LastSequence, "medical.query.cancelled");
                Record(key, MedicalPurposes.ReceiveObservations, OperationState.Completed, trace.LastSequence, "medical.message.acknowledged");
            }
            if (!Invalid) Tracker.Synchronize();
            return new(Tracker.Current.ChannelOperationInProgress switch { true => TraceOperationState.InProgress, false => TraceOperationState.Idle, _ => TraceOperationState.Unknown }, "medical.operation.state");
        }
    }

    private sealed class Hl7Observer : Observer
    {
        private readonly IMedicalOperations _adapter;
        private MllpDecoder _received = new(), _transmitted = new();
        private readonly Dictionary<string, Guid> _messages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> _queries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid[]> _answers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);
        public Hl7Observer(IMedicalOperations adapter, Guid traceId) : base(traceId) => _adapter = adapter;
        protected override void Reset() { _received = new(); _transmitted = new(); _messages.Clear(); _queries.Clear(); _answers.Clear(); _payloads.Clear(); }
        protected override void OnEntry(TraceEntry entry)
        {
            var events = new List<MllpEvent>();
            (entry.Direction == TraceDirection.Received ? _received : _transmitted).Feed(entry.Data.Span, events);
            foreach (var item in events)
            {
                if (item.Kind != MllpEventKind.Message) { Invalidate(); break; }
                var parsed = Hl7Codec.Parse(Encoding.GetEncoding(_adapter.Profile.EncodingName).GetString(item.Payload!));
                if (parsed.Message is not { } message || parsed.Diagnostics.Count > 0 || message.ControlId.Length == 0) { Invalidate(); break; }
                if (message.Type is not ("ACK" or "ORL" or "OML" or "RSP") &&
                    _adapter.Decode(item.Payload!, new(TraceId, entry.Sequence, entry.Sequence), TimeZoneInfo.Utc).Diagnostics.Any(d => d.Severity == Astm.Abstractions.Diagnostics.AstmSeverity.Error))
                { Invalidate(); break; }
                var identity = entry.Direction + ":" + message.ControlId;
                var digest = SHA256.HashData(item.Payload!);
                if (_payloads.TryGetValue(identity, out var previous) && !previous.AsSpan().SequenceEqual(digest)) { Invalidate(); break; }
                if (message.Type is not ("ACK" or "ORL")) _payloads[identity] = digest;
                var id = Key(TraceId, "message:" + entry.Direction + ":" + message.ControlId);
                var ack = message.OfType("MSA").SingleOrDefault();
                var opposite = entry.Direction == TraceDirection.Received ? TraceDirection.Transmitted : TraceDirection.Received;
                if (ack is not null && (ack.Text(1) is not ("AA" or "AE" or "AR") || !_messages.ContainsKey(opposite + ":" + ack.Text(2))))
                { Invalidate(); break; }
                if (ack is not null && _messages.Remove(opposite + ":" + ack.Text(2), out var pending))
                {
                    _payloads.Remove(opposite + ":" + ack.Text(2));
                    var rejected = ack.Text(1) is "AE" or "AR" || message.OfType("ORC").Any(c => c.Text(1) == "UA");
                    var evidence = message.Type == "ORL" && message.OfType("ORC").Any(c => c.Text(1) == "OK")
                        ? OperationEvidence.ApplicationAccepted : OperationEvidence.MessageAcknowledged;
                    var pendingPurpose = Tracker.Current.Operations.Single(o => o.OperationId == pending).Purpose;
                    Record(pending, pendingPurpose, rejected ? OperationState.Failed : OperationState.Completed,
                        entry.Sequence, rejected ? "medical.message.rejected" : "medical.message.acknowledged", evidence);
                    if (_answers.Remove(ack.Text(2), out var queries)) foreach (var query in queries)
                        Record(query, MedicalPurposes.ReceiveQuery, rejected ? OperationState.Failed : OperationState.Completed, entry.Sequence, "medical.query.response-acknowledged");
                }
                else if (message.Type == "ACK") { Invalidate(); break; }
                if (message.Type != "ACK" && message.Type != "ORL")
                {
                    _messages[entry.Direction + ":" + message.ControlId] = id;
                    Record(id, message.Type == "QBP" ? MedicalPurposes.ReceiveQuery : message.Type == "OML" ? MedicalPurposes.SubmitOrders : MedicalPurposes.ReceiveObservations,
                        OperationState.Pending, entry.Sequence, "medical.message.awaiting-ack", OperationEvidence.Transport);
                }
                if (message.Type == "QBP" && entry.Direction == TraceDirection.Received)
                {
                    var sample = message.OfType("QPD").FirstOrDefault()?.Text(3);
                    if (string.IsNullOrWhiteSpace(sample)) { Invalidate(); break; }
                    var queryId = Key(TraceId, "query:" + message.ControlId);
                    if (_queries.TryGetValue(sample, out var existing) && existing != queryId) { Invalidate(); break; }
                    _queries[sample] = queryId;
                    Record(queryId, MedicalPurposes.ReceiveQuery, OperationState.Pending, entry.Sequence, "medical.query.pending", OperationEvidence.Transport);
                }
                if (entry.Direction == TraceDirection.Transmitted && message.Type is "RSP" or "OML")
                {
                    var samples = message.OfType("QPD").Select(q => q.Text(3)).Concat(message.OfType("SPM").Select(s => s.Text(2))).ToArray();
                    var answered = new List<Guid>();
                    foreach (var sample in samples) if (_queries.Remove(sample, out var query)) answered.Add(query);
                    if (answered.Count > 0) _answers[message.ControlId] = answered.ToArray();
                }
                if (!Invalid) Tracker.Synchronize();
            }
            Tracker.SetLink(_received.HasIncompleteBlock || _transmitted.HasIncompleteBlock);
        }
    }

    private sealed class ContentObserver : Observer
    {
        private readonly IMedicalOperations _adapter;
        private readonly IInstrumentTracePlugin? _plugin;
        private readonly MedicalOperationContext _context;
        private readonly List<TraceEntry> _entries = [];
        private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
        private int _bytes;
        public ContentObserver(IMedicalOperations adapter, IInstrumentTracePlugin? plugin, MedicalOperationContext context, Guid traceId) : base(traceId)
        { _adapter = adapter; _plugin = plugin; _context = context; }
        protected override void Reset() { _entries.Clear(); _applied.Clear(); _bytes = 0; }
        protected override void OnEntry(TraceEntry entry)
        {
            _bytes += entry.Data.Length;
            if (_bytes > 16 * 1024 * 1024) { Invalidate(); return; }
            _entries.Add(entry with { Data = entry.Data.ToArray() });
            if (_adapter.Profile.Format == "content")
            { Record(Key(TraceId, "import"), MedicalPurposes.ImportContent, OperationState.Running, entry.Sequence, "medical.import.reading", OperationEvidence.Transport, false, OperationCompletionScope.ContentImport); return; }
            var profile = _plugin!.Profiles.Single(p => p.Id == _adapter.Profile.Id);
            var decoded = _plugin.Interpret(profile, _entries, _context.Instrument, TraceId, TimeZoneInfo.Utc);
            foreach (var receipt in decoded.OperationReceipts)
                if (_applied.Add(receipt.OperationId + ":" + receipt.Sequence + ":" + receipt.State)) Tracker.Record(receipt);
            Tracker.SetLink(decoded.Diagnostics.Any(d => d.Code.EndsWith("INCOMPLETE", StringComparison.Ordinal)));
            foreach (var message in decoded.Messages.Where(m => m.Fields.GetValueOrDefault("record.valid") == "True"))
                Record(Key(TraceId, "record:" + message.FirstSequence + ":" + message.Code), MedicalPurposes.ObserveMessages,
                    OperationState.Completed, message.LastSequence, "medical.record.received", OperationEvidence.Transport);
            // A proprietary message boundary/ER response does not establish physical or channel-idle semantics.
            Record(Key(TraceId, "unknown-boundary"), MedicalPurposes.ObserveMessages, OperationState.Unknown, entry.Sequence,
                "medical.protocol.operation-boundary-unproven", OperationEvidence.None);
        }
        protected override void OnEnd()
        {
            if (_adapter.Profile.Format != "content" || _entries.Count == 0 || Invalid) return;
            var result = _adapter.Decode(_entries.SelectMany(e => e.Data.ToArray()).ToArray(), new(TraceId, _entries[0].Sequence, _entries[^1].Sequence), TimeZoneInfo.Utc);
            Record(Key(TraceId, "import"), MedicalPurposes.ImportContent,
                result.Diagnostics.Any(d => d.Severity == Astm.Abstractions.Diagnostics.AstmSeverity.Error) ? OperationState.Failed : OperationState.Completed,
                _entries[^1].Sequence, "medical.import.finished", OperationEvidence.ContentValidated, false, OperationCompletionScope.ContentImport);
        }
    }
}
