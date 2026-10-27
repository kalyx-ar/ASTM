using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Runtime.Sessions;
using Atsm.Runtime.Tracing;
using Atsm.Testing;
using Atsm.Tracing;
using Atsm.Tests.Architect;

namespace Atsm.Tests.Runtime;

public class AstmSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Results_sent_by_the_instrument_reach_the_host_with_trace_references()
    {
        await using var rig = new Rig();

        var sent = await rig.Simulator.SendAsync(ArchitectDialectTests.PatientUpload.Split('\r', StringSplitOptions.RemoveEmptyEntries)).WaitAsync(Timeout);
        var batch = await rig.WaitForBatchAsync();

        Assert.Equal(Lis01SendResult.Delivered, sent.Result);
        Assert.Equal(4, batch.Observations.Count);
        Assert.Equal("ARCHITECT", batch.Instrument.Model);
        Assert.Equal(rig.Trace.TraceId, batch.Trace.TraceId);
        Assert.True(batch.Trace.FirstSequence >= 0 && batch.Trace.LastSequence >= batch.Trace.FirstSequence);
        var entries = rig.Trace.Snapshot();
        Assert.Contains(entries, e => e.Direction == TraceDirection.Received && e.Data.Span[0] == ControlCharacters.Enq);
        Assert.Contains(entries, e => e.Direction == TraceDirection.Transmitted && e.Data.Span[0] == ControlCharacters.Ack);
    }

    [Fact]
    public async Task Host_query_is_answered_with_the_orders_of_the_host()
    {
        await using var rig = new Rig();
        rig.Host.OrdersBySpecimen["SID-Q1"] =
        [
            new WorkOrder("ORD-Q1", new SpecimenRef("SID-Q1"), new PatientContext("PID-Q1", "Roe", "Ann", AdministrativeSex: "F"),
                [CodeSet.Of(new CodedValue("530", null, ArchitectDialect.VendorSystem))]),
        ];

        await rig.Simulator.SendAsync("H|\\^&|||ARCHITECT^9.45^F0000000001", "Q|1|^SID-Q1||^^^ALL||||||||O", "L|1").WaitAsync(Timeout);
        var answer = await rig.Simulator.Received.ReadAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal("H|\\^&||||||||||P|1\rP|1|PID-Q1|||Roe^Ann|||F\rO|1|SID-Q1||^^^530|R||||||N||||||||||||||Q\rL|1\r", answer);
        Assert.Equal("SID-Q1", Assert.Single(rig.Host.Queries).Specimens.Single().SpecimenId);
    }

    [Fact]
    public async Task Unknown_specimen_gets_a_negative_query_response()
    {
        await using var rig = new Rig();

        await rig.Simulator.SendAsync("H|\\^&", "Q|1|^SID-NONE||^^^ALL||||||||O", "L|1").WaitAsync(Timeout);
        var answer = await rig.Simulator.Received.ReadAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal("H|\\^&||||||||||P|1\rQ|1|^SID-NONE||^^^ALL||||||||X\rL|1\r", answer);
    }

    [Fact]
    public async Task Slow_host_query_falls_back_to_the_negative_response_within_budget()
    {
        await using var rig = new Rig(budget: TimeSpan.FromMilliseconds(200));
        rig.Host.QueryDelay = TimeSpan.FromSeconds(5);

        await rig.Simulator.SendAsync("H|\\^&", "Q|1|^SID-SLOW||^^^ALL||||||||O", "L|1").WaitAsync(Timeout);
        var answer = await rig.Simulator.Received.ReadAsync().AsTask().WaitAsync(Timeout);

        Assert.Contains("|X\r", answer);
        Assert.Contains(rig.Diagnostics, d => d.Code == SessionCodes.HostQueryTimeout);
    }

    [Fact]
    public async Task Orders_downloaded_by_the_host_reach_the_instrument()
    {
        await using var rig = new Rig();
        var order = new WorkOrder("ORD-D1", new SpecimenRef("SID-D1"), null, [CodeSet.Of(new CodedValue("16", null, ArchitectDialect.VendorSystem))], Action: OrderAction.Add);

        var completed = await rig.Session.SendOrdersAsync([order], CancellationToken.None).WaitAsync(Timeout);
        var received = await rig.Simulator.Received.ReadAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(Lis01SendResult.Delivered, completed.Result);
        Assert.Equal("H|\\^&||||||||||P|1\rP|1\rO|1|SID-D1||^^^16|R||||||A||||||||||||||O\rL|1\r", received);
    }

    [Fact]
    public async Task Replaying_the_captured_trace_reproduces_the_same_observations()
    {
        MemoryTraceSink captured;
        IReadOnlyList<InstrumentObservation> live;
        await using (var rig = new Rig())
        {
            await rig.Simulator.SendAsync(ArchitectDialectTests.PatientUpload.Split('\r', StringSplitOptions.RemoveEmptyEntries)).WaitAsync(Timeout);
            live = (await rig.WaitForBatchAsync()).Observations;
            captured = rig.Trace;
        }

        // Round trip through the trace file format before replaying.
        var file = new StringWriter();
        TraceFile.Write(file, new TraceHeader(captured.TraceId, "test", "architect-01", ArchitectPlugin.AsiProfileId, DateTimeOffset.UtcNow), captured.Snapshot());
        var (_, entries) = TraceFile.Read(new StringReader(file.ToString()));

        var host = new InMemoryLabHost();
        var plugin = new ArchitectPlugin(new ArchitectDialectOptions { InstrumentTimeZone = TimeZoneInfo.Utc });
        var profile = plugin.Profiles[0];
        var instrument = new InstrumentRef("architect-01");
        var replay = new ReplayStream(entries);
        var session = new AstmInstrumentSession(replay, plugin.CreateDialect(profile, new DialectContext(instrument, TimeProvider.System)), profile, host, instrument, new MemoryTraceSink(), new AstmSessionOptions { TransmitHostMessages = false });

        await session.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        var replayed = Assert.Single(host.Batches).Observations;
        Assert.Equal(Project(live), Project(replayed));
        Assert.All(replay.Transmitted, bytes => Assert.Equal(ControlCharacters.Ack, Assert.Single(bytes)));
    }

    private static List<string> Project(IEnumerable<InstrumentObservation> observations) =>
        observations.Select(o => $"{o.Specimen.SpecimenId}|{o.Test.Vendor.Code}|{o.ResultKind}|{o.Value.RawText}|{o.InstrumentStatus}|{string.Join(',', o.Comments)}").ToList();

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public Rig(TimeSpan? budget = null)
        {
            var (hostSide, instrumentSide) = DuplexStream.CreatePair();
            var plugin = new ArchitectPlugin(new ArchitectDialectOptions { InstrumentTimeZone = TimeZoneInfo.Utc });
            var profile = plugin.Profiles[0] with { QueryAnswerBudget = budget ?? TimeSpan.FromSeconds(5) };
            var instrument = new InstrumentRef("architect-01");

            Session = new AstmInstrumentSession(hostSide, plugin.CreateDialect(profile, new DialectContext(instrument, TimeProvider.System)), profile, Host, instrument, Trace,
                new AstmSessionOptions { Diagnostics = d => { lock (Diagnostics) Diagnostics.Add(d); } });
            Simulator = new InstrumentSimulator(instrumentSide, encoding: profile.Encoding);
            _run = Session.RunAsync(_stop.Token);
        }

        public InMemoryLabHost Host { get; } = new();

        public MemoryTraceSink Trace { get; } = new();

        public List<AtsmDiagnostic> Diagnostics { get; } = [];

        public AstmInstrumentSession Session { get; }

        public InstrumentSimulator Simulator { get; }

        public async Task<InstrumentObservationBatch> WaitForBatchAsync()
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Host.Batches.Count > 0)
                    return Host.Batches[0];
                await Task.Delay(10);
            }
            throw new TimeoutException("No batch reached the host.");
        }

        public async ValueTask DisposeAsync()
        {
            await Simulator.DisposeAsync();
            _stop.Cancel();
            try
            {
                await _run.WaitAsync(Timeout);
            }
            catch (OperationCanceledException)
            {
            }
            _stop.Dispose();
        }
    }
}
