using static Astm.Protocol.Lis01.ControlCharacters;

namespace Astm.Protocol.Lis01;

public enum Lis01State { Idle, Establishing, Sending, WaitingToRetry, Contention, Receiving }

/// <summary>
/// CLSI LIS01-A2 data link as a pure state machine. It performs no I/O and owns no timers:
/// the caller feeds received bytes and the current monotonic time, executes the returned actions in order,
/// and calls <see cref="Poll"/> when <see cref="NextDeadline"/> is reached or after enqueuing a transmission.
/// Not thread-safe: drive one instance from a single logical thread.
/// </summary>
public sealed class Lis01Link
{
    private readonly Lis01Options _options;
    private readonly Queue<Transmission> _queue = new();
    private long _nextTransmissionId = 1;

    private Transmission? _current;
    private int _frameIndex;
    private int _retransmissions;
    private bool _retryAfterContention;

    private readonly List<byte> _frame = new(Lis01Frame.MaxFrameLength);
    private bool _inFrame;
    private int _expectedFrameNumber;
    private readonly List<byte[]> _receivedMessages = [];
    private readonly List<byte> _messageBuffer = [];

    public Lis01Link(Lis01Role role, Lis01Options? options = null)
    {
        _options = options ?? Lis01Options.Default;
        _options.Validate();
        Role = role;
    }

    public Lis01Role Role { get; }

    public Lis01State State { get; private set; }

    /// <summary>Monotonic time at which <see cref="Poll"/> must be called, or null when no timer is armed.</summary>
    public TimeSpan? NextDeadline { get; private set; }

    public int PendingTransmissions => _queue.Count + (_current is null ? 0 : 1);

    /// <summary>Queues a transmission. Each message is sent as ETB-chained frames ending with ETX.</summary>
    public long Enqueue(IReadOnlyList<ReadOnlyMemory<byte>> messages)
    {
        var frames = Lis01Frame.BuildTransmission(messages, _options.MaxTextLength);
        var transmission = new Transmission(_nextTransmissionId++, frames);
        _queue.Enqueue(transmission);
        return transmission.Id;
    }

    /// <summary>Removes a transmission that has not started. A transmission in progress cannot be withdrawn.</summary>
    public bool Cancel(long transmissionId, List<Lis01Action> output)
    {
        if (_current?.Id == transmissionId && State is Lis01State.Sending)
            return false;
        if (_current?.Id == transmissionId)
        {
            var establishing = State is Lis01State.Establishing;
            Complete(Lis01SendResult.Failed, Lis01Codes.Cancelled, output);
            if (State is Lis01State.Receiving)
                return true;
            if (establishing)
                output.Add(new Lis01Write([Eot]));
            ToIdle();
            return true;
        }
        var remaining = _queue.Where(t => t.Id != transmissionId).ToList();
        if (remaining.Count == _queue.Count)
            return false;
        _queue.Clear();
        foreach (var transmission in remaining)
            _queue.Enqueue(transmission);
        output.Add(new Lis01SendCompleted(transmissionId, Lis01SendResult.Failed, 0, 0, Lis01Codes.Cancelled));
        return true;
    }

    /// <summary>Handles an expired deadline and starts the next queued transmission when the link is neutral.</summary>
    public void Poll(TimeSpan now, List<Lis01Action> output)
    {
        if (NextDeadline is { } deadline && now >= deadline)
        {
            NextDeadline = null;
            OnTimeout(now, output);
        }
        StartIfIdle(now, output);
    }

    public void OnBytes(ReadOnlySpan<byte> data, TimeSpan now, List<Lis01Action> output)
    {
        var noise = 0;
        foreach (var value in data)
        {
            if (!OnByte(value, now, output))
                noise++;
        }
        if (noise > 0)
            output.Add(new Lis01Notice(Lis01Codes.Noise, $"{noise} byte(s) ignored in state {State}"));
        StartIfIdle(now, output);
    }

