using System.Text;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Tracing;
using Atsm.Diagnostics;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugin.Sysmex.Urinalysis;
using Atsm.Plugins;

namespace Atsm.Tests.Diagnostics;

public sealed class DiagnosticUsabilityTests
{
    private static readonly DiagnosticTiming Timing = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeZoneInfo.Utc);
    private static readonly string[] Records = ["H|\\^&|||UF-5000", "P|1", "O|1|SYN-SAMPLE", "R|1|^^^WBC^^^F|1.1^RAW|/uL", "L|1|N"];
    private static byte[] Log(char direction) => Encoding.Latin1.GetBytes(string.Join("\r\n", Records.Select((record, i) => $"2026/10/25 10:00:00.{i:000} {direction} 000{{{record}[CR]}}")));

    [Theory]
    [InlineData('R', RecordedBy.Host)]
    [InlineData('S', RecordedBy.Instrument)]
    public void Detects_Sysmex_log_and_recorder_side_before_reconstructing_results(char direction, RecordedBy recorder)
    {
        var catalog = new DiagnosticCatalog(); var selected = catalog.Resolve(CfxPlugin.CsvProfileId); var bytes = Log(direction);
        var detection = DiagnosticInputDetector.Detect(bytes, selected, catalog);
        Assert.Equal(DiagnosticInputFormat.SysmexLog, detection.Format); Assert.Equal(recorder, detection.Recorder);
        var input = DiagnosticReader.Read(bytes, DiagnosticInputFormat.Auto, selected, "opaque.log", TimeSpan.Zero);
        var profile = catalog.Resolve(input.Header.Profile!); var analysis = DiagnosticReader.Analyze(input, profile, Timing);
        Assert.Equal(recorder.ToString(), input.Header.Properties!["recordedBy"]);
        Assert.All(input.Entries.Where(e => e.Direction != TraceDirection.Event), e => Assert.Equal(TraceDirection.Received, e.Direction));
        Assert.Single(analysis.Messages); Assert.Single(analysis.Observations);
        Assert.Equal("SYN-SAMPLE", analysis.Observations[0].Specimen.SpecimenId);
    }

    [Fact]
    public void Manual_recorder_override_is_respected_and_unknown_side_is_explicit()
    {
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.WithoutChemistryProfile);
        var input = DiagnosticReader.Read(Log('R'), DiagnosticInputFormat.SysmexLog, profile, "log", TimeSpan.Zero, RecordedBy.Instrument);
        Assert.All(input.Entries, e => Assert.Equal(TraceDirection.Transmitted, e.Direction));
        var idle = Encoding.ASCII.GetBytes("2026/10/25 10:00:00.000 S 001{[ENQ]}\r\n");
        var capture = DiagnosticReader.Read(idle, DiagnosticInputFormat.Auto, profile, "log", TimeSpan.Zero);
        Assert.Contains(DiagnosticReader.Analyze(capture, profile, Timing).Diagnostics, d => d.Code == "ATSM-DIAG-DIRECTION-UNCERTAIN");
    }

    [Fact]
    public void HL7_inside_a_monitor_log_keeps_its_protocol_and_infers_the_instrument_result_direction()
    {
        foreach (var direction in new[] { 'R', 'S' })
        {
            var log = Encoding.ASCII.GetBytes($"2026/10/25 10:00:00.000 {direction} 000{{[VT]MSH|^~\\&|UF-5000||||20261025100000||OUL^R22|SYN-ID|P|2.5.1[CR][FS][CR]}}\r\n");
            var catalog = new DiagnosticCatalog(); var selected = catalog.Resolve("abbott.architect.hl7-2.5.1");
            var detection = DiagnosticInputDetector.Detect(log, selected, catalog);
            Assert.Equal("abbott.architect.hl7-2.5.1", detection.ProfileId); Assert.False(detection.UnsupportedProtocol);
            var capture = DiagnosticReader.Read(log, DiagnosticInputFormat.Auto, selected, "opaque.log", TimeSpan.Zero);
            Assert.Equal(TraceDirection.Received, Assert.Single(capture.Entries).Direction);
            var analysis = DiagnosticReader.Analyze(capture, catalog.Resolve(detection.ProfileId!), Timing);
            Assert.Equal("OUL^R22", Assert.Single(analysis.Messages).Type);
            Assert.DoesNotContain(analysis.Diagnostics, d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL");
            var unrelated = catalog.Resolve(CfxPlugin.CsvProfileId);
            Assert.Null(DiagnosticInputDetector.Detect(log, unrelated, catalog).ProfileId);
            Assert.Throws<InvalidDataException>(() => DiagnosticReader.Read(log, DiagnosticInputFormat.Auto, unrelated, "opaque.log", TimeSpan.Zero));
        }
    }

    [Fact]
    public void Unsupported_internal_binary_log_keeps_bytes_without_inventing_ASTM_records()
    {
        var bytes = Encoding.Latin1.GetBytes("2026/10/25 10:00:00.000 S 007{[STX]Y<8~\\[ETX]}\r\n");
        var catalog = new DiagnosticCatalog(); var profile = catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var detection = DiagnosticInputDetector.Detect(bytes, profile, catalog); Assert.True(detection.UnsupportedProtocol);
        var capture = DiagnosticReader.Read(bytes, DiagnosticInputFormat.Auto, profile, "internal.log", TimeSpan.Zero);
        var analysis = DiagnosticReader.Analyze(capture, profile, Timing);
        Assert.NotEmpty(analysis.Entries); Assert.Empty(analysis.Observations); Assert.Empty(analysis.Messages);
        Assert.Contains(analysis.Diagnostics, d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL");
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Code.Contains("INVALID-FRAME", StringComparison.Ordinal));
    }

    [Fact]
    public void Ambiguous_or_unknown_content_never_silently_selects_a_format()
    {
        var catalog = new DiagnosticCatalog(); var selected = catalog.Profiles[0];
        var unknown = DiagnosticInputDetector.Detect("opaque bytes"u8.ToArray(), selected, catalog);
        Assert.Empty(unknown.Candidates); Assert.Throws<InvalidDataException>(() => unknown.Format);
        var mixed = Log('R').Concat(Encoding.Latin1.GetBytes("\nINFO 2026-10-25 10:00:00.000 AM \x96 Synthetic <<< >\x05<1\r\n")).ToArray();
        var ambiguous = DiagnosticInputDetector.Detect(mixed, selected, catalog);
        Assert.Equal(2, ambiguous.Candidates.Count); Assert.False(ambiguous.Recognized); Assert.Throws<InvalidDataException>(() => ambiguous.Format);
    }

    [Theory]
    [InlineData("Well,Sample,Target,Cq\nA01,SYN,T1,23.4\n", DiagnosticInputFormat.Export, CfxPlugin.CsvProfileId)]
    [InlineData("Target,Efficiency %,Slope,Y-intercept,R^2\nT1,99,-3,40,0.99\n", DiagnosticInputFormat.Export, CfxPlugin.CurveProfileId)]
    [InlineData("<rdml version=\"1.1\"/>", DiagnosticInputFormat.Export, CfxPlugin.RdmlProfileId)]
    [InlineData("H|\\^&\rP|1\rL|1\r", DiagnosticInputFormat.AstmMessage, StandardAstmPlugin.ProfileId)]
    [InlineData("MSH|^~\\&|SYN||||20261025100000||OUL^R22|1|P|2.5.1\r", DiagnosticInputFormat.Hl7Message, null)]
    public void Detects_by_content_independently_of_extension(string content, DiagnosticInputFormat format, string? profileId)
    {
        var catalog = new DiagnosticCatalog(); var detection = DiagnosticInputDetector.Detect(Encoding.UTF8.GetBytes(content), catalog.Profiles[0], catalog);
        Assert.Equal(format, detection.Format); Assert.Equal(profileId, detection.ProfileId);
    }

    [Fact]
    public void Grouping_preserves_every_original_detail_and_does_not_truncate_after_2000_rows()
    {
        var rows = Enumerable.Range(0, 10001).Select(i => new DiagnosticRow(["Advertencia", "Trama inválida", "entry:" + i], "ORIGINAL-" + i, "same-code")).ToArray();
        var group = Assert.Single(DiagnosticGrouping.Build(rows, group: true));
        Assert.Equal(10001, group.Count); Assert.Same(rows[^1], group.Children[^1]);
        Assert.Equal("ORIGINAL-5000", group.Children[5000].Details);
        var summary = group.Summary(n => n + " valores"); Assert.Equal("10001 valores", summary.Cells[2]);
        Assert.Equal(rows.Length, DiagnosticGrouping.Build(rows, group: false).Count);
    }

    [Fact]
    public void Different_values_share_a_group_but_keys_never_collide_on_separators()
    {
        Assert.NotEqual(DiagnosticGrouping.Key("A", "B\u001fC"), DiagnosticGrouping.Key("A\u001fB", "C"));
        var groups = DiagnosticGrouping.Build([new(["WBC", "1.1"], "first", "test-WBC"), new(["WBC", "2.2"], "second", "test-WBC")], true);
        Assert.Equal(2, Assert.Single(groups).Count); Assert.Equal("second", groups[0].Children[1].Details);
    }

    [Fact]
    public void Uwam_is_one_instrument_with_two_query_deadlines_in_the_same_plugin()
    {
        var catalog = new DiagnosticCatalog(); var uwam = Assert.Single(catalog.Instruments, i => i.Id == SysmexUrinalysisPlugin.PluginId);
        Assert.Equal(8, catalog.Instruments.Count); Assert.Equal(4, uwam.Profiles.Count);
        var astm = uwam.Profiles.Where(p => p.AstmProfile is not null).ToArray();
        Assert.Same(astm[0].AstmPlugin, astm[1].AstmPlugin);
        Assert.All(uwam.Profiles.Where(p => p.TracePlugin is not null), p => Assert.Same(astm[0].AstmPlugin, p.TracePlugin));
        Assert.Equal(new[] { 6d, 15d }, astm.Select(p => p.AstmProfile!.QueryAnswerBudget.TotalSeconds));
        Assert.Equal(astm[0].AstmProfile!.Link, astm[1].AstmProfile!.Link);
        Assert.NotEqual(astm[0].Configuration, astm[1].Configuration);
    }

    [Fact]
    public void Expert_meanings_use_plugin_vocabulary_and_keep_raw_record_text()
    {
        var catalog = new DiagnosticCatalog(); var uwam = catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var raw = "H|\\^&\rS|1|||||SYN-RESOURCE\rL|1\r";
        Assert.Contains("control de calidad", ProtocolExplainer.RecordNames(raw, uwam));
        Assert.Contains("R315168", ProtocolExplainer.RecordEvidence(raw, uwam));
        Assert.Contains("sin interpretación documentada", ProtocolExplainer.RecordNames("S|1\r", catalog.Resolve(StandardAstmPlugin.ProfileId)));
        Assert.DoesNotContain('\r', ProtocolExplainer.SingleLine(raw)); Assert.DoesNotContain('\n', ProtocolExplainer.SingleLine(raw));
        Assert.Equal("H|\\^&\rS|1|||||SYN-RESOURCE\rL|1\r", raw);
        Assert.Contains("Solicitud de inicio", ProtocolExplainer.Entry(new(0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, new byte[] { 5 }), uwam));
    }

    [Fact]
    public void Spanish_findings_do_not_mutate_the_invariant_original_or_code()
    {
        var finding = new AtsmDiagnostic("ATSM-DIAG-LIS01-INVALID-FRAME", AtsmSeverity.Warning, "STX inside a frame", "entry:217");
        Assert.Equal("Se recibió un inicio de trama (STX) dentro de otra trama abierta.", DiagnosticText.Finding(finding));
        Assert.Equal("STX inside a frame", finding.Message); Assert.Equal("entry:217", finding.Location);
        Assert.Contains("sin una solicitud", DiagnosticText.Finding(new("ATSM-STD-RESULT-WITHOUT-ORDER", AtsmSeverity.Warning, "Original")));
    }

    [Theory]
    [InlineData(760)]
    [InlineData(1400)]
    public void Automatic_columns_fill_the_viewport_and_give_content_the_available_room(double viewport)
    {
        var headers = new[] { "#", "Dirección", "Tipo", "Contenido" };
        var rows = new[] { new DiagnosticRow(["1", "Equipo → anfitrión", "Resultados", new string('x', 400)], "original", "group") };
        var widths = DiagnosticColumnLayout.Fit(headers, rows, viewport, 3);
        Assert.Equal(viewport, widths.Sum() + DiagnosticColumnLayout.Gutter, 5);
        Assert.True(widths[3] > widths[0]); Assert.All(widths, w => Assert.True(w >= 48));
    }

    [Fact]
    public void Very_narrow_viewports_scroll_instead_of_hiding_column_headers()
    {
        var headers = new[] { "Muestra", "Posición", "Determinación", "Valor original", "Estado del equipo" };
        var widths = DiagnosticColumnLayout.Fit(headers, [], 100, 4);
        Assert.True(widths.Sum() + DiagnosticColumnLayout.Gutter > 100); Assert.All(widths, w => Assert.True(w >= 48));
    }
}
