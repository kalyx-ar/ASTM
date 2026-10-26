using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Atsm.Abstractions.Channels;

namespace Atsm.Channels.SerialTunnel;

public sealed record SerialTunnelOptions(string ControllerId, string PortId)
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan WriteStatusTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxUnitBytes { get; init; } = 64 * 1024;

    /// <summary>Answer of this owner to a cooperative handover request; null keeps the port (spec/admin-v1.md).</summary>
    public Func<RemoteUsbHandoverRequest, bool>? HandoverDecision { get; init; }
}

public sealed record RemoteUsbHandoverRequest(ulong HandoverId, string Requester, string? Reason, TimeSpan Timeout);

public sealed record RemoteUsbHandoverResult(bool Yielded, string? Code);

public sealed class SerialTunnelException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>
/// Channel to one port of a native ATSM agent over ATSM Link v1 (spec/link-v1.md). Ownership is enforced by the
/// agent; the lease is renewed automatically while the channel holds it.
/// </summary>
public sealed class SerialTunnelChannel : IInstrumentChannel
{
    private readonly Stream _stream;
    private readonly IDisposable _owner;
    private readonly SerialTunnelOptions _options;
    private readonly ushort _port;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Channel<ReadOnlyMemory<byte>> _received = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<ChannelEvent> _events = Channel.CreateUnbounded<ChannelEvent>();
    private readonly Channel<RemoteUsbEvent> _securityEvents = Channel.CreateUnbounded<RemoteUsbEvent>();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SortedDictionary<ulong, object?>>> _controlReplies = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SortedDictionary<ulong, object?>>> _writeReplies = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly LinkDecoder _decoder;
    private Task _readLoop = Task.CompletedTask;
    private Task _heartbeatLoop = Task.CompletedTask;
    private ReadOnlyMemory<byte> _pending;
    private long _requestId;
    private long _writeSequence;
    private Grant? _grant;
    private ulong _connection;
    private volatile bool _closed;
    private string? _closeReason;

    private SerialTunnelChannel(Stream stream, IDisposable owner, SerialTunnelOptions options, ushort port, Guid agentEpoch, ChannelDescriptor descriptor, ChannelCapabilities capabilities)
    {
        _stream = stream;
        _owner = owner;
        _options = options;
        _port = port;
        AgentEpoch = agentEpoch;
        Descriptor = descriptor;
        Capabilities = capabilities;
        _decoder = new LinkDecoder(options.MaxUnitBytes);
    }

    public ChannelDescriptor Descriptor { get; }

    public ChannelCapabilities Capabilities { get; }

    public Guid AgentEpoch { get; }

    public static async Task<SerialTunnelChannel> ConnectAsync(string channelId, TcpEndpointOptions endpoint, SerialTunnelOptions options, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.HandshakeTimeout);
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
            // TLS is decided by the endpoint and never downgraded; the agent verifies the client certificate (mTLS).
            var stream = await TcpNativeChannel.SecureAsync(client.GetStream(), endpoint, asServer: false, timeout.Token).ConfigureAwait(false);
            var decoder = new LinkDecoder(options.MaxUnitBytes);
            var units = new List<LinkUnit>();

            await WriteAsync(stream, LinkUnit.Control(LinkType.Hello, 0, LinkCbor.Map((0, 1UL), (1, "controller"), (2, options.ControllerId))), timeout.Token).ConfigureAwait(false);
            var hello = await ReceiveAsync(stream, decoder, units, LinkType.Hello, timeout.Token).ConfigureAwait(false);
            var capabilities = await ReceiveAsync(stream, decoder, units, LinkType.Capabilities, timeout.Token).ConfigureAwait(false);

            var epoch = hello.Map()?.GetValueOrDefault(3UL) is byte[] { Length: 16 } epochBytes ? new Guid(epochBytes) : Guid.Empty;
            var ports = capabilities.Map()?.GetValueOrDefault(0UL) as List<object?> ?? [];
            ushort index = 0;
            SortedDictionary<ulong, object?>? port = null;
            for (var i = 0; i < ports.Count; i++)
            {
                if (ports[i] is SortedDictionary<ulong, object?> p && p.GetValueOrDefault(0UL) as string == options.PortId)
                {
                    index = (ushort)(i + 1);
                    port = p;
                }
            }
            if (port is null)
                throw new SerialTunnelException("ATSM-CH-UNKNOWN-PORT", $"agent does not expose port {options.PortId}");