    private bool OnByte(byte value, TimeSpan now, List<Lis01Action> output)
    {
        switch (State)
        {
            case Lis01State.Idle:
                if (value != Enq)
                    return false;
                AcceptEstablishment(now, output);
                return true;

            case Lis01State.Establishing:
                switch (value)
                {
                    case Ack:
                        State = Lis01State.Sending;
                        _frameIndex = 0;
                        _retransmissions = 0;
                        SendCurrentFrame(now, output);
                        return true;
                    case Nak:
                        output.Add(new Lis01Notice(Lis01Codes.ReceiverBusy));
                        WaitToRetry(now, _options.BusyRetryDelay, afterContention: false);
                        return true;
                    case Enq:
                        output.Add(new Lis01Notice(Lis01Codes.Contention, $"role {Role}"));
                        if (Role == Lis01Role.Host)
                        {
                            State = Lis01State.Contention;
                            NextDeadline = now + _options.ContentionTimeout;
                        }
                        else
                        {
                            WaitToRetry(now, _options.ContentionRetryDelay, afterContention: true);
                        }
                        return true;
                    default:
                        return false;
                }

            case Lis01State.Contention:
            case Lis01State.WaitingToRetry when !(Role == Lis01Role.Instrument && _retryAfterContention):
                if (value != Enq)
                    return false;
                AcceptEstablishment(now, output);
                return true;

            case Lis01State.WaitingToRetry:
                return false;

            case Lis01State.Sending:
                OnSenderReply(value, now, output);
                return true;

            case Lis01State.Receiving:
                return OnReceiverByte(value, now, output);

            default:
                return false;
        }
    }

    private void OnSenderReply(byte value, TimeSpan now, List<Lis01Action> output)
    {
        var transmission = _current!;
        if (value is Ack or Eot)
        {
            _frameIndex++;
            _retransmissions = 0;
            if (_frameIndex == transmission.Frames.Count)
            {
                output.Add(new Lis01Write([Eot]));
                Complete(Lis01SendResult.Delivered, null, output);
                ToIdle();
            }
            else if (value == Eot)
            {
                // Receiver interrupt: the frame counts as accepted and the sender terminates.
                output.Add(new Lis01Notice(Lis01Codes.ReceiverInterrupt, $"after frame {_frameIndex} of {transmission.Frames.Count}"));
                output.Add(new Lis01Write([Eot]));
                Complete(Lis01SendResult.Interrupted, Lis01Codes.ReceiverInterrupt, output);
                ToIdle();
            }
            else
            {
                SendCurrentFrame(now, output);
            }
            return;
        }

        // Anything other than ACK or EOT is treated as NAK.
        _retransmissions++;
        output.Add(new Lis01Notice(Lis01Codes.FrameRejected, $"frame {_frameIndex + 1}, reply 0x{value:X2}, attempt {_retransmissions}"));
        if (_retransmissions > _options.MaxRetransmissions)
        {
            output.Add(new Lis01Write([Eot]));
            Complete(Lis01SendResult.Failed, Lis01Codes.RetransmissionsExhausted, output);
            ToIdle();
            return;
        }
        SendCurrentFrame(now, output);
    }

    private bool OnReceiverByte(byte value, TimeSpan now, List<Lis01Action> output)
    {
        if (!_inFrame)
        {
            switch (value)
            {
                case Stx:
                    _inFrame = true;
                    _frame.Clear();
                    return true;
                case Eot:
                    FinishReception(complete: true, now, output);
                    return true;
                default:
                    return false;
            }
        }

        if (value == Stx)
        {
            output.Add(new Lis01Notice(Lis01Codes.InvalidFrame, "STX inside a frame; restarting frame"));
            _frame.Clear();
            return true;
        }

        _frame.Add(value);
        if (_frame.Count > Math.Max(_options.MaxTextLength, Lis01Frame.MaxTextLength) + Lis01Frame.Overhead - 1)
        {
            output.Add(new Lis01Notice(Lis01Codes.FrameTooLong, $"{_frame.Count} bytes without terminator"));
            RejectFrame(now, output);
            return true;
        }
        if (value != Lf)
            return true;

        var parsed = Lis01Frame.Parse(_frame.ToArray(), _options.AcceptLowercaseChecksum, _options.StrictTextCharacters);
        _inFrame = false;
        if (!parsed.IsValid)
        {
            output.Add(new Lis01Notice(Lis01Codes.InvalidFrame, parsed.Error.ToString()));
            RejectFrame(now, output);
            return true;
        }
        if (parsed.FrameNumber != _expectedFrameNumber)
        {
            output.Add(new Lis01Notice(Lis01Codes.UnexpectedFrameNumber, $"expected {_expectedFrameNumber}, received {parsed.FrameNumber}"));
            RejectFrame(now, output);
            return true;
        }

        _messageBuffer.AddRange(parsed.Text.Span);
        if (parsed.IsFinal)
        {
            _receivedMessages.Add([.. _messageBuffer]);
            _messageBuffer.Clear();
        }
        _expectedFrameNumber = (_expectedFrameNumber + 1) % 8;
        output.Add(new Lis01Write([Ack]));
        NextDeadline = now + _options.ReceiverTimeout;
        return true;
    }

    private void RejectFrame(TimeSpan now, List<Lis01Action> output)
    {
        _inFrame = false;
        _frame.Clear();
        output.Add(new Lis01Write([Nak]));
        NextDeadline = now + _options.ReceiverTimeout;
    }

