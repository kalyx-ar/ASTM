using System.Collections.Concurrent;
using System.Text;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Hosting;
using Atsm.Channels;
using Atsm.Protocol.Hl7v2;
using Atsm.Protocol.Mllp;
using Atsm.Runtime.Sessions;
using Atsm.Runtime.Tracing;
using Atsm.Testing;
using Atsm.Tests.Runtime;

namespace Atsm.Tests.Hl7;

public class Hl7SessionTests
{
    [Fact]
    public async Task Fragmented_MLLP_and_coalesced_blocks_are_acknowledged_individually()
    {
        await using var rig = new Rig();
        var bytes = MllpFraming.Wrap(Encoding.UTF8.GetBytes(Hl7Data.Result()));
        for (var i = 0; i < bytes.Length; i += 3) await rig.Peer.WriteAsync(bytes.AsMemory(i, Math.Min(3, bytes.Length - i)));
        Assert.Equal("AA", Hl7Acknowledgements.Read(await rig.ReceiveAsync())!.Code);
        var first = MllpFraming.Wrap(Encoding.UTF8.GetBytes(Hl7Data.Result().Replace("MSG-SYN", "MSG-SYN-2")));
        var second = MllpFraming.Wrap(Encoding.UTF8.GetBytes(Hl7Data.Result().Replace("MSG-SYN", "MSG-SYN-3")));
        await rig.Peer.WriteAsync(first.Concat(second).ToArray());
        Assert.Equal("MSG-SYN-2", Hl7Acknowledgements.Read(await rig.ReceiveAsync())!.ControlId);
        Assert.Equal("MSG-SYN-3", Hl7Acknowledgements.Read(await rig.ReceiveAsync())!.ControlId);
        Assert.Equal(3, rig.Host.Batches.Count);
    }

    [Fact]
    public async Task Repeated_HL7_control_identity_preserves_batch_and_observation_ids()
    {
        await using var rig = new Rig();
        await rig.SendAsync(Hl7Data.Result()); await rig.ReceiveAsync();
        await rig.SendAsync(Hl7Data.Result()); await rig.ReceiveAsync();
        Assert.Equal(rig.Host.Batches[0].BatchId, rig.Host.Batches[1].BatchId);
        Assert.Equal(rig.Host.Batches[0].Observations.Select(o => o.ObservationId), rig.Host.Batches[1].Observations.Select(o => o.ObservationId));
    }

