using System.Net;
using System.Net.Sockets;
using Astm.Abstractions.Model;
using Astm.Abstractions.Transport;
using Astm.Plugin.Testing.Synthetic;
using Astm.Plugins;
using Astm.Runtime.Sessions;
using Astm.Runtime.Tracing;
using Astm.Testing;
using Astm.Tests.PortGate;
using PortGate.Client;

namespace Astm.Tests.Integration;

/// <summary>
/// Whole circuit with synthetic data: host double → ASTM session with the synthetic test plugin → (duplex stream or the real
/// PortGate agent and its test serial port) → simulated analyzer, rigid or randomized, and back. The analyzer ledger and
/// what the host ingested must match field by field.
/// </summary>
public sealed class SyntheticCircuitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Rigid_analyzer_returns_golden_values_for_downloaded_queried_and_unsolicited_work()
    {
        var (hostSide, instrumentSide) = DuplexStream.CreatePair();
        await using var circuit = new Circuit(hostSide, instrumentSide, new RigidSimulation());
        using var timeout = new CancellationTokenSource(Timeout);

        await circuit.DownloadAsync([Order("SYN-D001", "GLU", "CREA")], timeout.Token);
        circuit.Host.OrdersBySpecimen["SYN-Q001"] = [Order("SYN-Q001", "NA", "K")];
        var answered = await circuit.Analyzer.QueryAsync(["SYN-Q001"], timeout.Token);
        Assert.Equal(["NA", "K"], answered.Single().Tests);
        await circuit.Analyzer.ReportAsync(answered, timeout.Token);
        Assert.Empty(await circuit.Analyzer.QueryAsync(["SYN-Q404"], timeout.Token));  // no information: nothing to run
        await circuit.Analyzer.ReportAsync([new("SYN-U001", ["HGB", "ZZZ"])], timeout.Token);

        await circuit.VerifyAsync(timeout.Token);
        var golden = circuit.Analyzer.Sent.ToDictionary(r => (r.SpecimenId, r.TestCode), r => (r.Value, r.Status));
        Assert.Equal(("5.0", "F"), golden[("SYN-D001", "GLU")]);
        Assert.Equal(("80", "F"), golden[("SYN-D001", "CREA")]);
        Assert.Equal(("140", "F"), golden[("SYN-Q001", "NA")]);
        Assert.Equal(("4.3", "F"), golden[("SYN-Q001", "K")]);
        Assert.Equal(("14.8", "F"), golden[("SYN-U001", "HGB")]);
        Assert.Equal(("", "X"), golden[("SYN-U001", "ZZZ")]);  // not on the menu: not performed, with a comment
        Assert.Equal(0, circuit.Analyzer.CorruptedFrames);
        Assert.Equal(2, circuit.Host.Queries.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    [InlineData(31337)]
    public async Task Randomized_analyzer_survives_faults_and_the_host_ingests_exactly_what_it_reported(int seed)
    {
        var (hostSide, instrumentSide) = DuplexStream.CreatePair();
        await using var circuit = new Circuit(hostSide, instrumentSide, new RandomizedSimulation(seed));
        using var timeout = new CancellationTokenSource(Timeout);
        await circuit.RunWorkloadAsync(seed, specimens: 8, timeout.Token);
        await circuit.VerifyAsync(timeout.Token);
        Assert.NotEmpty(circuit.Analyzer.Sent);
        Assert.True(circuit.Analyzer.CorruptedFrames > 0, "the randomized link must exercise NAK and retransmission");
    }

    [Fact]
    public async Task Randomized_runs_with_the_same_seed_report_the_same_results()
    {
        async Task<IReadOnlyList<SimulatedResult>> RunAsync()
        {
            var (hostSide, instrumentSide) = DuplexStream.CreatePair();
            await using var circuit = new Circuit(hostSide, instrumentSide, new RandomizedSimulation(99));
            using var timeout = new CancellationTokenSource(Timeout);
            await circuit.RunWorkloadAsync(99, specimens: 5, timeout.Token);
            await circuit.VerifyAsync(timeout.Token);
            return circuit.Analyzer.Sent;
        }
        var first = await RunAsync();
        var second = await RunAsync();
        Assert.Equal(first.Select(Key), second.Select(Key));
        Assert.Contains(first, r => r.Flags.Count > 0 || r.Status != "F");  // the run really leaves the happy path
    }

    [PortGateFact]
    public async Task Rigid_and_randomized_analyzers_answer_through_the_real_PortGate_test_serial_port()
    {
        foreach (ISimulationPolicy policy in new ISimulationPolicy[] { new RigidSimulation(), new RandomizedSimulation(5) })
        {
            using var peerListener = new TcpListener(IPAddress.Loopback, 0);
            peerListener.Start();
            var serial = new SerialSettings(115200);
            await using var agent = await PortGateProcess.LaunchAsync();
            await agent.ConfigureAsync(PortGateProcess.Configuration(0) with
            {
                Ports = [new PortGatePort("synthetic-port", "synthetic-01", PortGateDevice.TestSerial(((IPEndPoint)peerListener.LocalEndpoint).Port, serial),
                    [new PortGateController("edc-test", 10)])],
            });
            using var peer = await peerListener.AcceptTcpClientAsync().WaitAsync(Timeout);
            peer.NoDelay = true;
            var hostSide = await new PortGateTransportProvider().OpenAsync(
                new PortGateTarget("synthetic", new("127.0.0.1", agent.Port), "synthetic-port", "edc-test") { Settings = serial }, CancellationToken.None);
            await using var circuit = new Circuit(hostSide, peer.GetStream(), policy);
            using var timeout = new CancellationTokenSource(Timeout);
            await circuit.RunWorkloadAsync(5, specimens: 6, timeout.Token);
            await circuit.VerifyAsync(timeout.Token);
        }
    }

    private static WorkOrder Order(string specimen, params string[] tests) =>
        new(Guid.CreateVersion7().ToString(), new(specimen), null,
            tests.Select(t => CodeSet.Of(new CodedValue(t, null, CodeSystems.Vendor(SyntheticAstmPlugin.PluginId)))).ToArray());

    private static string Key(SimulatedResult r) => string.Join('\u001F', r.SpecimenId, r.TestCode, r.Value, r.Unit, r.ReferenceRange,
        string.Join(',', r.Flags), r.Status, string.Join('\u001E', r.Comments), r.ResultTime.ToString("O"));

    private static string Key(InstrumentObservation o) => string.Join('\u001F', o.Specimen.SpecimenId, o.Test.Vendor.Code, o.Value.RawText,
        o.Value.RawUnit, o.ReferenceRangeText, string.Join(',', o.Flags.Select(f => f.Code)), o.InstrumentStatus, string.Join('\u001E', o.Comments),
        o.ResultTime?.DateTime.ToString("O"));

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        private readonly Stream _hostSide;
        private readonly AstmInstrumentSession _session;

        public Circuit(Stream hostSide, Stream instrumentSide, ISimulationPolicy policy)
        {
            var plugin = new SyntheticAstmPlugin();
            var profile = plugin.Profiles[0];
            var instrument = new InstrumentRef("synthetic-01");
            _hostSide = hostSide;
            _session = new AstmInstrumentSession(hostSide, plugin.CreateDialect(profile, new DialectContext(instrument, TimeProvider.System)), profile, Host,
                instrument, new MemoryTraceSink(), new AstmSessionOptions());
            Analyzer = new SimulatedAnalyzer(instrumentSide, policy, profile);
            _run = _session.RunAsync(_stop.Token);
        }

        public InMemoryLabHost Host { get; } = new();

        public SimulatedAnalyzer Analyzer { get; }

        public async Task DownloadAsync(IReadOnlyList<WorkOrder> orders, CancellationToken token)
        {
            var sending = _session.SendOrdersAsync(orders, token);
            var received = await Analyzer.ReceiveOrdersAsync(token);
            Assert.Equal(Lis01SendResultDelivered, (await sending).Result);
            Assert.Equal(orders.Select(o => (o.Specimen.SpecimenId, string.Join(',', o.Tests.Select(t => t.Vendor.Code)))),
                received.Select(o => (o.SpecimenId, string.Join(',', o.Tests))));
            await Analyzer.ReportAsync(received, token);
        }

        /// <summary>Synthetic specimens with tests drawn from the menu (plus an unknown code now and then), split among
        /// order download, host query and unsolicited results; all decided by <paramref name="seed"/>.</summary>
        public async Task RunWorkloadAsync(int seed, int specimens, CancellationToken token)
        {
            var random = new Random(seed);
            var menu = SyntheticAstmPlugin.Menu.Select(t => t.Code).ToArray();
            for (var i = 1; i <= specimens; i++)
            {
                var tests = menu.OrderBy(_ => random.Next()).Take(random.Next(1, 5)).ToList();
                if (random.Next(8) == 0)
                    tests.Add("ZZZ");
                var specimen = $"SYN-{seed}-{i:D3}";
                switch (random.Next(3))
                {
                    case 0:
                        await DownloadAsync([Order(specimen, [.. tests])], token);
                        break;
                    case 1:
                        Host.OrdersBySpecimen[specimen] = [Order(specimen, [.. tests])];
                        var answered = await Analyzer.QueryAsync([specimen], token);
                        Assert.Equal(tests, answered.Single().Tests);
                        await Analyzer.ReportAsync(answered, token);
                        break;
                    default:
                        await Analyzer.ReportAsync([new(specimen, tests)], token);
                        break;
                }
            }
        }

        /// <summary>Every transmitted result reached the host once, with the same value, unit, range, flags, status,
        /// comments and time.</summary>
        public async Task VerifyAsync(CancellationToken token)
        {
            var expected = Analyzer.Sent.Select(Key).Order(StringComparer.Ordinal).ToArray();
            while (Host.Batches.Sum(b => b.Observations.Count) < expected.Length)
                await Task.Delay(5, token);
            var actual = Host.Batches.SelectMany(b => b.Observations).Select(Key).Order(StringComparer.Ordinal).ToArray();
            Assert.True(expected.SequenceEqual(actual), $"{Analyzer.Policy.Description}: ingested results differ from the analyzer ledger");
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _run; }
            catch (OperationCanceledException) { }
            await Analyzer.DisposeAsync();
            await _hostSide.DisposeAsync();
            _stop.Dispose();
        }
    }

    private const Astm.Protocol.Lis01.Lis01SendResult Lis01SendResultDelivered = Astm.Protocol.Lis01.Lis01SendResult.Delivered;
}
