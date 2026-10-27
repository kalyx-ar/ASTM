using System.Text;
using Astm.Protocol.Lis01;
using static Astm.Protocol.Lis01.ControlCharacters;

namespace Astm.Tests.Lis01;

public class Lis01FrameTests
{
    [Fact]
    public void Checksum_matches_frame_observed_from_a_CellDyn_Ruby()
    {
        // Frame seen in a production log: <STX>4L|1|I<CR><ETX>02<CR><LF>
        var frame = Lis01Frame.Build(4, "L|1|I\r"u8, isFinal: true);

        Assert.Equal([Stx, (byte)'4', .. "L|1|I\r"u8.ToArray(), Etx, (byte)'0', (byte)'2', Cr, Lf], frame);
    }

    [Fact]
    public void Parse_round_trips_a_built_frame()
    {
        var frame = Lis01Frame.Build(7, "R|1|^^^530|181|pg/mL\r"u8, isFinal: false);

        var parsed = Lis01Frame.Parse(frame.AsMemory(1));

        Assert.True(parsed.IsValid);
        Assert.Equal(7, parsed.FrameNumber);
        Assert.False(parsed.IsFinal);
        Assert.Equal("R|1|^^^530|181|pg/mL\r", Encoding.ASCII.GetString(parsed.Text.Span));
    }

    [Fact]
    public void Parse_rejects_a_corrupted_checksum()
    {
        var frame = Lis01Frame.Build(1, "H|\\^&\r"u8, isFinal: true);
        frame[^4] ^= 0x01;

        Assert.Equal(Lis01FrameError.Checksum, Lis01Frame.Parse(frame.AsMemory(1)).Error);
    }

    [Fact]
    public void Parse_accepts_lowercase_checksum_only_when_allowed()
    {
        var frame = Lis01Frame.Build(1, "Q|1|^S1||^^^ALL\r"u8, isFinal: true);
        frame[^4] = (byte)char.ToLowerInvariant((char)frame[^4]);
        frame[^3] = (byte)char.ToLowerInvariant((char)frame[^3]);

        Assert.True(Lis01Frame.Parse(frame.AsMemory(1), acceptLowercaseChecksum: true).IsValid);
        if (frame[^4] is >= (byte)'a' || frame[^3] is >= (byte)'a')
            Assert.Equal(Lis01FrameError.Checksum, Lis01Frame.Parse(frame.AsMemory(1), acceptLowercaseChecksum: false).Error);
    }

    [Fact]
    public void BuildTransmission_splits_long_messages_with_ETB_and_wraps_frame_numbers()
    {
        var message = Encoding.ASCII.GetBytes(new string('A', 5000) + "\r");

        var frames = Lis01Frame.BuildTransmission([message]);

        Assert.Equal(21, frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal((byte)('0' + (i + 1) % 8), frames[i][1]);
            Assert.True(frames[i].Length <= Lis01Frame.MaxFrameLength);
            Assert.Equal(i == frames.Count - 1 ? Etx : Etb, frames[i][^5]);
        }
    }

    [Fact]
    public void BuildTransmission_rejects_restricted_characters()
    {
        Assert.Throws<ArgumentException>(() => Lis01Frame.BuildTransmission([new byte[] { (byte)'A', Lf }]));
    }
}
