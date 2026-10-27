using Atsm.Abstractions.Tracing;

namespace Atsm.Tracing;

/// <summary>
/// Feeds the received bytes of a captured trace to a session as its transport. It never opens a physical port: every
/// write is only recorded in <see cref="Transmitted"/>, so a replay cannot have external effects.
/// </summary>
public sealed class ReplayStream : Stream
{
    private readonly Queue<ReadOnlyMemory<byte>> _pending;
    private readonly List<byte[]> _transmitted = [];
    private readonly Func<int, ValueTask>? _beforeRead;
    private int _reads;

    /// <param name="beforeRead">Optional hook to pace the replay (for example, wait until the engine answered).</param>
    public ReplayStream(IEnumerable<TraceEntry> entries, Func<int, ValueTask>? beforeRead = null)
    {
        _pending = new(entries.Where(e => e.Direction == TraceDirection.Received && !e.Data.IsEmpty).Select(e => e.Data));
        _beforeRead = beforeRead;
    }

    public IReadOnlyList<byte[]> Transmitted
    {
        get
        {
            lock (_transmitted)
                return _transmitted.ToArray();
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_beforeRead is not null)
            await _beforeRead(_reads).ConfigureAwait(false);
        if (!_pending.TryPeek(out var next))
            return 0;
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
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_transmitted)
            _transmitted.Add(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