    [Fact]
    public async Task Only_matching_application_ORL_completes_an_order_send()
    {
        await using var rig = new Rig();
        var send = rig.Session.SendOrdersAsync([Hl7Data.Order()]);
        var order = await rig.ReceiveAsync();
        await rig.SendAsync(Hl7Data.Header("ORL^O34^ORL_O34", "REPLY-SYN") + "MSA|CA|" + order.ControlId + "\r");
        await rig.Unmatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(send.IsCompleted);
        await rig.SendAsync(Hl7Data.Header("ORL^O34^ORL_O34", "REPLY-SYN-2") + "MSA|AA|" + order.ControlId + "\r");
        var result = await send.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OrderDeliveryState.Sent, result.State); Assert.Equal("AA", result.AcknowledgementCode);
    }

    [Fact]
    public async Task Missing_response_is_uncertain_and_does_not_resend_orders()
    {
        await using var rig = new Rig();
        var send = rig.Session.SendOrdersAsync([Hl7Data.Order()]);
        await rig.ReceiveAsync();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OrderDeliveryState.Uncertain, result.State);
        Assert.Equal("ATSM-HL7-RESPONSE-TIMEOUT", result.Code);
        Assert.Single(rig.Trace.Snapshot(), e => e.Direction == Atsm.Abstractions.Tracing.TraceDirection.Transmitted);
    }

    [Fact]
    public async Task Unsupported_enhanced_mode_is_rejected_before_ingestion()
    {
        await using var rig = new Rig();
        await rig.SendAsync(Hl7Data.Result().Replace("|2.5.1||||||", "|2.5.1|||AL|AL||"));
        var response = await rig.ReceiveAsync();
        Assert.Equal("AR", Hl7Acknowledgements.Read(response)!.Code); Assert.Empty(rig.Host.Batches);
    }

    [Fact]
    public async Task Truncated_connection_is_an_error_and_never_ingests_partial_content()
    {
        await using var rig = new Rig();
        await rig.Peer.WriteAsync(new byte[] { 0x0b, (byte)'M', (byte)'S', (byte)'H' });
        rig.Peer.Dispose();
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(rig.Host.Batches); rig.ExpectedFailure = true;
    }

    [Fact]
    public async Task Trace_capture_failure_escapes_the_receive_loop()
    {
        var plugin = Hl7Data.Plugin();
        var channel = new SuppliedChannel(MllpFraming.Wrap(Encoding.UTF8.GetBytes(Hl7Data.Result())));
        var session = new Hl7InstrumentSession(channel, Hl7Data.Dialect(), plugin.Profiles[0], new ControlledHost(), new("a1"), new FailingTrace(true), "host");
        await Assert.ThrowsAsync<IOException>(() => session.RunAsync(CancellationToken.None));
        Assert.Empty(channel.Writes); Assert.Equal(1, channel.Releases);
    }

    [Fact]
    public async Task Trace_failure_before_outgoing_order_is_not_hidden_as_a_transport_timeout()
    {
        var (local, peer) = DuplexStream.CreatePair();
        await using var channel = new StreamInstrumentChannel(new("hl7", ChannelKind.Simulated, "memory://hl7"), local);
        using var stop = new CancellationTokenSource();
        var session = new Hl7InstrumentSession(channel, Hl7Data.Dialect(), Hl7Data.Plugin().Profiles[0], new ControlledHost(), new("a1"), new FailingTrace(true), "host");
        var run = session.RunAsync(stop.Token);
        await Assert.ThrowsAsync<IOException>(() => session.SendOrdersAsync([Hl7Data.Order()]));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        peer.Dispose();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly MllpDecoder _decoder = new();
        private readonly Queue<Hl7Message> _messages = new();
        private readonly DuplexStream _local;
        public Rig()
        {
            (_local, Peer) = DuplexStream.CreatePair();
            var channel = new StreamInstrumentChannel(new("hl7", ChannelKind.Simulated, "memory://hl7"), _local);
            Session = new(channel, Hl7Data.Dialect(), Hl7Data.Plugin().Profiles[0], Host, new("a1"), Trace, "host")
            { Diagnostics = d => { Findings.Enqueue(d.Code); if (d.Code == "ATSM-HL7-UNMATCHED-ACK") Unmatched.TrySetResult(); } };
            Run = Session.RunAsync(_stop.Token);
        }
        public ControlledHost Host { get; } = new();
        public MemoryTraceSink Trace { get; } = new();
        public ConcurrentQueue<string> Findings { get; } = new();
        public TaskCompletionSource Unmatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DuplexStream Peer { get; }
        public Hl7InstrumentSession Session { get; }
        public Task Run { get; }
        public bool ExpectedFailure { get; set; }
        public ValueTask SendAsync(string text) => Peer.WriteAsync(MllpFraming.Wrap(Encoding.UTF8.GetBytes(text)));
        public async Task<Hl7Message> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (_messages.Count == 0)
            {
                var buffer = new byte[4096]; var events = new List<MllpEvent>();
                var read = await Peer.ReadAsync(buffer, timeout.Token);
                if (read == 0) throw new IOException("Peer closed.");
                _decoder.Feed(buffer.AsSpan(0, read), events);
                foreach (var item in events.Where(e => e.Kind == MllpEventKind.Message))
                    _messages.Enqueue(Assert.IsType<Hl7Message>(Hl7Codec.Parse(Encoding.UTF8.GetString(item.Payload!)).Message));
            }
            return _messages.Dequeue();
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (InvalidDataException) when (ExpectedFailure) { }
            finally { Peer.Dispose(); _local.Dispose(); _stop.Dispose(); }
        }
    }
}
