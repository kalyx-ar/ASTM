using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Runtime.Sessions;
using Atsm.Tracing;

namespace Atsm.Diagnostics;

public static class LiveDiagnostic
{
    public static Task RunAsync(DiagnosticProfile profile, DiagnosticTiming timing, DiagnosticTraceSink trace,
        Func<CancellationToken, ValueTask<IInstrumentChannel>> connect, bool simulateHost, string controllerId, CancellationToken token)
    {
        if (!profile.CanConnect) throw new NotSupportedException("ATSM-DIAG-FILE-PROFILE");
        if (simulateHost && !profile.CanSimulateHost) throw new NotSupportedException("ATSM-DIAG-PASSIVE-PROFILE");
        return InstrumentCommunicationHost.RunAsync(trace, connect, async (channel, stop) =>
        {
            if (!simulateHost)
            {
                // This is a direct capture connection, not a sniffer of another controller's session.
                // SerialTunnel acquisition is exclusive even when no protocol bytes are transmitted.
                var lease = await channel.AcquireAsync(new(controllerId), stop);
                try
                {
                    var admitted = await channel.ConfirmTransferAsync(lease, lease.TransferId, stop);
                    if (admitted.State != TransferAdmissionState.Open) throw new IOException("ATSM-DIAG-TRANSFER-REJECTED");
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var read = await channel.ReadAsync(buffer, stop);
                        if (read.Count > 0) trace.Write(TraceDirection.Received, buffer.AsSpan(0, read.Count));
                        if (read.EndOfStream) return;
                    }
                }
                finally { await channel.ReleaseAsync(lease, CancellationToken.None); }
            }
            else
            {
                var host = new DiagnosticWorkflowHost(trace);
                if (profile.IsHl7)
                {
                    var (plugin, selected) = profile.Hl7(timing);
                    await new Hl7InstrumentSession(channel, plugin.CreateDialect(selected, new(new("diagnostic"), TimeProvider.System)), selected,
                        host, new("diagnostic"), trace, controllerId).RunAsync(stop);
                }
                else await new AstmInstrumentSession(channel, profile.AstmPlugin!.CreateDialect(profile.AstmProfile!, new(new("diagnostic"), TimeProvider.System)), profile.AstmProfile!,
                    host, new("diagnostic"), trace, new(controllerId)).RunAsync(stop);
            }
        }, token);
    }
}
