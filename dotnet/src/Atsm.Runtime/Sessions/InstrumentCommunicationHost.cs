using Atsm.Abstractions.Tracing;

namespace Atsm.Runtime.Sessions;

/// <summary>The application prepares its protected trace store before invoking this boundary. The transport factory
/// (COM, TCP/TLS, PortGate...) is not called until readiness succeeds; the transport is disposed when the session ends.</summary>
public static class InstrumentCommunicationHost
{
    public static async Task RunAsync(ITraceSink trace, Func<CancellationToken, ValueTask<Stream>> connect,
        Func<Stream, CancellationToken, Task> runSession, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); trace.EnsureReady();
        await using var transport = await connect(token).ConfigureAwait(false);
        trace.EnsureReady();
        await runSession(transport, token).ConfigureAwait(false);
    }
}
