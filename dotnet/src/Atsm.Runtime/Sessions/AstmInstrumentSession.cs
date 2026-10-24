using System.Threading.Channels;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;

namespace Atsm.Runtime.Sessions;

public sealed record AstmSessionOptions(string ControllerId, Lis01Role Role = Lis01Role.Host)
{
    public int ReadBufferBytes { get; init; } = 4096;

    /// <summary>
    /// False for diagnostic replay: a recorded instrument cannot react, so host messages (query answers, orders) are
    /// built and written to the trace as events but never transmitted. Low-level ACK/NAK replies are still produced.
    /// </summary>
    public bool TransmitHostMessages { get; init; } = true;

    /// <summary>Receives every technical and interpretation finding of the session (also written to the trace).</summary>
    public Action<AtsmDiagnostic>? Diagnostics { get; init; }
}

public static class SessionCodes
{
    public const string IncompleteTransmission = "ATSM-RT-INCOMPLETE-TRANSMISSION";
    public const string HostIngestFailed = "ATSM-RT-HOST-INGEST-FAILED";
    public const string HostIngestNotAccepted = "ATSM-RT-HOST-INGEST-NOT-ACCEPTED";
    public const string HostQueryFailed = "ATSM-RT-HOST-QUERY-FAILED";
    public const string HostQueryTimeout = "ATSM-RT-HOST-QUERY-TIMEOUT";
    public const string SendNotDelivered = "ATSM-RT-SEND-NOT-DELIVERED";
    public const string WriteNotCompleted = "ATSM-RT-WRITE-NOT-COMPLETED";
    public const string DialectFailed = "ATSM-RT-DIALECT-FAILED";
    public const string TransmissionSuppressed = "ATSM-RT-TRANSMISSION-SUPPRESSED";
}

/// <summary>
/// Drives one instrument over ASTM: channel bytes -> LIS01-A2 link -> LIS02-A2 codec -> dialect -> host.
/// The link runs on a single loop so frame acknowledgements never wait for the host; interpretation and
/// host calls run on a second, ordered loop.
/// </summary>
public sealed class AstmInstrumentSession
{
    private readonly IInstrumentChannel _channel;
    private readonly IAstmDialect _dialect;
    private readonly AstmProfile _profile;
    private readonly ITraceSink _trace;
    private readonly AstmSessionOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lis01Link _link;
    private readonly AstmMessageProcessor _processor;
    private readonly Channel<Input> _inputs = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Reception> _receptions = Channel.CreateUnbounded<Reception>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<long, TaskCompletionSource<Lis01SendCompleted>> _pendingSends = [];
    private InstrumentRef _instrument;
    private long? _receptionStart;
    private long _lastReceivedSequence = -1;
    private volatile bool _channelEnded;

    public AstmInstrumentSession(
        IInstrumentChannel channel,
        IAstmDialect dialect,
        AstmProfile profile,
        ILabWorkflowHost host,
        InstrumentRef instrument,
        ITraceSink trace,
        AstmSessionOptions options,
        TimeProvider? clock = null)
    {
        _channel = channel;
        _dialect = dialect;
        _profile = profile;
        _instrument = instrument;
        _trace = trace;
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _link = new Lis01Link(options.Role, profile.Link);
        _processor = new(dialect, profile, host, instrument, trace, options.Diagnostics);
    }

    public InstrumentRef Instrument => _instrument;

    public Lis01State LinkState => _link.State;

