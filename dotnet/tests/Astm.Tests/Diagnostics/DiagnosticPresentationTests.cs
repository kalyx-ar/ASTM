using Astm.Abstractions.Model;
using Astm.Diagnostics;
using Astm.Plugins;

namespace Astm.Tests.Diagnostics;

public sealed class DiagnosticPresentationTests
{
    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(156, "00:00:00.156")]
    [InlineData(21623400, "06:00:23.400")]
    [InlineData(90061001, "25:01:01.001")]
    [InlineData(-1001, "-00:00:01.001")]
    public void Durations_are_readable_and_do_not_wrap_at_midnight(long milliseconds, string text) =>
        Assert.Equal(text, DiagnosticDuration.Format(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void A_missing_clock_is_not_zero() => Assert.Equal("—", DiagnosticDuration.Format(null));

    [Fact]
    public void A_different_communication_breaks_a_repetition_run_without_reordering_original_rows()
    {
        var rows = new[] { new DiagnosticRow(["A"], "A1", "A"), new(["A"], "A2", "A"), new(["B"], "B", "B"), new(["A"], "A3", "A") };
        var groups = DiagnosticGrouping.Build(rows, true, consecutive: true);
        Assert.Equal(new[] { 2, 1, 1 }, groups.Select(g => g.Count));
        Assert.Equal(new[] { "A1", "A2", "B", "A3" }, groups.SelectMany(g => g.Children).Select(r => r.Details));
        Assert.Equal(3, groups.Select(g => g.Key).Distinct().Count());
        Assert.Equal(2, DiagnosticGrouping.Build(rows, true).Count);
    }

    [Fact]
    public void Nul_read_has_a_short_description_without_an_invented_ACK_meaning()
    {
        var entry = new Astm.Abstractions.Tracing.TraceEntry(1, TimeSpan.Zero, DateTimeOffset.UnixEpoch, Astm.Abstractions.Tracing.TraceDirection.Received, new byte[5]);
        Assert.Equal("Fragmento 5 bytes - NUL", ProtocolExplainer.Entry(entry, new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId)));
        Assert.Equal("Inicio de captura", ProtocolExplainer.Entry(entry with { Direction = Astm.Abstractions.Tracing.TraceDirection.Event, Data = ReadOnlyMemory<byte>.Empty, Detail = "OPEN" }, new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId)));
    }

    [Fact]
    public void Short_binary_fragment_shows_its_content_and_does_not_turn_a_payload_byte_into_a_link_ACK()
    {
        var profile = new DiagnosticCatalog().Resolve(Astm.Plugin.Sysmex.Urinalysis.SysmexUrinalysisPlugin.UfBinaryProfile);
        var entry = new Astm.Abstractions.Tracing.TraceEntry(1, TimeSpan.Zero, DateTimeOffset.UnixEpoch, Astm.Abstractions.Tracing.TraceDirection.Received, new byte[] { 6 });
        Assert.Equal("ACK (06)", ProtocolExplainer.Entry(entry, profile));
        Assert.Equal("ENQ (05)", ProtocolExplainer.Entry(entry with { Data = new byte[] { 5 } }, profile));
        Assert.Equal("EOT (04)", ProtocolExplainer.Entry(entry with { Data = new byte[] { 4 } }, profile));
        Assert.Equal("FF", ProtocolExplainer.Entry(entry with { Data = new byte[] { 255 } }, profile));
        Assert.Equal("Fragmento 3 bytes - NUL (00) ACK (06) ETX (03)", ProtocolExplainer.Entry(entry with { Data = new byte[] { 0, 6, 3 } }, profile));
    }

    [Fact]
    public void Import_metadata_is_not_a_finding_and_real_source_problems_are_explicit()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var valid = "2026/10/25 10:00:00.000 R 001{[ENQ]}\r\n";
        DiagnosticAnalysis Read(string text) => DiagnosticReader.Analyze(DiagnosticReader.Read(System.Text.Encoding.ASCII.GetBytes(text), DiagnosticInputFormat.SysmexLog,
            profile, "synthetic", TimeSpan.Zero), profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        Assert.DoesNotContain(Read(valid).Diagnostics, d => d.Code.Contains("SOURCE", StringComparison.Ordinal));
        var invalid = Read("2026/99/25 10:00:00.000 R 001{[ENQ]}\r\n" + valid.Replace("001", "009", StringComparison.Ordinal));
        Assert.Equal("Registros omitidos por fecha inválida: 1", DiagnosticText.Finding(Assert.Single(invalid.Diagnostics, d => d.Code == "ASTM-DIAG-SOURCE-SKIPPED")));
        Assert.Equal("Registros con longitud informada distinta de los bytes capturados: 1", DiagnosticText.Finding(Assert.Single(invalid.Diagnostics, d => d.Code == "ASTM-DIAG-SOURCE-LENGTH-MISMATCH")));
        Assert.Equal("Mensaje concreto del capturador", DiagnosticText.Finding(new("ASTM-DIAG-SOURCE-LIMITATION", Astm.Abstractions.Diagnostics.AstmSeverity.Information, "Mensaje concreto del capturador")));
    }

    [Theory]
    [InlineData("PNG&R&[UF-5000^SYN]&R&[SYN]_SCATTER.png", true)]
    [InlineData("SYN-DATE&PNG\\[SYN-MODULE]\\file.PNG", true)]
    [InlineData("https://example.invalid/scatter.JpEg?ref=1", true)]
    [InlineData("data:image/png;base64,SYNTHETIC", true)]
    [InlineData("C:\\SYN\\plot.tiff", true)]
    [InlineData("plot.png.bak", false)]
    [InlineData("Informe sobre PNG", false)]
    [InlineData("SYN.pdf", false)]
    [InlineData("42", false)]
    public void Image_references_work_across_profiles_without_a_vendor_type_code(string raw, bool image)
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var value = new ObservationValue(raw, Numeric: image ? 42 : null, RawUnit: "SYN");
        var observation = new InstrumentObservation(Guid.NewGuid(), new("SYN"), new("SYN", Role: SpecimenRole.Control),
            CodeSet.Of(new("SYN", null, "SYN")), value, null, [], "", InstrumentStatusMeaning.Final, null, null, null, null, [], [], VendorIdentity.Empty, new(Guid.Empty, 0, 0));
        Assert.Equal(image, InstrumentContent.IsImageReference(observation));
        if (!image) return;
        Assert.Equal("Imagen", ProtocolExplainer.ResultKind(observation, profile));
        Assert.Equal("Imagen", ProtocolExplainer.ResultValue(observation, profile));
        Assert.Equal("—", ProtocolExplainer.Unit(observation, profile));
        var series = Assert.Single(QualitySeriesBuilder.Build([observation]));
        Assert.Equal(0, series.NumericCount); Assert.Null(series.Median); Assert.Null(series.Range.Minimum);
        Assert.Equal("Imagen", Assert.Single(series.ValueFrequencies).Value);
        Assert.Equal(raw, observation.Value.RawText);
    }
}
