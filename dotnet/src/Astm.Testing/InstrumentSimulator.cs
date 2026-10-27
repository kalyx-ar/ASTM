using System.Text;
using System.Threading.Channels;
using Astm.Protocol.Lis01;

namespace Astm.Testing;

/// <summary>
/// Plays the instrument side of LIS01-A2 over a stream, with real timers. Use it to drive a host session end to end
/// without hardware. Records are given as text and sent one record per message (ETX), as most analyzers do.
/// </summary>
public sealed class InstrumentSimulator : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly TimeProvider _clock;
    private readonly Lis01Link _link;
    private readonly Encoding _encoding;
    private readonly Channel<Action<List<Lis01Action>, TimeSpan>> _inputs = Channel.CreateUnbounded<Action<List<Lis01Action>, TimeSpan>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
    private readonly Dictionary<long, TaskCompletionSource<Lis01SendCompleted>> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    public InstrumentSimulator(Stream stream, Lis01Options? options = null, Encoding? encoding = null, TimeProvider? clock = null)
    {
        _stream = stream;
        _clock = clock ?? TimeProvider.System;
        _encoding = encoding ?? Encoding.Latin1;
        _link = new Lis01Link(Lis01Role.Instrument, options);
        _run = Task.Run(RunAsync);
    }

    /// <summary>Text of every complete transmission received from the host (records joined, CR terminated).</summary>
    public ChannelReader<string> Received => _received.Reader;

    public List<Lis01Notice> Notices { get; } = [];

    public Task<Lis01SendCompleted> SendAsync(params string[] records)
        => SendMessagesAsync(records.Select(r => (ReadOnlyMemory<byte>)_encoding.GetBytes(r.EndsWith('\r') ? r : r + "\r")).ToArray());

    /// <summary>Already framed messages of a profile; supports frame-per-record and frame-per-message without changing their contents.</summary>
    public Task<Lis01SendCompleted> SendMessagesAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads)
    {
        var completion = new TaskCompletionSource<Lis01SendCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = payloads.Select(p => (ReadOnlyMemory<byte>)p.ToArray()).ToList();
        if (!_inputs.Writer.TryWrite((output, now) =>
        {
            _pending[_link.Enqueue(messages)] = completion;
            _link.Poll(now, output);
        })) completion.TrySetException(new ObjectDisposedException(nameof(InstrumentSimulator)));
        return completion.Task;
    }

    /// <summary>Writes raw bytes, bypassing the link (to inject noise or malformed frames).</summary>
    public Task WriteRawAsync(byte[] bytes) => _stream.WriteAsync(bytes).AsTask();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _inputs.Writer.TryComplete();
        await _run.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var start = _clock.GetTimestamp();
        using var timer = _clock.CreateTimer(_ => _inputs.Writer.TryWrite((output, now) => _link.Poll(now, output)), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[1024];
            while (!_stop.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = await _stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (count == 0)
                    break;
                var data = buffer.AsSpan(0, count).ToArray();
                _inputs.Writer.TryWrite((output, now) => _link.OnBytes(data, now, output));
            }
        });

        var actions = new List<Lis01Action>();
        try
        {
            await foreach (var input in _inputs.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                actions.Clear();
                input(actions, _clock.GetElapsedTime(start));
                foreach (var action in actions)
                {
                    switch (action)
                    {
                        case Lis01Write write:
                            await _stream.WriteAsync(write.Bytes, _stop.Token).ConfigureAwait(false);
                            break;
                        case Lis01Received received when received.Complete:
                            _received.Writer.TryWrite(string.Concat(received.Messages.Select(m => _encoding.GetString(m))));
                            break;
                        case Lis01SendCompleted completed:
                            if (_pending.Remove(completed.TransmissionId, out var completion))
                                completion.TrySetResult(completed);
                            break;
                        case Lis01Notice notice:
                            lock (Notices)
                                Notices.Add(notice);
                            break;
                    }
                }
                var deadline = _link.NextDeadline;
                var due = deadline is { } d ? d - _clock.GetElapsedTime(start) : Timeout.InfiniteTimeSpan;
                timer.Change(deadline is null ? Timeout.InfiniteTimeSpan : due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _received.Writer.TryComplete();
            foreach (var completion in _pending.Values)
                completion.TrySetCanceled();
            await reader.ConfigureAwait(false);
        }
    }
}
