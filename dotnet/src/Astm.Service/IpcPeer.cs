using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Astm.Service;

public interface IServiceCallbacks
{
    ValueTask<TReply> InvokeAsync<TRequest, TReply>(Guid sessionId, string method, TRequest data, CancellationToken token,
        DateTimeOffset? deadlineUtc = null, SessionCorrelation? correlation = null);
}

/// <summary>One reader always dispatches callback replies, even while requests wait for the host.</summary>
public sealed class IpcPeer(Stream stream, ServiceLimits limits) : IServiceCallbacks, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcEnvelope>> _callbacks = new();
    private readonly ConcurrentDictionary<Guid, ActiveRequest> _requests = new();
    private readonly Channel<Outgoing> _outbound = Channel.CreateBounded<Outgoing>(limits.OutboundCapacity);
    private readonly SemaphoreSlim _callbackSlots = new(limits.MaxCallbacks);
    private int _aborted;
    public Guid Id { get; } = Guid.CreateVersion7();
    public CancellationToken Disconnected => _stop.Token;
    public int PendingCallbacks => _callbacks.Count;
    public Action<string>? ResponseSent { get; init; }
    public async Task RunAsync(Func<IpcEnvelope, CancellationToken, ValueTask<JsonElement>> handle, CancellationToken token)
    {
        using var registration = token.Register(Abort);
        var writer = WriteLoopAsync();
        try
        {
            while (await IpcFrames.ReadAsync(stream, limits.MaxFrameBytes, _stop.Token, limits.CallbackTimeoutMs).ConfigureAwait(false) is { } message)
            {
                if (message.Kind is "result" or "error")
                {
                    if (_callbacks.TryGetValue(message.Id, out var pending)) pending.TrySetResult(message);
                    continue; // Late replies to expired callbacks cannot resurrect them.
                }
                if (message.Kind != "request" || string.IsNullOrWhiteSpace(message.Method)) throw new ServiceException("ipc.kind.invalid");
                if (message.Method == "requests.cancel")
                {
                    var target = ServiceJson.Read<CancelRequest>(message.Data);
                    if (_requests.TryGetValue(target.RequestId, out var request))
                    { try { request.Stop.Cancel(); } catch (ObjectDisposedException) { /* Request completed concurrently. */ } }
                    await ReplyAsync(message, ServiceJson.Element(new Ok()), null).ConfigureAwait(false);
                    continue;
                }
                if (_requests.Count >= limits.MaxRequests)
                { await ReplyAsync(message, ServiceJson.Element(new Ok(false)), "ipc.requests.capacity").ConfigureAwait(false); continue; }
                var active = new ActiveRequest(CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
                if (!_requests.TryAdd(message.Id, active)) throw new ServiceException("ipc.request.duplicate");
                active.Stop.CancelAfter(limits.RequestTimeoutMs);
                var requestMessage = message;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        JsonElement response;
                        string? error = null;
                        try { response = await handle(requestMessage, active.Stop.Token).ConfigureAwait(false); }
                        catch (Exception exception)
                        {
                            response = ServiceJson.Element(new Ok(false));
                            error = exception switch
                            {
                                ServiceException known => known.Code,
                                Astm.Operations.OperationCompatibilityException known => known.Code,
                                OperationCanceledException => "request.cancelled-or-expired",
                                JsonException or ArgumentException or InvalidOperationException => "request.invalid",
                                _ => "request.failed",
                            };
                        }
                        await ReplyAsync(requestMessage, response, error).ConfigureAwait(false);
                        if (error is null) ResponseSent?.Invoke(requestMessage.Method!);
                    }
                    catch { Abort(); }
                    finally { _requests.TryRemove(requestMessage.Id, out _); active.Stop.Dispose(); active.Completion.TrySetResult(); }
                }, CancellationToken.None);
            }
        }
        finally
        {
            Abort(); _outbound.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); } catch (Exception) { /* Disconnection is terminal. */ }
            await Task.WhenAll(_requests.Values.Select(r => r.Completion.Task)).ConfigureAwait(false);
        }
    }
    public async ValueTask<TReply> InvokeAsync<TRequest, TReply>(Guid sessionId, string method, TRequest data, CancellationToken token,
        DateTimeOffset? deadlineUtc = null, SessionCorrelation? correlation = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        var due = DateTimeOffset.UtcNow.AddMilliseconds(limits.CallbackTimeoutMs);
        if (deadlineUtc is { } requested && requested < due) due = requested;
        var remaining = due - DateTimeOffset.UtcNow;
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        await _callbackSlots.WaitAsync(deadline.Token).ConfigureAwait(false);
        var id = Guid.CreateVersion7();
        var completion = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!_callbacks.TryAdd(id, completion)) throw new InvalidOperationException();
            await SendAsync(new(1, id, "callback", ServiceJson.Element(data))
            { Method = method, SessionId = sessionId, DeadlineUtc = due, Correlation = correlation }, deadline.Token).ConfigureAwait(false);
            var reply = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (reply.Kind == "error") throw new ServiceException("callback.rejected");
            return ServiceJson.Read<TReply>(reply.Data);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_stop.IsCancellationRequested)
        { throw new ServiceException("callback.timeout"); }
        finally { _callbacks.TryRemove(id, out _); _callbackSlots.Release(); }
    }
    private ValueTask ReplyAsync(IpcEnvelope request, JsonElement data, string? error) => SendAsync(
        new(1, request.Id, error is null ? "result" : "error", data) { SessionId = request.SessionId, Error = error }, _stop.Token);
    private async ValueTask SendAsync(IpcEnvelope envelope, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        deadline.CancelAfter(limits.CallbackTimeoutMs);
        var outgoing = new Outgoing(envelope, new(TaskCreationOptions.RunContinuationsAsynchronously), deadline.Token);
        await _outbound.Writer.WriteAsync(outgoing, deadline.Token).ConfigureAwait(false);
        await outgoing.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
    }
    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    if (item.Token.IsCancellationRequested) { item.Completion.TrySetCanceled(); continue; }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, item.Token);
                    deadline.CancelAfter(limits.CallbackTimeoutMs);
                    await IpcFrames.WriteAsync(stream, item.Envelope, limits.MaxFrameBytes, deadline.Token).ConfigureAwait(false);
                    item.Completion.TrySetResult();
                }
                catch (Exception exception) { item.Completion.TrySetException(exception); throw; }
            }
        }
        finally { Abort(); while (_outbound.Reader.TryRead(out var pending)) pending.Completion.TrySetCanceled(); }
    }
    private void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
        _stop.Cancel();
        stream.Dispose();
    }
    public ValueTask DisposeAsync() { Abort(); _stop.Dispose(); _callbackSlots.Dispose(); return ValueTask.CompletedTask; }
    private sealed record Outgoing(IpcEnvelope Envelope, TaskCompletionSource Completion, CancellationToken Token);
    private sealed class ActiveRequest(CancellationTokenSource stop)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
