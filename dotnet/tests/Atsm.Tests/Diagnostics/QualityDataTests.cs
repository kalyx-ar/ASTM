using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugin.Sysmex.Urinalysis;
using Atsm.Plugins;
using Atsm.Protocol.Hl7v2;
using Atsm.Protocol.Lis02;
using Atsm.Runtime.Quality;
using Atsm.Runtime.Sessions;
using Atsm.Runtime.Tracing;
using Atsm.Testing;
using Atsm.Tests.Architect;
using Atsm.Tests.Hl7;

namespace Atsm.Tests.Diagnostics;

public class QualityDataTests
{
    [Fact]
    public void CFX_keeps_reported_replicate_statistics_and_does_not_invent_a_target_or_lot()
    {
        var result = Cfx(CfxPlugin.CsvProfileId, "Well,Sample,Target,Cq,Content,Cq Mean,Cq Std. Dev,Starting Quantity (SQ)\nA01,SYN-C,T1,23.4,Pos-01,23.5,0.14,1000\n");
        var observation = Assert.Single(result.Observations); var context = Assert.IsType<AnalyticalQualityContext>(observation.Quality);
        Assert.Equal(SpecimenRole.Control, observation.Specimen.Role); Assert.Equal(QualityMaterialKind.PositiveControl, context.Material!.Kind);
        Assert.Null(context.Material.Lot); Assert.Null(context.Material.Level);
        Assert.Equal(23.4m, observation.Value.Numeric);
        var mean = Assert.Single(context.Statistics, s => s.Kind == ReportedStatisticKind.CqMean);
        Assert.Equal(23.5m, mean.Value.Numeric); Assert.Equal(StatisticScope.ReplicateGroup, mean.Scope); Assert.Null(mean.GroupId);
        Assert.Equal("CSV:Cq Mean", mean.SourceField);
        Assert.Equal(StatisticScope.Well, Assert.Single(context.Statistics, s => s.Kind == ReportedStatisticKind.StartingQuantity).Scope);
        var stored = JsonSerializer.Deserialize<InstrumentObservation>(JsonSerializer.Serialize(observation, TraceJson.Options), TraceJson.Options)!;
        Assert.Equal(mean, Assert.Single(stored.Quality!.Statistics, s => s.Kind == ReportedStatisticKind.CqMean));
    }

    [Theory]
    [InlineData("Std-01", QualityMaterialKind.Standard, SpecimenRole.Unknown)]
    [InlineData("NTC", QualityMaterialKind.NoTemplateControl, SpecimenRole.Control)]
    [InlineData("NRT", QualityMaterialKind.NoReverseTranscriptaseControl, SpecimenRole.Control)]
    public void CFX_distinguishes_standards_and_negative_controls(string content, QualityMaterialKind kind, SpecimenRole role)
    {
        var value = Assert.Single(Cfx(CfxPlugin.CsvProfileId, $"Well,Sample,Target,Cq,Content\nA01,SYN,T1,Undetermined,{content}\n").Observations);
        Assert.Equal(kind, value.Quality!.Material!.Kind); Assert.Equal(role, value.Specimen.Role); Assert.Null(value.Value.Numeric);
    }

    [Fact]
    public void CFX_standard_curve_statistics_are_not_converted_into_individual_measurements()
    {
        var value = Assert.Single(Cfx(CfxPlugin.CurveProfileId, "Target,Efficiency %,Slope,Y-intercept,R^2\nT1,94.2,-3.468,36.863,0.998\n").Observations);
        Assert.Null(value.Value.Numeric); Assert.Equal("", value.Specimen.SpecimenId); Assert.Equal("standard-curve", value.ResultKind);
        Assert.Equal(4, value.Quality!.Statistics.Count); Assert.All(value.Quality.Statistics, s => Assert.Equal(StatisticScope.StandardCurve, s.Scope));
        Assert.Equal(94.2m, value.Quality.Statistics[0].Value.Numeric); Assert.Equal("94.2", value.Quality.Statistics[0].Value.RawText);
        Assert.Equal(0.998m, value.Quality.Statistics[3].Value.Numeric);
        var absent = Cfx(CfxPlugin.CurveProfileId, "Target,Efficiency %,Slope,Y-intercept,R^2\nT1,not-exported,-3.468,36.863,0.998\n");
        Assert.Null(Assert.Single(absent.Observations).Quality!.Statistics[0].Value.Numeric);
        Assert.Contains(absent.Diagnostics, d => d.Code == "ATSM-CFX-NONNUMERIC-STATISTIC");
    }

