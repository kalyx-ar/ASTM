using System.Text;
using Astm.Abstractions.Tracing;
using Astm.Diagnostics;
using Astm.Plugin.Sysmex.Urinalysis;
using Astm.Plugins;

namespace Astm.Tests.Diagnostics;

public sealed class UfBinaryInterpreterTests
{
    internal static byte[] Notice(string code = "DBS_LANG_02215", string label = "Limpieza", string qualifier = "Necesidad")
    {
        var bytes = new byte[266]; bytes[0] = 1; bytes[265] = 3;
        Encoding.ASCII.GetBytes("SYNTHETIC").CopyTo(bytes.AsSpan(3)); Encoding.ASCII.GetBytes(label).CopyTo(bytes.AsSpan(37));
        Encoding.ASCII.GetBytes(code + qualifier).CopyTo(bytes.AsSpan(87)); Encoding.ASCII.GetBytes("DBS_LANG_01745").CopyTo(bytes.AsSpan(151));
        return bytes;
    }
    private static readonly DiagnosticCatalog Catalog = new();
    private static readonly DiagnosticTiming Timing = new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc);
    [Theory]
    [InlineData("DBS_LANG_02215", "Limpieza", "Limpieza requerida")]
    [InlineData("DBS_LANG_01746", "Chequeo fondo", "Chequeo de fondo requerido")]
    public void Reported_maintenance_need_has_messages_observations_and_findings_without_fabricating_a_specimen_or_operation(string code, string label, string meaning)
    {
        var selected = Catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var input = DiagnosticReader.Read(Notice(code, label), DiagnosticInputFormat.Auto, selected, "unrelated.bin", TimeSpan.Zero);
        var profile = Catalog.Resolve(input.Header.Profile!); var result = DiagnosticReader.Analyze(input, profile, Timing);
        Assert.Equal(SysmexUrinalysisPlugin.UfBinaryProfile, profile.Id);
        Assert.Single(result.Messages); var item = Assert.Single(result.Observations);
        Assert.Equal("", item.Specimen.SpecimenId); Assert.Null(item.Quality); Assert.Null(item.Value.Numeric);
        Assert.Equal("Necesidad", item.Value.RawText); Assert.Equal(code, item.Value.Coded!.Code);
        Assert.Equal(meaning, ProtocolExplainer.ResultValue(item, profile)); Assert.Equal("Aviso del equipo", ProtocolExplainer.ResultKind(item, profile));
        Assert.Equal("Requiere atención", ProtocolExplainer.Status(item, profile)); Assert.Equal("—", ProtocolExplainer.Unit(item, profile));
        Assert.Contains(result.Diagnostics, d => DiagnosticText.Finding(d, profile) == meaning);
        Assert.Empty(result.Timings); Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ASTM-DIAG-UNSUPPORTED-PROTOCOL");
        Assert.False(ProtocolExplainer.MessageHasError(result.Messages[0], profile, result.Observations));
        Assert.Empty(QualitySeriesBuilder.Build(result.Observations));
    }
    [Fact]
    public void Activity_record_is_reassembled_at_every_read_boundary_without_mixing_directions()
    {
        var plugin = new SysmexUrinalysisPlugin(); var profile = plugin.TraceProfiles.Single(p => p.Id == SysmexUrinalysisPlugin.UfBinaryProfile);
        var bytes = Notice();
        for (var split = 1; split < bytes.Length; split++)
        {
            var entries = new[] { new TraceEntry(1, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, bytes[..split]),
                new TraceEntry(2, TimeSpan.FromMilliseconds(7), DateTimeOffset.UnixEpoch, TraceDirection.Received, bytes[split..]) };
            var result = plugin.Interpret(profile, entries, new("SYN"), Guid.Empty, TimeZoneInfo.Utc);
            var message = Assert.Single(result.Messages); Assert.Single(result.Observations); Assert.Equal(1, message.FirstSequence); Assert.Equal(2, message.LastSequence);
            var otherDirection = entries[1] with { Direction = TraceDirection.Transmitted };
            Assert.Empty(plugin.Interpret(profile, [entries[0], otherDirection], new("SYN"), Guid.Empty, TimeZoneInfo.Utc).Observations);
        }
    }
    [Fact]
    public void Isolated_counter_bytes_do_not_prove_a_frame_or_an_acknowledgement()
    {
        var bytes = new byte[] { 2, 0, 0, 0, 0, 0, 247, 9, 100, 0, 0, 0, 3 };
        var profile = Catalog.Resolve(SysmexUrinalysisPlugin.UfBinaryProfile);
        var input = DiagnosticReader.Read(bytes, DiagnosticInputFormat.WireBytes, profile, "synthetic", TimeSpan.Zero);
        var result = DiagnosticReader.Analyze(input, profile, Timing); Assert.Empty(result.Messages);
        Assert.Empty(result.Observations); Assert.Empty(result.Timings);
        Assert.DoesNotContain(CommunicationStatistics.Build(result), s => s.Code is "EnquiryAck" or "FrameAck" or "OperationTotal");
        Assert.Null(DiagnosticInputDetector.Detect(bytes, Catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile), Catalog).ProfileId);
        for (var split = 1; split < bytes.Length; split++)
        {
            var fragments = input with { Entries = [new(1, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, bytes[..split]),
                new(2, TimeSpan.FromMilliseconds(10), DateTimeOffset.UnixEpoch, TraceDirection.Received, bytes[split..])] };
            Assert.Empty(DiagnosticReader.Analyze(fragments, profile, Timing).Messages);
        }
    }
    [Theory]
    [InlineData("DBS_LANG_99999", "Necesidad")]
    [InlineData("DBS_LANG_02215", "Otro")]
    public void Unknown_resource_or_changed_context_is_retained_without_asserting_a_known_need(string code, string qualifier)
    {
        var profile = Catalog.Resolve(SysmexUrinalysisPlugin.UfBinaryProfile);
        var input = DiagnosticReader.Read(Notice(code, qualifier: qualifier), DiagnosticInputFormat.WireBytes, profile, "synthetic", TimeSpan.Zero);
        var result = DiagnosticReader.Analyze(input, profile, Timing);
        Assert.Single(result.Messages); Assert.Single(result.Observations);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ASTM-UF-CONTROL-DBS_LANG_02215");
    }
    [Fact]
    public void Missing_record_end_and_inconsistent_padding_are_not_accepted_as_activity_records()
    {
        var profile = Catalog.Resolve(SysmexUrinalysisPlugin.UfBinaryProfile);
        foreach (var bytes in new[] { Notice()[..^1], Notice() })
        {
            if (bytes.Length == 266) bytes[150] = 65;
            var input = DiagnosticReader.Read(bytes, DiagnosticInputFormat.WireBytes, profile, "synthetic", TimeSpan.Zero);
            Assert.Empty(DiagnosticReader.Analyze(input, profile, Timing).Observations);
        }
    }
    [Fact]
    public void Reanalysis_uses_current_content_and_keeps_a_single_family_with_profile_specific_detection()
    {
        var selected = Catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var input = DiagnosticReader.Read(Notice(), DiagnosticInputFormat.WireBytes, selected, "UF-5000Control.txt", TimeSpan.Zero);
        input = input with { Header = input.Header with { Properties = new Dictionary<string, string> { ["unsupportedProtocol"] = "true", ["unsupportedPayload"] = "binary" } } };
        var resolved = Catalog.ResolveInterpretation(input, selected); Assert.Equal(SysmexUrinalysisPlugin.UfBinaryProfile, resolved.Id);
        var result = DiagnosticReader.Analyze(input, resolved, Timing);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ASTM-DIAG-UNSUPPORTED-PROTOCOL");
        Assert.Single(Catalog.Instruments, i => i.Id == SysmexUrinalysisPlugin.PluginId); Assert.Equal(PluginApi.Version, selected.AstmPlugin!.Describe().EngineApi);
    }
}
