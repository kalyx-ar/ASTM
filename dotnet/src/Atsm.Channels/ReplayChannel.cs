using System.Runtime.CompilerServices;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Tracing;
using Atsm.Channels.Ownership;

namespace Atsm.Channels;

/// <summary>
/// Feeds the received bytes of a captured trace to the engine. It never opens a physical port: every write
/// is only recorded in <see cref="Transmitted"/>, so a replay cannot have external effects.
/// </summary>
public sealed class ReplayChannel : IInstrumentChannel
{
    private readonly Queue<ReadOnlyMemory<byte>> _pending;
    private readonly LocalLeaseArbiter _arbiter;
    private readonly List<byte[]> _transmitted = [];
    private readonly Func<int, ValueTask>? _beforeRead;
    private int _reads;

    /// <param name="beforeRead">Optional hook to pace the replay (for example, wait until the engine answered).</param>
    public ReplayChannel(string channelId, IEnumerable<TraceEntry> entries, Func<int, ValueTask>? beforeRead = null)
    {
        Descriptor = new ChannelDescriptor(channelId, ChannelKind.Replay, "replay://" + channelId);
        _pending = new(entries.Where(e => e.Direction == TraceDirection.Received && !e.Data.IsEmpty).Select(e => e.Data));
        _arbiter = new LocalLeaseArbiter(channelId, OwnershipEnforcement.Advisory);
        _beforeRead = beforeRead;
    }

    public ChannelDescriptor Descriptor { get; }

    public ChannelCapabilities Capabilities { get; } = ChannelCapabilities.Stream(OwnershipEnforcement.Advisory);

    public IReadOnlyList<byte[]> Transmitted
    {
        get
        {
            lock (_transmitted)
                return _transmitted.ToArray();
        }
    }

    public ValueTask<ControlLease> AcquireAsync(ControlRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(_arbiter.Acquire(request));

    public ValueTask<ControlLease> RenewAsync(ControlLease lease, CancellationToken cancellationToken) => ValueTask.FromResult(_arbiter.Renew(lease));

    public ValueTask<TransferAdmission> ConfirmTransferAsync(ControlLease lease, Guid transferId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new TransferAdmission(TransferAdmissionState.Open));

    public ValueTask<ReleaseOutcome> ReleaseAsync(ControlLease lease, CancellationToken cancellationToken) => ValueTask.FromResult(_arbiter.Release(lease));

    public async ValueTask<ReadOutcome> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_beforeRead is not null)
            await _beforeRead(_reads).ConfigureAwait(false);
        if (!_pending.TryPeek(out var next))
            return ReadOutcome.End;
        _reads++;
        var count = Math.Min(buffer.Length, next.Length);
        next[..count].CopyTo(buffer);
        _pending.Dequeue();
        if (count < next.Length)
        {
            var rest = new Queue<ReadOnlyMemory<byte>>([next[count..], .. _pending]);
            _pending.Clear();
            foreach (var item in rest)
                _pending.Enqueue(item);
        }
        return new ReadOutcome(count, EndOfStream: false);
    }

    public ValueTask<WriteOutcome> WriteAsync(ReadOnlyMemory<byte> bytes, ControlLease lease, CancellationToken cancellationToken)
    {
        lock (_transmitted)
            _transmitted.Add(bytes.ToArray());
        return ValueTask.FromResult(new WriteOutcome(Guid.CreateVersion7(), bytes.Length, bytes.Length, WriteProgress.Completed));
    }

    public async IAsyncEnumerable<ChannelEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