    /// <summary>Runs until the channel ends or <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _trace.EnsureReady();
        var lease = await _channel.AcquireAsync(new ControlRequest(_options.ControllerId), cancellationToken).ConfigureAwait(false);
        // A new session starts a fresh conversation: the low-level link begins neutral, so the technical
        // transfer state is accepted as is. Operation-level recovery is decided by the dialect and the host.
        try
        {
            var admission = await _channel.ConfirmTransferAsync(lease, lease.TransferId, cancellationToken).ConfigureAwait(false);
            if (admission.State != TransferAdmissionState.Open)
                throw new InvalidOperationException($"Channel refused the transfer: {admission.Code}");
        }
        catch
        {
            await _channel.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var start = _clock.GetTimestamp();
        using var timer = _clock.CreateTimer(_ => _inputs.Writer.TryWrite(Input.Tick), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var reader = Task.Run(() => ReadLoopAsync(stop.Token), CancellationToken.None);
        var business = Task.Run(() => BusinessLoopAsync(stop.Token), CancellationToken.None);
        try
        {
            var link = LinkLoopAsync(lease, start, timer, stop.Token);
            var first = await Task.WhenAny(link, reader, business).ConfigureAwait(false);
            if (first.IsFaulted) await first.ConfigureAwait(false);
            await link.ConfigureAwait(false);
            // EOF closes the protocol loop, then drains already assembled receptions. Cancelling
            // the business loop first could lose the final message of a replay or a finite source.
            _receptions.Writer.TryComplete();
            await business.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            _receptions.Writer.TryComplete();
            try
            {
                try { await Task.WhenAll(reader, business).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            finally
            {
                foreach (var pending in _pendingSends.Values) pending.TrySetCanceled(CancellationToken.None);
                await _channel.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Downloads orders without a prior query and waits for the low-level outcome.</summary>
    public Task<Lis01SendCompleted> SendOrdersAsync(IReadOnlyList<WorkOrder> orders, CancellationToken cancellationToken) =>
        _options.TransmitHostMessages
            ? SendRecordsAsync(_dialect.BuildOrders(orders), cancellationToken)
            : throw new InvalidOperationException("This session does not transmit host messages.");

    private Task<Lis01SendCompleted> SendRecordsAsync(IReadOnlyList<Lis02Record> records, CancellationToken cancellationToken)
    {
        if (_channelEnded) return Task.FromResult(new Lis01SendCompleted(0, Lis01SendResult.Failed, 0, 0, "ATSM-CH-CLOSED"));
        var completion = new TaskCompletionSource<Lis01SendCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bytes = _profile.Frame(records, _profile.HostFraming);
        if (!_inputs.Writer.TryWrite(new Input(InputKind.Send, null, -1, bytes, completion)))
            completion.TrySetCanceled(cancellationToken);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private async Task LinkLoopAsync(ControlLease lease, long start, ITimer timer, CancellationToken cancellationToken)
    {
        var actions = new List<Lis01Action>();
        await foreach (var input in _inputs.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = _clock.GetElapsedTime(start);
            actions.Clear();
            switch (input.Kind)
            {
                case InputKind.Bytes:
                    var wasReceiving = _link.State == Lis01State.Receiving;
                    _lastReceivedSequence = input.Sequence;
                    _link.OnBytes(input.Data!, now, actions);
                    if (!wasReceiving && (_link.State == Lis01State.Receiving || actions.Any(a => a is Lis01Received)))
                        _receptionStart = input.Sequence;
                    break;
                case InputKind.Tick:
                    _link.Poll(now, actions);
                    break;
                case InputKind.Send:
                    try
                    {
                        var id = _link.Enqueue(input.Records!);
                        _pendingSends[id] = input.Completion!;
                        _link.Poll(now, actions);
                    }
                    catch (ArgumentException exception)
                    {
                        input.Completion!.TrySetException(exception);
                    }
                    break;
                case InputKind.End:
                    _channelEnded = true;
                    _inputs.Writer.TryComplete();
                    foreach (var pending in _pendingSends.Values)
                        pending.TrySetResult(new(0, Lis01SendResult.Failed, 0, 0, "ATSM-CH-CLOSED"));
                    _pendingSends.Clear();
                    while (_inputs.Reader.TryRead(out var remaining))
                        remaining.Completion?.TrySetResult(new(0, Lis01SendResult.Failed, 0, 0, "ATSM-CH-CLOSED"));
                    return;
            }

            foreach (var action in actions)
                await ExecuteAsync(action, lease, cancellationToken).ConfigureAwait(false);

            if (_link.NextDeadline is { } deadline)
            {
                var due = deadline - _clock.GetElapsedTime(start);
                timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
            }
            else
            {
                timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async ValueTask ExecuteAsync(Lis01Action action, ControlLease lease, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case Lis01Write write:
                _trace.Write(TraceDirection.Transmitted, write.Bytes);
                var outcome = await _channel.WriteAsync(write.Bytes, lease, cancellationToken).ConfigureAwait(false);
                if (outcome.Progress != WriteProgress.Completed)
                    Report(SessionCodes.WriteNotCompleted, AtsmSeverity.Error, $"{outcome.Progress}: {outcome.Accepted}/{outcome.Requested} bytes ({outcome.Code})");
                break;
            case Lis01Received received:
                var trace = new TraceRef(_trace.TraceId, _receptionStart ?? _lastReceivedSequence, _lastReceivedSequence);
                _receptionStart = null;
                _receptions.Writer.TryWrite(new Reception(received, trace));
                break;
            case Lis01SendCompleted completed:
                if (completed.Result != Lis01SendResult.Delivered)
                    Report(SessionCodes.SendNotDelivered, AtsmSeverity.Warning, $"transmission {completed.TransmissionId}: {completed.Result}, {completed.FramesAcknowledged}/{completed.TotalFrames} frames ({completed.Code})");
                if (_pendingSends.Remove(completed.TransmissionId, out var completion))
                    completion.TrySetResult(completed);
                break;
            case Lis01Notice notice:
                Report(notice.Code, AtsmSeverity.Information, notice.Detail ?? string.Empty);
                break;
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.ReadBufferBytes];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var outcome = await _channel.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (outcome.Count > 0)
                {
                    var data = buffer.AsSpan(0, outcome.Count).ToArray();
                    var sequence = _trace.Write(TraceDirection.Received, data);
                    _inputs.Writer.TryWrite(new Input(InputKind.Bytes, data, sequence, null, null));
                }
                if (outcome.EndOfStream)
                    break;
            }
        }
        finally
        {
            _inputs.Writer.TryWrite(Input.End);
        }
    }

    private async Task BusinessLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var reception in _receptions.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reception.Received.Complete)
            {
                // Evidence only: an incomplete transmission is never interpreted as a normal message.
                Report(SessionCodes.IncompleteTransmission, AtsmSeverity.Warning, $"trace {reception.Trace.FirstSequence}-{reception.Trace.LastSequence}");
                continue;
            }

            var payload = _profile.Encoding.GetBytes(string.Concat(reception.Received.Messages.Select(m => _profile.Encoding.GetString(m))));
            var result = await _processor.ProcessRecordedAsync(payload, reception.Trace, Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
            _instrument = _processor.Instrument;
            foreach (var records in result.Responses)
            {
                if (_options.TransmitHostMessages) await SendRecordsAsync(records, cancellationToken).ConfigureAwait(false);
                else Report(SessionCodes.TransmissionSuppressed, AtsmSeverity.Information, $"query answer of {records.Count} records not transmitted");
            }
        }
    }

    private void Report(string code, AtsmSeverity severity, string message) => Report(new AtsmDiagnostic(code, severity, message));

    private void Report(AtsmDiagnostic diagnostic)
    {
        _trace.Write(TraceDirection.Event, [], diagnostic.Code, diagnostic.Message);
        _options.Diagnostics?.Invoke(diagnostic);
    }

    private enum InputKind { Bytes, Tick, Send, End }

    private sealed record Input(InputKind Kind, byte[]? Data, long Sequence, List<ReadOnlyMemory<byte>>? Records, TaskCompletionSource<Lis01SendCompleted>? Completion)
    {
        public static readonly Input Tick = new(InputKind.Tick, null, -1, null, null);
        public static readonly Input End = new(InputKind.End, null, -1, null, null);
    }

    private sealed record Reception(Lis01Received Received, TraceRef Trace);
}
