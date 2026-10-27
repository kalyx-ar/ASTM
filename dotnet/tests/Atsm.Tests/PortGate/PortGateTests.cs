using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Transport;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugins;
using Atsm.Runtime.Sessions;
using Atsm.Runtime.Tracing;
using Atsm.Testing;
using Atsm.Tests.Architect;
using Atsm.Transport;
using PortGate.Client;

namespace Atsm.Tests.PortGate;

/// <summary>Runs only when PortGate is built (PortGate/build/*/Release/portgate[.exe]) or PORTGATE_EXE points to it.</summary>
public sealed class PortGateFactAttribute : FactAttribute
{
    public PortGateFactAttribute()
    {
        if (PortGateProcess.Executable() is null)
            Skip = "PortGate not built: run CMake in PortGate/ or set PORTGATE_EXE.";
    }
}

public class PortGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [PortGateFact]
    public async Task Instrument_results_and_host_query_cross_PortGate_as_the_session_transport()
    {
        // The device of the agent is a TCP connection to this listener, where the simulator plays the instrument.
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await PortGateProcess.StartAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port);
        using var instrumentSocket = await instrumentListener.AcceptTcpClientAsync().WaitAsync(Timeout);
        await using var simulator = new InstrumentSimulator(instrumentSocket.GetStream(), encoding: new ArchitectPlugin().Profiles[0].Encoding);

        // The host chooses the transport; ATSM only receives the Stream of the held port.
        var router = new TransportRouter([new LocalTransportProvider(), new PortGateTransportProvider()]);
        await using var transport = await router.OpenAsync(new PortGateTarget("tunnel", new TcpEndpoint("127.0.0.1", agent.Port), "architect-port", "edc-test"), CancellationToken.None);

        var host = new InMemoryLabHost();
        host.OrdersBySpecimen["SID-TUNNEL"] = [new WorkOrder("ORD-T", new SpecimenRef("SID-TUNNEL"), null, [CodeSet.Of(new CodedValue("530", null, ArchitectDialect.VendorSystem))])];
        var plugin = new ArchitectPlugin(new ArchitectDialectOptions { InstrumentTimeZone = TimeZoneInfo.Utc });
        var profile = plugin.Profiles[0];
        var instrument = new InstrumentRef("architect-01");
        var session = new AstmInstrumentSession(transport, plugin.CreateDialect(profile, new DialectContext(instrument, TimeProvider.System)), profile, host, instrument, new MemoryTraceSink(), new());
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);
        try
        {
            await WaitUntilAsync(() => session.LinkState == Atsm.Protocol.Lis01.Lis01State.Idle);
            await Task.Delay(300);  // ConfirmTransfer reaches the agent before the instrument talks
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
        }
    }

    [PortGateFact]
    public async Task Second_controller_is_queued_and_the_stream_of_a_finished_tenure_cannot_write()
    {
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await PortGateProcess.StartAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port);
        using var instrumentSocket = await instrumentListener.AcceptTcpClientAsync().WaitAsync(Timeout);

        await using var first = await PortGateChannel.ConnectAsync(new TcpEndpoint("127.0.0.1", agent.Port), new PortGateOptions("edc-test", "architect-port"), CancellationToken.None);
        await using var second = await PortGateChannel.ConnectAsync(new TcpEndpoint("127.0.0.1", agent.Port), new PortGateOptions("edc-backup", "architect-port"), CancellationToken.None);
        Assert.Equal("architect-01", first.Port.InstrumentId);
        var lease = await first.AcquireAsync();
        var stream = first.Stream!;

        var waiting = second.AcquireAsync();
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);

        await first.ReleaseAsync();
        var secondLease = await waiting.WaitAsync(Timeout);
        Assert.Equal(lease.Generation + 1, secondLease.Generation);
        // The stream of the finished tenure ends and cannot write anymore.
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Equal("PORTGATE-CH-NOT-OWNER", (await Assert.ThrowsAsync<PortGateException>(async () => await stream.WriteAsync(new byte[] { 0x05 }))).Code);
    }

    [PortGateFact]
    public async Task Owner_yields_the_port_to_a_cooperative_handover_request()
    {
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await PortGateProcess.StartAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port);
        using var instrumentSocket = await instrumentListener.AcceptTcpClientAsync().WaitAsync(Timeout);

        PortGateHandoverRequest? asked = null;
        await using var owner = await PortGateChannel.ConnectAsync(new TcpEndpoint("127.0.0.1", agent.Port),
            new PortGateOptions("edc-test", "architect-port") { HandoverDecision = request => { asked = request; return true; } }, CancellationToken.None);
        await using var requester = await PortGateChannel.ConnectAsync(new TcpEndpoint("127.0.0.1", agent.Port), new PortGateOptions("edc-backup", "architect-port"), CancellationToken.None);
        var lease = await owner.AcquireAsync();

        // Only a queued controller may ask.
        Assert.Equal(new PortGateHandoverResult(false, "PORTGATE-HANDOVER-NOT-QUEUED"), await requester.RequestHandoverAsync("quality run", CancellationToken.None).WaitAsync(Timeout));
        var waiting = requester.AcquireAsync();
        await Task.Delay(300);
        var result = await requester.RequestHandoverAsync("quality run", CancellationToken.None).WaitAsync(Timeout);

        Assert.True(result.Yielded);
        Assert.Equal(("edc-backup", "quality run"), (asked?.Requester, asked?.Reason));
        Assert.Equal(lease.Generation + 1, (await waiting.WaitAsync(Timeout)).Generation);
    }

    [PortGateFact]
    public async Task The_same_serial_settings_are_applied_behind_the_tunnel_or_refused()
    {
        // The agent's serial port does not exist on this machine: settings are configuration, independent of the device.
        await using var agent = await PortGateProcess.LaunchAsync();
        var configuration = PortGateProcess.Configuration(0) with
        {
            Ports = [new PortGatePort("architect-port", "architect-01", PortGateDevice.Serial("COM250"), [new PortGateController("edc-test", 10)])],
        };
        await agent.ConfigureAsync(configuration);
        var required = new SerialSettings(19200, Parity: SerialParity.Even) { Dtr = false };
        var target = new PortGateTarget("tunnel", new TcpEndpoint("127.0.0.1", agent.Port), "architect-port", "edc-test") { Settings = required };

        // Without administration the client never uses other settings than the ones required.
        var refused = await Assert.ThrowsAsync<PortGateException>(async () => await new PortGateTransportProvider().OpenAsync(target, CancellationToken.None));
        Assert.Equal("PORTGATE-CH-SETTINGS-MISMATCH", refused.Code);

        // With administration they are routed to the agent and applied hot: only that port reopens, the Link endpoint
        // and every other connection stay up.
        await using var bystander = await PortGateChannel.ConnectAsync(new TcpEndpoint("127.0.0.1", agent.Port), new PortGateOptions("edc-test", "architect-port"), CancellationToken.None);
        var provider = new PortGateTransportProvider((_, token) => agent.AdminAsync());
        await using (var stream = await provider.OpenAsync(target, CancellationToken.None))
            Assert.True(stream.CanWrite);
        Assert.Equal("PORTGATE-HANDOVER-NO-OWNER", (await bystander.RequestHandoverAsync(null, CancellationToken.None).WaitAsync(Timeout)).Code);
        await using var check = await PortGateChannel.ConnectAsync(new TcpEndpoint("127.0.0.1", agent.Port), new PortGateOptions("edc-test", "architect-port"), CancellationToken.None);
        Assert.Equal(required, check.Port.Serial);
        Assert.Equal("serial", check.Port.DeviceKind);
        await using var admin = await agent.AdminAsync();
        Assert.Contains(await admin.ReadEventsAsync(), e => e.Kind == "config-applied" && e.Detail!.Contains("without restarting Link", StringComparison.Ordinal));
    }

    [PortGateFact]
    public async Task Ethernet_instrument_that_connects_reaches_the_host_through_the_tunnel()
    {
        int devicePort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            devicePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        await using var agent = await PortGateProcess.LaunchAsync();
        await agent.ConfigureAsync(PortGateProcess.Configuration(0) with
        {
            Ports = [new PortGatePort("uwam", "uwam-01", PortGateDevice.TcpListen(devicePort, "127.0.0.1"), [new PortGateController("edc-test", 10)])],
        });
        await using var transport = await new PortGateTransportProvider().OpenAsync(
            new PortGateTarget("tunnel", new TcpEndpoint("127.0.0.1", agent.Port), "uwam", "edc-test"), CancellationToken.None);
        await Task.Delay(300);

        // The instrument is the TCP client (U-WAM, XN-L, CS-2500 over Ethernet): it connects to the agent, not to the host.
        using var instrument = new TcpClient();
        await instrument.ConnectAsync(IPAddress.Loopback, devicePort).WaitAsync(Timeout);
        await Task.Delay(300);
        await instrument.GetStream().WriteAsync(new byte[] { 0x05 });
        var received = new byte[1];
        Assert.Equal(1, await transport.ReadAsync(received).AsTask().WaitAsync(Timeout));
        Assert.Equal(0x05, received[0]);
        await transport.WriteAsync(new byte[] { 0x06 });
        var answer = new byte[1];
        await instrument.GetStream().ReadExactlyAsync(answer).AsTask().WaitAsync(Timeout);
        Assert.Equal(0x06, answer[0]);
    }

    [PortGateFact]
    public async Task Administration_never_accepts_private_keys_over_plain_text_and_reports_the_devices()
    {
        await using var agent = await PortGateProcess.LaunchAsync();
        await using var admin = await agent.AdminAsync();
        var refused = await Assert.ThrowsAsync<PortGateAdminException>(() => admin.SetTlsAsync(false, null, null, pskIdentity: "edc", psk: new byte[32]));
        Assert.Equal("PORTGATE-INSECURE-CHANNEL", refused.Code);
        var status = await admin.GetStatusAsync();
        Assert.Equal((false, false), (status[5UL], status[6UL]));
        Assert.NotNull(await admin.ListDevicesAsync());

        await using var secure = await agent.AdminAsync(tls: true);
        Assert.Equal("PORTGATE-WEAK-PSK", (await Assert.ThrowsAsync<PortGateAdminException>(() => secure.SetTlsAsync(false, null, null, pskIdentity: "edc", psk: new byte[8]))).Code);
        await secure.SetTlsAsync(false, null, null, pskIdentity: "edc", psk: System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
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

/// <summary>PortGate started as the service runs it (<c>portgate run</c>) on a throw-away state directory and provisioned through its administration endpoint.</summary>
internal sealed class PortGateProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _state;

    private PortGateProcess(Process process, string state, int adminPort, string certificateSha256)
    {
        _process = process;
        _state = state;
        AdminPort = adminPort;
        AgentCertificateSha256 = certificateSha256;
    }

    public int AdminPort { get; }

    public string AgentCertificateSha256 { get; }

    /// <summary>Link port of the configuration in force.</summary>
    public int Port { get; private set; }

    public static string? Executable()
    {
        var configured = Environment.GetEnvironmentVariable("PORTGATE_EXE");
        if (!string.IsNullOrEmpty(configured))
            return File.Exists(configured) ? configured : null;
        var name = OperatingSystem.IsWindows() ? "portgate.exe" : "portgate";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var build = Path.Combine(directory.FullName, "PortGate", "build");
            if (!Directory.Exists(build))
                continue;
            return Directory.EnumerateFiles(build, name, SearchOption.AllDirectories).FirstOrDefault(p => p.Contains("Release", StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    public static PortGateConfiguration Configuration(int instrumentPort, bool tls = false, string? backupCertificateSha256 = null,
        IReadOnlyList<PortGateController>? extraControllers = null, PortGateSecurity? security = null) =>
        new("test-agent", [new PortGatePort("architect-port", "architect-01", PortGateDevice.Tcp("127.0.0.1", instrumentPort),
            [new PortGateController("edc-test", 10), new PortGateController("edc-backup", 20, backupCertificateSha256), .. extraControllers ?? []])
            { Lease = TimeSpan.FromSeconds(6) }])
        {
            PlatformProfile = "test",
            ListenHost = "127.0.0.1",
            Heartbeat = TimeSpan.FromSeconds(2),
            Tls = tls,
            Security = security ?? new PortGateSecurity(),
        };

    /// <summary>Starts an unconfigured agent.</summary>
    public static async Task<PortGateProcess> LaunchAsync(params string[] arguments)
    {
        var state = Path.Combine(Path.GetTempPath(), $"portgate-{Guid.NewGuid():N}");
        var process = Process.Start(new ProcessStartInfo(Executable()!, ["run", "--state", state, "--admin", "127.0.0.1:0", "--name", "test-agent", "--quiet", .. arguments])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var parts = line?.Split(' ');
        if (parts is not ["PORTGATE-ADMIN", _, _])
        {
            process.Kill();
            throw new InvalidOperationException($"Agent did not start: {line}");
        }
        return new PortGateProcess(process, state, int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2]);
    }

    /// <summary>Plain agent with the standard test configuration.</summary>
    public static async Task<PortGateProcess> StartAsync(int instrumentPort)
    {
        var agent = await LaunchAsync();
        await agent.ConfigureAsync(Configuration(instrumentPort));
        return agent;
    }

    public Task<PortGateAdminClient> AdminAsync(bool tls = false, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null, string serverId = "edc-admin") =>
        PortGateAdminClient.ConnectAsync("127.0.0.1", AdminPort, serverId, tls ? AgentCertificateSha256 : null, certificate);

    public async Task<int> ConfigureAsync(PortGateConfiguration configuration, Func<PortGateAdminClient, Task>? before = null, bool tls = false,
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null)
    {
        await using var admin = await AdminAsync(tls, certificate);
        if (before is not null)
            await before(admin);
        Port = await admin.SetConfigAsync(1, configuration);
        return Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