            var ownership = (port.GetValueOrDefault(2UL) as ulong?) switch { 2 => OwnershipEnforcement.Enforced, 1 => OwnershipEnforcement.Advisory, _ => OwnershipEnforcement.None };
            var channelCapabilities = new ChannelCapabilities(ownership, port.GetValueOrDefault(6UL) is true, TraceCaptureLevel.None,
                port.GetValueOrDefault(4UL) is true, MessageBoundaries: false, (int)(port.GetValueOrDefault(5UL) as ulong? ?? 16 * 1024));
            var descriptor = new ChannelDescriptor(channelId, ChannelKind.SerialTunnel, $"atsm-link://{endpoint.Host}:{endpoint.Port}/{options.PortId}", port.GetValueOrDefault(1UL) as string);

            var channel = new SerialTunnelChannel(stream, client, options, index, epoch, descriptor, channelCapabilities);
            channel.Start(decoder, units);
            return channel;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask<ControlLease> AcquireAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var requestId = (ulong)Interlocked.Increment(ref _requestId);
        var reply = Register(_controlReplies, requestId);
        var deadline = (ulong)(request.Deadline?.TotalMilliseconds ?? 0);
        await SendAsync(LinkUnit.Control(LinkType.Acquire, _port, LinkCbor.Map((0, requestId), (1, (long)request.Priority), (2, deadline))), cancellationToken).ConfigureAwait(false);
        var granted = await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var grant = new Grant(
            (uint)(granted.GetValueOrDefault(1UL) as ulong? ?? 0),
            granted.GetValueOrDefault(2UL) as byte[] ?? new byte[16],
            TimeSpan.FromMilliseconds(granted.GetValueOrDefault(3UL) as ulong? ?? 15000),
            granted.GetValueOrDefault(5UL) is true,
            granted.GetValueOrDefault(6UL) as ulong? ?? 0);
        _grant = grant;
        _connection++;
        if (grant.UncertainTx || grant.UnownedRxBytes > 0)
            Publish(ChannelEventKind.Discontinuity, "ATSM-CH-TRANSFER-STATE", $"uncertainTx={grant.UncertainTx}, unownedRxBytes={grant.UnownedRxBytes}");
        Publish(ChannelEventKind.OwnershipGranted, null, $"generation {grant.Generation}");
        _ = RenewLoopAsync(request.ControllerId, grant);
        return Lease(request.ControllerId, grant);
    }

    public ValueTask<ControlLease> RenewAsync(ControlLease lease, CancellationToken cancellationToken) =>
        _grant is { } grant && grant.Generation == lease.Generation
            ? ValueTask.FromResult(lease with { GrantedAt = DateTimeOffset.UtcNow })
            : throw new SerialTunnelException("ATSM-CH-STALE-LEASE", "lease is not current");

    public async ValueTask<TransferAdmission> ConfirmTransferAsync(ControlLease lease, Guid transferId, CancellationToken cancellationToken)
    {
        if (_grant is not { } grant || grant.Generation != lease.Generation || new Guid(grant.TransferId) != transferId)
            return new TransferAdmission(TransferAdmissionState.Rejected, "ATSM-CH-STALE-LEASE");
        await SendAsync(LinkUnit.Control(LinkType.ConfirmTransfer, _port, LinkCbor.Map((1, (ulong)grant.Generation), (2, grant.TransferId))), cancellationToken).ConfigureAwait(false);
        return new TransferAdmission(TransferAdmissionState.Open);
    }

    public async ValueTask<ReleaseOutcome> ReleaseAsync(ControlLease lease, CancellationToken cancellationToken)
    {
        if (_grant is not { } grant || grant.Generation != lease.Generation)
            return ReleaseOutcome.NotOwner;
        _grant = null;
        try
        {
            await SendAsync(LinkUnit.Control(LinkType.Release, _port, LinkCbor.Map((1, (ulong)grant.Generation))), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return ReleaseOutcome.AlreadyExpired;
        }
        return ReleaseOutcome.Released;
    }

    public async ValueTask<ReadOutcome> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty)
        {
            if (!await _received.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) || !_received.Reader.TryRead(out _pending))
                return ReadOutcome.End;
        }
        var count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        return new ReadOutcome(count, EndOfStream: false);
    }

    public async ValueTask<WriteOutcome> WriteAsync(ReadOnlyMemory<byte> bytes, ControlLease lease, CancellationToken cancellationToken)
    {
        var writeId = Guid.CreateVersion7();
        var sequence = (ulong)Interlocked.Increment(ref _writeSequence);
        var reply = Register(_writeReplies, sequence);
        try
        {
            await SendAsync(LinkUnit.Serial(_port, sequence, 0, lease.Generation, bytes.Span), cancellationToken).ConfigureAwait(false);
            var status = await reply.Task.WaitAsync(_options.WriteStatusTimeout, cancellationToken).ConfigureAwait(false);
            var written = (int)(status.GetValueOrDefault(2UL) as ulong? ?? 0);
            var progress = (status.GetValueOrDefault(3UL) as ulong?) switch
            {
                0 => WriteProgress.Completed,
                1 => WriteProgress.Partial,
                3 => WriteProgress.Rejected,
                _ => WriteProgress.Unknown,
            };
            return new WriteOutcome(writeId, bytes.Length, written, progress, status.GetValueOrDefault(4UL) as string);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            _writeReplies.TryRemove(sequence, out _);
            return new WriteOutcome(writeId, bytes.Length, 0, WriteProgress.Unknown, "ATSM-CH-WRITE-STATUS-LOST");
        }
    }

    /// <summary>Asks the current owner to yield the port. The requester must already be queued (AcquireAsync pending).</summary>
    public async Task<RemoteUsbHandoverResult> RequestHandoverAsync(string? reason, CancellationToken cancellationToken)
    {
        var map = await ControlRequestAsync(LinkType.RequestHandover, reason is null ? [] : LinkCbor.Map((1, reason)), cancellationToken).ConfigureAwait(false);
        return new RemoteUsbHandoverResult(map.GetValueOrDefault(1UL) is true, map.GetValueOrDefault(2UL) as string);
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

    /// <summary>Semaphore and security events of this port (handover, kill switch, contest, secure close).</summary>
    public async IAsyncEnumerable<RemoteUsbEvent> SecurityEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _securityEvents.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    private async Task<SortedDictionary<ulong, object?>> ControlRequestAsync(LinkType type, SortedDictionary<ulong, object?> fields, CancellationToken cancellationToken)
    {
        var requestId = (ulong)Interlocked.Increment(ref _requestId);
        var reply = Register(_controlReplies, requestId);
        fields[0] = requestId;
        await SendAsync(LinkUnit.Control(type, _port, fields), cancellationToken).ConfigureAwait(false);
        return await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChannelEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var channelEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return channelEvent;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _owner.Dispose();
        await Task.WhenAll(_readLoop, _heartbeatLoop).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        _sendGate.Dispose();
        _stop.Dispose();
    }

    private void Start(LinkDecoder handshakeDecoder, List<LinkUnit> leftover)
    {
        foreach (var unit in leftover)
            Dispatch(unit);
        _readLoop = Task.Run(() => ReadLoopAsync(handshakeDecoder));
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
                    Publish(ChannelEventKind.Faulted, "ATSM-CH-BAD-UNIT", decoder.Error.ToString());
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
            _received.Writer.TryComplete();
            _securityEvents.Writer.TryComplete();
            Publish(ChannelEventKind.Closed, null, "agent connection closed");
            foreach (var reply in _controlReplies.Values.Concat(_writeReplies.Values))
                reply.TrySetException(new IOException(_closeReason ?? "Agent connection closed."));
        }
    }

    private void Dispatch(LinkUnit unit)
    {
        if (unit.Type == LinkType.SerialData)
        {
            if (unit.Channel == _port && unit.SerialPayload() is { } data)
                _received.Writer.TryWrite(data.Data);
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
                var code = map.GetValueOrDefault(1UL) as string ?? "ATSM-CH-DENIED";
                denied.TrySetException(new SerialTunnelException(code, "acquire denied by the agent"));
                break;
            case LinkType.Queued:
                Publish(ChannelEventKind.OwnershipQueued, "ATSM-CH-QUEUED", $"position {map.GetValueOrDefault(1UL)}");
                break;
            case LinkType.Revoked:
                if (_grant is { } grant && (map.GetValueOrDefault(1UL) as ulong?) == grant.Generation)
                    _grant = null;
                Publish(ChannelEventKind.OwnershipRevoked, map.GetValueOrDefault(2UL) as string, null);
                break;
            case LinkType.WriteStatus when _writeReplies.TryRemove(requestId, out var status):
                status.TrySetResult(map);
                break;
            case LinkType.PortEvent:
                Publish(ChannelEventKind.Faulted, map.GetValueOrDefault(1UL) as string, map.GetValueOrDefault(2UL) as string);
                break;
            case LinkType.HandoverResult or LinkType.SecurityResult when _controlReplies.TryRemove(requestId, out var result):
                result.TrySetResult(map);
                break;
            case LinkType.HandoverAsked when unit.Channel == _port:
                var asked = new RemoteUsbHandoverRequest(requestId, map.GetValueOrDefault(1UL) as string ?? "", map.GetValueOrDefault(2UL) as string,
                    TimeSpan.FromMilliseconds(map.GetValueOrDefault(3UL) as ulong? ?? 0));
                var yield = _options.HandoverDecision?.Invoke(asked) ?? false;
                _ = SendAsync(LinkUnit.Control(LinkType.HandoverAnswer, _port, LinkCbor.Map((0, asked.HandoverId), (1, yield))), _stop.Token)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
                break;
            case LinkType.SecurityEvent when unit.Channel == _port:
                _securityEvents.Writer.TryWrite(new RemoteUsbEvent(requestId,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(map.GetValueOrDefault(9UL) as ulong? ?? 0)), map.GetValueOrDefault(1UL) as string ?? "",
                    map.GetValueOrDefault(2UL) as string, map.GetValueOrDefault(3UL) as string, map.GetValueOrDefault(4UL) as string,
                    (uint)(map.GetValueOrDefault(5UL) as ulong? ?? 0), map.GetValueOrDefault(6UL) as string,
                    map.GetValueOrDefault(7UL) is ulong until ? DateTimeOffset.FromUnixTimeMilliseconds((long)until) : null, map.GetValueOrDefault(8UL) is true, null));
                break;
            case LinkType.Error when map.GetValueOrDefault(1UL) is ulong failed && failed != 0 && _controlReplies.TryRemove(failed, out var refused):
                refused.TrySetException(new SerialTunnelException(map.GetValueOrDefault(0UL) as string ?? "ATSM-CH-AGENT-ERROR", map.GetValueOrDefault(2UL) as string ?? ""));
                break;
            case LinkType.Error:
                _closeReason = $"Agent error {map.GetValueOrDefault(0UL)}: {map.GetValueOrDefault(2UL)}";
                Publish(ChannelEventKind.Faulted, map.GetValueOrDefault(0UL) as string, map.GetValueOrDefault(2UL) as string);
                break;
        }
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

    private async Task RenewLoopAsync(string controllerId, Grant grant)
    {
        try
        {
            var interval = grant.Lease / 3;
            while (!_stop.IsCancellationRequested && _grant == grant)
            {
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                if (_grant != grant)
                    break;
                var requestId = (ulong)Interlocked.Increment(ref _requestId);
                var reply = Register(_controlReplies, requestId);
                await SendAsync(LinkUnit.Control(LinkType.Renew, _port, LinkCbor.Map((0, requestId), (1, (ulong)grant.Generation))), _stop.Token).ConfigureAwait(false);
                await reply.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SerialTunnelException)
        {
            if (_grant == grant)
            {
                _grant = null;
                Publish(ChannelEventKind.OwnershipRevoked, "ATSM-CH-RENEW-FAILED", $"{controllerId} generation {grant.Generation}: {exception.Message}");
            }
        }
    }

    private ControlLease Lease(string controllerId, Grant grant) => new(
        Descriptor.ChannelId, controllerId, AgentEpoch, _connection, grant.Generation, new Guid(grant.TransferId),
        OwnershipEnforcement.Enforced, DateTimeOffset.UtcNow, grant.Lease);

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
                throw new SerialTunnelException(error.Map()?.GetValueOrDefault(0UL) as string ?? "ATSM-CH-AGENT-ERROR", "agent refused the handshake");
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new IOException("Agent closed the connection during the handshake.");
            if (decoder.Feed(buffer.AsSpan(0, count), units) != LinkDecodeError.None)
                throw new SerialTunnelException("ATSM-CH-BAD-UNIT", decoder.Error.ToString());
        }
    }

    private void Publish(ChannelEventKind kind, string? code, string? detail) =>
        _events.Writer.TryWrite(new ChannelEvent(kind, DateTimeOffset.UtcNow, code, detail));

    private sealed record Grant(uint Generation, byte[] TransferId, TimeSpan Lease, bool UncertainTx, ulong UnownedRxBytes);
}
