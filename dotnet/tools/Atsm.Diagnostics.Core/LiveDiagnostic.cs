using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Runtime.Sessions;
using Atsm.Tracing;
using Atsm.Protocol.Lis01;

namespace Atsm.Diagnostics;

public sealed record DiagnosticCommandConnection(IInstrumentCommandPlugin Plugin, string ProfileId,
    Func<InstrumentCommandRequest, CancellationToken, Task<Lis01SendCompleted>> SendAsync)
{
    public IReadOnlyList<InstrumentCommandDescriptor> Commands => Plugin.Commands(ProfileId);
}

public static class LiveDiagnostic
{
    /// <param name="connect">Opens the final transport (TCP, TLS, local COM or a port behind PortGate, already held).</param>
    public static Task RunAsync(DiagnosticProfile profile, DiagnosticTiming timing, DiagnosticTraceSink trace,
        Func<CancellationToken, ValueTask<Stream>> connect, bool simulateHost, CancellationToken token,
        Action<DiagnosticCommandConnection?>? commands = null)
    {
        if (!profile.CanConnect) throw new NotSupportedException("ATSM-DIAG-FILE-PROFILE");
        if (simulateHost && !profile.CanSimulateHost) throw new NotSupportedException("ATSM-DIAG-PASSIVE-PROFILE");
        return InstrumentCommunicationHost.RunAsync(trace, connect, async (transport, stop) =>
        {
            if (!simulateHost)
            {
                // This is a direct capture connection, not a sniffer of another controller's session.
                // A port behind PortGate is held exclusively even when no protocol bytes are transmitted.
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await transport.ReadAsync(buffer, stop);
                    if (count == 0) return;
                    trace.Write(TraceDirection.Received, buffer.AsSpan(0, count));
                }
            }
            var host = new DiagnosticWorkflowHost(trace);
            if (profile.IsHl7)
            {
                var (plugin, selected) = profile.Hl7(timing);
                await new Hl7InstrumentSession(transport, plugin.CreateDialect(selected, new(new("diagnostic"), TimeProvider.System)), selected,
                    host, new("diagnostic"), trace).RunAsync(stop);
            }
            else
            {
                var session = new AstmInstrumentSession(transport, profile.AstmPlugin!.CreateDialect(profile.AstmProfile!, new(new("diagnostic"), TimeProvider.System)), profile.AstmProfile!,
                    host, new("diagnostic"), trace, new());
                try
                {
                    if (profile.AstmPlugin is IInstrumentCommandPlugin plugin)
                        commands?.Invoke(new(plugin, profile.Id, (request, cancellation) => session.SendCommandAsync(plugin, request, cancellation)));
                    await session.RunAsync(stop);
                }
                finally { commands?.Invoke(null); }
            }
        }, token);
    }
}