    private void OnTimeout(TimeSpan now, List<Lis01Action> output)
    {
        switch (State)
        {
            case Lis01State.Establishing:
                output.Add(new Lis01Notice(Lis01Codes.EstablishmentTimeout, $"attempt {_current!.EstablishmentAttempts}"));
                output.Add(new Lis01Write([Eot]));
                if (_current.EstablishmentAttempts >= _options.MaxEstablishmentAttempts)
                {
                    Complete(Lis01SendResult.Failed, Lis01Codes.EstablishmentExhausted, output);
                    ToIdle();
                }
                else
                {
                    WaitToRetry(now, _options.BusyRetryDelay, afterContention: false);
                }
                break;

            case Lis01State.Sending:
                output.Add(new Lis01Notice(Lis01Codes.FrameAckTimeout, $"frame {_frameIndex + 1}"));
                output.Add(new Lis01Write([Eot]));
                Complete(Lis01SendResult.Failed, Lis01Codes.FrameAckTimeout, output);
                ToIdle();
                break;

            case Lis01State.WaitingToRetry:
                SendEnq(now, output);
                break;

            case Lis01State.Contention:
                output.Add(new Lis01Notice(Lis01Codes.ContentionTimeout));
                State = _current is null ? Lis01State.Idle : Lis01State.WaitingToRetry;
                if (_current is not null)
                    SendEnq(now, output);
                break;

            case Lis01State.Receiving:
                output.Add(new Lis01Notice(Lis01Codes.ReceiverTimeout, _inFrame ? "inside a frame" : "between frames"));
                FinishReception(complete: false, now, output);
                break;
        }
    }

    private void StartIfIdle(TimeSpan now, List<Lis01Action> output)
    {
        if (State != Lis01State.Idle)
            return;
        if (_current is null && !_queue.TryDequeue(out _current))
            return;
        SendEnq(now, output);
    }

    private void SendEnq(TimeSpan now, List<Lis01Action> output)
    {
        _current!.EstablishmentAttempts++;
        State = Lis01State.Establishing;
        NextDeadline = now + _options.EstablishmentTimeout;
        output.Add(new Lis01Write([Enq]));
    }

    private void WaitToRetry(TimeSpan now, TimeSpan delay, bool afterContention)
    {
        State = Lis01State.WaitingToRetry;
        _retryAfterContention = afterContention;
        NextDeadline = now + delay;
    }

    private void AcceptEstablishment(TimeSpan now, List<Lis01Action> output)
    {
        // A pending transmission keeps its attempts and is resumed after this reception.
        State = Lis01State.Receiving;
        _inFrame = false;
        _frame.Clear();
        _expectedFrameNumber = 1;
        _receivedMessages.Clear();
        _messageBuffer.Clear();
        NextDeadline = now + _options.ReceiverTimeout;
        output.Add(new Lis01Write([Ack]));
    }

    private void FinishReception(bool complete, TimeSpan now, List<Lis01Action> output)
    {
        var partial = _messageBuffer.Count > 0 || (_inFrame && _frame.Count > 0);
        if (_messageBuffer.Count > 0)
            _receivedMessages.Add([.. _messageBuffer]);
        if (partial)
            output.Add(new Lis01Notice(Lis01Codes.IncompleteMessage));
        if (_receivedMessages.Count > 0)
            output.Add(new Lis01Received(_receivedMessages.ToArray(), complete && !partial));
        else if (complete)
            output.Add(new Lis01Notice(Lis01Codes.EmptyTransmission));
        _receivedMessages.Clear();
        _messageBuffer.Clear();
        _frame.Clear();
        _inFrame = false;
        State = Lis01State.Idle;
        NextDeadline = null;
        if (_current is not null)
        {
            // Resume the interrupted establishment of our own transmission.
            State = Lis01State.WaitingToRetry;
            _retryAfterContention = false;
            NextDeadline = now;
        }
    }

    private void SendCurrentFrame(TimeSpan now, List<Lis01Action> output)
    {
        output.Add(new Lis01Write(_current!.Frames[_frameIndex]));
        NextDeadline = now + _options.FrameAckTimeout;
    }

    private void Complete(Lis01SendResult result, string? code, List<Lis01Action> output)
    {
        var transmission = _current!;
        output.Add(new Lis01SendCompleted(transmission.Id, result, _frameIndex, transmission.Frames.Count, code));
        _current = null;
    }

    private void ToIdle()
    {
        State = Lis01State.Idle;
        NextDeadline = null;
        _frameIndex = 0;
        _retransmissions = 0;
    }

    private sealed class Transmission(long id, List<byte[]> frames)
    {
        public long Id { get; } = id;

        public List<byte[]> Frames { get; } = frames;

        public int EstablishmentAttempts { get; set; }
    }
}
