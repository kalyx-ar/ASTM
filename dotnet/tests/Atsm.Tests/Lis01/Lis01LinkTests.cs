using System.Text;
using Atsm.Protocol.Lis01;
using static Atsm.Protocol.Lis01.ControlCharacters;

namespace Atsm.Tests.Lis01;

public class Lis01LinkTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);

    [Fact]
    public void Host_receives_an_instrument_transmission_and_acknowledges_every_frame()
    {
        var pair = new LinkPair();
        pair.Instrument.Enqueue([Ascii("H|\\^&|||ARCHITECT\r"), Ascii("L|1\r")]);

        pair.Run(T0);

        var received = Assert.Single(pair.HostReceived);
        Assert.True(received.Complete);
        Assert.Equal(["H|\\^&|||ARCHITECT\r", "L|1\r"], received.Messages.Select(Text));
        var completed = Assert.Single(pair.InstrumentCompleted);
        Assert.Equal(Lis01SendResult.Delivered, completed.Result);
        Assert.Equal(2, completed.FramesAcknowledged);
        Assert.Equal(Lis01State.Idle, pair.Host.State);
    }

    [Fact]
    public void Long_message_is_reassembled_across_ETB_frames()
    {
        var pair = new LinkPair();
        var text = string.Concat(Enumerable.Range(0, 400).Select(i => $"{i:D4}|")) + "\r";
        pair.Instrument.Enqueue([Ascii(text)]);

        pair.Run(T0);

        Assert.Equal(text, Text(Assert.Single(Assert.Single(pair.HostReceived).Messages)));
    }

    [Fact]
    public void Corrupted_frame_is_rejected_and_retransmitted()
    {
        var pair = new LinkPair { CorruptInstrumentFrames = 1 };
        pair.Instrument.Enqueue([Ascii("R|1|^^^530|181\r")]);

        pair.Run(T0);

        Assert.Contains(pair.HostNotices, n => n.Code == Lis01Codes.InvalidFrame);
        Assert.Contains(pair.InstrumentNotices, n => n.Code == Lis01Codes.FrameRejected);
        Assert.Equal("R|1|^^^530|181\r", Text(Assert.Single(Assert.Single(pair.HostReceived).Messages)));
    }

    [Fact]
    public void Sender_gives_up_after_six_retransmissions()
    {
        var link = new Lis01Link(Lis01Role.Host);
        var output = new List<Lis01Action>();
        link.Enqueue([Ascii("O|1|S1\r")]);
        link.Poll(T0, output);
        link.OnBytes([Ack], T0, output);
        output.Clear();

        for (var i = 0; i < 7; i++)
            link.OnBytes([Nak], T0, output);

        var frameWrites = output.OfType<Lis01Write>().Count(w => w.Bytes[0] == Stx);
        Assert.Equal(6, frameWrites);
        Assert.Equal(Eot, output.OfType<Lis01Write>().Last().Bytes.Single());
        var completed = Assert.Single(output.OfType<Lis01SendCompleted>());
        Assert.Equal(Lis01SendResult.Failed, completed.Result);
        Assert.Equal(Lis01Codes.RetransmissionsExhausted, completed.Code);
    }

    [Fact]
    public void Busy_receiver_delays_the_next_enquiry()
    {
        var link = new Lis01Link(Lis01Role.Host);
        var output = new List<Lis01Action>();
        link.Enqueue([Ascii("O|1|S1\r")]);
        link.Poll(T0, output);
        link.OnBytes([Nak], T0, output);
        output.Clear();

        link.Poll(T0 + TimeSpan.FromSeconds(9), output);
        Assert.Empty(output.OfType<Lis01Write>());

        link.Poll(T0 + TimeSpan.FromSeconds(10), output);
        Assert.Equal(Enq, Assert.Single(output.OfType<Lis01Write>()).Bytes.Single());
    }

    [Fact]
    public void Establishment_timeout_terminates_and_retries_until_exhausted()
    {
        var options = Lis01Options.Default with { MaxEstablishmentAttempts = 2 };
        var link = new Lis01Link(Lis01Role.Host, options);
        var output = new List<Lis01Action>();
        link.Enqueue([Ascii("O|1|S1\r")]);

        link.Poll(T0, output);
        link.Poll(link.NextDeadline!.Value, output);
        link.Poll(link.NextDeadline!.Value, output);
        link.Poll(link.NextDeadline!.Value, output);

        Assert.Equal(2, output.OfType<Lis01Write>().Count(w => w.Bytes.Single() == Enq));
        Assert.Equal(Lis01Codes.EstablishmentExhausted, Assert.Single(output.OfType<Lis01SendCompleted>()).Code);
        Assert.Equal(Lis01State.Idle, link.State);
    }

    [Fact]
    public void On_contention_the_host_yields_and_sends_after_receiving()
    {
        var pair = new LinkPair();
        pair.Host.Enqueue([Ascii("H|\\^&\r"), Ascii("L|1|N\r")]);
        pair.Instrument.Enqueue([Ascii("H|\\^&\r"), Ascii("Q|1|^S1||^^^ALL||||||||O\r"), Ascii("L|1\r")]);

        // Both start at the same time: ENQ crosses ENQ.
        pair.StartBoth(T0);
        pair.Run(T0);

        Assert.Contains(pair.HostNotices, n => n.Code == Lis01Codes.Contention);
        Assert.Equal(3, Assert.Single(pair.HostReceived).Messages.Count);
        Assert.Equal(2, Assert.Single(pair.InstrumentReceived).Messages.Count);
        Assert.Equal(Lis01SendResult.Delivered, Assert.Single(pair.HostCompleted).Result);
    }

    [Fact]
    public void Receiver_timeout_reports_the_partial_transmission()
    {
        var link = new Lis01Link(Lis01Role.Host);
        var output = new List<Lis01Action>();
        link.OnBytes([Enq], T0, output);
        var frames = Lis01Frame.BuildTransmission([Ascii(new string('X', 300) + "\r")]);
        link.OnBytes(frames[0], T0, output);

        link.Poll(T0 + TimeSpan.FromSeconds(30), output);

        var received = Assert.Single(output.OfType<Lis01Received>());
        Assert.False(received.Complete);
        Assert.Equal(240, Assert.Single(received.Messages).Length);
        Assert.Contains(output.OfType<Lis01Notice>(), n => n.Code == Lis01Codes.ReceiverTimeout);
        Assert.Equal(Lis01State.Idle, link.State);
    }

    [Fact]
    public void Receiver_rejects_a_repeated_frame_number()
    {
        var link = new Lis01Link(Lis01Role.Host);
        var output = new List<Lis01Action>();
        var frame = Lis01Frame.Build(1, "H|\\^&\r"u8, isFinal: true);
        link.OnBytes([Enq], T0, output);
        link.OnBytes(frame, T0, output);
        output.Clear();

        link.OnBytes(frame, T0, output);

        Assert.Equal(Nak, Assert.Single(output.OfType<Lis01Write>()).Bytes.Single());
        Assert.Contains(output.OfType<Lis01Notice>(), n => n.Code == Lis01Codes.UnexpectedFrameNumber);
    }

    [Fact]
    public void Receiver_interrupt_terminates_the_transmission()
    {
        var link = new Lis01Link(Lis01Role.Host);
        var output = new List<Lis01Action>();
        link.Enqueue([Ascii("H|\\^&\r"), Ascii("P|1\r"), Ascii("L|1\r")]);
        link.Poll(T0, output);
        link.OnBytes([Ack], T0, output);

        link.OnBytes([Eot], T0, output);

        var completed = Assert.Single(output.OfType<Lis01SendCompleted>());
        Assert.Equal(Lis01SendResult.Interrupted, completed.Result);
        Assert.Equal(1, completed.FramesAcknowledged);
        Assert.Equal(Eot, output.OfType<Lis01Write>().Last().Bytes.Single());
    }

    [Fact]
    public void Bytes_split_at_every_position_produce_the_same_reception()
    {
        var frames = Lis01Frame.BuildTransmission([Ascii("H|\\^&|||UF-5000\r"), Ascii("R|1|^^^^RBC|12|/uL\r"), Ascii("L|1|N\r")]);
        var stream = new List<byte> { Enq };
        foreach (var frame in frames)
            stream.AddRange(frame);
        stream.Add(Eot);
        var all = stream.ToArray();

        for (var split = 1; split < all.Length; split++)
        {
            var link = new Lis01Link(Lis01Role.Host);
            var output = new List<Lis01Action>();
            link.OnBytes(all.AsSpan(0, split), T0, output);
            link.OnBytes(all.AsSpan(split), T0, output);

            var received = Assert.Single(output.OfType<Lis01Received>());
            Assert.True(received.Complete);
            Assert.Equal(3, received.Messages.Count);
        }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static string Text(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    /// <summary>Connects two links back to back and pumps bytes between them.</summary>
    private sealed class LinkPair
    {
        public Lis01Link Host { get; } = new(Lis01Role.Host);

        public Lis01Link Instrument { get; } = new(Lis01Role.Instrument);

        public int CorruptInstrumentFrames { get; set; }

        public List<Lis01Received> HostReceived { get; } = [];

        public List<Lis01Received> InstrumentReceived { get; } = [];

        public List<Lis01SendCompleted> HostCompleted { get; } = [];

        public List<Lis01SendCompleted> InstrumentCompleted { get; } = [];

        public List<Lis01Notice> HostNotices { get; } = [];

        public List<Lis01Notice> InstrumentNotices { get; } = [];

        private readonly Queue<byte[]> _toHost = new();
        private readonly Queue<byte[]> _toInstrument = new();

        public void StartBoth(TimeSpan now)
        {
            var hostOut = new List<Lis01Action>();
            var instrumentOut = new List<Lis01Action>();
            Host.Poll(now, hostOut);
            Instrument.Poll(now, instrumentOut);
            Route(hostOut, fromHost: true);
            Route(instrumentOut, fromHost: false);
        }

        public void Run(TimeSpan now)
        {
            for (var guard = 0; guard < 10_000; guard++)
            {
                if (_toHost.Count == 0 && _toInstrument.Count == 0)
                {
                    var hostOut = new List<Lis01Action>();
                    var instrumentOut = new List<Lis01Action>();
                    // Advance virtual time to the earliest deadline so retries after contention happen.
                    var next = new[] { Host.NextDeadline, Instrument.NextDeadline }.Where(d => d is not null).Min();
                    if (next is { } deadline && deadline > now)
                        now = deadline;
                    Host.Poll(now, hostOut);
                    Instrument.Poll(now, instrumentOut);
                    Route(hostOut, fromHost: true);
                    Route(instrumentOut, fromHost: false);
                    if (_toHost.Count == 0 && _toInstrument.Count == 0)
                        return;
                    continue;
                }
                if (_toHost.TryDequeue(out var toHost))
                {
                    var output = new List<Lis01Action>();
                    Host.OnBytes(toHost, now, output);
                    Route(output, fromHost: true);
                }
                if (_toInstrument.TryDequeue(out var toInstrument))
                {
                    var output = new List<Lis01Action>();
                    Instrument.OnBytes(toInstrument, now, output);
                    Route(output, fromHost: false);
                }
            }
            throw new InvalidOperationException("Link pair did not settle.");
        }

        private void Route(List<Lis01Action> actions, bool fromHost)
        {
            foreach (var action in actions)
            {
                switch (action)
                {
                    case Lis01Write write when fromHost:
                        _toInstrument.Enqueue(write.Bytes);
                        break;
                    case Lis01Write write:
                        var bytes = write.Bytes;
                        if (bytes[0] == Stx && CorruptInstrumentFrames > 0)
                        {
                            CorruptInstrumentFrames--;
                            bytes = [.. bytes];
                            bytes[2] ^= 0x20;
                        }
                        _toHost.Enqueue(bytes);
                        break;
                    case Lis01Received received:
                        (fromHost ? HostReceived : InstrumentReceived).Add(received);
                        break;
                    case Lis01SendCompleted completed:
                        (fromHost ? HostCompleted : InstrumentCompleted).Add(completed);
                        break;
                    case Lis01Notice notice:
                        (fromHost ? HostNotices : InstrumentNotices).Add(notice);
                        break;
                }
            }
        }
    }
}
