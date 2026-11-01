using System.Text;
using System.Text.Json;
using Astm.Abstractions.Tracing;
using Astm.Plugins;
using Astm.Protocol.Lis01;
using Astm.Protocol.Lis02;
using Astm.Runtime.Tracing;
using Astm.Tracing;
using static Astm.Protocol.Lis01.ControlCharacters;

namespace Astm.Tests.Tracing;

public class OperationObserverTests
{
    private static readonly string[] Results = ["H|\\^&", "P|1", "O|1|SYN-1", "R|1|^^^TEST|5", "L|1|N"];
    private static readonly string[] Query = ["H|\\^&", "Q|1|SYN-1", "L|1|N"];
    private static readonly string[] Answer = ["H|\\^&", "P|1", "O|1|SYN-1", "L|1|N"];

    private sealed class Flow(IAstmOperationPolicy? policy, int limit = 1024 * 1024)
    {
        public AstmOperationObserver Observer { get; } = new(Guid.NewGuid(), Encoding.Latin1, policy, limit);
        public long Sequence;
        public TraceOperationSnapshot Feed(TraceDirection direction, params byte[] bytes) => Observer.Feed(
            new(Sequence++, TimeSpan.FromMilliseconds(Sequence), DateTimeOffset.UnixEpoch.AddMilliseconds(Sequence), direction, bytes));

        public void Start(TraceDirection direction = TraceDirection.Received)
        {
            Feed(direction, Enq); Feed(Other(direction), Ack);
        }

        public void Frames(string[] records, TraceDirection direction = TraceDirection.Received, bool perRecord = false, bool fragments = false)
        {
            var messages = perRecord ? records.Select(r => (ReadOnlyMemory<byte>)Encoding.Latin1.GetBytes(r + "\r")).ToList()
                : [(ReadOnlyMemory<byte>)Encoding.Latin1.GetBytes(string.Concat(records.Select(r => r + "\r")))];
            foreach (var frame in Lis01Frame.BuildTransmission(messages, maxTextLength: 32))
            {
                if (fragments) foreach (var value in frame) Feed(direction, value);
                else Feed(direction, frame);
                Feed(Other(direction), Ack);
            }
        }

        public TraceOperationSnapshot Transmission(string[] records, TraceDirection direction = TraceDirection.Received)
        {
            Start(direction); Frames(records, direction); return Feed(direction, Eot);
        }
    }

    private static TraceDirection Other(TraceDirection direction) =>
        direction == TraceDirection.Received ? TraceDirection.Transmitted : TraceDirection.Received;

    [Fact]
    public void Instrument_plugin_advertises_explicit_operation_scope_and_creates_independent_policies()
    {
        IInstrumentOperationPolicyProvider provider = new StandardAstmPlugin();
        var profile = Assert.Single(provider.OperationProfiles(StandardAstmPlugin.ProfileId));
        var first = provider.CreateOperationPolicy(StandardAstmPlugin.ProfileId, profile.Id);
        var second = provider.CreateOperationPolicy(StandardAstmPlugin.ProfileId, profile.Id);
        Assert.NotSame(first, second);
        var a = new Flow(first); var b = new Flow(second);
        a.Transmission(Query);
        Assert.True(b.Transmission(Results).NoPendingOperations);
        Assert.False(a.Transmission(Results).NoPendingOperations);
        Assert.Throws<ArgumentException>(() => provider.CreateOperationPolicy(StandardAstmPlugin.ProfileId, "unsupported"));
    }

    [Fact]
    public void Initial_silence_and_link_completion_without_a_policy_do_not_declare_operation_idle()
    {
        var flow = new Flow(null);
        Assert.Null(flow.Observer.Current.OperationInProgress);
        Assert.False(flow.Observer.Current.NoPendingOperations);
        Assert.Null(flow.Observer.Current.LinkInProgress);
        flow.Feed(TraceDirection.Event);
        Assert.Null(flow.Observer.Current.OperationInProgress);
        flow.Start(); Assert.True(flow.Observer.Current.OperationInProgress);
        flow.Frames(Results);
        var ended = flow.Feed(TraceDirection.Received, Eot);
        Assert.False(ended.LinkInProgress);
        Assert.Null(ended.OperationInProgress);
        Assert.Equal("ASTM-OP-POLICY-REQUIRED", ended.Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Results_operation_ends_only_after_confirmed_frames_terminator_and_EOT(bool perRecord, bool fragments)
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy());
        var longResults = Results.ToArray(); longResults[3] += new string('9', 600); // ETB chains and modulo-8 frame wrap.
        flow.Start(); flow.Frames(longResults, perRecord: perRecord, fragments: fragments);
        var before = flow.Observer.Current;
        Assert.True(before.OperationInProgress);
        Assert.False(before.NoPendingOperations);
        var done = flow.Feed(TraceDirection.Received, Eot);
        Assert.False(done.OperationInProgress);
        Assert.False(done.LinkInProgress);
        Assert.True(done.NoPendingOperations);
        Assert.Equal(flow.Sequence - 1, done.LastSequence);
        Assert.True(done.Revision > before.Revision);
        Assert.True(before.OperationInProgress); // Previously published snapshots stay immutable.
        Assert.True(flow.Feed(TraceDirection.Received, Stx).OperationInProgress); // First fragment already fences idle.
    }

