using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Atsm.Abstractions.Channels;
using Atsm.Channels.Ownership;

namespace Atsm.Channels;

/// <summary>
/// Channel over any bidirectional <see cref="Stream"/> (socket, TLS, serial). Ownership is enforced only
/// inside this process; capabilities say so.
/// </summary>
public class StreamInstrumentChannel : IInstrumentChannel
{
    private readonly Stream _stream;
    private readonly LocalLeaseArbiter _arbiter;
    private readonly TimeProvider _clock;
    private readonly Channel<ChannelEvent> _events = Channel.CreateUnbounded<ChannelEvent>();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ulong _offset;

    public StreamInstrumentChannel(ChannelDescriptor descriptor, Stream stream, ChannelCapabilities? capabilities = null, TimeProvider? clock = null)
    {
        Descriptor = descriptor;
        _stream = stream;
        _clock = clock ?? TimeProvider.System;
        Capabilities = capabilities ?? ChannelCapabilities.Stream(OwnershipEnforcement.Advisory);
        _arbiter = new LocalLeaseArbiter(descriptor.ChannelId, Capabilities.Ownership, _clock);
        Publish(ChannelEventKind.Opened);
    }

    public ChannelDescriptor Descriptor { get; }

    public ChannelCapabilities Capabilities { get; }

    public ValueTask<ControlLease> AcquireAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var lease = _arbiter.Acquire(request);
        Publish(ChannelEventKind.OwnershipGranted, detail: $"{lease.ControllerId} generation {lease.Generation}");
        return ValueTask.FromResult(lease);
    }

    public ValueTask<ControlLease> RenewAsync(ControlLease lease, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_arbiter.Renew(lease));

    public ValueTask<TransferAdmission> ConfirmTransferAsync(ControlLease lease, Guid transferId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_arbiter.IsCurrent(lease) && lease.TransferId == transferId
            ? new TransferAdmission(TransferAdmissionState.Open)
            : new TransferAdmission(TransferAdmissionState.Rejected, "ATSM-CH-STALE-LEASE"));

    public ValueTask<ReleaseOutcome> ReleaseAsync(ControlLease lease, CancellationToken cancellationToken)
    {
        var outcome = _arbiter.Release(lease);
        if (outcome == ReleaseOutcome.Released)
            Publish(ChannelEventKind.OwnershipRevoked, detail: "released");
        return ValueTask.FromResult(outcome);
    }

    public async ValueTask<ReadOutcome> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            var count = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                Publish(ChannelEventKind.Closed, detail: "end of stream");
                return ReadOutcome.End;
            }
            var offset = _offset;
            _offset += (ulong)count;
            return new ReadOutcome(count, EndOfStream: false, StreamOffset: offset);
        }
        catch (IOException exception)
        {
            Publish(ChannelEventKind.Faulted, "ATSM-CH-READ-FAILED", exception.Message);
            return ReadOutcome.End;
        }
    }

    public async ValueTask<WriteOutcome> WriteAsync(ReadOnlyMemory<byte> bytes, ControlLease lease, CancellationToken cancellationToken)
    {
        var writeId = Guid.CreateVersion7();
        if (!_arbiter.IsCurrent(lease))
            return new WriteOutcome(writeId, bytes.Length, 0, WriteProgress.Rejected, "ATSM-CH-STALE-LEASE");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new WriteOutcome(writeId, bytes.Length, bytes.Length, WriteProgress.Completed);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The stream API does not report how many bytes left before the failure.
            Publish(ChannelEventKind.Faulted, "ATSM-CH-WRITE-FAILED", exception.Message);
            return new WriteOutcome(writeId, bytes.Length, 0, WriteProgress.Unknown, "ATSM-CH-WRITE-FAILED");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<ChannelEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var channelEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return channelEvent;
    }

    public virtual async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
    }

    protected void Publish(ChannelEventKind kind, string? code = null, string? detail = null) =>
        _events.Writer.TryWrite(new ChannelEvent(kind, _clock.GetUtcNow(), code, detail));
}
