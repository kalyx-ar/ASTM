using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Tracing;

namespace Atsm.Runtime.Sessions;

/// <summary>The application prepares its protected trace store before invoking this boundary.
/// The factory is not called until readiness succeeds, including TCP/TLS/SerialTunnel handshakes.</summary>
public static class InstrumentCommunicationHost
{
    public static async Task RunAsync(ITraceSink trace, Func<CancellationToken, ValueTask<IInstrumentChannel>> connect,
        Func<IInstrumentChannel, CancellationToken, Task> runSession, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); trace.EnsureReady();
        await using var channel = await connect(token).ConfigureAwait(false);
        trace.EnsureReady();
        await runSession(channel, token).ConfigureAwait(false);
    }
}
