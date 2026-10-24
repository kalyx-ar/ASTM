namespace Atsm.Abstractions.Channels;

/// <summary>
/// Ordered, bidirectional byte stream to one instrument, with declared capabilities and events.
/// A read may hold part of a frame, one frame or several; framing belongs to the protocol layer.
/// </summary>
public interface IInstrumentChannel : IAsyncDisposable
{
    ChannelDescriptor Descriptor { get; }

    ChannelCapabilities Capabilities { get; }

    ValueTask<ControlLease> AcquireAsync(ControlRequest request, CancellationToken cancellationToken);

    ValueTask<ControlLease> RenewAsync(ControlLease lease, CancellationToken cancellationToken);

    ValueTask<TransferAdmission> ConfirmTransferAsync(ControlLease lease, Guid transferId, CancellationToken cancellationToken);

    ValueTask<ReleaseOutcome> ReleaseAsync(ControlLease lease, CancellationToken cancellationToken);

    ValueTask<ReadOutcome> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask<WriteOutcome> WriteAsync(ReadOnlyMemory<byte> bytes, ControlLease lease, CancellationToken cancellationToken);

    IAsyncEnumerable<ChannelEvent> EventsAsync(CancellationToken cancellationToken);
}
