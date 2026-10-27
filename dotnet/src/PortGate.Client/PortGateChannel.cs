using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Atsm.Abstractions.Transport;
using Atsm.Transport;

namespace PortGate.Client;

public sealed record PortGateOptions(string ControllerId, string PortId)
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan WriteStatusTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxUnitBytes { get; init; } = 64 * 1024;

    /// <summary>Answer of this owner to a cooperative handover request; null keeps the port (spec/admin-v1.md).</summary>
    public Func<PortGateHandoverRequest, bool>? HandoverDecision { get; init; }
}

public sealed record PortGateHandoverRequest(ulong HandoverId, string Requester, string? Reason, TimeSpan Timeout);

public sealed record PortGateHandoverResult(bool Yielded, string? Code);

public sealed class PortGateException(string code, string message) : IOException($"{code}: {message}")
{
    public string Code { get; } = code;
}

public enum PortGateEventKind { Queued, Granted, Revoked, Discontinuity, Faulted, Closed }

public sealed record PortGateEvent(PortGateEventKind Kind, DateTimeOffset Timestamp, string? Code = null, string? Detail = null);

/// <summary>Tenure of the port: generation and transfer state reported by the agent when it was granted.</summary>
public sealed record PortGateLease(uint Generation, Guid TransferId, TimeSpan Duration, bool UncertainTx, ulong UnownedRxBytes);

/// <summary>Port as the agent announces it, including the serial settings in force (null for TCP devices).</summary>
public sealed record PortGatePortInfo(string PortId, string? InstrumentId, int MaxWriteBytes, string? DeviceKind, SerialSettings? Serial);

