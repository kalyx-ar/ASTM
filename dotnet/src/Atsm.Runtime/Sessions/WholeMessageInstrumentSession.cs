using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;

namespace Atsm.Runtime.Sessions;

/// <summary>Consumes complete messages supplied by the host (exports, queues, files it owns). No LIS01 handshakes,
/// directory watchers, file moves or disk buffers. When the medium can answer, the host passes <c>respond</c>;
/// otherwise query answers are traced and not transmitted. A transport write is not a business acknowledgement.</summary>
public sealed class WholeMessageInstrumentSession(AstmMessageProcessor processor, AstmProfile profile, ITraceSink trace)
{
    public Action<AstmProcessingResult>? Processed { get; init; }

    public async Task RunAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> messages, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? respond,
        CancellationToken token)
    {
        trace.EnsureReady();
        await foreach (var message in messages.WithCancellation(token).ConfigureAwait(false))
        {
            if (message.IsEmpty) continue;
            if (message.Length > processor.MaxMessageBytes) throw new InvalidDataException("ATSM-MESSAGE-TOO-LARGE");
            var sequence = trace.Write(TraceDirection.Received, message.Span);
            var result = await processor.ProcessRecordedAsync(message.ToArray(), new(trace.TraceId, sequence, sequence), Guid.CreateVersion7(), token).ConfigureAwait(false);
            foreach (var response in result.Responses)
            {
                if (respond is null)
                {
                    trace.Write(TraceDirection.Event, [], SessionCodes.TransmissionSuppressed, $"answer of {response.Count} records not transmitted: receive-only medium");
                    continue;
                }
                var payload = profile.Encoding.GetBytes(Lis02Codec.Serialize(response));
                trace.Write(TraceDirection.Transmitted, payload);
                await respond(payload, token).ConfigureAwait(false);
            }
            Processed?.Invoke(result);
        }
    }
}
