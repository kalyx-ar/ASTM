using System.Text;
using Atsm.Abstractions.Tracing;
using Atsm.Diagnostics;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugin.Sysmex.Urinalysis;
using Atsm.Protocol.Lis01;
using Atsm.Tracing;

namespace Atsm.Tests.Diagnostics;

public sealed class ProtocolRecognitionTests
{
    private static readonly DiagnosticTiming Timing = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeZoneInfo.Utc);
    private static byte[] Message(string sender) => Encoding.ASCII.GetBytes($"H|\\^&|||{sender}\rP|1\rO|1|SYN-SAMPLE\rR|1|^^^WBC^A^1^S|1.1^RAW|/uL\rL|1|N\r");
    private static byte[] Log(IEnumerable<(char Direction, byte[] Bytes)> entries) => Encoding.Latin1.GetBytes(string.Join("\r\n", entries.Select((e, i) =>
        $"2026/10/25 10:00:00.{i:000} {e.Direction} {e.Bytes.Length:000}{{{string.Concat(e.Bytes.Select(b => b < 32 ? "[" + ControlName(b) + "]" : ((char)b).ToString()))}}}")));
    private static string ControlName(byte value) => value switch { 2 => "STX", 3 => "ETX", 4 => "EOT", 5 => "ENQ", 6 => "ACK", 11 => "VT", 13 => "CR", 10 => "LF", 21 => "NAK", 28 => "FS", _ => throw new ArgumentException("Unexpected synthetic control") };

    [Theory]
    [InlineData("U-WAM")]
    [InlineData("UF-3000")]
    [InlineData("UF-3000x")]
    [InlineData("UF-4000")]
    [InlineData("UF-5000")]
    [InlineData("UC-1000")]
    [InlineData("UC-3500")]
    [InlineData("UD-10")]
    public void Uwam_documented_sender_models_share_one_plugin_and_preserve_explicit_options(string sender)
    {
        var catalog = new DiagnosticCatalog(); var bytes = Message(sender + "^VERSION-SYN^SERIAL-SYN");
        var selected = catalog.Resolve(CfxPlugin.CsvProfileId);
        var detection = DiagnosticInputDetector.Detect(bytes, selected, catalog);
        Assert.Equal(SysmexUrinalysisPlugin.WithChemistryProfile, detection.ProfileId);
        var capture = DiagnosticReader.Read(bytes, DiagnosticInputFormat.Auto, selected, "opaque", TimeSpan.Zero);
        var result = DiagnosticReader.Analyze(capture, catalog.Resolve(capture.Header.Profile!), Timing);
        Assert.Single(result.Observations); Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL");
        var explicitProfile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.WithoutChemistryProfile);
        Assert.Equal(explicitProfile.Id, DiagnosticInputDetector.Detect(bytes, explicitProfile, catalog).ProfileId);
    }

    [Theory]
    [InlineData("    XN-350^00-11^SYN", "sysmex.xnl.astm02.results")]
    [InlineData("XN-450^00-11^SYN", "sysmex.xnl.astm02.results")]
    [InlineData("XN-550^00-11^SYN", "sysmex.xnl.astm02.results")]
    [InlineData("CS-2500^00-00^SYN", "sysmex.cs2500.astm02.results")]
    [InlineData("ASI^1.0^SYN^H1P1O1R1Q1L1C1", "abbott.architect.asi-rs232")]
    [InlineData("SYN-SERIAL^CDRuby^2.2 ML^1.0", "abbott.celldyn-ruby.lis2a")]
    public void Existing_plugins_own_their_sender_signatures(string sender, string expected)
    {
        var catalog = new DiagnosticCatalog(); var selected = catalog.Resolve(CfxPlugin.CsvProfileId);
        Assert.Equal(expected, DiagnosticInputDetector.Detect(Message(sender), selected, catalog).ProfileId);
    }

    [Theory]
    [InlineData('R', RecordedBy.Host)]
    [InlineData('S', RecordedBy.Instrument)]
    public void Every_header_split_in_a_monitor_log_is_recognized_and_interpreted(char direction, RecordedBy recorder)
    {
        var catalog = new DiagnosticCatalog(); var selected = catalog.Resolve(CfxPlugin.CsvProfileId);
        var frame = Lis01Frame.Build(1, Message("UF-5000^VERSION-SYN^SERIAL-SYN"), true);
        var headerEnd = Array.IndexOf(frame, (byte)13);
        for (var split = 1; split < headerEnd; split++)
        {
            var other = direction == 'R' ? 'S' : 'R';
            var bytes = Log([(direction, new byte[] { 5 }), (other, new byte[] { 6 }), (direction, frame[..split]),
                (other, new byte[] { 6 }), (direction, frame[split..]), (other, new byte[] { 6 }), (direction, new byte[] { 4 })]);
            var detection = DiagnosticInputDetector.Detect(bytes, selected, catalog);
            Assert.Equal(SysmexUrinalysisPlugin.WithChemistryProfile, detection.ProfileId); Assert.False(detection.UnsupportedProtocol); Assert.Equal(recorder, detection.Recorder);
            var capture = DiagnosticReader.Read(bytes, DiagnosticInputFormat.Auto, selected, "opaque", TimeSpan.Zero);
            var result = DiagnosticReader.Analyze(capture, catalog.Resolve(capture.Header.Profile!), Timing);
            Assert.Single(result.Messages); Assert.Single(result.Observations);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL" || d.Code.Contains("RECORD-OUTSIDE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_previous_unsupported_classification_is_revalidated_without_mutating_stored_evidence()
    {
        var catalog = new DiagnosticCatalog(); var profile = catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var capture = DiagnosticReader.Read(Message("UF-5000"), DiagnosticInputFormat.AstmMessage, profile, "opaque", TimeSpan.Zero);
        capture = capture with { Header = capture.Header with { Properties = new Dictionary<string, string> { ["unsupportedProtocol"] = "true", ["other"] = "keep" } } };
        var validated = DiagnosticReader.RevalidateProtocol(capture, profile);
        Assert.False(validated.Header.Properties!.ContainsKey("unsupportedProtocol")); Assert.Equal("keep", validated.Header.Properties["other"]);
        Assert.Equal("true", capture.Header.Properties!["unsupportedProtocol"]); Assert.Same(capture.Entries, validated.Entries);
        var result = DiagnosticReader.Analyze(capture, profile, Timing); Assert.Single(result.Observations);
        Assert.Equal("Numérico", ProtocolExplainer.ResultKind(result.Observations[0], profile));
        Assert.Equal("1.1", result.Observations[0].Value.RawText); Assert.Equal("S/RAW", result.Observations[0].ResultKind);
        Assert.Equal(Message("UF-5000"), Assert.Single(validated.Entries).Data.ToArray());
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL");
    }

    [Fact]
    public void Model_names_in_comments_event_labels_or_unknown_senders_do_not_select_a_dialect()
    {
        var catalog = new DiagnosticCatalog(); var selected = catalog.Resolve(CfxPlugin.CsvProfileId);
        var bytes = Encoding.ASCII.GetBytes("H|\\^&|||OTHER^UF-5000\rC|1|I|UF-5000 and XN-550\rL|1\r");
        Assert.Equal(Atsm.Plugins.StandardAstmPlugin.ProfileId, DiagnosticInputDetector.Detect(bytes, selected, catalog).ProfileId);
        TraceSegment[] separated = [new(null, TraceDirection.Received, SegmentContent.Wire, "H|\\"u8.ToArray()),
            new(null, TraceDirection.Transmitted, SegmentContent.Wire, "^&|||UF-5000\r"u8.ToArray()),
            new(null, TraceDirection.Event, SegmentContent.Event, Message("UF-5000"))];
        Assert.False(DiagnosticInputDetector.HasAstmHeader(separated));
        Assert.False(DiagnosticInputDetector.HasHl7Header(new TraceSegment[] { new(null, TraceDirection.Event, SegmentContent.Event, "MSH|^~\\&|SYN\r"u8.ToArray()) }));
        var mixed = Message("UF-5000").Concat(Message("XN-550")).ToArray();
        Assert.Throws<InvalidDataException>(() => DiagnosticInputDetector.Detect(mixed, selected, catalog));
    }

    [Fact]
    public void A_split_HL7_header_does_not_become_an_ASTM_dialect_due_to_the_model_name()
    {
        var catalog = new DiagnosticCatalog(); var selected = catalog.Resolve("abbott.architect.hl7-2.5.1");
        var message = Encoding.ASCII.GetBytes("\vMSH|^~\\&|UF-5000||||20261025100000||OUL^R22|SYN-ID|P|2.5.1\r\x1c\r");
        var bytes = Log([('R', message[..3]), ('R', message[3..])]);
        var detection = DiagnosticInputDetector.Detect(bytes, selected, catalog);
        Assert.Equal(selected.Id, detection.ProfileId); Assert.False(detection.UnsupportedProtocol); Assert.Equal(RecordedBy.Host, detection.Recorder);
        var capture = DiagnosticReader.Read(bytes, DiagnosticInputFormat.Auto, selected, "opaque", TimeSpan.Zero);
        Assert.Equal("OUL^R22", Assert.Single(DiagnosticReader.Analyze(capture, selected, Timing).Messages).Type);
    }

    [Fact]
    public void Recognizing_the_model_does_not_turn_a_rejected_frame_into_measurements()
    {
        var catalog = new DiagnosticCatalog(); var selected = catalog.Resolve(CfxPlugin.CsvProfileId);
        var frame = Lis01Frame.Build(1, Message("UF-5000"), true); frame[^4] = frame[^4] == '0' ? (byte)'1' : (byte)'0';
        var bytes = Log([('R', new byte[] { 5 }), ('S', new byte[] { 6 }), ('R', frame), ('S', new byte[] { 21 }), ('R', new byte[] { 4 })]);
        var capture = DiagnosticReader.Read(bytes, DiagnosticInputFormat.Auto, selected, "opaque", TimeSpan.Zero);
        Assert.Equal(SysmexUrinalysisPlugin.WithChemistryProfile, capture.Header.Profile);
        var result = DiagnosticReader.Analyze(capture, catalog.Resolve(capture.Header.Profile!), Timing);
        Assert.Empty(result.Observations); Assert.Empty(result.Messages);
        Assert.Contains(result.Diagnostics, d => d.Code == "ATSM-DIAG-LIS01-FRAME-REJECTED");
    }
}