    [Fact]
    public void CFX_malformed_curve_does_not_leak_a_partial_result()
    {
        var result = Cfx(CfxPlugin.CurveProfileId, "Target,Efficiency %,Slope,Y-intercept,R^2\nT1,94.2,-3.4,36,0.99\nT2,95.0\n");
        Assert.Empty(result.Observations); Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void RDML_keeps_control_type_and_run_context_without_calling_a_standard_an_instrument_calibrator()
    {
        var xml = "<rdml xmlns='http://www.rdml.org' version='1.1'><sample id='SYN'><type>std</type></sample><target id='T1'/><experiment id='E1'><run id='R1'><react id='1'><sample id='SYN'/><data><tar id='T1'/><cq>23</cq></data></react></run></experiment></rdml>";
        var value = Assert.Single(Cfx(CfxPlugin.RdmlProfileId, xml).Observations);
        Assert.Equal(QualityMaterialKind.Standard, value.Quality!.Material!.Kind); Assert.Equal("E1/R1", value.Quality.RunId);
        Assert.Equal(SpecimenRole.Unknown, value.Specimen.Role);
    }

    [Fact]
    public void ASTM_Architect_control_lot_signal_range_flags_and_module_survive_projection()
    {
        var plugin = new ArchitectPlugin(); var observation = plugin.CreateDialect(plugin.Profiles[0], new(new("ARC"), TimeProvider.System))
            .Interpret(Assert.Single(Lis02Codec.Parse(ArchitectDialectTests.PatientUpload).Messages), TraceRef.None).Observations[3];
        Assert.Equal(QualityMaterialKind.Control, observation.Quality!.Material!.Kind);
        Assert.Equal("40080", observation.Quality.Material.Lot); Assert.Equal(observation.VendorIdentity["module.serial"], observation.Quality.ModuleId);
        Assert.Equal(observation.ReferenceRangeText, observation.Quality.SourceFields["R.6"]);
    }

    [Fact]
    public void HL7_Architect_INV_and_ZCL_apply_to_the_specimen_and_order_without_crossing_orders()
    {
        var d = Hl7Delimiters.Default;
        var text = Hl7Data.Result().Replace("|||||||||P\r", "|||||||||Q\r", StringComparison.Ordinal);
        var inv = Hl7Segment.Build("INV", d, (1, "SYN-LOW"), (2, "OK"), (3, "CO"), (12, "20271231"), (16, "LOT-SYN")).Raw;
        text = text.Replace("SAC|1\r", inv + "\rSAC|1\r", StringComparison.Ordinal);
        text += "ZCL|0021^SYN^ARCHITECT|RG-SYN|20261024120000\rOBR|2|||0099^OTHER^ARCHITECT\r"
            + Hl7Segment.Build("OBX", d, (1, "1"), (2, "NM"), (3, "0099&UNDILUTED&F^OTHER^ARCHITECT"), (5, "2"), (11, "F")).Raw + "\r";
        var result = Hl7Data.Dialect().Interpret(Hl7Codec.Parse(text).Message!, TraceRef.None);
        Assert.Empty(result.Diagnostics); Assert.Equal(4, result.Observations.Count);
        Assert.All(result.Observations, o => Assert.Equal("LOT-SYN", o.Quality!.Material!.Lot));
        Assert.All(result.Observations.Take(3), o => Assert.Equal("20261024120000", o.Quality!.Calibration!.PerformedAtRaw));
        Assert.Null(result.Observations[3].Quality!.Calibration); Assert.Equal("20271231", result.Observations[0].Quality!.Material!.ExpirationRaw);
    }

    [Fact]
    public void HL7_a_calibration_for_another_assay_is_not_attached_to_the_current_result()
    {
        var result = Hl7Data.Dialect().Interpret(Hl7Codec.Parse(Hl7Data.Result() + "ZCL|OTHER^SYN^ARCHITECT|RG-SYN|20261024120000\r").Message!, TraceRef.None);
        Assert.All(result.Observations, o => Assert.Null(o.Quality!.Calibration));
        Assert.Contains(result.Diagnostics, d => d.Code == "ATSM-ARC-HL7-UNMATCHED-CALIBRATION");
    }

    [Theory]
    [InlineData("QC-SYNLOT", "SYNLOT")]
    [InlineData("42", null)]
    public void XNL_QC_modes_keep_an_unknown_lot_unknown(string id, string? lot)
    {
        var observation = Astm("sysmex.xnl.astm02.results", $"H|\\^&|||XN-450^00-11^SYN-MODULE\rP|1\rO|1||^^{id}^A||||||||Q\rR|1|^^^^WBC^1|7.1|10*3/uL||N||F||||20261024120000\rL|1\r");
        Assert.Equal(SpecimenRole.Control, observation.Specimen.Role); Assert.Equal(lot, observation.Quality!.Material!.Lot);
        Assert.Equal("SYN-MODULE", observation.Quality.ModuleId);
    }

    [Fact]
    public void CS2500_fixed_width_QC_identity_is_separated_without_guessing_a_level()
    {
        var observation = Astm("sysmex.cs2500.astm02.results", "H|\\^&\rP|1\rO|1||^^QCLOWNAMELOT123^A||||||||Q\rR|1|^^^041^PT sec^100.00^2|10.2|sec||N||||||20261024120000\rL|1\r");
        Assert.Equal("LOWNAME", observation.Quality!.Material!.Identifier); Assert.Equal("LOT123", observation.Quality.Material.Lot);
        Assert.Null(observation.Quality.Material.Level); Assert.Equal("2", observation.ResultKind); // reported average is not a replicate
    }

    [Fact]
    public void UWAM_QC_retains_composite_identifier_and_actual_analyzer_serial()
    {
        var observation = Astm("sysmex.uwam.lis2a2.without-chemistry", "H|\\^&\rP|1\rO|1|QC-SYNHX|||||||||Q||||||||||||||F\rR|1|^^^RBC^A^1^S|2.8^RAW|/ul||N||||||20261024120000|SER-SYN\rL|1\r");
        Assert.Equal("QC-SYNHX", observation.Quality!.Material!.Identifier); Assert.Null(observation.Quality.Material.Lot);
        Assert.Equal("SER-SYN", observation.Quality.ModuleId); Assert.Equal("A", observation.Quality.Method);
    }

    [Fact]
    public async Task UWAM_resource_only_messages_use_the_same_custody_boundary_and_stable_reception_identity()
    {
        var plugin = new SysmexUrinalysisPlugin(); var host = new InMemoryLabHost(); var receipt = Guid.NewGuid();
        var processor = new AstmMessageProcessor(plugin.CreateDialect(plugin.Profiles[0], new(new("U1"), TimeProvider.System)), plugin.Profiles[0], host, new("U1"), new MemoryTraceSink());
        var bytes = Encoding.ASCII.GetBytes("H|\\^&\rS|1|||||\\\\SYN-SERVER\\QC Data\\targets.csv\rL|1\r");
        await processor.ProcessAsync(bytes, receipt); await processor.ProcessAsync(bytes, receipt);
        Assert.Equal(2, host.Batches.Count); Assert.Empty(host.Batches[0].Observations);
        var reference = Assert.Single(host.Batches[0].QualityResources);
        Assert.Equal(reference.ResourceId, Assert.Single(host.Batches[1].QualityResources).ResourceId);
        Assert.Equal("\\\\SYN-SERVER\\QC Data\\targets.csv", reference.RawLocator);
        var roundTrip = JsonSerializer.Deserialize<InstrumentObservationBatch>(JsonSerializer.Serialize(host.Batches[0], TraceJson.Options), TraceJson.Options)!;
        Assert.Equal(reference, Assert.Single(roundTrip.QualityResources));
        var source = new ObservationQualityDataSource(ct => Batches(host.Batches, ct));
        Assert.Equal(2, (await Collect(source.ReadResourcesAsync(new("U1")))).Count); // repetitions are evidence, not silently deduplicated
        Assert.Empty(await Collect(source.ReadResourcesAsync(new("OTHER"))));
    }

    [Fact]
    public async Task Read_port_filters_exact_series_dimensions_and_keeps_flags_repeats_and_unknown_values()
    {
        var original = Astm("sysmex.xnl.astm02.results", "H|\\^&|||XN-450^00-11^MOD-1\rP|1\rO|1||^^QC-LOT1^A||||||||Q\rR|1|^^^^WBC^1|7.1|u||L||P||||20261024120000\rL|1\r");
        var at = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var selected = original with { ResultTime = at }; var unknown = selected with { ResultTime = null };
        var otherSystem = selected with { Test = CodeSet.Of(selected.Test.Vendor with { System = "other-system" }) };
        var patient = selected with { Specimen = selected.Specimen with { Role = SpecimenRole.Patient } };
        var data = new InstrumentObservationBatch(Guid.NewGuid(), selected.Instrument, [selected, unknown, otherSystem, patient], TraceRef.None);
        var source = new ObservationQualityDataSource(ct => Batches([data], ct));
        var query = new QualityDataQuery(InstrumentId: selected.Instrument.InstrumentId, Test: selected.Test.Vendor,
            MaterialKind: QualityMaterialKind.Control, MaterialLot: "LOT1", ModuleId: "MOD-1", FromInclusive: at, UntilExclusive: at.AddSeconds(1));
        var match = Assert.Single(await Collect(source.ReadAsync(query)));
        Assert.Equal(data.BatchId, match.BatchId); Assert.Equal("L", match.Observation.Flags.Single().Code); Assert.Equal("P", match.Observation.InstrumentStatus);
        Assert.Equal(2, (await Collect(source.ReadAsync(query with { IncludePatientMeasurements = true }))).Count);
        Assert.Empty(await Collect(source.ReadAsync(query with { MaterialLot = "NOT-ASSIGNED" })));
        Assert.Empty(await Collect(source.ReadAsync(query with { UntilExclusive = at, FromInclusive = at.AddSeconds(-1) })));
    }

    [Fact]
    public async Task Read_port_cancellation_happens_before_calling_the_store()
    {
        var read = false; var source = new ObservationQualityDataSource(ct => { read = true; return Batches([], ct); });
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(source.ReadAsync(new(), stop.Token)));
        Assert.False(read);
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(source.ReadAsync(new(FromInclusive: DateTimeOffset.MaxValue, UntilExclusive: DateTimeOffset.MinValue))));
    }

    private static ContentInterpretation Cfx(string id, string text)
    { var plugin = new CfxPlugin(); return plugin.Interpret(plugin.Profiles.Single(p => p.Id == id), Encoding.UTF8.GetBytes(text), new("CFX-SYN"), TraceRef.None); }
    private static InstrumentObservation Astm(string id, string text)
    { var profile = new Atsm.Diagnostics.DiagnosticCatalog().Resolve(id); return Assert.Single(profile.AstmPlugin!.CreateDialect(profile.AstmProfile!, new(new("SYN-I"), TimeProvider.System)).Interpret(Assert.Single(Lis02Codec.Parse(text).Messages), TraceRef.None).Observations); }
    private static async IAsyncEnumerable<InstrumentObservationBatch> Batches(IEnumerable<InstrumentObservationBatch> batches, [EnumeratorCancellation] CancellationToken token)
    { foreach (var batch in batches) { token.ThrowIfCancellationRequested(); yield return batch; } await Task.CompletedTask; }
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> items)
    { var values = new List<T>(); await foreach (var item in items) values.Add(item); return values; }
}
