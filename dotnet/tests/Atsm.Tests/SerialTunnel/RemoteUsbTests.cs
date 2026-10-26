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

/// <summary>Runs only when RemoteUSB is built (RemoteUSB/build/*/Release/remoteusb[.exe]) or REMOTEUSB_EXE points to it.</summary>
public sealed class RemoteUsbFactAttribute : FactAttribute
{
    public RemoteUsbFactAttribute()
    {
        if (RemoteUsbProcess.Executable() is null)
            Skip = "RemoteUSB not built: run CMake in RemoteUSB/ or set REMOTEUSB_EXE.";
    }
}

public class RemoteUsbTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [RemoteUsbFact]
    public async Task Instrument_results_and_host_query_cross_RemoteUSB()
    {
        // The "serial" device of the agent is a TCP connection to this listener, where the simulator plays the instrument.
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await RemoteUsbProcess.StartAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port);
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

    [RemoteUsbFact]
    public async Task Second_controller_is_queued_while_the_first_owns_the_port()
    {
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await RemoteUsbProcess.StartAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port);
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

    [RemoteUsbFact]
    public async Task Owner_yields_the_port_to_a_cooperative_handover_request()
    {
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await RemoteUsbProcess.StartAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port);
        using var instrumentSocket = await instrumentListener.AcceptTcpClientAsync().WaitAsync(Timeout);

        RemoteUsbHandoverRequest? asked = null;
        await using var owner = await SerialTunnelChannel.ConnectAsync("owner", new TcpEndpointOptions("127.0.0.1", agent.Port),
            new SerialTunnelOptions("edc-test", "architect-port") { HandoverDecision = request => { asked = request; return true; } }, CancellationToken.None);
        await using var requester = await SerialTunnelChannel.ConnectAsync("requester", new TcpEndpointOptions("127.0.0.1", agent.Port), new SerialTunnelOptions("edc-backup", "architect-port"), CancellationToken.None);
        var lease = await owner.AcquireAsync(new("edc-test"), CancellationToken.None);

        // Only a queued controller may ask.
        Assert.Equal(new RemoteUsbHandoverResult(false, "REMOTEUSB-HANDOVER-NOT-QUEUED"), await requester.RequestHandoverAsync("quality run", CancellationToken.None).WaitAsync(Timeout));
        var waiting = requester.AcquireAsync(new("edc-backup"), CancellationToken.None).AsTask();
        await Task.Delay(300);
        var result = await requester.RequestHandoverAsync("quality run", CancellationToken.None).WaitAsync(Timeout);

        Assert.True(result.Yielded);
        Assert.Equal(("edc-backup", "quality run"), (asked?.Requester, asked?.Reason));
        Assert.Equal(lease.Generation + 1, (await waiting.WaitAsync(Timeout)).Generation);
    }

    [RemoteUsbFact]
    public async Task Administration_never_accepts_private_keys_over_plain_text_and_reports_the_devices()
    {
        await using var agent = await RemoteUsbProcess.LaunchAsync();
        await using var admin = await agent.AdminAsync();
        var refused = await Assert.ThrowsAsync<RemoteUsbAdminException>(() => admin.SetTlsAsync(false, null, null, pskIdentity: "edc", psk: new byte[32]));
        Assert.Equal("REMOTEUSB-INSECURE-CHANNEL", refused.Code);
        var status = await admin.GetStatusAsync();
        Assert.Equal((false, false), (status[5UL], status[6UL]));
        Assert.NotNull(await admin.ListDevicesAsync());

        await using var secure = await agent.AdminAsync(tls: true);
        Assert.Equal("REMOTEUSB-WEAK-PSK", (await Assert.ThrowsAsync<RemoteUsbAdminException>(() => secure.SetTlsAsync(false, null, null, pskIdentity: "edc", psk: new byte[8]))).Code);
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

/// <summary>RemoteUSB started as the service runs it (<c>remoteusb run</c>) on a throw-away state directory and provisioned through its administration endpoint.</summary>
internal sealed class RemoteUsbProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _state;

    private RemoteUsbProcess(Process process, string state, int adminPort, string certificateSha256)
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
        var configured = Environment.GetEnvironmentVariable("REMOTEUSB_EXE");
        if (!string.IsNullOrEmpty(configured))
            return File.Exists(configured) ? configured : null;
        var name = OperatingSystem.IsWindows() ? "remoteusb.exe" : "remoteusb";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var build = Path.Combine(directory.FullName, "RemoteUSB", "build");
            if (!Directory.Exists(build))
                continue;
            return Directory.EnumerateFiles(build, name, SearchOption.AllDirectories).FirstOrDefault(p => p.Contains("Release", StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    public static RemoteUsbConfiguration Configuration(int instrumentPort, bool tls = false, string? backupCertificateSha256 = null,
        IReadOnlyList<RemoteUsbController>? extraControllers = null, RemoteUsbSecurity? security = null) =>
        new("test-agent", [new RemoteUsbPort("architect-port", "architect-01", RemoteUsbDevice.Tcp("127.0.0.1", instrumentPort),
            [new RemoteUsbController("edc-test", 10), new RemoteUsbController("edc-backup", 20, backupCertificateSha256), .. extraControllers ?? []])
            { Lease = TimeSpan.FromSeconds(6) }])
        {
            PlatformProfile = "test",
            ListenHost = "127.0.0.1",
            Heartbeat = TimeSpan.FromSeconds(2),
            Tls = tls,
            Security = security ?? new RemoteUsbSecurity(),
        };

    /// <summary>Starts an unconfigured agent.</summary>
    public static async Task<RemoteUsbProcess> LaunchAsync(params string[] arguments)
    {
        var state = Path.Combine(Path.GetTempPath(), $"remoteusb-{Guid.NewGuid():N}");
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
        if (parts is not ["REMOTEUSB-ADMIN", _, _])
        {
            process.Kill();
            throw new InvalidOperationException($"Agent did not start: {line}");
        }
        return new RemoteUsbProcess(process, state, int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2]);
    }

    /// <summary>Plain agent with the standard test configuration.</summary>
    public static async Task<RemoteUsbProcess> StartAsync(int instrumentPort)
    {
        var agent = await LaunchAsync();
        await agent.ConfigureAsync(Configuration(instrumentPort));
        return agent;
    }

    public Task<RemoteUsbAdminClient> AdminAsync(bool tls = false, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null, string serverId = "edc-admin") =>
        RemoteUsbAdminClient.ConnectAsync("127.0.0.1", AdminPort, serverId, tls ? AgentCertificateSha256 : null, certificate);

    public async Task<int> ConfigureAsync(RemoteUsbConfiguration configuration, Func<RemoteUsbAdminClient, Task>? before = null, bool tls = false,
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
