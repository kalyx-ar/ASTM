using System.Collections.Concurrent;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Hosting;
using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;
using Astm.Plugins;
using Astm.Protocol.Hl7v2;
using Astm.Protocol.Mllp;

namespace Astm.Runtime.Sessions;

public sealed record Hl7SendOutcome(OrderDeliveryState State, string? AcknowledgementCode = null, string? Code = null);

/// <summary>Original-mode HL7 over MLLP. Message acceptance never proves assay acceptance or
/// execution. Uncertain sends are reported to the host and are never retried automatically.
/// The host supplies the open transport; its end or failure ends the session.</summary>
public sealed class Hl7InstrumentSession(Stream transport, IHl7Dialect dialect, Hl7Profile profile,
    ILabWorkflowHost host, InstrumentRef instrument, ITraceSink trace)
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private volatile bool _running;
    private int _started;
    public Action<AstmDiagnostic>? Diagnostics { get; init; }
    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    public async Task RunAsync(CancellationToken token)
    {
        trace.EnsureReady();
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("HL7 session already started.");
        try
        {
            _running = true;
            var processor = new Hl7MessageProcessor(dialect, profile, host, instrument, trace, Diagnostics) { MaxMessageBytes = MaxMessageBytes };
            var decoder = new MllpDecoder(MaxMessageBytes);
            var events = new List<MllpEvent>();
            var buffer = new byte[4096];
            long first = -1;
            while (true)
            {
                var count = await transport.ReadAsync(buffer, token).ConfigureAwait(false);
                long last = -1;
                if (count > 0)
                {
                    last = trace.Write(TraceDirection.Received, buffer.AsSpan(0, count));
                    if (first < 0) first = last;
                    events.Clear(); decoder.Feed(buffer.AsSpan(0, count), events);
                    foreach (var item in events)
                    {
                        if (item.Kind != MllpEventKind.Message) { Report("ASTM-MLLP-" + item.Kind.ToString().ToUpperInvariant()); continue; }
                        var payload = item.Payload!;
                        Hl7Message? message;
                        try { message = Hl7Codec.Parse(profile.Encoding.GetString(payload)).Message; }
                        catch (System.Text.DecoderFallbackException) { Report("ASTM-HL7-INVALID-ENCODING"); continue; }
                        if (message is not null && message.Type is "ACK" or "ORL" or "RSP")
                        {
                            var ack = Hl7Acknowledgements.Read(message);
                            if (ack is not null && ack.Phase == Hl7AcknowledgementPhase.Application
                                && message.Header.Text(12) == profile.Hl7Version && message.Header.Text(9, 3) == "ORL_O34"
                                && _pending.TryGetValue(ack.ControlId, out var waiting) && message.Type == waiting.Type && message.Trigger == waiting.Trigger)
                                waiting.Completion.TrySetResult(new(ack.Accepted ? OrderDeliveryState.Sent : OrderDeliveryState.Rejected, ack.Code));
                            else Report("ASTM-HL7-UNMATCHED-ACK");
                            continue;
                        }
                        var receptionId = message is null ? Guid.CreateVersion7() : ReceptionIdentity.Derive(
                            new Guid("35362182-a3be-420e-8403-cae3bcf27ac9"),
                            string.Join('\n', profile.Id, instrument.InstrumentId, message.Header.Field(3), message.Header.Field(4), message.ControlId), 0);
                        var result = await processor.ProcessRecordedAsync(payload, new(trace.TraceId, first, last), receptionId, token).ConfigureAwait(false);
                        foreach (var response in result.Responses) await WriteAsync(response, token).ConfigureAwait(false);
                    }
                    // If a following block is split across reads, retain the earliest supporting entry.
                    if (!decoder.HasIncompleteBlock) first = -1;
                }
                if (count == 0)
                {
                    if (decoder.HasIncompleteBlock) throw new InvalidDataException("ASTM-MLLP-INCOMPLETE-BLOCK");
                    return;
                }
            }
        }
        finally
        {
            _running = false;
            foreach (var waiting in _pending.Values) waiting.Completion.TrySetResult(new(OrderDeliveryState.Uncertain, Code: "ASTM-HL7-CONNECTION-CLOSED"));
        }
    }

    public async Task<Hl7SendOutcome> SendOrdersAsync(IReadOnlyList<WorkOrder> orders, CancellationToken token = default)
    {
        var message = dialect.BuildOrders(orders, Guid.CreateVersion7().ToString());
        var waiting = new Pending("ORL", "O34", new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_running) throw new InvalidOperationException("ASTM-HL7-SESSION-NOT-RUNNING");
        if (!_pending.TryAdd(message.ControlId, waiting)) throw new InvalidOperationException("ASTM-HL7-DUPLICATE-CONTROL-ID");
        try
        {
            try { await WriteAsync(message, token).ConfigureAwait(false); }
            catch (ChannelWriteException) { return new(OrderDeliveryState.Uncertain, Code: SessionCodes.WriteNotCompleted); }
            try { return await waiting.Completion.Task.WaitAsync(profile.ResponseTimeout, token).ConfigureAwait(false); }
            catch (TimeoutException) { return new(OrderDeliveryState.Uncertain, Code: "ASTM-HL7-RESPONSE-TIMEOUT"); }
        }
        finally { _pending.TryRemove(message.ControlId, out _); }
    }

    private async ValueTask WriteAsync(Hl7Message message, CancellationToken token)
    {
        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var bytes = MllpFraming.Wrap(profile.Encoding.GetBytes(message.Serialize()));
            trace.Write(TraceDirection.Transmitted, bytes);
            if (!_running) throw new ChannelWriteException(new IOException("ASTM-HL7-CONNECTION-CLOSED"));
            try
            {
                await transport.WriteAsync(bytes, token).ConfigureAwait(false);
                await transport.FlushAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or NotSupportedException) { throw new ChannelWriteException(error); }
        }
        finally { _send.Release(); }
    }

    private void Report(string code)
    {
        trace.Write(TraceDirection.Event, [], code);
        Diagnostics?.Invoke(new(code, AstmSeverity.Warning, "HL7 transport diagnostic; inspect the protected trace."));
    }
    private sealed record Pending(string Type, string Trigger, TaskCompletionSource<Hl7SendOutcome> Completion);
    private sealed class ChannelWriteException(Exception? cause = null) : IOException(SessionCodes.WriteNotCompleted, cause);
}
