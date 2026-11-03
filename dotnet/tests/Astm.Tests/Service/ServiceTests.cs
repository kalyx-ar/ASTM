using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Hosting;
using Astm.Formats.Medical.Model;
using Astm.Operations;
using Astm.Plugin.Abbott.Architect;
using Astm.Service;
using Astm.Service.Host;
using Astm.Testing;
using Astm.Tests.Hl7;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Astm.Protocol.Mllp;
using Astm.Protocol.Hl7v2;
using Astm.Tests.PortGate;
using PortGate.Client;
using Astm.Plugin.Testing.Synthetic;
using Astm.Plugins;
using Astm.Tests.Architect;

namespace Astm.Tests.Service;

public sealed class ServiceTests
{
    [Fact]
    public void Minimal_JSON_configuration_preserves_defaults_without_reflection()
    {
        var config = JsonSerializer.Deserialize("""
            {"endpoint":"synthetic","ownershipDirectory":"unused","instruments":[
            {"id":"synthetic","profileId":"example","transport":{"kind":"messages"}}]}
            """, ServiceJson.Type<ServiceConfiguration>())!;
        Assert.Equal(4 * 1024 * 1024, config.Limits.MaxFrameBytes);
        Assert.Equal(10000, config.Instruments[0].Transport.ConnectTimeoutMs);
        Assert.Equal("UTC", config.Instruments[0].TimeZone);
    }
    [PortGateFact]
    public async Task Real_PortGate_test_serial_uses_one_lease_for_results_and_queries()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await using var agent = await PortGateProcess.LaunchAsync();
        var plugin = new ArchitectPlugin(); var profile = plugin.Profiles[0];
        var serial = profile.Transport!.Serial!.Default;
        await agent.ConfigureAsync(PortGateProcess.Configuration(0) with
        { Ports = [new PortGatePort("synthetic-port", "synthetic", PortGateDevice.TestSerial(((IPEndPoint)listener.LocalEndpoint).Port, serial),
            [new PortGateController("astm-service/synthetic", 10)])] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var instrument = await listener.AcceptTcpClientAsync(timeout.Token);
        var target = new TransportConfiguration("portgate") { Host = "127.0.0.1", Port = agent.Port, PortId = "synthetic-port", Serial = serial };
        await using var rig = new Rig(target: target, profileId: profile.Id);
        var opened = await rig.Open();
        Assert.NotNull(opened.Tenure); Assert.True(opened.Tenure.Generation > 0);
        await using var simulator = new InstrumentSimulator(instrument.GetStream(), profile.Link, profile.Encoding);
        var sent = await simulator.SendAsync(ArchitectDialectTests.PatientUpload.Split('\r', StringSplitOptions.RemoveEmptyEntries)).WaitAsync(timeout.Token);
        Assert.Equal(Astm.Protocol.Lis01.Lis01SendResult.Delivered, sent.Result);
        await rig.Client.IngestEntered.Task.WaitAsync(timeout.Token);
        Assert.Equal(4, rig.Client.Batches.Single().Observations.Count);
        await simulator.SendAsync("H|\\^&|||ARCHITECT^9.45^SYN", "Q|1|^SID-SYN-1||^^^ALL||||||||O", "L|1").WaitAsync(timeout.Token);
        Assert.NotEmpty(await simulator.Received.ReadAsync(timeout.Token));
        var status = await rig.Client.Call<SessionStatus>("sessions.status", new Ok(), opened.SessionId);
        Assert.Equal(opened.Generation, status.Generation); Assert.Equal(opened.Tenure, status.Tenure);
        await rig.Client.Call<Ok>("sessions.close", new Ok(), opened.SessionId);
    }

    [Fact]
    public async Task FIFO_operations_distinguish_delivered_from_cancelled_before_send()
    {
        var (host, instrument) = DuplexStream.CreatePair(); await using var peer = instrument;
        await using var rig = new Rig(new FakeTransport(host), live: true); var session = await rig.Open();
        var operations = ((IMedicalOperationProvider)Hl7Data.Plugin()).CreateOperations(ArchitectHl7Plugin.ProfileId, new(new("synthetic"), TimeProvider.System));
        var first = MedicalRequests.Inventory(operations, new("SYN", "1"), Guid.NewGuid());
        var second = MedicalRequests.Inventory(operations, new("SYN", "2"), Guid.NewGuid());
        await rig.Client.Call<OperationStatus>("operations.start", first, session.SessionId);
        var wire = Hl7Codec.Parse(Encoding.UTF8.GetString(await ReadMllp(instrument))).Message!;
        Assert.Equal("Queued", (await rig.Client.Call<OperationStatus>("operations.start", second, session.SessionId)).State);
        await rig.Client.Call<OperationStatus>("operations.cancel", new OperationKey(second.OperationId), session.SessionId);
        await instrument.WriteAsync(MllpFraming.Wrap(Encoding.UTF8.GetBytes(Hl7Acknowledgements.Build(wire, "AA", "ACK-SYN", DateTimeOffset.UnixEpoch).Serialize())));
        OperationStatus status;
        do { await Task.Delay(10); status = await rig.Client.Call<OperationStatus>("operations.status", new OperationKey(first.OperationId), session.SessionId); }
        while (status.State != "Finished");
        Assert.Equal(OrderDeliveryState.Sent, status.Delivery!.State);
        do { await Task.Delay(10); status = await rig.Client.Call<OperationStatus>("operations.status", new OperationKey(second.OperationId), session.SessionId); }
        while (status.State != "Cancelled");
        Assert.Null(status.Delivery);
        await rig.Client.Call<Ok>("sessions.close", new Ok(), session.SessionId);
        Assert.Single(rig.Client.Traces, t => t.Entry.Direction == TraceDirection.Transmitted);
    }

    [Fact]
    public async Task Exact_decimal_and_raw_text_are_both_preserved_across_IPC()
    {
        await using var rig = new Rig(); var session = await rig.Open();
        const string exact = "12345678901234567890.12345678";
        var message = Hl7Data.Result().Replace("|9245|", "|" + exact + "|", StringComparison.Ordinal);
        await rig.Client.Call<ContentResult>("messages.process", new ContentRequest(Guid.NewGuid(), Encoding.UTF8.GetBytes(message)), session.SessionId);
        var observation = rig.Client.Batches.Single().Observations.Single(o => o.Value.RawText == exact);
        Assert.Equal(decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture), observation.Value.Numeric);
    }
    [Fact]
    public async Task Local_OS_endpoint_is_independent_of_a_client_and_supports_reconnection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "astm-ipc-" + Guid.NewGuid().ToString("N"));
        var endpoint = OperatingSystem.IsWindows() ? "astm-test-" + Guid.NewGuid().ToString("N") : Path.Combine(directory, "service.sock");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var listener = new LocalIpcListener(endpoint, directory);
            await using var service = new CommunicationService(new(endpoint, directory, []), BundledServiceProfiles.Create());
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var accepting = listener.AcceptAsync(timeout.Token).AsTask();
                Stream stream;
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(timeout.Token); stream = pipe;
                }
                else
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), timeout.Token); stream = new NetworkStream(socket, ownsSocket: true);
                }
                var serving = service.ServeAsync(await accepting, timeout.Token);
                await using (var client = new TestClient(stream))
                {
                    var hello = await client.Call<ServiceHello>("service.hello", new Ok());
                    Assert.Equal(service.InstanceId, hello.InstanceId);
                }
                await serving;
            }
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Direct_TCP_receives_fragmented_messages_and_confirms_ingest_before_AA()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var target = new TransportConfiguration("tcp") { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        await using var rig = new Rig(target: target);
        var opening = rig.Open();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var session = await opening;
        var bytes = MllpFraming.Wrap(Encoding.UTF8.GetBytes(Hl7Data.Result()));
        for (var i = 0; i < bytes.Length; i += 7) await peer.GetStream().WriteAsync(bytes.AsMemory(i, Math.Min(7, bytes.Length - i)), timeout.Token);
        var response = Hl7Codec.Parse(Encoding.UTF8.GetString(await ReadMllp(peer.GetStream()))).Message!;
        Assert.Equal("AA", Hl7Acknowledgements.Read(response)!.Code);
        Assert.Single(rig.Client.Batches);
        Assert.Equal(session.TraceId, rig.Client.Batches.Single().Trace.TraceId);
    }
    [Fact]
    public async Task Bundled_catalog_preserves_advanced_operations_and_contracts()
    {
        await using var rig = new Rig();
        var catalog = await rig.Client.Call<ProfileDescription[]>("profiles.list", new Ok());
        Assert.Equal(14, catalog.Length);
        foreach (var item in catalog)
        {
            var details = await rig.Client.Call<ProfileDetails>("profiles.describe", new ProfileRequest(item.Id, 200, 300));
            Assert.NotEmpty(details.Operations);
            foreach (var reference in details.Operations.SelectMany(d => new[] { d.Input, d.Output }).OfType<OperationContractReference>())
                Assert.Contains(details.Contracts, contract => contract.Reference == reference);
        }
        var hl7 = await rig.Client.Call<ProfileDetails>("profiles.describe", new ProfileRequest(ArchitectHl7Plugin.ProfileId, 200, 300));
        Assert.Contains(hl7.Operations, operation => operation.Id == MedicalPurposes.ReceiveCalibration);
        Assert.Contains(hl7.Operations, operation => operation.Id == MedicalPurposes.QueryInventory);
        var projected = hl7.WireOutputs.Single(output => output.Engine == MedicalDataContracts.Decoded).Wire;
        Assert.Equal(OperationDataKind.String, projected.Root.Fields["observations"].Schema.Items!.Fields["value"].Schema.Fields["numeric"].Schema.Kind);
        var asi = await rig.Client.Call<ProfileDetails>("profiles.describe", new ProfileRequest("abbott.architect.asi-rs232"));
        Assert.DoesNotContain(asi.Operations, operation => operation.Id == MedicalPurposes.QueryInventory);
    }

    [Theory]
    [InlineData(IngestState.Accepted, "AA")]
    [InlineData(IngestState.Duplicate, "AA")]
    [InlineData(IngestState.Rejected, "AR")]
    [InlineData(IngestState.Deferred, "AE")]
    public async Task Ingest_callback_controls_ack_and_replay_preserves_identity(IngestState state, string ack)
    {
        await using var rig = new Rig(); rig.Client.Outcome = new(state);
        var session = await rig.Open();
        var reception = Guid.NewGuid();
        var first = await rig.Client.Call<ContentResult>("messages.process", new ContentRequest(reception, Encoding.UTF8.GetBytes(Hl7Data.Result())), session.SessionId);
        Assert.Equal(state, Assert.Single(first.Outcomes).State);
        Assert.Contains("MSA|" + ack + "|", Encoding.UTF8.GetString(Assert.Single(first.Responses)));
        await rig.Client.Call<ContentResult>("messages.process", new ContentRequest(reception, Encoding.UTF8.GetBytes(Hl7Data.Result())), session.SessionId);
        var batches = rig.Client.Batches.ToArray(); Assert.Equal(2, batches.Length);
        Assert.Equal(batches[0].BatchId, batches[1].BatchId);
        Assert.Equal(batches[0].Observations.Select(o => o.ObservationId), batches[1].Observations.Select(o => o.ObservationId));
        Assert.All(batches[0].Observations, o => Assert.Equal(session.TraceId, o.Trace.TraceId));
        Assert.Contains(rig.Client.Traces, t => t.Entry.Direction == TraceDirection.Received && t.Entry.Data.Length > 0);
    }

    [Fact]
    public async Task Canonical_calibration_and_inventory_cross_the_same_acknowledged_boundary()
    {
        await using var rig = new Rig(); var session = await rig.Open();
        foreach (var content in new[] { ArchitectCalibrationTests.Calibration(), ArchitectCalibrationTests.Inventory() })
            await rig.Client.Call<ContentResult>("messages.process", new ContentRequest(Guid.NewGuid(), Encoding.UTF8.GetBytes(content)), session.SessionId);
        var batches = rig.Client.Batches.ToArray();
        Assert.Equal(ArchitectCalibrationTests.ReportText, Assert.Single(batches[0].CalibrationReports).Text);
        Assert.Empty(batches[0].Observations);
        Assert.NotEmpty(batches[1].InventoryStatuses); Assert.Empty(batches[1].Observations);
    }

    [Fact]
    public async Task Slow_ingest_does_not_finish_a_request_and_status_stays_available()
    {
        await using var rig = new Rig(); var session = await rig.Open();
        rig.Client.IngestGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = rig.Client.Call<ContentResult>("messages.process", new ContentRequest(Guid.NewGuid(), Encoding.UTF8.GetBytes(Hl7Data.Result())), session.SessionId);
        await rig.Client.IngestEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(processing.IsCompleted);
        var status = await rig.Client.Call<SessionStatus>("sessions.status", new Ok(), session.SessionId);
        Assert.Equal("Running", status.State);
        rig.Client.IngestGate.SetResult(new(IngestState.Deferred));
        Assert.Equal(IngestState.Deferred, Assert.Single((await processing).Outcomes).State);
    }

    [Fact]
    public async Task Query_callback_has_a_deadline_and_returns_orders_or_unavailable()
    {
        await using var rig = new Rig(); var session = await rig.Open();
        rig.Client.Answer = new(QueryAnswerState.Orders, [Hl7Data.Order()]);
        var result = await rig.Client.Call<ContentResult>("messages.process", new ContentRequest(Guid.NewGuid(), Encoding.UTF8.GetBytes(Hl7Data.Query())), session.SessionId);
        Assert.Contains("SID-SYN-1", Encoding.UTF8.GetString(Assert.Single(result.Responses)));
        Assert.NotNull(rig.Client.QueryDeadline);
        rig.Client.QueryDelay = TimeSpan.FromMilliseconds(400);
        result = await rig.Client.Call<ContentResult>("messages.process", new ContentRequest(Guid.NewGuid(), Encoding.UTF8.GetBytes(Hl7Data.Query())), session.SessionId);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ASTM-RT-HOST-QUERY-TIMEOUT");
    }

    [Fact]
    public async Task Live_HL7_uses_runtime_send_and_observer_without_automatic_retries()
    {
        var (host, instrument) = DuplexStream.CreatePair();
        await using var peer = instrument;
        await using var rig = new Rig(new FakeTransport(host), live: true);
        var session = await rig.Open();
        var details = await rig.Client.Call<ProfileDetails>("profiles.describe", new ProfileRequest(ArchitectHl7Plugin.ProfileId, 200, 300));
        var operations = ((IMedicalOperationProvider)Hl7Data.Plugin()).CreateOperations(ArchitectHl7Plugin.ProfileId, new(new("synthetic"), TimeProvider.System));
        var envelope = MedicalRequests.Inventory(operations, new("SYN-MODULE", "1"), Guid.NewGuid());
        await rig.Client.Call<OperationStatus>("operations.start", envelope, session.SessionId);
        var wire = await ReadMllp(instrument);
        Assert.Contains("INR^U06", Encoding.UTF8.GetString(wire));
        OperationStatus status;
        do { await Task.Delay(20); status = await rig.Client.Call<OperationStatus>("operations.status", new OperationKey(envelope.OperationId), session.SessionId); }
        while (status.State != "Finished");
        Assert.Equal(OrderDeliveryState.Uncertain, status.Delivery!.State);
        var duplicate = await rig.Client.Call<OperationStatus>("operations.start", envelope, session.SessionId);
        Assert.Equal(status, duplicate);
        var ended = await rig.Client.Call<SessionStatus>("sessions.status", new Ok(), session.SessionId);
        Assert.False(ended.Observation.NoPendingOperations);
    }

    [Fact]
    public async Task Closing_and_reopening_allocates_new_session_generation_without_replay()
    {
        await using var rig = new Rig(); var first = await rig.Open();
        await rig.Client.Call<Ok>("sessions.close", new Ok(), first.SessionId);
        var second = await rig.Open();
        Assert.NotEqual(first.SessionId, second.SessionId); Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(0, second.OperationCount);
    }

    [Fact]
    public async Task Reconnection_after_lost_ingest_reply_preserves_reception_and_allows_duplicate()
    {
        await using var rig = new Rig(); var first = await rig.Open();
        rig.Client.IngestGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reception = Guid.NewGuid(); var input = new ContentRequest(reception, Encoding.UTF8.GetBytes(Hl7Data.Result()));
        var processing = rig.Client.Call<ContentResult>("messages.process", input, first.SessionId);
        await rig.Client.IngestEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var recorded = rig.Client.Batches.Single();
        await rig.Reconnect();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        rig.Client.Outcome = new(IngestState.Duplicate, PreviousReference: "synthetic-previous-commit");
        var second = await rig.Open();
        var result = await rig.Client.Call<ContentResult>("messages.process", input, second.SessionId);
        Assert.Equal(IngestState.Duplicate, Assert.Single(result.Outcomes).State);
        Assert.Equal(recorded.BatchId, rig.Client.Batches.Single().BatchId);
        Assert.Equal(recorded.Observations.Select(o => o.ObservationId), rig.Client.Batches.Single().Observations.Select(o => o.ObservationId));
        Assert.NotEqual(first.Generation, second.Generation);
    }

    [Fact]
    public async Task Cancelling_an_inflight_operation_closes_the_channel_and_retains_uncertainty()
    {
        var (host, instrument) = DuplexStream.CreatePair(); await using var peer = instrument;
        await using var rig = new Rig(new FakeTransport(host), live: true); var session = await rig.Open();
        var operations = ((IMedicalOperationProvider)Hl7Data.Plugin()).CreateOperations(ArchitectHl7Plugin.ProfileId, new(new("synthetic"), TimeProvider.System));
        var request = MedicalRequests.Inventory(operations, new("SYN", "1"), Guid.NewGuid());
        await rig.Client.Call<OperationStatus>("operations.start", request, session.SessionId);
        await ReadMllp(instrument);
        await rig.Client.Call<OperationStatus>("operations.cancel", new OperationKey(request.OperationId), session.SessionId);
        OperationStatus status;
        do { await Task.Delay(10); status = await rig.Client.Call<OperationStatus>("operations.status", new OperationKey(request.OperationId), session.SessionId); }
        while (status.State != "Finished");
        Assert.Equal(OrderDeliveryState.Uncertain, status.Delivery!.State);
        Assert.Equal(0, await instrument.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(status, await rig.Client.Call<OperationStatus>("operations.start", request, session.SessionId));
    }

    internal static async Task<byte[]> ReadMllp(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var decoder = new Astm.Protocol.Mllp.MllpDecoder(); var events = new List<Astm.Protocol.Mllp.MllpEvent>(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, timeout.Token); Assert.True(count > 0);
            decoder.Feed(buffer.AsSpan(0, count), events);
            if (events.FirstOrDefault(e => e.Kind == Astm.Protocol.Mllp.MllpEventKind.Message) is { } message) return message.Payload!;
        }
    }
    internal sealed class FakeTransport(Stream stream) : IServiceTransportFactory
    {
        public int Opened;
        public ValueTask<IReadOnlyList<string>> OwnershipKeysAsync(InstrumentConfiguration config, CancellationToken token) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        public ValueTask<ServiceConnection> OpenAsync(InstrumentConfiguration config, ProfileBinding binding, ITraceSink trace, CancellationToken token)
        { Interlocked.Increment(ref Opened); return ValueTask.FromResult(new ServiceConnection(stream)); }
    }
    internal sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private readonly CommunicationService _service;
        private Task _serving;
        public TestClient Client { get; private set; }
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "astm-service-tests", Guid.NewGuid().ToString("N"));
        public Rig(IServiceTransportFactory? transports = null, bool live = false, ServiceLimits? limits = null, TransportConfiguration? target = null,
            string profileId = ArchitectHl7Plugin.ProfileId)
        {
            var (server, client) = DuplexStream.CreatePair();
            _service = new(new("unused", Directory, [new("synthetic", profileId, target ?? new(live ? "tcp" : "messages"))
            { QueryBudgetMs = 200, ResponseTimeoutMs = 300 }]) { Limits = limits ?? new() }, BundledServiceProfiles.Create(), transports);
            Client = new(client); _serving = _service.ServeAsync(server, _stop.Token);
        }
        public Task<SessionStatus> Open() => Client.Call<SessionStatus>("sessions.open", new OpenSessionRequest("synthetic", Guid.NewGuid()));
        public async Task Reconnect()
        {
            await Client.DisposeAsync(); await _serving;
            var (server, client) = DuplexStream.CreatePair(); Client = new(client); _serving = _service.ServeAsync(server, _stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync(); _stop.Cancel(); await _serving; await _service.DisposeAsync(); _stop.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }
    internal sealed class TestClient : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcEnvelope>> _pending = new();
        private readonly Task _reader;
        public ConcurrentQueue<InstrumentObservationBatch> Batches { get; } = new();
        public ConcurrentQueue<TraceAppendRequest> Traces { get; } = new();
        public IngestOutcome Outcome { get; set; } = IngestOutcome.Accepted;
        public OrderQueryAnswer Answer { get; set; } = OrderQueryAnswer.NoInformation;
        public TimeSpan QueryDelay { get; set; }
        public DateTimeOffset? QueryDeadline { get; private set; }
        public TaskCompletionSource<IngestOutcome>? IngestGate;
        public TaskCompletionSource IngestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TestClient(Stream stream) { _stream = stream; _reader = ReadLoop(); }
        public async Task<T> Call<T>(string method, object data, Guid? session = null)
        {
            var payload = data switch
            {
                Ok value => ServiceJson.Element(value), ProfileRequest value => ServiceJson.Element(value), OpenSessionRequest value => ServiceJson.Element(value),
                ContentRequest value => ServiceJson.Element(value), OperationEnvelope value => ServiceJson.Element(value), OperationKey value => ServiceJson.Element(value),
                _ => throw new NotSupportedException(),
            };
            var id = Guid.NewGuid(); var completion = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                await Write(new(1, id, "request", payload) { Method = method, SessionId = session });
                var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (reply.Kind == "error") throw new ServiceException(reply.Error ?? "unknown");
                return ServiceJson.Read<T>(reply.Data);
            }
            finally { _pending.TryRemove(id, out _); }
        }
        private async Task ReadLoop()
        {
            try
            {
                while (await IpcFrames.ReadAsync(_stream, 4 * 1024 * 1024, _stop.Token) is { } message)
                    if (message.Kind == "callback") _ = Task.Run(() => Callback(message));
                    else if (_pending.TryGetValue(message.Id, out var pending)) pending.TrySetResult(message);
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }
        private async Task Callback(IpcEnvelope message)
        {
            try
            {
                JsonElement data;
                switch (message.Method)
                {
                    case "trace.ready":
                        var ready = ServiceJson.Read<TraceReadyRequest>(message.Data); data = ServiceJson.Element(new TraceReadyReply(ready.TraceId, true)); break;
                    case "trace.append":
                        var trace = ServiceJson.Read<TraceAppendRequest>(message.Data); Traces.Enqueue(trace);
                        data = ServiceJson.Element(new TraceAppendReply(trace.TraceId, trace.Entry.Sequence, true)); break;
                    case "workflow.ingest":
                        Batches.Enqueue(ServiceJson.Read<InstrumentObservationBatch>(message.Data)); IngestEntered.TrySetResult();
                        data = ServiceJson.Element(IngestGate is null ? Outcome : await IngestGate.Task.WaitAsync(_stop.Token)); break;
                    case "workflow.query":
                        QueryDeadline = message.DeadlineUtc; await Task.Delay(QueryDelay, _stop.Token); data = ServiceJson.Element(Answer); break;
                    default: throw new NotSupportedException();
                }
                await Write(new(1, message.Id, "result", data));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        private async Task Write(IpcEnvelope envelope)
        { await _write.WaitAsync(_stop.Token); try { await IpcFrames.WriteAsync(_stream, envelope, 4 * 1024 * 1024, _stop.Token); } finally { _write.Release(); } }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _stream.Dispose();
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            await _reader;
        }
    }
}