    [Theory]
    [InlineData(Eot)]
    [InlineData(Ack)]
    [InlineData(0x41)]
    public void Stray_controls_or_unknown_protocol_cannot_create_idle(byte value)
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy());
        flow.Feed(TraceDirection.Received, value);
        Assert.Null(flow.Observer.Current.OperationInProgress);
        Assert.False(flow.Observer.Current.NoPendingOperations);
        flow.Transmission(Results);
        Assert.False(flow.Observer.Current.NoPendingOperations); // Latch requires explicit resynchronization.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Valid_NAK_retry_and_acknowledged_duplicate_do_not_duplicate_the_result(bool duplicateAfterAck)
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy());
        flow.Start();
        var frame = Lis01Frame.Build(1, Encoding.Latin1.GetBytes(string.Join('\r', Results) + "\r"), true);
        flow.Feed(TraceDirection.Received, frame);
        flow.Feed(TraceDirection.Transmitted, duplicateAfterAck ? Ack : Nak);
        flow.Feed(TraceDirection.Received, frame);
        Assert.True(flow.Observer.Current.OperationInProgress);
        flow.Feed(TraceDirection.Transmitted, Ack);
        Assert.True(flow.Feed(TraceDirection.Received, Eot).NoPendingOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_ACK_or_partial_ETB_never_becomes_idle(bool partial)
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy()); flow.Start();
        flow.Feed(TraceDirection.Received, Lis01Frame.Build(1, Encoding.Latin1.GetBytes(string.Join('\r', Results) + "\r"), !partial));
        if (partial) flow.Feed(TraceDirection.Transmitted, Ack);
        var state = flow.Feed(TraceDirection.Received, Eot);
        Assert.Null(state.OperationInProgress);
        Assert.False(state.NoPendingOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Checksum_or_frame_order_loss_requires_resynchronization(bool wrongNumber)
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy()); flow.Start();
        var frame = Lis01Frame.Build(wrongNumber ? 2 : 1, "H|\\^&\r"u8, true);
        if (!wrongNumber) frame[3] ^= 1;
        flow.Feed(TraceDirection.Received, frame);
        flow.Feed(TraceDirection.Transmitted, Ack);
        flow.Feed(TraceDirection.Received, Eot);
        Assert.Null(flow.Observer.Current.OperationInProgress);
        flow.Transmission(Results);
        Assert.False(flow.Observer.Current.NoPendingOperations);
        flow.Observer.Resynchronize(flow.Sequence);
        Assert.Null(flow.Observer.Current.OperationInProgress);
        Assert.True(flow.Transmission(Results).NoPendingOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sequence_gaps_and_duplicates_invalidate_an_idle_snapshot(bool duplicate)
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy()); flow.Transmission(Results);
        if (duplicate) flow.Sequence--; else flow.Sequence++;
        flow.Feed(TraceDirection.Event);
        Assert.Equal("ASTM-OP-SEQUENCE-GAP", flow.Observer.Current.Code);
        Assert.False(flow.Observer.Current.NoPendingOperations);
        flow.Transmission(Results); Assert.False(flow.Observer.Current.NoPendingOperations);
        flow.Observer.Resynchronize(flow.Sequence);
        Assert.True(flow.Transmission(Results).NoPendingOperations);
    }

    [Fact]
    public void A_query_stays_pending_across_EOT_silence_and_unrelated_results()
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy());
        var pending = flow.Transmission(Query);
        Assert.True(pending.OperationInProgress);
        Assert.False(pending.LinkInProgress);
        Assert.Equal("ASTM-OP-QUERY-PENDING", pending.Code);
        flow.Feed(TraceDirection.Event);
        Assert.True(flow.Transmission(Results).OperationInProgress);
        Assert.False(flow.Observer.Current.NoPendingOperations);
    }

    private sealed class QueryPolicy : IAstmOperationPolicy
    {
        private readonly HashSet<string> _pending = [];
        public string Id => "test.query-answer";
        public IReadOnlyList<string> Evidence => ["Synthetic test-only query/answer contract"];
        public AstmOperationDecision OnTransmission(TraceDirection direction, IReadOnlyList<Lis02Message> messages, TraceRef trace)
        {
            foreach (var message in messages)
                if (direction == TraceDirection.Received) foreach (var query in message.OfType('Q')) _pending.Add(query.Field(3));
                else foreach (var order in message.OfType('O')) _pending.Remove(order.Field(3));
            return new(_pending.Count > 0 ? TraceOperationState.InProgress : TraceOperationState.Idle, "test.query-state");
        }
        public void Reset() => _pending.Clear();
    }

    [Fact]
    public void An_empty_link_check_preserves_the_separate_business_state()
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy());
        flow.Start(); Assert.Null(flow.Feed(TraceDirection.Received, Eot).OperationInProgress);
        flow.Transmission(Results); flow.Start();
        Assert.True(flow.Feed(TraceDirection.Received, Eot).NoPendingOperations);
        flow.Transmission(Query); flow.Start();
        Assert.True(flow.Feed(TraceDirection.Received, Eot).OperationInProgress);
        Assert.False(flow.Observer.Current.NoPendingOperations);
    }

    [Fact]
    public void A_plugin_policy_can_correlate_a_multi_transmission_operation_without_changing_PortGate()
    {
        var flow = new Flow(new QueryPolicy()); flow.Transmission(Query);
        Assert.False(flow.Transmission(["H|\\^&", "P|1", "O|1|SYN-OTHER", "L|1"], TraceDirection.Transmitted).NoPendingOperations);
        Assert.True(flow.Transmission(Answer, TraceDirection.Transmitted).NoPendingOperations);
    }

    [Fact]
    public void Unsupported_workflow_does_not_get_cleared_by_an_unrelated_result()
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy()); flow.Transmission(Answer, TraceDirection.Transmitted);
        Assert.Null(flow.Transmission(Results).OperationInProgress);
        flow.Observer.Resynchronize(flow.Sequence);
        Assert.True(flow.Transmission(Results).NoPendingOperations);
    }

    [Fact]
    public void Capture_loss_EOF_and_memory_limit_never_prove_completion()
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy()); flow.Transmission(Results);
        Assert.False(flow.Observer.Invalidate().NoPendingOperations);
        flow.Observer.Resynchronize(flow.Sequence); flow.Start();
        var ended = flow.Observer.Complete();
        Assert.Null(ended.OperationInProgress); Assert.False(ended.IsObserving);
        Assert.Throws<InvalidOperationException>(() => flow.Feed(TraceDirection.Received, Eot));
        var limited = new Flow(new AstmResultsMessageOperationPolicy(), limit: 8); limited.Transmission(Results);
        Assert.Equal("ASTM-OP-BUFFER-LIMIT", limited.Observer.Current.Code);
        Assert.False(limited.Observer.Current.NoPendingOperations);
    }

    private sealed class FaultPolicy : IAstmOperationPolicy
    {
        public bool Fail;
        public string Id => "test.failure";
        public IReadOnlyList<string> Evidence => [];
        public AstmOperationDecision OnTransmission(TraceDirection direction, IReadOnlyList<Lis02Message> messages, TraceRef trace) =>
            Fail ? throw new InvalidOperationException("test policy failure") : new(TraceOperationState.Idle, "test.idle");
        public void Reset() => Fail = false;
    }

    [Fact]
    public void Contention_and_embedded_control_characters_cannot_prove_idle()
    {
        var contended = new Flow(new AstmResultsMessageOperationPolicy());
        contended.Feed(TraceDirection.Received, Enq); contended.Feed(TraceDirection.Transmitted, Enq);
        Assert.Equal("ASTM-OP-CONTENTION", contended.Observer.Current.Code);
        contended.Transmission(Results); Assert.False(contended.Observer.Current.NoPendingOperations);
        var restricted = new Flow(new AstmResultsMessageOperationPolicy()); restricted.Start();
        var text = string.Join('\r', Results).Replace("TEST", "TE" + (char)Enq + "ST", StringComparison.Ordinal) + "\r";
        restricted.Feed(TraceDirection.Received, Lis01Frame.Build(1, Encoding.Latin1.GetBytes(text), true));
        restricted.Feed(TraceDirection.Transmitted, Ack); restricted.Feed(TraceDirection.Received, Eot);
        Assert.Null(restricted.Observer.Current.OperationInProgress);
    }

    [Fact]
    public void Plugin_failure_invalidates_previously_idle_state_and_propagates()
    {
        var policy = new FaultPolicy(); var flow = new Flow(policy); flow.Transmission(Results); policy.Fail = true;
        Assert.Throws<InvalidOperationException>(() => flow.Transmission(Results));
        Assert.Null(flow.Observer.Current.OperationInProgress);
        Assert.Equal("ASTM-OP-DECODER-FAILED", flow.Observer.Current.Code);
    }

    [Fact]
    public void Snapshot_serializes_with_NativeAOT_metadata_without_result_payloads()
    {
        var flow = new Flow(new AstmResultsMessageOperationPolicy()); var state = flow.Transmission(Results);
        var json = JsonSerializer.Serialize(state, AstmJsonContext.Default.TraceOperationSnapshot);
        var copy = JsonSerializer.Deserialize(json, AstmJsonContext.Default.TraceOperationSnapshot);
        Assert.Equal(state, copy);
        Assert.DoesNotContain("SYN-1", json);
        Assert.DoesNotContain("TEST", json);
    }
}
