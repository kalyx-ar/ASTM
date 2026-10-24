using System.Text;
using Atsm.Protocol.Lis02;

namespace Atsm.Tests.Lis02;

public class Lis02CodecTests
{
    // Synthetic message with the record layout of the ARCHITECT ASI manual (91407-108, figures 2.5-2.9).
    private const string ArchitectUpload =
        "H|\\^&|||ARCHITECT^9.45^F0000000000^H1P1O1R1C1Q1L1|||||||P|1|20260126104616\r" +
        "P|1\r" +
        "O|1|SID001|SID001^A123^5|^^^530^B12 II^UNDILUTED^P|R||||||||||||||||||||F\r" +
        "R|1|^^^530^B12 II^UNDILUTED^P^00000UD00^00001^^F|< 1.20|pg/mL|141 TO 261|EXP^<||F||ADMIN^ADMIN||20260126104344|i1SR00000\r" +
        "C|1|I|Example Result Comment|G\r" +
        "R|2|^^^530^B12 II^UNDILUTED^P^00000UD00^00001^^P|87507|RLU||||F||ADMIN^ADMIN||20260126104344|i1SR00000\r" +
        "L|1\r";

    [Fact]
    public void Parses_records_with_one_based_fields_and_components()
    {
        var result = Lis02Codec.Parse(ArchitectUpload);

        Assert.Empty(result.Diagnostics);
        var message = Assert.Single(result.Messages);
        Assert.True(message.IsTerminated);
        var header = message.Header!;
        Assert.Equal("\\^&", header.Field(2));
        Assert.Equal("ARCHITECT", header.Component(5, 1));
        Assert.Equal("9.45", header.Component(5, 2));

        var order = message.OfType('O').Single();
        Assert.Equal("SID001", order.Field(3));
        Assert.Equal("A123", order.Component(4, 2));
        Assert.Equal("F", order.Field(26));

        var result1 = message.OfType('R').First();
        Assert.Equal("530", result1.Component(3, 4));
        Assert.Equal("F", result1.Component(3, 11));
        Assert.Equal("< 1.20", result1.Field(4));
        Assert.Equal(["EXP", "<"], result1.Components(7));
        Assert.Equal("ADMIN", result1.Component(11, 2));
        Assert.Equal(string.Empty, result1.Field(40));
    }

    [Fact]
    public void Uses_the_delimiters_declared_in_the_header()
    {
        var result = Lis02Codec.Parse("H!~#$\rP!1\rO!1!S1\rR!1!#T1~#T2#X!5\rL!1\r");

        Assert.Empty(result.Diagnostics);
        var message = Assert.Single(result.Messages);
        Assert.Equal(new Lis02Delimiters('!', '~', '#', '$'), message.Delimiters);
        var record = message.OfType('R').Single();
        Assert.Equal(["#T1", "#T2#X"], record.Repeats(3));
        Assert.Equal("T2", record.Component(3, 2, repeat: 2));
        Assert.Equal("5", record.Field(4));
    }

    [Fact]
    public void Decodes_and_encodes_escape_sequences()
    {
        var delimiters = Lis02Delimiters.Default;

        Assert.Equal("a|b^c\\d&e", delimiters.Decode("a&F&b&S&c&R&d&E&e"));
        Assert.Equal("a&F&b&S&c&R&d&E&e", delimiters.Encode("a|b^c\\d&e"));
        Assert.Equal("&X0D&", delimiters.Decode("&X0D&"));
    }

    [Fact]
    public void Builder_round_trips_through_the_parser()
    {
        var d = Lis02Delimiters.Default;
        var records = new[]
        {
            Lis02RecordBuilder.Header(d).SetRaw(13, "P").SetRaw(14, "1").Build(),
            new Lis02RecordBuilder('P', d).SetText(2, "1").SetComponents(6, "Doe", "Jane").Build(),
            new Lis02RecordBuilder('O', d).SetText(2, "1").SetText(3, "S|1").SetComponents(5, null, null, null, "16").SetText(12, "N").Build(),
            new Lis02RecordBuilder('L', d).SetText(2, "1").SetText(3, "N").Build(),
        };

        var text = Lis02Codec.Serialize(records);
        var message = Assert.Single(Lis02Codec.Parse(text).Messages);

        Assert.Equal("H|\\^&|||||||||||P|1\rP|1||||Doe^Jane\rO|1|S&F&1||^^^16|||||||N\rL|1|N\r", text);
        Assert.Equal("S|1", message.OfType('O').Single().Text(3));
        Assert.Equal("Jane", message.OfType('P').Single().Component(6, 2));
    }

    [Fact]
    public void Splits_several_messages_and_tolerates_CR_LF()
    {
        var text = "H|\\^&\r\nQ|1|^S1||^^^ALL||||||||O\r\nL|1\r\nH|\\^&\r\nL|1\r\n";

        var result = Lis02Codec.Parse(text);

        Assert.Equal(2, result.Messages.Count);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Reports_order_problems_without_dropping_records()
    {
        var result = Lis02Codec.Parse("H|\\^&\rR|1|^^^1|5\rO|1|S1\r");

        var message = Assert.Single(result.Messages);
        Assert.Equal(3, message.Records.Count);
        Assert.Contains(result.Diagnostics, d => d.Code == Lis02Codes.UnexpectedRecordOrder);
        Assert.Contains(result.Diagnostics, d => d.Code == Lis02Codes.MissingTerminator);
    }

    [Fact]
    public void Rejects_a_header_with_non_distinct_delimiters()
    {
        var result = Lis02Codec.Parse("H|DeltaLab|1.1\rL|1\r");

        Assert.Contains(result.Diagnostics, d => d.Code == Lis02Codes.InvalidDelimiters);
    }

    [Fact]
    public void Record_bytes_end_with_CR_for_framing()
    {
        var records = Lis02Codec.Parse("H|\\^&\rL|1\r").Messages[0].Records;

        var bytes = Lis02Codec.ToRecordBytes(records, Encoding.Latin1);

        Assert.All(bytes, b => Assert.Equal((byte)'\r', b.Span[^1]));
    }
}
