using System.Threading.Channels;

namespace Astm.Testing;

/// <summary>Two connected in-memory streams: bytes written to one are read from the other, preserving write boundaries.</summary>
public sealed class DuplexStream : Stream
{
    private readonly ChannelReader<byte[]> _input;
    private readonly ChannelWriter<byte[]> _output;
    private byte[] _current = [];
    private int _position;

    private DuplexStream(ChannelReader<byte[]> input, ChannelWriter<byte[]> output)
    {
        _input = input;
        _output = output;
    }

    public static (DuplexStream A, DuplexStream B) CreatePair()
    {
        var aToB = Channel.CreateUnbounded<byte[]>();
        var bToA = Channel.CreateUnbounded<byte[]>();
        return (new DuplexStream(bToA.Reader, aToB.Writer), new DuplexStream(aToB.Reader, bToA.Writer));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position == _current.Length)
        {
            try
            {
                _current = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
            _position = 0;
        }
        var count = Math.Min(buffer.Length, _current.Length - _position);
        _current.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_output.TryWrite(buffer.ToArray()))
            throw new IOException("The other side is closed.");
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _output.TryComplete();
        base.Dispose(disposing);
    }
}
