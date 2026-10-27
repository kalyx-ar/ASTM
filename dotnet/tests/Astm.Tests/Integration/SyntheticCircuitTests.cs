using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Astm.Abstractions.Transport;
using Astm.Diagnostics;
using Astm.Plugin.Testing.Synthetic;
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
        await using var circuit = new SyntheticCircuit(hostSide, instrumentSide, new RigidSimulation());
        using var timeout = new CancellationTokenSource(Timeout);
        var started = Stopwatch.GetTimestamp();

        await circuit.DownloadAsync([SyntheticCircuit.Order("SYN-D001", "GLU", "CREA")], timeout.Token);
        await circuit.QueryAsync(SyntheticCircuit.Order("SYN-Q001", "NA", "K"), timeout.Token);
        Assert.Empty(await circuit.Analyzer.QueryAsync(["SYN-Q404"], timeout.Token));  // no information: nothing to run
        await circuit.UnsolicitedAsync(new("SYN-U001", ["HGB", "ZZZ"]), timeout.Token);

        var report = await circuit.CompareAsync(started, timeout.Token);
        Assert.True(report.Passed);
        Assert.Equal((6, 0), (report.Results, report.Mismatches));
        var golden = circuit.Analyzer.Sent.ToDictionary(r => (r.SpecimenId, r.TestCode), r => (r.Value, r.Status));
        Assert.Equal(("5.0", "F"), golden[("SYN-D001", "GLU")]);
        Assert.Equal(("80", "F"), golden[("SYN-D001", "CREA")]);
        Assert.Equal(("140", "F"), golden[("SYN-Q001", "NA")]);
        Assert.Equal(("4.3", "F"), golden[("SYN-Q001", "K")]);
        Assert.Equal(("14.8", "F"), golden[("SYN-U001", "HGB")]);
        Assert.Equal(("", "X"), golden[("SYN-U001", "ZZZ")]);  // not on the menu: not performed, with a comment
        Assert.Equal(0, report.CorruptedFrames);
        Assert.Equal(2, report.HostQueries);
        Assert.Equal(6, report.Analysis!.Observations.Count);  // the captured case is analyzed with the same plugin
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
        using var timeout = new CancellationTokenSource(Timeout);
        var report = await SyntheticCircuit.RunAsync(hostSide, instrumentSide, new(SimulationMode.Randomized, seed, 8), timeout.Token);
        Assert.True(report.Passed, $"{report.Policy}: {report.Mismatches} mismatches, {report.Ingested}/{report.Results} ingested");
        Assert.True(report.CorruptedFrames > 0, "the randomized link must exercise NAK and retransmission");
        Assert.Equal(8, report.Downloaded + report.Queried + report.Unsolicited);
        Assert.True(report.Downloaded > 0 && report.Queried > 0 && report.Unsolicited > 0);
    }

    [Fact]
    public async Task Randomized_runs_with_the_same_seed_report_the_same_results()
    {
        async Task<IReadOnlyList<SimulatedResult>> RunAsync()
        {
            var (hostSide, instrumentSide) = DuplexStream.CreatePair();
            await using var circuit = new SyntheticCircuit(hostSide, instrumentSide, new RandomizedSimulation(99));
            using var timeout = new CancellationTokenSource(Timeout);
            await circuit.RunWorkloadAsync(99, specimens: 5, timeout.Token);
            Assert.True((await circuit.CompareAsync(Stopwatch.GetTimestamp(), timeout.Token)).Passed);
            return circuit.Analyzer.Sent;
        }
        var first = await RunAsync();
        var second = await RunAsync();
        Assert.Equal(first.Select(SyntheticCircuit.Key), second.Select(SyntheticCircuit.Key));
        Assert.Contains(first, r => r.Flags.Count > 0 || r.Status != "F");  // the run really leaves the happy path
    }

    [Fact]
    public async Task A_host_result_that_differs_from_the_ledger_fails_the_run()
    {
        var (hostSide, instrumentSide) = DuplexStream.CreatePair();
        await using var circuit = new SyntheticCircuit(hostSide, instrumentSide, new RigidSimulation());
        using var timeout = new CancellationTokenSource(Timeout);
        await circuit.UnsolicitedAsync(new("SYN-M001", ["GLU"]), timeout.Token);
        var clean = await circuit.CompareAsync(Stopwatch.GetTimestamp(), timeout.Token);
        Assert.True(clean.Passed);
        // The host also holds a result the analyzer never sent (same specimen and test, another value).
        var batch = circuit.Host.Batches.Single();
        var altered = batch.Observations[0] with { Value = batch.Observations[0].Value with { RawText = "9.9" } };
        await circuit.Host.IngestAsync(batch with { Observations = [altered] }, timeout.Token);
        var report = await circuit.CompareAsync(Stopwatch.GetTimestamp(), timeout.Token);
        Assert.False(report.Passed);
        Assert.Equal((1, 2, 1), (report.Results, report.Ingested, report.Mismatches));
    }

    [PortGateFact]
    public async Task Rigid_and_randomized_analyzers_answer_through_the_real_PortGate_test_serial_port()
    {
        foreach (var options in new SimulationOptions[] { new(SimulationMode.Rigid, 5), new(SimulationMode.Randomized, 5) })
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
            using var timeout = new CancellationTokenSource(Timeout);
            var report = await SyntheticCircuit.RunAsync(hostSide, peer.GetStream(), options, timeout.Token);
            Assert.True(report.Passed, report.Policy);
        }
    }

    [PortGateFact]
    public async Task The_PortGate_bench_runs_the_synthetic_analyzer_as_astm_trace_simulate_and_Diagnostics_do()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var report = await PortGateBench.RunAsync(PortGateProcess.Executable()!, new SimulationOptions(SimulationMode.Randomized, 11, 4), timeout.Token);
        Assert.True(report.Passed, report.Policy);
        Assert.NotEmpty(report.CapturedCase!.Entries);
    }
}
