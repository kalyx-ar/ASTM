using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;

namespace Atsm.Runtime.Sessions;

/// <summary>Consumes message boundaries supplied by the host. No LIS01 handshakes, directory
/// watchers, file moves or disk buffers. A channel write is not a business acknowledgement.</summary>
public sealed class WholeMessageInstrumentSession(IInstrumentChannel channel, AstmMessageProcessor processor,
    AstmProfile profile, ITraceSink trace, string controllerId)
{
    public Action<AstmProcessingResult>? Processed { get; init; }

    public async Task RunAsync(CancellationToken token)
    {
        trace.EnsureReady();
        if (!channel.Capabilities.MessageBoundaries) throw new InvalidOperationException("ATSM-CH-MESSAGE-BOUNDARIES-REQUIRED");
        var lease = await channel.AcquireAsync(new(controllerId), token).ConfigureAwait(false);
        try
        {
            var admission = await channel.ConfirmTransferAsync(lease, lease.TransferId, token).ConfigureAwait(false);
            if (admission.State != TransferAdmissionState.Open) throw new InvalidOperationException("ATSM-CH-TRANSFER-REJECTED");
            using var message = new MemoryStream();
            var buffer = new byte[4096];
            long first = -1, last = -1;
            while (true)
            {
                var read = await channel.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read.Count > 0)
                {
                    if (message.Length + read.Count > processor.MaxMessageBytes) throw new InvalidDataException("ATSM-MESSAGE-TOO-LARGE");
                    last = trace.Write(TraceDirection.Received, buffer.AsSpan(0, read.Count));
                    if (first < 0) first = last;
                    message.Write(buffer, 0, read.Count);
                }
                if (read.IsMessageBoundary && message.Length > 0)
                {
                    var result = await processor.ProcessRecordedAsync(message.ToArray(), new(trace.TraceId, first, last), Guid.CreateVersion7(), token).ConfigureAwait(false);
                    foreach (var response in result.Responses)
                    {
                        var payload = profile.Encoding.GetBytes(Lis02Codec.Serialize(response));
                        trace.Write(TraceDirection.Transmitted, payload);
                        var written = await channel.WriteAsync(payload, lease, token).ConfigureAwait(false);
                        if (written.Progress != WriteProgress.Completed) throw new IOException(SessionCodes.WriteNotCompleted);
                    }
                    Processed?.Invoke(result);
                    message.SetLength(0); first = last = -1;
                }
                if (read.EndOfStream)
                {
                    if (message.Length > 0) throw new InvalidDataException(SessionCodes.IncompleteTransmission);
                    return;
                }
            }
        }
        finally { await channel.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
    }
}
