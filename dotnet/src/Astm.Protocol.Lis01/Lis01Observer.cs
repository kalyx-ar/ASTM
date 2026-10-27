using static Astm.Protocol.Lis01.ControlCharacters;

namespace Astm.Protocol.Lis01;

public enum Lis01ObservationKind
{
    Enquiry,
    Established,
    Busy,
    Contention,
    Frame,
    InvalidFrame,
    FrameAccepted,
    FrameRejected,
    Retransmission,
    ImplicitAcknowledge,
    AlteredByRecorder,
    ReceiverInterrupt,
    TransmissionEnd,
    Noise,
}

/// <param name="Side">Side that wrote the bytes of this observation.</param>
/// <param name="Messages">For <see cref="Lis01ObservationKind.TransmissionEnd"/>: text of each message up to ETX.</param>
public sealed record Lis01Observation(
    Lis01ObservationKind Kind,
    Lis01Role Side,
    long Sequence,
    int FrameNumber = -1,
    bool Final = false,
    string? Detail = null,
    IReadOnlyList<byte[]>? Messages = null,
    bool Complete = true,
    long FirstSequence = -1);

/// <summary>
/// Passive LIS01-A2 decoder for both directions of a recorded conversation. It never answers: it reports what each side
/// did (enquiries, frames, acknowledgements, retransmissions, terminations) and reassembles the messages that the receiver
/// accepted. When a log lost an acknowledgement, a new frame from the sender implicitly accepts the previous one.
/// </summary>
public sealed class Lis01Observer
{
    private readonly Dictionary<Lis01Role, List<byte>> _frames = new() { [Lis01Role.Host] = [], [Lis01Role.Instrument] = [] };
    private readonly Dictionary<Lis01Role, bool> _inFrame = new() { [Lis01Role.Host] = false, [Lis01Role.Instrument] = false };
    private readonly List<byte[]> _messages = [];
    private readonly List<byte> _buffer = [];
    private Lis01Role? _enquiring;
    private Lis01Role? _sender;
    private long _transmissionStart = -1;
    private Lis01ParsedFrame? _pending;
    private int _lastAccepted = -1;

    public Lis01Role? Sender => _sender;

    public void Feed(Lis01Role side, ReadOnlySpan<byte> data, long sequence, List<Lis01Observation> output)
    {
        var noise = 0;
        foreach (var value in data)
        {
            if (!OnByte(side, value, sequence, output))
                noise++;
        }
        if (noise > 0)
            output.Add(new Lis01Observation(Lis01ObservationKind.Noise, side, sequence, Detail: $"{noise} byte(s)"));
    }

    /// <summary>Reports a transmission left open at the end of the trace.</summary>
    public void Flush(long sequence, List<Lis01Observation> output)
    {
        if (_sender is { } sender)
            EndTransmission(sender, sequence, complete: false, output);
    }

    private bool OnByte(Lis01Role side, byte value, long sequence, List<Lis01Observation> output)
    {
        if (_inFrame[side])
        {
            var frame = _frames[side];
            if (value == Stx)
            {
                output.Add(new Lis01Observation(Lis01ObservationKind.InvalidFrame, side, sequence, Detail: "STX inside a frame"));
                frame.Clear();
                return true;
            }
            frame.Add(value);
            if (value != Lf && frame.Count <= 64 * 1024)
                return true;
            _inFrame[side] = false;
            OnFrame(side, Lis01Frame.Parse(frame.ToArray(), acceptLowercaseChecksum: true, strictText: false), sequence, output);
            return true;
        }

        var other = side == Lis01Role.Host ? Lis01Role.Instrument : Lis01Role.Host;
        switch (value)
        {
            case Stx:
                _inFrame[side] = true;
                _frames[side].Clear();
                return true;
            case Enq:
                if (_enquiring == other)
                    output.Add(new Lis01Observation(Lis01ObservationKind.Contention, side, sequence));
                else
                    output.Add(new Lis01Observation(Lis01ObservationKind.Enquiry, side, sequence));
                if (_sender is null)
                {
                    _enquiring = side;
                    _transmissionStart = sequence;
                }
                return true;
            case Ack:
                if (_enquiring == other && _sender is null)
                {
                    _sender = other;
                    _enquiring = null;
                    _messages.Clear();
                    _buffer.Clear();
                    _pending = null;
                    _lastAccepted = 0;
                    output.Add(new Lis01Observation(Lis01ObservationKind.Established, other, sequence, FirstSequence: _transmissionStart));
                }
                else if (_sender == other && _pending is { } accepted)
                {
                    Accept(other, accepted, sequence, output, Lis01ObservationKind.FrameAccepted);
                }
                else
                {
                    return false;
                }
                return true;
            case Nak:
                if (_enquiring == other && _sender is null)
                {
                    output.Add(new Lis01Observation(Lis01ObservationKind.Busy, side, sequence));
                    _enquiring = null;
                }
                else if (_sender == other && _pending is { } rejected)
                {
                    output.Add(new Lis01Observation(Lis01ObservationKind.FrameRejected, side, sequence, rejected.FrameNumber));
                    _pending = null;
                }
                else
                {
                    return false;
                }
                return true;
            case Eot:
                if (_sender == side)
                {
                    if (_pending is { } last)
                        Accept(side, last, sequence, output, Lis01ObservationKind.ImplicitAcknowledge);
                    EndTransmission(side, sequence, complete: true, output);
                }
                else if (_sender == other && _pending is { } interrupted)
                {
                    output.Add(new Lis01Observation(Lis01ObservationKind.ReceiverInterrupt, side, sequence, interrupted.FrameNumber));
                    Accept(other, interrupted, sequence, output, Lis01ObservationKind.FrameAccepted);
                }
                else if (_enquiring == side)
                {
                    // Sender gave up the establishment (termination after a timeout).
                    _enquiring = null;
                }
                else
                {
                    return false;
                }
                return true;
            default:
                return false;
        }
    }

