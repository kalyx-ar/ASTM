using Astm.Abstractions.Model;
using Astm.Diagnostics;
using Astm.Plugin.Abbott.Architect;
using Astm.Plugin.Sysmex.Urinalysis;
using Astm.Plugins;
using Astm.Protocol.Lis01;
using Astm.Runtime.Sessions;
using Astm.Runtime.Tracing;
using Astm.Testing;

namespace Astm.Tests.Diagnostics;

public sealed class InstrumentCommandTests
{
    private static InstrumentCommandRequest Request(string id = AstmOrderCommands.SendOrder) => new(id, new Dictionary<string, string> { ["sample"] = "SYN-001", ["tests"] = "GLU,UREA" });
    [Fact]
    public void Each_plugin_adopts_its_own_action_catalog_and_builds_with_its_dialect()
    {
        foreach (var plugin in new IInstrumentPlugin[] { new StandardAstmPlugin(), new ArchitectPlugin(), new SysmexUrinalysisPlugin() })
        {
            var commands = (IInstrumentCommandPlugin)plugin;
            foreach (var profile in plugin.Profiles)
            {
                var action = Assert.Single(commands.Commands(profile.Id)); Assert.Equal(InstrumentCommandEffect.Order, action.Effect);
                var records = commands.BuildAstmCommand(profile.Id, Request(), new(new("SYN"), TimeProvider.System));
                Assert.Equal('H', records[0].Type); Assert.Equal('L', records[^1].Type);
                Assert.Contains(records, r => r.Type == 'O' && r.Raw.Contains("SYN-001", StringComparison.Ordinal));
                Assert.Throws<NotSupportedException>(() => commands.BuildAstmCommand(profile.Id, Request("invented.status"), new(new("SYN"), TimeProvider.System)));
            }
        }
        Assert.Empty(new SysmexUrinalysisPlugin().Commands(SysmexUrinalysisPlugin.UfBinaryProfile));
    }
    [Fact]
    public async Task Plugin_command_uses_the_existing_link_and_waits_for_ACK_without_another_reader_or_raw_write()
    {
        var plugin = new StandardAstmPlugin(); var profile = plugin.Profiles[0]; var (host, peer) = DuplexStream.CreatePair(); await using var owned = host;
        var session = new AstmInstrumentSession(host, plugin.CreateDialect(profile, new(new("SYN"), TimeProvider.System)), profile,
            new InMemoryLabHost(), new("SYN"), new MemoryTraceSink(), new());
        await using var instrument = new InstrumentSimulator(peer);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var running = session.RunAsync(stop.Token);
        try
        {
            var receipt = await session.SendCommandAsync(plugin, Request(), stop.Token);
            Assert.Equal(Lis01SendResult.Delivered, receipt.Result);
            Assert.Contains("SYN-001", await instrument.Received.ReadAsync(stop.Token));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendCommandAsync(plugin, Request(), cancelled.Token));
        }
        finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public void Connection_targets_preserve_local_and_remote_serial_settings()
    {
        var settings = new Astm.Abstractions.Transport.SerialSettings(19200, 7, Astm.Abstractions.Transport.SerialParity.Even);
        var local = new DiagnosticConnection(DiagnosticTransport.LocalSerial, "", 0) { SerialPort = "COM210", Serial = settings };
        Assert.Equal(settings, Assert.IsType<Astm.Transport.SerialTarget>(local.Target()).Settings);
        var remote = local with { Kind = DiagnosticTransport.PortGate, Host = "127.0.0.1", Port = 28240, AgentPort = "p1", Controller = "test", ConfigureSerial = true };
        Assert.Equal(settings, Assert.IsType<global::PortGate.Client.PortGateTarget>(remote.Target()).Settings);
        Assert.Null(Assert.IsType<global::PortGate.Client.PortGateTarget>((remote with { ConfigureSerial = false }).Target()).Settings);
    }
}
