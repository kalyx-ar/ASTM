using System.Text;
using System.Threading.Channels;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Hosting;
using Astm.Formats.Medical.Model;
using Astm.Operations;
using Astm.Protocol.Lis01;
using Astm.Protocol.Lis02;
using Astm.Runtime.Sessions;
using Astm.Abstractions.Diagnostics;

namespace Astm.Service;

internal sealed class ServiceSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly InstrumentConfiguration _config;
    private readonly ProfileBinding _binding;
    private readonly ServiceLimits _limits;
    private readonly CancellationTokenSource _stop;
    private readonly RemoteWorkflowHost _host;
    private readonly RemoteTraceSink _trace;
    private readonly IMedicalOperationObserver _observer;
    private readonly SemaphoreSlim _contentGate = new(1, 1);
    private readonly Channel<QueuedOperation> _queue;
    private readonly Dictionary<Guid, QueuedOperation> _operations = [];
    private readonly List<IDisposable> _claims = [];
    private ServiceConnection? _connection;
    private AstmInstrumentSession? _astm;
    private Hl7InstrumentSession? _hl7;
    private Task _run = Task.CompletedTask, _worker = Task.CompletedTask;
    private string _state = "Opening";
    private string? _code;
    private long _lastSequence = -1;
    private DateTimeOffset _lastProgress = DateTimeOffset.UtcNow;
    private bool _reconciled;
    private int _disposed;
    private readonly TaskCompletionSource _openingFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid Id { get; } = Guid.CreateVersion7();
    public Guid Generation { get; } = Guid.CreateVersion7();
    public Guid Owner { get; }
    public string InstrumentId => _config.Id;
    public IMedicalOperations Operations => _binding.Operations;
    public ServiceSession(Guid owner, InstrumentConfiguration config, ProfileBinding binding, Guid traceId,
        IServiceCallbacks callbacks, ServiceLimits limits, CancellationToken lifetime)
    {
        Owner = owner; _config = config; _binding = binding; _limits = limits;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _queue = Channel.CreateBounded<QueuedOperation>(limits.OperationCapacity);
        var correlation = new SessionCorrelation(Generation, config.Id, config.ProfileId, binding.Operations.Profile.Revision, traceId);
        _host = new(callbacks, Id, binding.Operations.Profile.QueryAnswerBudget, correlation);
        _observer = binding.Plugin.CreateOperationObserver(config.ProfileId, new(new(config.Id), TimeProvider.System), traceId);
        _trace = new(callbacks, Id, Generation, traceId, _observer, _stop.Token, sequence =>
        { lock (_gate) { _lastSequence = sequence; _lastProgress = DateTimeOffset.UtcNow; } }, correlation);
    }
    public SessionStatus Status
    {
        get
        {
            lock (_gate) return new(Id, Generation, _config.Id, _config.ProfileId, _state, _trace.TraceId,
                _lastSequence, _lastProgress, _code, _observer.Current, _connection?.Tenure, _operations.Count)
                { ProfileRevision = Operations.Profile.Revision };
        }
    }
    public async Task OpenAsync(IServiceTransportFactory transports, string ownershipDirectory, CancellationToken request)
    {
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(request, _stop.Token);
        try
        {
            _claims.Add(LocalAccess.Claim(ownershipDirectory, "instrument:" + _config.Id.ToUpperInvariant()));
            foreach (var key in await transports.OwnershipKeysAsync(_config, opening.Token).ConfigureAwait(false))
                _claims.Add(LocalAccess.Claim(ownershipDirectory, key));
            await Task.Run(_trace.EnsureReady, opening.Token).ConfigureAwait(false);
            opening.Token.ThrowIfCancellationRequested();
            if (_config.Transport.Kind == "messages")
            {
                lock (_gate) _state = "Running";
            }
            else
            {
                if (_binding.Astm is null && _binding.Hl7 is null && _binding.TracePlugin is null)
                    throw new ServiceException("session.profile.content-only");
                _connection = await transports.OpenAsync(_config, _binding, _trace, opening.Token).ConfigureAwait(false);
                // Acknowledged trace bridge is prepared before the transport factory, and rechecked by the runtime.
                var instrument = new InstrumentRef(_config.Id);
                if (_binding.Astm is { } astm)
                    _astm = new(_connection.Stream, _binding.AstmPlugin!.CreateDialect(astm, new(instrument, TimeProvider.System)), astm,
                        _host, instrument, _trace, new() { MaxPendingInputs = 128, MaxPendingReceptions = 16, MaxReceptionBytes = _limits.MaxContentBytes, Diagnostics = CaptureDiagnostic });
                else if (_binding.Hl7 is { } hl7)
                    _hl7 = new(_connection.Stream, _binding.Hl7Plugin!.CreateDialect(hl7, new(instrument, TimeProvider.System)), hl7,
                        _host, instrument, _trace) { MaxMessageBytes = _limits.MaxContentBytes, Diagnostics = CaptureDiagnostic };
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _run = Task.Run(async () =>
                {
                    try
                    {
                        var running = _astm?.RunAsync(_stop.Token) ?? _hl7?.RunAsync(_stop.Token) ?? RunPassiveAsync(_stop.Token);
                        lock (_gate) _state = "Running";
                        started.TrySetResult();
                        await running.ConfigureAwait(false);
                        lock (_gate) if (_state == "Running") _state = "Ended";
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                    catch (Exception) { lock (_gate) { _state = "Faulted"; _code = "session.communication.failed"; } }
                    finally
                    {
                        started.TrySetResult(); Stop(); _observer.Complete();
                        await _connection.DisposeAsync().ConfigureAwait(false);
                    }
                }, CancellationToken.None);
                await started.Task.WaitAsync(opening.Token).ConfigureAwait(false);
            }
            _worker = Task.Run(OperationLoopAsync, CancellationToken.None);
        }
        catch { Stop(); throw; }
        finally { _openingFinished.TrySetResult(); }
    }
    public PrepareResult Prepare(OperationEnvelope request)
    {
        var prepared = Operations.Prepare(request);
        return new(prepared.Format, prepared.Bytes.ToArray(), prepared.Definition);
    }
    public OperationStatus Start(OperationEnvelope request)
    {
        if (request.OperationId == Guid.Empty) throw new ServiceException("operation.id.required");
        var prepared = Operations.Prepare(request);
        if (!prepared.Definition.Capabilities.HasFlag(OperationCapabilities.Execute)) throw new ServiceException("operation.execute.unsupported");
        lock (_gate)
        {
            if (_operations.TryGetValue(request.OperationId, out var existing))
            {
                if (existing.Request with { Data = request.Data } != request || !System.Text.Json.JsonElement.DeepEquals(existing.Request.Data, request.Data))
                    throw new ServiceException("operation.id.conflict");
                return existing.Status;
            }
            if (_state != "Running" || _stop.IsCancellationRequested) throw new ServiceException("session.not-running");
            if (_astm is null && _hl7 is null) throw new ServiceException("session.receive-only");
            if (!_reconciled && _connection?.Tenure is { } tenure && (tenure.UncertainTx || tenure.UnownedRxBytes > 0))
                throw new ServiceException("session.reconciliation-required");
            if (_operations.Count >= _limits.OperationHistory) throw new ServiceException("operation.history.capacity");
            var queued = new QueuedOperation(request, CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
            if (!_queue.Writer.TryWrite(queued)) { queued.Stop.Dispose(); throw new ServiceException("operation.queue.capacity"); }
            _operations.Add(request.OperationId, queued);
            return queued.Status;
        }
    }
    public void Reconcile() { lock (_gate) _reconciled = true; }
    public OperationStatus Operation(Guid id, bool cancel = false, bool forget = false)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var found)) throw new ServiceException("operation.unknown");
            if (cancel && found.Status.State is "Queued" or "Running")
            {
                found.Stop.Cancel();
                if (found.Status.State == "Running") Stop("operation.cancelled-in-flight"); // No safe mid-frame abort/reuse of the same channel.
            }
            if (forget)
            {
                if (found.Status.State is "Queued" or "Running") throw new ServiceException("operation.pending");
                _operations.Remove(id); found.Stop.Dispose();
            }
            return found.Status;
        }
    }
    private async Task OperationLoopAsync()
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (queued.Stop.IsCancellationRequested)
                { queued.Status = new(queued.Request.OperationId, "Cancelled", null, "operation.cancelled-before-send"); continue; }
                queued.Status = new(queued.Request.OperationId, "Running", null, null);
            }
            OrderDeliveryOutcome delivery;
            try
            {
                if (_hl7 is not null)
                {
                    var outcome = await _hl7.SendOperationAsync(queued.Request, queued.Stop.Token).ConfigureAwait(false);
                    delivery = new(outcome.State, outcome.Code ?? outcome.AcknowledgementCode);
                }
                else
                {
                    var outcome = await _astm!.SendOperationAsync(queued.Request, queued.Stop.Token).ConfigureAwait(false);
                    delivery = new(outcome.Result == Lis01SendResult.Delivered ? OrderDeliveryState.Sent : OrderDeliveryState.Uncertain, outcome.Code);
                }
            }
            catch (Exception) { delivery = new(OrderDeliveryState.Uncertain, "operation.interrupted"); }
            lock (_gate) queued.Status = new(queued.Request.OperationId, "Finished", delivery, delivery.Code);
            if (delivery.State == OrderDeliveryState.Uncertain) Stop("operation.reconciliation-required");
        }
    }
    public async ValueTask<ContentResult> ProcessAsync(ContentRequest input, CancellationToken token)
    {
        if (_config.Transport.Kind != "messages") throw new ServiceException("session.live-input-owned");
        if (input.ReceptionId == Guid.Empty || input.Content is null || input.Content.Length < 1 || input.Content.Length > _limits.MaxContentBytes)
            throw new ServiceException("content.invalid-or-limit");
        if (!await _contentGate.WaitAsync(0, token).ConfigureAwait(false)) throw new ServiceException("content.busy");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        _host.ReceptionId = input.ReceptionId;
        try
        {
            if (_stop.IsCancellationRequested) throw new ServiceException("session.not-running");
            var instrument = new InstrumentRef(_config.Id);
            if (_binding.Hl7 is { } hl7)
            {
                var processor = new Hl7MessageProcessor(_binding.Hl7Plugin!.CreateDialect(hl7, new(instrument, TimeProvider.System)), hl7, _host, instrument, _trace)
                    { MaxMessageBytes = _limits.MaxContentBytes };
                var result = await processor.ProcessAsync(input.Content, input.ReceptionId, deadline.Token).ConfigureAwait(false);
                return new(result.Outcome is null ? [] : [result.Outcome], result.Responses.Select(r => hl7.Encoding.GetBytes(r.Serialize())).ToArray(),
                    result.Diagnostics.ToArray());
            }
            if (_binding.Astm is { } astm)
            {
                var processor = new AstmMessageProcessor(_binding.AstmPlugin!.CreateDialect(astm, new(instrument, TimeProvider.System)), astm, _host, instrument, _trace)
                    { MaxMessageBytes = _limits.MaxContentBytes };
                var result = await processor.ProcessAsync(input.Content, input.ReceptionId, deadline.Token).ConfigureAwait(false);
                return new(result.Outcomes, result.Responses.Select(r => astm.Encoding.GetBytes(Lis02Codec.Serialize(r))).ToArray(), result.Diagnostics.ToArray());
            }
            var exchange = await new MedicalExchangeProcessor(Operations, _host, _trace)
                .ProcessAsync(input.Content, input.ReceptionId, _binding.TimeZone, deadline.Token).ConfigureAwait(false);
            foreach (var diagnostic in exchange.Diagnostics) CaptureDiagnostic(diagnostic);
            return new(exchange.Outcome is null ? [] : [exchange.Outcome], exchange.Responses.Select(r => r.Bytes.ToArray()).ToArray(), exchange.Diagnostics.ToArray());
        }
        catch { Stop("session.content.failed"); throw; }
        finally { _host.ReceptionId = null; _contentGate.Release(); }
    }
    private async Task RunPassiveAsync(CancellationToken token)
    {
        if (_binding.TracePlugin is not Astm.Plugins.IInstrumentTraceStreamPlugin plugin)
            throw new ServiceException("passive.streaming.unsupported");
        var reader = plugin.CreateReader(_binding.TraceProfile!, new(_config.Id), _trace.TraceId, _binding.TimeZone, _limits.MaxContentBytes);
        var buffer = new byte[4096];
        var ordinal = 0;
        async Task Ingest(Astm.Plugins.TraceInterpretation decoded)
        {
            foreach (var diagnostic in decoded.Diagnostics) CaptureDiagnostic(diagnostic);
            if (decoded.Diagnostics.Any(d => d.Severity == Astm.Abstractions.Diagnostics.AstmSeverity.Error))
                throw new ServiceException("passive.decode.failed");
            if (decoded.Observations.Count == 0) return;
            var observations = decoded.Observations;
            var reference = new TraceRef(_trace.TraceId, observations.Min(o => o.Trace.FirstSequence), observations.Max(o => o.Trace.LastSequence));
            var id = ReceptionIdentity.Derive(_trace.TraceId, "passive/batch", checked(ordinal++));
            var outcome = await _host.IngestAsync(new(id, new(_config.Id), observations, reference), token).ConfigureAwait(false);
            if (outcome.State is not (IngestState.Accepted or IngestState.Duplicate)) throw new ServiceException("passive.ingest.not-accepted");
        }
        while (true)
        {
            var count = await _connection!.Stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) { await Ingest(reader.Complete()).ConfigureAwait(false); return; }
            var payload = buffer.AsMemory(0, count).ToArray();
            var sequence = _trace.Write(TraceDirection.Received, payload);
            await Ingest(reader.Feed(new(sequence, TimeSpan.Zero, DateTimeOffset.UtcNow, TraceDirection.Received, payload))).ConfigureAwait(false);
        }
    }
    private void CaptureDiagnostic(AstmDiagnostic diagnostic) => _trace.Write(TraceDirection.Event, [], "service.diagnostic",
        System.Text.Json.JsonSerializer.Serialize(diagnostic, ServiceJson.Type<AstmDiagnostic>()));
    private void Stop(string? code = null)
    {
        _stop.Cancel(); _queue.Writer.TryComplete();
        lock (_gate)
        {
            if (_state is "Running" or "Opening") _state = "Stopped";
            if (code is not null) _code ??= code;
        }
        _connection?.Stream.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _closed.Task.ConfigureAwait(false); return; }
        try
        {
            Stop();
            await _openingFinished.Task.ConfigureAwait(false);
            await Task.WhenAll(_run, _worker).ConfigureAwait(false);
            await _contentGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _observer.Complete();
                if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
            }
            finally { _contentGate.Release(); }
        }
        finally
        {
            foreach (var operation in _operations.Values) operation.Stop.Dispose();
            foreach (var claim in _claims) claim.Dispose();
            lock (_gate) _state = "Closed";
            _stop.Dispose();
            _closed.TrySetResult();
        }
    }
    private sealed class QueuedOperation(OperationEnvelope request, CancellationTokenSource stop)
    {
        public OperationEnvelope Request { get; } = request with { Data = request.Data.Clone() };
        public CancellationTokenSource Stop { get; } = stop;
        public OperationStatus Status { get; set; } = new(request.OperationId, "Queued", null, null);
    }
}
