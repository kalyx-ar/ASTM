using System.Collections.Concurrent;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Hl7v2;
using Atsm.Protocol.Mllp;

namespace Atsm.Runtime.Sessions;

public sealed record Hl7SendOutcome(OrderDeliveryState State, string? AcknowledgementCode = null, string? Code = null);

/// <summary>Original-mode HL7 over MLLP. Message acceptance never proves assay acceptance or
/// execution. Uncertain sends are reported to the host and are never retried automatically.</summary>
public sealed class Hl7InstrumentSession(IInstrumentChannel channel, IHl7Dialect dialect, Hl7Profile profile,
    ILabWorkflowHost host, InstrumentRef instrument, ITraceSink trace, string controllerId)
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private ControlLease? _lease;
    private int _started;
    public Action<AtsmDiagnostic>? Diagnostics { get; init; }
    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    public async Task RunAsync(CancellationToken token)
    {
        trace.EnsureReady();
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("HL7 session already started.");
        var lease = await channel.AcquireAsync(new(controllerId), token).ConfigureAwait(false);
        try
        {
            var admission = await channel.ConfirmTransferAsync(lease, lease.TransferId, token).ConfigureAwait(false);
            if (admission.State != TransferAdmissionState.Open) throw new InvalidOperationException("ATSM-CH-TRANSFER-REJECTED");
            _lease = lease;
            var processor = new Hl7MessageProcessor(dialect, profile, host, instrument, trace, Diagnostics) { MaxMessageBytes = MaxMessageBytes };
            var decoder = new MllpDecoder(MaxMessageBytes);
            var events = new List<MllpEvent>();
            var buffer = new byte[4096];
            long first = -1;
            while (true)
            {
                var read = await channel.ReadAsync(buffer, token).ConfigureAwait(false);
                long last = -1;
                if (read.Count > 0)
                {
                    last = trace.Write(TraceDirection.Received, buffer.AsSpan(0, read.Count));
                    if (first < 0) first = last;
                    events.Clear(); decoder.Feed(buffer.AsSpan(0, read.Count), events);
                    foreach (var item in events)
                    {
                        if (item.Kind != MllpEventKind.Message) { Report("ATSM-MLLP-" + item.Kind.ToString().ToUpperInvariant()); continue; }
                        var payload = item.Payload!;
                        Hl7Message? message;
                        try { message = Hl7Codec.Parse(profile.Encoding.GetString(payload)).Message; }
                        catch (System.Text.DecoderFallbackException) { Report("ATSM-HL7-INVALID-ENCODING"); continue; }
                        if (message is not null && message.Type is "ACK" or "ORL" or "RSP")
                        {
                            var ack = Hl7Acknowledgements.Read(message);
                            if (ack is not null && ack.Phase == Hl7AcknowledgementPhase.Application
                                && message.Header.Text(12) == profile.Hl7Version && message.Header.Text(9, 3) == "ORL_O34"
                                && _pending.TryGetValue(ack.ControlId, out var waiting) && message.Type == waiting.Type && message.Trigger == waiting.Trigger)
                                waiting.Completion.TrySetResult(new(ack.Accepted ? OrderDeliveryState.Sent : OrderDeliveryState.Rejected, ack.Code));
                            else Report("ATSM-HL7-UNMATCHED-ACK");
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
                if (read.EndOfStream)
                {
                    if (decoder.HasIncompleteBlock) throw new InvalidDataException("ATSM-MLLP-INCOMPLETE-BLOCK");
                    return;
                }
            }
        }
        finally
        {
            _lease = null;
            foreach (var waiting in _pending.Values) waiting.Completion.TrySetResult(new(OrderDeliveryState.Uncertain, Code: "ATSM-HL7-CONNECTION-CLOSED"));
            await channel.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<Hl7SendOutcome> SendOrdersAsync(IReadOnlyList<WorkOrder> orders, CancellationToken token = default)
    {
        var message = dialect.BuildOrders(orders, Guid.CreateVersion7().ToString());
        var waiting = new Pending("ORL", "O34", new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_lease is null) throw new InvalidOperationException("ATSM-HL7-SESSION-NOT-RUNNING");
        if (!_pending.TryAdd(message.ControlId, waiting)) throw new InvalidOperationException("ATSM-HL7-DUPLICATE-CONTROL-ID");
        try
        {
            try { await WriteAsync(message, token).ConfigureAwait(false); }
            catch (ChannelWriteException) { return new(OrderDeliveryState.Uncertain, Code: SessionCodes.WriteNotCompleted); }
            try { return await waiting.Completion.Task.WaitAsync(profile.ResponseTimeout, token).ConfigureAwait(false); }
            catch (TimeoutException) { return new(OrderDeliveryState.Uncertain, Code: "ATSM-HL7-RESPONSE-TIMEOUT"); }
        }
        finally { _pending.TryRemove(message.ControlId, out _); }
    }

    private async ValueTask WriteAsync(Hl7Message message, CancellationToken token)
    {
        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var lease = _lease ?? throw new IOException("ATSM-HL7-CONNECTION-CLOSED");
            var bytes = MllpFraming.Wrap(profile.Encoding.GetBytes(message.Serialize()));
            trace.Write(TraceDirection.Transmitted, bytes);
            WriteOutcome result;
            try { result = await channel.WriteAsync(bytes, lease, token).ConfigureAwait(false); }
            catch (IOException error) { throw new ChannelWriteException(error); }
            if (result.Progress != WriteProgress.Completed) throw new ChannelWriteException();
        }
        finally { _send.Release(); }
    }

    private void Report(string code)
    {
        trace.Write(TraceDirection.Event, [], code);
        Diagnostics?.Invoke(new(code, AtsmSeverity.Warning, "HL7 transport diagnostic; inspect the protected trace."));
    }
    private sealed record Pending(string Type, string Trigger, TaskCompletionSource<Hl7SendOutcome> Completion);
    private sealed class ChannelWriteException(Exception? cause = null) : IOException(SessionCodes.WriteNotCompleted, cause);
}