/// <summary>
/// Connection to one port of a PortGate agent (spec/link-v1.md, spec/admin-v1.md). Acquiring the port yields a
/// <see cref="Stream"/> for that tenure: an ATSM session runs on it, and it ends when the tenure ends (release,
/// revocation, kill switch, connection loss). The lease is renewed automatically while held.
/// </summary>
public sealed class PortGateChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly PortGateOptions _options;
    private readonly ushort _port;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Channel<PortGateEvent> _events = Channel.CreateUnbounded<PortGateEvent>();
    private readonly Channel<PortGateAuditEvent> _securityEvents = Channel.CreateUnbounded<PortGateAuditEvent>();
    private readonly Channel<PortGatePortState> _portStates = Channel.CreateUnbounded<PortGatePortState>();
    private readonly Channel<PortGateObservation> _observations = Channel.CreateUnbounded<PortGateObservation>();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SortedDictionary<ulong, object?>>> _controlReplies = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SortedDictionary<ulong, object?>>> _writeReplies = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _readLoop = Task.CompletedTask;
    private Task _heartbeatLoop = Task.CompletedTask;
    private long _requestId;
    private long _writeSequence;
    private Tenure? _tenure;
    private volatile bool _closed;
    private string? _closeReason;
    private int _disposed;

    private PortGateChannel(Stream stream, PortGateOptions options, ushort port, Guid agentEpoch, PortGatePortInfo info)
    {
        _stream = stream;
        _options = options;
        _port = port;
        AgentEpoch = agentEpoch;
        Port = info;
    }

    public Guid AgentEpoch { get; }

    public PortGatePortInfo Port { get; }

    /// <summary>Stream of the current tenure, or null while the port is not held.</summary>
    public Stream? Stream => _tenure?.Stream;

    public static async Task<PortGateChannel> ConnectAsync(TcpEndpoint agent, PortGateOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HandshakeTimeout);
        // TLS is decided by the endpoint and never downgraded; the agent verifies the client certificate (mTLS) or PSK.
        var stream = await LocalTransportProvider.ConnectAsync(agent, timeout.Token).ConfigureAwait(false);
        return await OpenAsync(stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the channel on an established connection: the one this host dialed, or one the agent dialed in the
    /// agent-initiated mode (<see cref="PortGateListener"/>). Answering Hello as a controller gives it that role.
    /// The channel owns <paramref name="stream"/>.
    /// </summary>
    public static async Task<PortGateChannel> OpenAsync(Stream stream, PortGateOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HandshakeTimeout);
        try
        {
            var decoder = new LinkDecoder(options.MaxUnitBytes);
            var units = new List<LinkUnit>();
            await WriteAsync(stream, LinkUnit.Control(LinkType.Hello, 0, LinkCbor.Map((0, 1UL), (1, "controller"), (2, options.ControllerId))), timeout.Token).ConfigureAwait(false);
            var hello = await ReceiveAsync(stream, decoder, units, LinkType.Hello, timeout.Token).ConfigureAwait(false);
            var capabilities = await ReceiveAsync(stream, decoder, units, LinkType.Capabilities, timeout.Token).ConfigureAwait(false);

            var epoch = hello.Map()?.GetValueOrDefault(3UL) is byte[] { Length: 16 } epochBytes ? new Guid(epochBytes) : Guid.Empty;
            var ports = capabilities.Map()?.GetValueOrDefault(0UL) as List<object?> ?? [];
            for (var i = 0; i < ports.Count; i++)
            {
                if (ports[i] is not SortedDictionary<ulong, object?> p || p.GetValueOrDefault(0UL) as string != options.PortId)
                    continue;
                var device = p.GetValueOrDefault(7UL) as SortedDictionary<ulong, object?>;
                var info = new PortGatePortInfo(options.PortId, p.GetValueOrDefault(1UL) as string, (int)(p.GetValueOrDefault(5UL) as ulong? ?? 16 * 1024),
                    device is null ? null : (device.GetValueOrDefault(0UL) as ulong?) switch { 0 => "serial", 1 => "tcp", 2 => "tcp-listen", _ => null },
                    PortGateDevices.Serial(device));
                var channel = new PortGateChannel(stream, options, (ushort)(i + 1), epoch, info);
                channel.Start(decoder, units);
                return channel;
            }
            throw new PortGateException("PORTGATE-CH-UNKNOWN-PORT", $"agent does not expose port {options.PortId}");
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Acquires the port (queued by priority when held) and opens write admission. The tenure's stream is <see cref="Stream"/>.</summary>
    public async Task<PortGateLease> AcquireAsync(int priority = 0, TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        var granted = await ControlRequestAsync(LinkType.Acquire, LinkCbor.Map((1, (long)priority), (2, (ulong)(deadline?.TotalMilliseconds ?? 0))), cancellationToken).ConfigureAwait(false);
        var lease = new PortGateLease(
            (uint)(granted.GetValueOrDefault(1UL) as ulong? ?? 0),
            new Guid(granted.GetValueOrDefault(2UL) as byte[] ?? new byte[16]),
            TimeSpan.FromMilliseconds(granted.GetValueOrDefault(3UL) as ulong? ?? 15000),
            granted.GetValueOrDefault(5UL) is true,
            granted.GetValueOrDefault(6UL) as ulong? ?? 0);
        var tenure = new Tenure(this, lease, granted.GetValueOrDefault(2UL) as byte[] ?? new byte[16]);
        _tenure = tenure;
        if (lease.UncertainTx || lease.UnownedRxBytes > 0)
            Publish(PortGateEventKind.Discontinuity, "PORTGATE-CH-TRANSFER-STATE", $"uncertainTx={lease.UncertainTx}, unownedRxBytes={lease.UnownedRxBytes}");
        Publish(PortGateEventKind.Granted, null, $"generation {lease.Generation}");
        await SendAsync(LinkUnit.Control(LinkType.ConfirmTransfer, _port, LinkCbor.Map((1, (ulong)lease.Generation), (2, tenure.TransferId))), cancellationToken).ConfigureAwait(false);
        _ = RenewLoopAsync(tenure);
        return lease;
    }

    /// <summary>Releases the port; the tenure's stream ends.</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_tenure is not { } tenure)
            return;
        End(tenure, "PORTGATE-CH-RELEASED");
        try
        {
            await SendAsync(LinkUnit.Control(LinkType.Release, _port, LinkCbor.Map((1, (ulong)tenure.Lease.Generation))), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Asks the current owner to yield the port. The requester must already be queued (AcquireAsync pending).</summary>
    public async Task<PortGateHandoverResult> RequestHandoverAsync(string? reason, CancellationToken cancellationToken)
    {
        var map = await ControlRequestAsync(LinkType.RequestHandover, reason is null ? [] : LinkCbor.Map((1, reason)), cancellationToken).ConfigureAwait(false);
        return new PortGateHandoverResult(map.GetValueOrDefault(1UL) is true, map.GetValueOrDefault(2UL) as string);
    }

    /// <summary>Forced interruption of the current owner (needs TLS with an authenticated peer and the right). Returns the event id to contest.</summary>
    public async Task<ulong> KillSwitchAsync(string reason, CancellationToken cancellationToken)
    {
        var map = await ControlRequestAsync(LinkType.KillSwitch, LinkCbor.Map((1, reason)), cancellationToken).ConfigureAwait(false);
        return map.GetValueOrDefault(1UL) as ulong? ?? 0;
    }

    /// <summary>Contests a kill switch: the agent drops and blocks its author; <paramref name="secureClose"/> also closes the port.</summary>
    public async Task<ulong> ContestAsync(ulong killEventId, string reason, bool secureClose, CancellationToken cancellationToken)
    {
        var map = await ControlRequestAsync(LinkType.Contest, LinkCbor.Map((1, killEventId), (2, reason), (3, secureClose)), cancellationToken).ConfigureAwait(false);
        return map.GetValueOrDefault(1UL) as ulong? ?? 0;
    }

    public async IAsyncEnumerable<PortGateEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    /// <summary>Starts or stops observing the port (needs the Observe right; announced to everyone on the port). Every observer
    /// follows the semaphore and the port state; the traffic is copied only when the port is shared.</summary>
    public async Task<PortGateObserving> ObserveAsync(bool enable, CancellationToken cancellationToken)
    {
        var map = await ControlRequestAsync(LinkType.Observe, LinkCbor.Map((1, enable)), cancellationToken).ConfigureAwait(false);
        if (map.GetValueOrDefault(2UL) is string code)
            throw new PortGateException(code, "observation refused by the agent");
        return new PortGateObserving(map.GetValueOrDefault(1UL) is true, map.GetValueOrDefault(3UL) is true);
    }

    /// <summary>Semaphore and state of the port as seen by an observer: sent on start and whenever it changes.</summary>
    public async IAsyncEnumerable<PortGatePortState> PortStatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _portStates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    /// <summary>Observed traffic of a shared port, both directions, whoever owns it. Copies the agent had to discard for a
    /// slow observer show as jumps in <see cref="PortGateObservation.Sequence"/>.</summary>
    public async IAsyncEnumerable<PortGateObservation> ObservationsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _observations.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    /// <summary>Semaphore and security events of this port (handover, kill switch, contest, secure close).</summary>
    public async IAsyncEnumerable<PortGateAuditEvent> SecurityEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _securityEvents.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_tenure is not null && !_closed)
        {
            try { await ReleaseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or TimeoutException or ObjectDisposedException) { }
        }
        _stop.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_readLoop, _heartbeatLoop).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        _sendGate.Dispose();
        _stop.Dispose();
    }

    private async Task<SortedDictionary<ulong, object?>> ControlRequestAsync(LinkType type, SortedDictionary<ulong, object?> fields, CancellationToken cancellationToken)
    {
        var requestId = (ulong)Interlocked.Increment(ref _requestId);
        var reply = Register(_controlReplies, requestId);
        fields[0] = requestId;
        await SendAsync(LinkUnit.Control(type, _port, fields), cancellationToken).ConfigureAwait(false);
        return await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteTenureAsync(Tenure tenure, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (_tenure != tenure || tenure.Ended)
            throw new PortGateException("PORTGATE-CH-NOT-OWNER", "the tenure of this stream ended");
        for (var offset = 0; offset < bytes.Length; offset += Port.MaxWriteBytes)
        {
            var chunk = bytes.Slice(offset, Math.Min(Port.MaxWriteBytes, bytes.Length - offset));
            var sequence = (ulong)Interlocked.Increment(ref _writeSequence);
            var reply = Register(_writeReplies, sequence);
            await SendAsync(LinkUnit.Serial(_port, sequence, 0, tenure.Lease.Generation, chunk.Span), cancellationToken).ConfigureAwait(false);
            SortedDictionary<ulong, object?> status;
            try { status = await reply.Task.WaitAsync(_options.WriteStatusTimeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _writeReplies.TryRemove(sequence, out _);
                throw new PortGateException("PORTGATE-CH-WRITE-STATUS-LOST", "no write status from the agent: outcome unknown");
            }
            if ((status.GetValueOrDefault(3UL) as ulong?) != 0)
                throw new PortGateException(status.GetValueOrDefault(4UL) as string ?? "PORTGATE-CH-WRITE", $"write of {chunk.Length} bytes not completed ({status.GetValueOrDefault(2UL)} written)");
        }
    }

    private void Start(LinkDecoder decoder, List<LinkUnit> leftover)
    {
        foreach (var unit in leftover)
            Dispatch(unit);
        _readLoop = Task.Run(() => ReadLoopAsync(decoder));
        _heartbeatLoop = Task.Run(HeartbeatLoopAsync);
    }

    private async Task ReadLoopAsync(LinkDecoder decoder)
    {
        var buffer = new byte[16 * 1024];
        var units = new List<LinkUnit>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var count = await _stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count == 0)
                    break;
                units.Clear();
                if (decoder.Feed(buffer.AsSpan(0, count), units) != LinkDecodeError.None)
                {
                    Publish(PortGateEventKind.Faulted, "PORTGATE-CH-BAD-UNIT", decoder.Error.ToString());
                    break;
                }
                foreach (var unit in units)
                    Dispatch(unit);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _closed = true;
            if (_tenure is { } tenure)
                End(tenure, "PORTGATE-CH-CLOSED");
            Publish(PortGateEventKind.Closed, null, _closeReason ?? "agent connection closed");
            _events.Writer.TryComplete();
            _securityEvents.Writer.TryComplete();
            _portStates.Writer.TryComplete();
            _observations.Writer.TryComplete();
            foreach (var reply in _controlReplies.Values.Concat(_writeReplies.Values))
                reply.TrySetException(new IOException(_closeReason ?? "Agent connection closed."));
        }
    }

    private void Dispatch(LinkUnit unit)
    {
        if (unit.Type == LinkType.SerialData)
        {
            if (unit.Channel == _port && unit.SerialPayload() is { } data && _tenure is { } tenure && data.Generation == tenure.Lease.Generation)
                tenure.Received.Writer.TryWrite(data.Data);
            return;
        }
        if (unit.Type == LinkType.ObservedData)
        {
            if (unit.Channel == _port && unit.ObservedPayload() is { } observed)
                _observations.Writer.TryWrite(new PortGateObservation(observed.Sequence, observed.Offset, observed.Generation, observed.Direction, observed.Data));
            return;
        }
        var map = unit.Map();
        if (map is null)
            return;
        var requestId = map.GetValueOrDefault(0UL) as ulong? ?? 0;
        switch (unit.Type)
        {
            case LinkType.Granted when _controlReplies.TryRemove(requestId, out var granted):
                granted.TrySetResult(map);
                break;
            case LinkType.Denied when _controlReplies.TryRemove(requestId, out var denied):
                denied.TrySetException(new PortGateException(map.GetValueOrDefault(1UL) as string ?? "PORTGATE-CH-DENIED", "acquire denied by the agent"));
                break;
            case LinkType.Queued:
                Publish(PortGateEventKind.Queued, "PORTGATE-CH-QUEUED", $"position {map.GetValueOrDefault(1UL)}");
                break;
            case LinkType.Revoked:
                var code = map.GetValueOrDefault(2UL) as string;
                if (_tenure is { } current && (map.GetValueOrDefault(1UL) as ulong?) == current.Lease.Generation)
                    End(current, code ?? "PORTGATE-CH-REVOKED");
                Publish(PortGateEventKind.Revoked, code, null);
                break;
            case LinkType.WriteStatus when _writeReplies.TryRemove(requestId, out var status):
                status.TrySetResult(map);
                break;
            case LinkType.PortEvent:
                Publish(PortGateEventKind.Faulted, map.GetValueOrDefault(1UL) as string, map.GetValueOrDefault(2UL) as string);
                break;
            case LinkType.HandoverResult or LinkType.SecurityResult or LinkType.ObserveResult when _controlReplies.TryRemove(requestId, out var result):
                result.TrySetResult(map);
                break;
            case LinkType.HandoverAsked when unit.Channel == _port:
                var asked = new PortGateHandoverRequest(requestId, map.GetValueOrDefault(1UL) as string ?? "", map.GetValueOrDefault(2UL) as string,
                    TimeSpan.FromMilliseconds(map.GetValueOrDefault(3UL) as ulong? ?? 0));
                var yield = _options.HandoverDecision?.Invoke(asked) ?? false;
                _ = SendAsync(LinkUnit.Control(LinkType.HandoverAnswer, _port, LinkCbor.Map((0, asked.HandoverId), (1, yield))), _stop.Token)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
                break;
            case LinkType.PortState when unit.Channel == _port:
                _portStates.Writer.TryWrite(new PortGatePortState(map.GetValueOrDefault(0UL) is true, map.GetValueOrDefault(1UL) as string,
                    (uint)(map.GetValueOrDefault(2UL) as ulong? ?? 0), (int)(map.GetValueOrDefault(3UL) as ulong? ?? 0), map.GetValueOrDefault(4UL) is true,
                    map.GetValueOrDefault(5UL) is true, map.GetValueOrDefault(6UL) is true));
                break;
            case LinkType.SecurityEvent when unit.Channel == _port:
                _securityEvents.Writer.TryWrite(new PortGateAuditEvent(requestId,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(map.GetValueOrDefault(9UL) as ulong? ?? 0)), map.GetValueOrDefault(1UL) as string ?? "",
                    map.GetValueOrDefault(2UL) as string, map.GetValueOrDefault(3UL) as string, map.GetValueOrDefault(4UL) as string,
                    (uint)(map.GetValueOrDefault(5UL) as ulong? ?? 0), map.GetValueOrDefault(6UL) as string,
                    map.GetValueOrDefault(7UL) is ulong until ? DateTimeOffset.FromUnixTimeMilliseconds((long)until) : null, map.GetValueOrDefault(8UL) is true, null));
                break;
            case LinkType.Error when map.GetValueOrDefault(1UL) is ulong failed && failed != 0 && _controlReplies.TryRemove(failed, out var refused):
                refused.TrySetException(new PortGateException(map.GetValueOrDefault(0UL) as string ?? "PORTGATE-CH-AGENT-ERROR", map.GetValueOrDefault(2UL) as string ?? ""));
                break;
            case LinkType.Error:
                _closeReason = $"Agent error {map.GetValueOrDefault(0UL)}: {map.GetValueOrDefault(2UL)}";
                Publish(PortGateEventKind.Faulted, map.GetValueOrDefault(0UL) as string, map.GetValueOrDefault(2UL) as string);
                break;
        }
    }

    private void End(Tenure tenure, string code)
    {
        if (tenure.Ended)
            return;
        tenure.Ended = true;
        tenure.EndCode = code;
        tenure.Received.Writer.TryComplete();
        if (_tenure == tenure)
            _tenure = null;
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await SendAsync(LinkUnit.Control(LinkType.Heartbeat, 0, LinkCbor.Map((0, (ulong)Environment.TickCount64))), _stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task RenewLoopAsync(Tenure tenure)
    {
        try
        {
            var interval = tenure.Lease.Duration / 3;
            while (!_stop.IsCancellationRequested && !tenure.Ended)
            {
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                if (tenure.Ended)
                    break;
                await ControlRequestAsync(LinkType.Renew, LinkCbor.Map((1, (ulong)tenure.Lease.Generation)), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!tenure.Ended)
            {
                End(tenure, "PORTGATE-CH-RENEW-FAILED");
                Publish(PortGateEventKind.Revoked, "PORTGATE-CH-RENEW-FAILED", $"generation {tenure.Lease.Generation}: {exception.Message}");
            }
        }
    }

    private void Publish(PortGateEventKind kind, string? code, string? detail) => _events.Writer.TryWrite(new PortGateEvent(kind, DateTimeOffset.UtcNow, code, detail));

    private TaskCompletionSource<SortedDictionary<ulong, object?>> Register(ConcurrentDictionary<ulong, TaskCompletionSource<SortedDictionary<ulong, object?>>> replies, ulong id)
    {
        var completion = new TaskCompletionSource<SortedDictionary<ulong, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        replies[id] = completion;
        // The read loop may have ended before this request was registered: fail it instead of waiting forever.
        if (_closed)
            completion.TrySetException(new IOException(_closeReason ?? "Agent connection closed."));
        return completion;
    }

    private async Task SendAsync(LinkUnit unit, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(_stream, unit, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task WriteAsync(Stream stream, LinkUnit unit, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(unit.Encode(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LinkUnit> ReceiveAsync(Stream stream, LinkDecoder decoder, List<LinkUnit> units, LinkType type, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var index = units.FindIndex(u => u.Type == type);
            if (index >= 0)
            {
                var unit = units[index];
                units.RemoveAt(index);
                return unit;
            }
            var error = units.FirstOrDefault(u => u.Type == LinkType.Error);
            if (error is not null)
                throw new PortGateException(error.Map()?.GetValueOrDefault(0UL) as string ?? "PORTGATE-CH-AGENT-ERROR", "agent refused the handshake");
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new IOException("Agent closed the connection during the handshake.");
            if (decoder.Feed(buffer.AsSpan(0, count), units) != LinkDecodeError.None)
                throw new PortGateException("PORTGATE-CH-BAD-UNIT", decoder.Error.ToString());
        }
    }

    /// <summary>One tenure: its stream reads what the instrument sends to this owner and writes with this generation.</summary>
    private sealed class Tenure
    {
        public Tenure(PortGateChannel channel, PortGateLease lease, byte[] transferId)
        {
            Lease = lease;
            TransferId = transferId;
            Stream = new TenureStream(channel, this);
        }

        public PortGateLease Lease { get; }
        public byte[] TransferId { get; }
        public Channel<ReadOnlyMemory<byte>> Received { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });
        public Stream Stream { get; }
        public volatile bool Ended;
        public string? EndCode;
    }

    private sealed class TenureStream(PortGateChannel channel, Tenure tenure) : Stream
    {
        private ReadOnlyMemory<byte> _pending;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty)
            {
                // End of the tenure is the end of this stream: the session treats it as a disconnection.
                if (!await tenure.Received.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) || !tenure.Received.Reader.TryRead(out _pending))
                    return 0;
            }
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(channel.WriteTenureAsync(tenure, buffer, cancellationToken));

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}


/// <summary>Read-only copy of the port traffic. <paramref name="Direction"/>: 0 instrument→agent, 1 agent→instrument.</summary>
public sealed record PortGateObservation(ulong Sequence, ulong Offset, uint Generation, int Direction, ReadOnlyMemory<byte> Data);

/// <summary>Answer to <see cref="PortGateChannel.ObserveAsync"/>: observing, and whether the traffic is copied (shared port).</summary>
public sealed record PortGateObserving(bool Observing, bool Traffic);

/// <summary>Port state for observers: device open, owner, generation, queued requests, secure close, quarantine and access mode.</summary>
public sealed record PortGatePortState(bool Open, string? Owner, uint Generation, int Queued, bool SecureClosed, bool Quarantined, bool Shared);
