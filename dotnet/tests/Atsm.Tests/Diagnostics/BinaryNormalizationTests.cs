using System.Text;
using Atsm.Diagnostics;
using Atsm.Plugins;
using Atsm.Plugins.Tracing;
using Atsm.Protocol.Lis01;

namespace Atsm.Tests.Diagnostics;

public sealed class BinaryNormalizationTests
{
    [Theory]
    [InlineData("02 00 FF 03")]
    [InlineData("0200FF03")]
    [InlineData("0x02, 0x00, 0xFF, 0x03")]
    [InlineData("\\x02\\x00\\xFF\\x03")]
    [InlineData("<STX><NUL><FF><ETX>")]
    public void Neutral_hex_and_control_notation_preserve_exact_bytes_including_NUL(string text) =>
        Assert.Equal(new byte[] { 2, 0, 255, 3 }, BinaryWireParser.ParseHex(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Literal_bytes_between_control_tokens_are_not_misread_as_hex_digits()
    {
        var sample = "<STX><00><00><00><00><00><F7><09>d<00><00><00><ETX>";
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 247, 9, 100, 0, 0, 0, 3 }, BinaryWireParser.ParseHex(Encoding.ASCII.GetBytes(sample)));
        Assert.False(BinaryWireParser.TryParseHex("123456"u8, out _));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("0xGG")]
    [InlineData("02 0G")]
    [InlineData("<UNKNOWN>")]
    [InlineData("[STX")]
    public void Invalid_hex_is_rejected_without_dropping_or_replacing_bytes(string text) =>
        Assert.Throws<FormatException>(() => BinaryWireParser.ParseHex(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Declarative_framing_is_not_tied_to_Sysmex_and_respects_payload_lengths()
    {
        var little = new BinaryFrameLayout(3, 1, 1, BinaryByteOrder.LittleEndian, 0xBB) { RequiredBytes = [new(0, [0xAA])] };
        Assert.True(BinaryWireParser.TryReadFrame(new byte[] { 0xAA, 3, 0x55, 6, 0, 3, 0xBB }, little, out var frame));
        Assert.Equal(new BinaryFrameBounds(3, 3, 7), frame);
        var big = new BinaryFrameLayout(4, 1, 2, BinaryByteOrder.BigEndian, 0x7F) { RequiredBytes = [new(0, [0x7E])] };
        Assert.True(BinaryWireParser.TryReadFrame(new byte[] { 0x7E, 0, 2, 0x99, 0xAB, 0xCD, 0x7F }, big, out var other));
        Assert.Equal(new BinaryFrameBounds(4, 2, 7), other);
        Assert.False(BinaryWireParser.TryReadFrame(new byte[] { 0xAA, 200, 0x55, 6, 0, 3, 0xBB }, little, out _));
        Assert.False(BinaryWireParser.TryReadFrame(new byte[] { 0xAA, 3, 0x55, 6, 0, 3, 0xCC }, little, out _));
    }
    [Fact]
    public void Raw_ASTMs_and_their_hex_representation_reach_the_same_common_decoder()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var wire = Lis01Frame.Build(1, "H|\\^&\rP|1\rO|1|SYN\rR|1|^^^SYN|1.2|u\rL|1|N\r"u8, true);
        var raw = DiagnosticReader.Read(wire, DiagnosticInputFormat.WireBytes, profile, "synthetic", TimeSpan.Zero);
        var hex = DiagnosticReader.Read(Encoding.ASCII.GetBytes(string.Join(" ", wire.Select(b => b.ToString("X2")))), DiagnosticInputFormat.Auto, profile, "synthetic", TimeSpan.Zero);
        var timing = new DiagnosticTiming(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc);
        var a = DiagnosticReader.Analyze(raw, profile, timing); var b = DiagnosticReader.Analyze(hex, profile, timing);
        Assert.Equal(nameof(DiagnosticInputFormat.HexBytes), hex.Header.Properties!["inputFormat"]);
        Assert.Equal(a.Messages.Select(m => m.Text), b.Messages.Select(m => m.Text));
        Assert.Equal(a.Observations.Select(o => o.Value.RawText), b.Observations.Select(o => o.Value.RawText));
        Assert.False(CommunicationStatistics.HasCaptureClock(hex));
    }
    [Fact]
    public void Generic_ASTMs_record_names_and_their_translation_source_are_shared_across_brands()
    {
        foreach (var plugin in new DiagnosticCatalog().AstmPlugins.OfType<IInstrumentDiagnosticVocabulary>())
        foreach (var code in new[] { "H", "P", "O", "R", "Q", "C", "M", "L" })
        {
            var shared = StandardDiagnosticVocabulary.Shared["record." + code];
            Assert.Same(shared, plugin.DiagnosticVocabulary["record." + code]);
            Assert.Equal("Atsm.Plugins", shared.SourceAssembly);
        }
    }
}