    private void OnFrame(Lis01Role side, Lis01ParsedFrame frame, long sequence, List<Lis01Observation> output)
    {
        if (!frame.IsValid)
        {
            output.Add(new Lis01Observation(Lis01ObservationKind.InvalidFrame, side, sequence, frame.FrameNumber, Detail: frame.Error.ToString()));
            // A structurally sound frame with a wrong checksum stays pending: if the receiver acknowledges it, the
            // original was valid and the difference was introduced by whoever recorded the trace.
            if (frame.Error != Lis01FrameError.Checksum || _sender != side)
                return;
        }
        else
        {
            output.Add(new Lis01Observation(Lis01ObservationKind.Frame, side, sequence, frame.FrameNumber, frame.IsFinal));
        }
        if (_sender != side)
            return;
        if (_pending is { } previous)
        {
            if (previous.FrameNumber == frame.FrameNumber)
            {
                output.Add(new Lis01Observation(Lis01ObservationKind.Retransmission, side, sequence, frame.FrameNumber));
                _pending = frame;
                return;
            }
            Accept(side, previous, sequence, output, Lis01ObservationKind.ImplicitAcknowledge);
        }
        if (frame.FrameNumber == _lastAccepted)
        {
            // Same number as the frame already accepted: the sender did not see our ACK.
            output.Add(new Lis01Observation(Lis01ObservationKind.Retransmission, side, sequence, frame.FrameNumber));
            return;
        }
        _pending = frame;
    }

    private void Accept(Lis01Role sender, Lis01ParsedFrame frame, long sequence, List<Lis01Observation> output, Lis01ObservationKind kind)
    {
        _pending = null;
        if (frame.FrameNumber == _lastAccepted)
            return;
        _lastAccepted = frame.FrameNumber;
        if (!frame.IsValid)
            output.Add(new Lis01Observation(Lis01ObservationKind.AlteredByRecorder, sender, sequence, frame.FrameNumber, frame.IsFinal,
                "receiver accepted a frame whose recorded checksum differs: the recording altered its content"));
        _buffer.AddRange(frame.Text.Span);
        if (frame.IsFinal)
        {
            _messages.Add([.. _buffer]);
            _buffer.Clear();
        }
        output.Add(new Lis01Observation(kind, sender, sequence, frame.FrameNumber, frame.IsFinal));
    }

    private void EndTransmission(Lis01Role sender, long sequence, bool complete, List<Lis01Observation> output)
    {
        var partial = _buffer.Count > 0;
        if (partial)
            _messages.Add([.. _buffer]);
        output.Add(new Lis01Observation(Lis01ObservationKind.TransmissionEnd, sender, sequence, Messages: _messages.ToArray(),
            Complete: complete && !partial, FirstSequence: _transmissionStart));
        _messages.Clear();
        _buffer.Clear();
        _pending = null;
        _sender = null;
        _enquiring = null;
        _lastAccepted = -1;
    }
}
