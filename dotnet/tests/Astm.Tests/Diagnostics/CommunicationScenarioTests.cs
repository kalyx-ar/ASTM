using System.Net;
using System.Net.Sockets;
using System.Text;
using Astm.Diagnostics;
using Astm.Plugins;
using Astm.Testing;
using Astm.Tests.PortGate;
using Astm.Transport;
using PortGate.Client;

namespace Astm.Tests.Diagnostics;

public sealed class CommunicationScenarioTests
{
    [LocalTracePortGateFact]
    public async Task Current_private_trace_messages_cross_PortGate_and_match_results_and_query_responses()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var input = DiagnosticReader.Read(File.ReadAllBytes(Environment.GetEnvironmentVariable("ASTM_SCENARIO_TRACE")!), DiagnosticInputFormat.Auto, profile, "private-local-scenario", TimeSpan.Zero);
        profile = new DiagnosticCatalog().Resolve(input.Header.Profile!); var scenario = CommunicationScenario.FromTrace(input, profile);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await using var agent = await PortGateProcess.StartAsync(((IPEndPoint)listener.LocalEndpoint).Port);
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await using var host = await new PortGateTransportProvider().OpenAsync(new PortGateTarget("scenario", new("127.0.0.1", agent.Port), "architect-port", "edc-test"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var report = await scenario.RunAsync(profile, host, peer.GetStream(), true, timeout.Token);
        Assert.Equal(scenario.ExpectedObservations.Count, report.Observations); Assert.Equal(scenario.ExpectedQueries, report.ResponsesConfirmed); Assert.True(report.ProtocolBudgetsMet);
    }
    private static readonly string Synthetic = "H|\\^&\rP|1\rO|1|SYN-001\rR|1|^^^GLU|5.4|mmol/L||||F\rL|1|N\r";
    private static (CommunicationScenario Scenario, DiagnosticProfile Profile) Prepare()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var input = DiagnosticReader.Read(Encoding.ASCII.GetBytes(Synthetic), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero);
        return (CommunicationScenario.FromTrace(input, profile), profile);
    }
    [Fact]
    public async Task A_trace_scenario_checks_results_ACK_NAK_and_response_times_over_duplex_streams()
    {
        var (scenario, profile) = Prepare(); var (host, instrument) = DuplexStream.CreatePair();
        await using var owner = host;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var report = await scenario.RunAsync(profile, host, instrument, true, timeout.Token);
        Assert.Equal(1, report.Messages); Assert.Equal(1, report.Observations); Assert.True(report.FramesAcknowledged > 0);
        Assert.True(report.ProtocolBudgetsMet);
        var timings = CommunicationStatistics.Build(report.Analysis);
        Assert.Contains(timings, t => t.Code == "FrameAck" && t.Distribution.Count > 0);
        Assert.Contains(timings, t => t.Code == "FrameNak" && t.Distribution.Count == 1);
    }
    [PortGateFact]
    public async Task Trace_scenarios_cross_the_real_PortGate_process_and_preserve_the_canonical_results()
    {
        var (scenario, profile) = Prepare();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await using var agent = await PortGateProcess.StartAsync(((IPEndPoint)listener.LocalEndpoint).Port);
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var provider = new PortGateTransportProvider();
        await using var host = await provider.OpenAsync(new PortGateTarget("scenario", new("127.0.0.1", agent.Port), "architect-port", "edc-test"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var report = await scenario.RunAsync(profile, host, peer.GetStream(), true, timeout.Token);
        Assert.Equal(1, report.Observations); Assert.Contains(CommunicationStatistics.Build(report.Analysis), t => t.Code == "FrameNak");
    }
    [Fact]
    public async Task A_batch_with_multiple_orders_can_answer_a_plugin_command_without_losing_other_results()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var text = Synthetic.Replace("L|1|N", "O|2|SYN-002\rR|1|^^^UREA|8.2|mmol/L||||F\rL|1|N", StringComparison.Ordinal);
        var scenario = CommunicationScenario.FromTrace(DiagnosticReader.Read(Encoding.ASCII.GetBytes(text), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero), profile);
        var (host, peer) = DuplexStream.CreatePair(); await using var owner = host;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var report = await scenario.RunAsync(profile, host, peer, false, timeout.Token);
        Assert.Equal(1, report.HostCommands); Assert.Equal(2, report.Observations);
    }
    [PortGateFact]
    public async Task Simulated_COM_reads_host_commands_and_responds_through_PortGate_without_an_OS_driver()
    {
        var (scenario, profile) = Prepare();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var report = await PortGateBench.RunAsync(PortGateProcess.Executable()!, scenario, profile, timeout.Token);
        Assert.Equal(scenario.ExpectedObservations.Count, report.Observations);
        Assert.Equal(scenario.ExpectedQueries, report.Queries);
        Assert.Equal(1, report.HostCommands);
        Assert.True(report.ProtocolBudgetsMet);
        var receivedBytes = report.Analysis.Entries.Where(e => e.Direction == Astm.Abstractions.Tracing.TraceDirection.Received).Sum(e => e.Data.Length);
        Assert.True(report.Elapsed >= TimeSpan.FromSeconds(receivedBytes * 10d / 9600));
        Assert.Contains(CommunicationStatistics.Build(report.Analysis), t => t.Code == "FrameAck" && t.Distribution.Count > 0);
    }
}
public sealed class LocalTracePortGateFactAttribute : FactAttribute
{
    public LocalTracePortGateFactAttribute()
    {
        if (PortGateProcess.Executable() is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASTM_SCENARIO_TRACE")))
            Skip = "Requires PortGate and a private local trace selected with ASTM_SCENARIO_TRACE; no patient data is bundled.";
    }
}
