using System.Text;
using Atsm.Protocol.Mllp;

namespace Atsm.Tests.Mllp;

public class MllpDecoderTests
{
    [Fact]
    public void Two_blocks_in_one_read_and_a_split_block_are_decoded()
    {
        var first = MllpFraming.Wrap("MSH|^~\\&|A\rOBX|1\r"u8);
        var second = MllpFraming.Wrap("MSH|^~\\&|B\r"u8);
        var stream = first.Concat(second).ToArray();
        var decoder = new MllpDecoder();
        var events = new List<MllpEvent>();

        decoder.Feed(stream.AsSpan(0, first.Length + 4), events);
        decoder.Feed(stream.AsSpan(first.Length + 4), events);

        var messages = events.Where(e => e.Kind == MllpEventKind.Message).Select(e => Encoding.ASCII.GetString(e.Payload!)).ToList();
        Assert.Equal(["MSH|^~\\&|A\rOBX|1\r", "MSH|^~\\&|B\r"], messages);
    }

    [Fact]
    public void Bytes_outside_a_block_are_reported_as_noise()
    {
        var decoder = new MllpDecoder();
        var events = new List<MllpEvent>();

        decoder.Feed([0x41, 0x42, .. MllpFraming.Wrap("X"u8)], events);

        Assert.Equal(new MllpEvent(MllpEventKind.Noise, null, 2), events[0]);
        Assert.Equal(MllpEventKind.Message, events[1].Kind);
    }

    [Fact]
    public void Oversized_block_is_discarded_with_an_overflow_event()
    {
        var decoder = new MllpDecoder(maxMessageBytes: 4);
        var events = new List<MllpEvent>();

        decoder.Feed(MllpFraming.Wrap("123456"u8), events);
        decoder.Feed(MllpFraming.Wrap("ok"u8), events);

        Assert.Equal(MllpEventKind.Overflow, events[0].Kind);
        Assert.Equal("ok", Encoding.ASCII.GetString(Assert.Single(events, e => e.Kind == MllpEventKind.Message).Payload!));
    }
}
