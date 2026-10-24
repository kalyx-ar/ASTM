using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Atsm.Abstractions.Model;
using Atsm.Channels;
using Atsm.Channels.SerialTunnel;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugins;
using Atsm.Runtime.Sessions;
using Atsm.Runtime.Tracing;
using Atsm.Testing;
using Atsm.Tests.Architect;

namespace Atsm.Tests.SerialTunnel;

/// <summary>Runs only when the native agent is built (native/build/*/Release/atsm-agent[.exe]) or ATSM_AGENT_EXE points to it.</summary>
public sealed class NativeAgentFactAttribute : FactAttribute
{
    public NativeAgentFactAttribute()
    {
        if (NativeAgent.Executable() is null)
            Skip = "Native agent not built: run CMake in native/ or set ATSM_AGENT_EXE.";
    }
}

public class NativeAgentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [NativeAgentFact]
    public async Task Instrument_results_and_host_query_cross_the_native_agent()
    {
        // The "serial" device of the agent is a TCP connection to this listener, where the simulator plays the instrument.
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await NativeAgent.StartAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port);
        using var instrumentSocket = await instrumentListener.AcceptTcpClientAsync().WaitAsync(Timeout);
        await using var simulator = new InstrumentSimulator(instrumentSocket.GetStream(), encoding: new ArchitectPlugin().Profiles[0].Encoding);

        var channel = await SerialTunnelChannel.ConnectAsync("tunnel", new TcpEndpointOptions("127.0.0.1", agent.Port), new SerialTunnelOptions("edc-test", "architect-port"), CancellationToken.None);
        Assert.Equal(Atsm.Abstractions.Channels.OwnershipEnforcement.Enforced, channel.Capabilities.Ownership);
        Assert.Equal("architect-01", channel.Descriptor.InstrumentId);

        var host = new InMemoryLabHost();
        host.OrdersBySpecimen["SID-TUNNEL"] = [new WorkOrder("ORD-T", new SpecimenRef("SID-TUNNEL"), null, [CodeSet.Of(new CodedValue("530", null, ArchitectDialect.VendorSystem))])];
        var plugin = new ArchitectPlugin(new ArchitectDialectOptions { InstrumentTimeZone = TimeZoneInfo.Utc });
        var profile = plugin.Profiles[0];
        var instrument = new InstrumentRef("architect-01");
        var session = new AstmInstrumentSession(channel, plugin.CreateDialect(profile, new DialectContext(instrument, TimeProvider.System)), profile, host, instrument, new MemoryTraceSink(), new AstmSessionOptions("edc-test"));
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);

        try
        {
            await WaitUntilAsync(() => session.LinkState == Atsm.Protocol.Lis01.Lis01State.Idle && channel.AgentEpoch != Guid.Empty);
            // Give the agent time to process ConfirmTransfer before the instrument starts talking.
            await Task.Delay(300);

            var sent = await simulator.SendAsync(ArchitectDialectTests.PatientUpload.Split('\r', StringSplitOptions.RemoveEmptyEntries)).WaitAsync(Timeout);
            Assert.Equal(Atsm.Protocol.Lis01.Lis01SendResult.Delivered, sent.Result);
            await WaitUntilAsync(() => host.Batches.Count == 1);
            Assert.Equal(4, host.Batches[0].Observations.Count);

            await simulator.SendAsync("H|\\^&", "Q|1|^SID-TUNNEL||^^^ALL||||||||O", "L|1").WaitAsync(Timeout);
            var answer = await simulator.Received.ReadAsync().AsTask().WaitAsync(Timeout);
            Assert.Equal("H|\\^&||||||||||P|1\rP|1\rO|1|SID-TUNNEL||^^^530|R||||||N||||||||||||||Q\rL|1\r", answer);
        }
        finally
        {
            await stop.CancelAsync();
            await run.ContinueWith(_ => { }, TaskScheduler.Default);
            await channel.DisposeAsync();
        }
    }

    [NativeAgentFact]
    public async Task Second_controller_is_queued_while_the_first_owns_the_port()
    {
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await NativeAgent.StartAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port);
        using var instrumentSocket = await instrumentListener.AcceptTcpClientAsync().WaitAsync(Timeout);

        await using var first = await SerialTunnelChannel.ConnectAsync("a", new TcpEndpointOptions("127.0.0.1", agent.Port), new SerialTunnelOptions("edc-test", "architect-port"), CancellationToken.None);
        await using var second = await SerialTunnelChannel.ConnectAsync("b", new TcpEndpointOptions("127.0.0.1", agent.Port), new SerialTunnelOptions("edc-backup", "architect-port"), CancellationToken.None);
        var lease = await first.AcquireAsync(new("edc-test"), CancellationToken.None);
        await first.ConfirmTransferAsync(lease, lease.TransferId, CancellationToken.None);

        var waiting = second.AcquireAsync(new("edc-backup"), CancellationToken.None).AsTask();
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);

        await first.ReleaseAsync(lease, CancellationToken.None);
        var secondLease = await waiting.WaitAsync(Timeout);
        Assert.Equal(lease.Generation + 1, secondLease.Generation);
        Assert.Equal(lease.AgentEpoch, secondLease.AgentEpoch);

        // The old generation can no longer write, even from its original connection.
        var stale = await first.WriteAsync(new byte[] { 0x05 }, lease, CancellationToken.None);
        Assert.Equal(Atsm.Abstractions.Channels.WriteProgress.Rejected, stale.Progress);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}

internal sealed class NativeAgent : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _config;

    private NativeAgent(Process process, string config, int port)
    {
        _process = process;
        _config = config;
        Port = port;
    }

    public int Port { get; }

    public static string? Executable()
    {
        var configured = Environment.GetEnvironmentVariable("ATSM_AGENT_EXE");
        if (!string.IsNullOrEmpty(configured))
            return File.Exists(configured) ? configured : null;
        var name = OperatingSystem.IsWindows() ? "atsm-agent.exe" : "atsm-agent";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var build = Path.Combine(directory.FullName, "native", "build");
            if (!Directory.Exists(build))
                continue;
            return Directory.EnumerateFiles(build, name, SearchOption.AllDirectories).FirstOrDefault(p => p.Contains("Release", StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    public static async Task<NativeAgent> StartAsync(int instrumentPort)
    {
        var config = Path.Combine(Path.GetTempPath(), $"atsm-agent-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(config, $$"""
            {
              "schemaVersion": "atsm-agent/1",
              "agentId": "test-agent",
              "platformProfile": "test",
              "listen": { "host": "127.0.0.1", "port": 0 },
              "heartbeatMs": 2000,
              "ports": [
                {
                  "portId": "architect-port",
                  "instrumentId": "architect-01",
                  "device": { "kind": "tcp", "host": "127.0.0.1", "port": {{instrumentPort}} },
                  "ownership": { "leaseMs": 6000, "controllers": [ { "id": "edc-test", "priority": 10 }, { "id": "edc-backup", "priority": 20 } ] }
                }
              ]
            }
            """);
        var process = Process.Start(new ProcessStartInfo(Executable()!, ["--config", config, "--quiet"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (line is null || !line.StartsWith("ATSM-AGENT-LISTENING ", StringComparison.Ordinal))
        {
            process.Kill();
            throw new InvalidOperationException($"Agent did not start: {line}");
        }
        return new NativeAgent(process, config, int.Parse(line["ATSM-AGENT-LISTENING ".Length..], System.Globalization.CultureInfo.InvariantCulture));
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
        File.Delete(_config);
    }
}
