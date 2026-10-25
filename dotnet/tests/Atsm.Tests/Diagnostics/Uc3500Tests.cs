using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Tracing;
using Atsm.Diagnostics;
using Atsm.Plugin.Sysmex.Urinalysis;
using Atsm.Plugins;
using Atsm.Tracing;
using Atsm.Tests.Runtime;
using Atsm.Runtime.Tracing;

namespace Atsm.Tests.Diagnostics;

public sealed class Uc3500Tests
{
    private const string Sample = "SYN-0001      "; // Fourteen fixed-width characters, never a real sample.
    private const string Query = "RQ02," + Sample + "01";
    private const string Order = "CM00,01," + Sample + "01 Strip 11A"; // The strip field is 10 characters.
    private static readonly DiagnosticTiming Timing = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeZoneInfo.Utc);
    private static byte[] Frame(string text, Uc3500CheckMode mode = Uc3500CheckMode.Bcc)
    {
        var payload = Encoding.ASCII.GetBytes(text); byte xor = 3; var sum = 3;
        foreach (var b in payload) { xor ^= b; sum += b; }
        byte[] check = mode switch { Uc3500CheckMode.Bcc => [xor], Uc3500CheckMode.Sum => [(byte)sum],
            Uc3500CheckMode.Sum2 => Encoding.ASCII.GetBytes(((byte)sum).ToString("X2")), _ => [] };
        return new byte[] { 2 }.Concat(payload).Append((byte)3).Concat(check).ToArray();
    }
    private static TraceEntry Entry(long sequence, byte[] bytes, TraceDirection direction = TraceDirection.Received) => new(sequence,
        TimeSpan.FromMilliseconds(sequence), DateTimeOffset.UnixEpoch, direction, bytes);
    private static TraceInterpretation Interpret(IEnumerable<TraceEntry> entries, Uc3500CheckMode mode = Uc3500CheckMode.Auto)
    { var plugin = new SysmexUrinalysisPlugin(mode); return plugin.Interpret(plugin.TraceProfiles[0], entries.ToArray(), new("SYN"), Guid.Empty, TimeZoneInfo.Utc); }
    private static string Result(bool reflectance = true, char series = 'C', string error = "0000")
    {
        var f = new List<string> { Sample, "0001", "01", series + "00000001", error, " Strip 11A", "2026/10/25", "10:15:20" };
        for (var i = 0; i < 12; i++) f.Add("0 " + "normal" + "      " + (reflectance ? "98.2 " + " " + "99.1 " : ""));
        for (var i = 0; i < 3; i++) f.Add("0 " + "1.010 " + "      ");
        f.Add("0 " + (reflectance ? "2120" : "") + "YELLOW    03" + " -");
        return string.Join(',', f);
    }

    [Theory]
    [InlineData(Uc3500CheckMode.Bcc)]
    [InlineData(Uc3500CheckMode.Sum)]
    [InlineData(Uc3500CheckMode.Sum2)]
    [InlineData(Uc3500CheckMode.None)]
    public void Reassembles_every_split_with_the_documented_check_algorithms(Uc3500CheckMode mode)
    {
        var bytes = Frame(Query, mode);
        for (var split = 1; split < bytes.Length; split++)
        {
            var result = Interpret([Entry(0, bytes[..split]), Entry(1, bytes[split..])], mode);
            var message = Assert.Single(result.Messages); Assert.Equal(Query, message.Text); Assert.Equal(0, message.FirstSequence); Assert.Equal(1, message.LastSequence);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code.Contains("CHECKSUM", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Handles_coalesced_frames_and_a_checksum_that_is_a_control_character()
    {
        // Find a synthetic barcode whose BCC is STX. A checksum is data even when it equals a control.
        var text = Enumerable.Range(32, 95).SelectMany(a => Enumerable.Range(32, 95).Select(b => "RQ02," + ((char)a) + ((char)b) + new string(' ', 12) + "01"))
            .First(t => Frame(t)[^1] == 2);
        var joined = Frame(text).Concat(Frame("ER00,000000")).ToArray();
        var result = Interpret([Entry(0, joined)]);
        Assert.Equal(2, result.Messages.Count); Assert.Equal(text, result.Messages[0].Text);
        Assert.All(result.Messages, m => Assert.Equal("BCC", m.Fields["checksum"]));
    }

    [Fact]
    public void Transparent_one_requires_CRLF_and_preserves_unknown_records()
    {
        var result = Interpret([Entry(0, Encoding.ASCII.GetBytes(Query + "\r")), Entry(1, Encoding.ASCII.GetBytes("\n" + "ZZ99,SYN\r\n"))]);
        Assert.Equal(2, result.Messages.Count); Assert.Equal("CRLF", result.Messages[0].Fields["checksum"]);
        Assert.Equal("UNKNOWN", result.Messages[1].Code); Assert.Empty(result.Observations);
        Assert.Contains(result.Diagnostics, d => d.Code == "ATSM-UC3500-UNKNOWN");
        var full = Interpret([Entry(0, Encoding.ASCII.GetBytes(Result() + "\r\n"))]);
        Assert.Equal(42, full.Observations.Count); Assert.DoesNotContain(full.Diagnostics, d => d.Code == "ATSM-UC3500-OVERSIZE");
    }

    [Theory]
    [InlineData("ER00", null)]
    [InlineData("ER01", "ATSM-UC3500-ER01")]
    [InlineData("ER03", "ATSM-UC3500-ER03")]
    [InlineData("ER04", "ATSM-UC3500-ER04")]
    [InlineData("ER05", "ATSM-UC3500-ER05")]
    [InlineData("ER99", "ATSM-UC3500-UNKNOWN")]
    public void Correlates_order_outcomes_without_inventing_measurements(string response, string? finding)
    {
        var result = Interpret([Entry(0, Frame(Query)), Entry(1, [6], TraceDirection.Transmitted), Entry(2, [4]),
            Entry(3, Frame(Order), TraceDirection.Transmitted), Entry(4, [6]), Entry(5, [4], TraceDirection.Transmitted), Entry(6, Frame(response + ",000000")), Entry(7, [6], TraceDirection.Transmitted)]);
        Assert.Equal(3, result.Messages.Count); Assert.Empty(result.Observations);
        Assert.Equal("0", result.Messages[2].Fields["query.sequence"]); Assert.Equal("3", result.Messages[2].Fields["order.sequence"]);
        Assert.Equal(" Strip 11A", result.Messages[2].Fields["strip.requested"]);
        if (finding is null) Assert.Empty(result.Diagnostics); else Assert.Equal(finding, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(5, result.Events.Count(e => e.Level == ProtocolEventLevel.Link));
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        Assert.All(result.Messages, m => Assert.NotEmpty(DiagnosticText.Explanation(profile.TracePlugin!, m.Explanation)));
        if (response == "ER04") Assert.Contains("rack de botellas", DiagnosticText.Finding(result.Diagnostics[0], profile));
    }

    [Theory]
    [InlineData(true, 42)]
    [InlineData(false, 17)]
    public void Decodes_measurements_reflectance_and_control_context_without_inventing_lots_or_units(bool reflectance, int count)
    {
        var data = Frame(Result(reflectance)); var result = Interpret(data.Select((b, i) => Entry(i, [b])));
        Assert.Equal("RESULT", Assert.Single(result.Messages).Code); Assert.Equal(count, result.Observations.Count);
        Assert.All(result.Observations, o => { Assert.Equal(SpecimenRole.Control, o.Specimen.Role); Assert.Null(o.Quality!.Material!.Lot); Assert.Null(o.Value.Unit); Assert.Equal(DateTimeOffset.Parse("2026-10-25T10:15:20Z"), o.ResultTime); });
        var r = result.Observations.FirstOrDefault(o => o.ResultKind == "REFLECTANCE1");
        if (reflectance) { Assert.Equal(98.2m, r!.Value.Numeric); Assert.Equal("%", r.Value.RawUnit); Assert.Equal("98.2 ", r.Value.RawText); }
        else Assert.Null(r);
        Assert.Equal(result.Observations.Select(o => o.ObservationId), Interpret(data.Select((b, i) => Entry(i, [b]))).Observations.Select(o => o.ObservationId));
    }

    [Fact]
    public void Invalid_frames_and_incomplete_capture_never_produce_canonical_results()
    {
        var bad = Frame(Result()); bad[^1] ^= 0x80;
        Assert.Empty(Interpret([Entry(0, bad)]).Observations);
        Assert.Contains(Interpret([Entry(0, bad)]).Diagnostics, d => d.Code == "ATSM-UC3500-CHECKSUM");
        var incomplete = Interpret([Entry(0, Frame(Result())[..80])]);
        Assert.Empty(incomplete.Observations); Assert.NotEmpty(incomplete.Messages); Assert.Contains(incomplete.Diagnostics, d => d.Code == "ATSM-UC3500-INCOMPLETE");
        var malformed = Interpret([Entry(0, Frame(Result().Replace("0001,01", "0001,1", StringComparison.Ordinal)))]);
        Assert.Empty(malformed.Observations);
        var missingCheck = Interpret([Entry(0, Frame(Query)), Entry(1, [6], TraceDirection.Transmitted), Entry(2, Frame(Result())[..^1])]);
        Assert.Empty(missingCheck.Observations); Assert.Contains(missingCheck.Diagnostics, d => d.Code == "ATSM-UC3500-CHECKSUM");
    }

    [Fact]
    public void Opposite_direction_and_mismatched_identifiers_are_explicit()
    {
        var wrong = Interpret([Entry(0, Frame(Result()), TraceDirection.Transmitted)]);
        Assert.Empty(wrong.Observations); Assert.Contains(wrong.Diagnostics, d => d.Code == "ATSM-UC3500-DIRECTION");
        var mismatch = Interpret([Entry(0, Frame(Query)), Entry(1, Frame(Order.Replace("SYN-0001", "SYN-0002", StringComparison.Ordinal)), TraceDirection.Transmitted)]);
        Assert.Contains(mismatch.Diagnostics, d => d.Code == "ATSM-UC3500-CORRELATION");
        Assert.Contains(mismatch.Diagnostics, d => d.Code == "ATSM-UC3500-PENDING");
    }

    [Theory]
    [InlineData('R', RecordedBy.Host)]
    [InlineData('S', RecordedBy.Instrument)]
    public void Detects_the_direct_protocol_from_content_and_reanalyzes_old_unsupported_cases(char direction, RecordedBy recorder)
    {
        // Bracket log encoder, with the BCC deliberately stored on its own line.
        var data = Frame(Query); var check = data[^1]; var payload = "[STX]" + Query + "[ETX]";
        var log = Encoding.ASCII.GetBytes($"2026/10/25 10:00:00.000 {direction} 023{{{payload}}}\r\n2026/10/25 10:00:00.001 {direction} 001{{{(char)check}}}\r\n");
        var catalog = new DiagnosticCatalog(); var selected = catalog.Profiles.First(p => p.ContentPlugin is not null);
        var detected = DiagnosticInputDetector.Detect(log, selected, catalog);
        Assert.Equal(SysmexUrinalysisPlugin.Uc3500Profile, detected.ProfileId); Assert.Equal(recorder, detected.Recorder); Assert.False(detected.UnsupportedProtocol);
        var input = DiagnosticReader.Read(log, DiagnosticInputFormat.Auto, selected, "opaque.any", TimeSpan.Zero);
        var profile = catalog.Resolve(input.Header.Profile!); Assert.True(profile.CanConnect); Assert.False(profile.CanSimulateHost);
        var old = input with { Header = input.Header with { Properties = new Dictionary<string, string> { ["unsupportedProtocol"] = "true" } } };
        var result = DiagnosticReader.Analyze(old, profile, Timing); var message = Assert.Single(result.Messages);
        Assert.Equal(Query, message.Text); Assert.Contains("Consulta de orden", ProtocolExplainer.MessageContent(message, profile, []));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL");
    }

    [Fact]
    public async Task Direct_capture_decodes_without_sending_and_active_commands_are_rejected_before_connecting()
    {
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        var header = new TraceHeader(Guid.NewGuid(), "synthetic", "test", profile.Id, DateTimeOffset.UnixEpoch);
        var trace = new DiagnosticTraceSink(header); var channel = new SuppliedChannel(Frame(Result()), fragment: 3);
        await LiveDiagnostic.RunAsync(profile, Timing, trace, _ => ValueTask.FromResult<IInstrumentChannel>(channel), false, "test", CancellationToken.None);
        Assert.Empty(channel.Writes); Assert.Equal(1, channel.Releases);
        Assert.NotEmpty(DiagnosticReader.Analyze(new(header, trace.Snapshot()), profile, Timing).Observations);
        var connected = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => LiveDiagnostic.RunAsync(profile, Timing, trace,
            _ => { connected = true; return ValueTask.FromResult<IInstrumentChannel>(channel); }, true, "test", CancellationToken.None));
        Assert.False(connected);
    }

    [Fact]
    public void Empty_strip_means_no_order_and_result_error_codes_are_distinct_from_ER_responses()
    {
        var result = Interpret([Entry(0, Frame(Query)), Entry(1, Frame(Order[..24] + new string(' ', 10)), TraceDirection.Transmitted),
            Entry(2, Frame("ER00,000000")), Entry(3, Frame(Result(error: "00CC")))]);
        Assert.Contains("no tiene orden", DiagnosticText.Explanation(new SysmexUrinalysisPlugin(), result.Messages[1].Explanation));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ATSM-UC3500-ER00");
        Assert.Contains(result.Diagnostics, d => d.Code == "ATSM-UC3500-ANALYSIS-00CC");
        Assert.All(result.Observations, o => Assert.Contains(o.Flags, f => f.Code == "00CC"));
    }
}
