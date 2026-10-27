using System.Text;
using Astm.Abstractions.Model;
using Astm.Diagnostics;
using Astm.Plugin.Sysmex.Urinalysis;
using Astm.Plugins;

namespace Astm.Tests.Diagnostics;

public sealed class QualityPresentationTests
{
    private static readonly DiagnosticCatalog Catalog = new();
    private static readonly DiagnosticProfile Profile = Catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
    private static readonly DiagnosticTiming Timing = new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc);
    private static InstrumentObservation Item(string flag, int sequence = 0) => Read($"H|\\^&|||U-WAM\rP|1\rO|1|QC-SYN|||||||||Q||||||||||||||F\rR|1|^^^WBC^A^1^S|7.2^RAW|/uL||{flag}|||||||UF-5000\rL|1|N\r").Single()
        with { Trace = new(Guid.Empty, sequence, sequence) };
    private static IReadOnlyList<InstrumentObservation> Read(string text) => DiagnosticReader.Analyze(DiagnosticReader.Read(Encoding.ASCII.GetBytes(text), DiagnosticInputFormat.AstmMessage, Profile, "synthetic", TimeSpan.Zero), Profile, Timing).Observations;

    [Theory]
    [InlineData("N", "Normal")]
    [InlineData("W", "Baja fiabilidad")]
    [InlineData("A", "Error de análisis")]
    public void Quality_preserves_the_actual_measurement_state_even_for_a_single_record(string code, string text)
    {
        var series = Assert.Single(QualitySeriesBuilder.Build([Item(code)], (IInstrumentValueKnowledge)Profile.AstmPlugin!));
        Assert.Equal(text, QualityStatus.Summarize(series, Profile));
        Assert.DoesNotContain("Una medición", QualityStatus.Summarize(series, Profile));
    }

    [Fact]
    public void A_group_keeps_each_state_and_places_errors_before_normal_results()
    {
        var items = new[] { Item("N", 0), Item("A", 1), Item("N", 2), Item("W", 3) };
        var series = Assert.Single(QualitySeriesBuilder.Build(items, (IInstrumentValueKnowledge)Profile.AstmPlugin!));
        var text = QualityStatus.Summarize(series, Profile);
        Assert.Contains("Error de análisis (1)", text); Assert.Contains("Baja fiabilidad (1)", text); Assert.EndsWith("Normal (2)", text);
        Assert.Equal(4, series.MeasurementCount); Assert.Equal(2, series.Flagged);
        Assert.Equal("Normal", ProtocolExplainer.Status(series.Records[0], Profile));
    }

    [Fact]
    public void UF5000_QC_components_are_not_a_format_and_do_not_split_one_material_by_each_value()
    {
        var first = "H|\\^&|||UF-5000^SYN-VERSION^SYN-SERIAL\rP|1\rO|1||^^CONTROL-SYN^B||||||||Q||||||||||||||F\rR|1|^^^^RBC^1^^^|208.60^208.6^UNASSIGNED^OTHER^META|/uL^/uL||N|||||||\rL|1|N\r";
        var second = first.Replace("208.60^208.6^UNASSIGNED^OTHER^META", "210.00^210.0^UNASSIGNED^OTHER^META", StringComparison.Ordinal);
        var records = Read(first + second); var observation = records[0];
        Assert.Equal("CONTROL-SYN", observation.Specimen.SpecimenId); Assert.Equal("CONTROL-SYN", observation.Quality!.Material!.Identifier);
        Assert.Null(observation.Quality.Method); Assert.Equal("", observation.VendorIdentity["analysis.method"]);
        Assert.Null(observation.ResultKind); Assert.Equal("Numérico", ProtocolExplainer.ResultKind(observation, Profile));
        Assert.Equal("/uL", ProtocolExplainer.Unit(observation, Profile)); Assert.Equal("/uL^/uL", observation.Value.RawUnit);
        Assert.Equal("208.60^208.6^UNASSIGNED^OTHER^META", observation.VendorIdentity["result.value.raw"]);
        Assert.Equal("/uL^/uL", observation.VendorIdentity["result.unit.raw"]);
        var series = Assert.Single(QualitySeriesBuilder.Build(records, (IInstrumentValueKnowledge)Profile.AstmPlugin!));
        Assert.Equal(2, series.NumericCount); Assert.Equal(209.30m, series.Median); Assert.Equal("/uL", series.Unit);
    }

    [Fact]
    public void All_catalog_plugins_share_readable_type_and_unit_fallbacks_without_mutating_raw_fields()
    {
        var original = Item("N") with { ResultKind = "/73.5", Test = CodeSet.Of(new("SYN", null, "urn:synthetic")), Value = new("73.5", 73.5m, RawUnit: "ch^") };
        foreach (var profile in Catalog.Profiles)
        {
            Assert.Equal("Numérico", ProtocolExplainer.ResultKind(original, profile));
            Assert.Equal("ch", ProtocolExplainer.Unit(original, profile));
        }
        Assert.Equal("/73.5", original.ResultKind); Assert.Equal("ch^", original.Value.RawUnit);
    }

    [Theory]
    [InlineData("/uL^/uL", "/uL")]
    [InlineData("ch^", "ch")]
    [InlineData("^", null)]
    [InlineData("10^3/uL", "10^3/uL")]
    [InlineData("mS/cm", "mS/cm")]
    public void Unit_cleanup_preserves_exponents_and_only_collapses_empty_or_duplicate_components(string raw, string? expected)
    {
        var value = new ObservationValue("1", 1, RawUnit: raw);
        Assert.Equal(expected, InstrumentUnits.Symbol(value)); Assert.Equal(raw, value.RawUnit);
    }

    [Theory]
    [InlineData("REFLECTANCE1", "BLD", "%")]
    [InlineData("REFLECTANCE2", "BLD", "%")]
    [InlineData("QUALITATIVE", "pH", "Adimensional")]
    [InlineData("QUALITATIVE", "SG.strip", "Adimensional")]
    [InlineData("QUALITATIVE", "BLD", "—")]
    [InlineData("CONCENTRATION", "BLD", "No informada")]
    public void UC3500_reports_protocol_defined_units_separately_and_does_not_guess_missing_concentration_units(string kind, string test, string unit)
    {
        var observation = Item("N") with { ResultKind = kind, Value = new("50", 50), Test = CodeSet.Of(new(test, null, "urn:astm:vendor:sysmex.uc3500")) };
        var profile = Catalog.Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        Assert.Equal(unit, ProtocolExplainer.Unit(observation, profile)); Assert.Null(observation.Value.RawUnit); Assert.Null(observation.Value.Unit);
    }

    [Fact]
    public void Direct_UC3500_error_and_analysis_state_use_plugin_knowledge_in_observations_and_quality()
    {
        var source = Item("N") with { Test = CodeSet.Of(new("BLD", null, "urn:astm:vendor:sysmex.uc3500")),
            VendorIdentity = new(new Dictionary<string, string> { ["analysis.state"] = "0" }), InstrumentStatusMeaning = InstrumentStatusMeaning.Warning,
            Flags = [new("00CC", null, "urn:astm:vendor:sysmex.uc3500")] };
        var profile = Catalog.Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        Assert.Equal("Error de solicitud (00CC)", ProtocolExplainer.Status(source, profile));
        var series = Assert.Single(QualitySeriesBuilder.Build([source], (IInstrumentValueKnowledge)profile.TracePlugin!));
        Assert.Equal("Error de solicitud (00CC)", QualityStatus.Summarize(series, profile));
        Assert.Equal("Analizado", ProtocolExplainer.Status(source with { Flags = [], InstrumentStatusMeaning = InstrumentStatusMeaning.Unknown }, profile));
        Assert.Contains("Error de solicitud", ProtocolExplainer.Status(source with { InstrumentStatusMeaning = InstrumentStatusMeaning.NotPerformed }, profile));
    }

    [Fact]
    public void Coded_outcomes_do_not_become_numeric_measurements_because_their_code_is_a_digit()
    {
        var item = Item("N") with { Test = CodeSet.Of(new("SYN", null, "urn:synthetic")), ResultKind = "I",
            Value = new("1", 1, Coded: new("1", "SYN-CATEGORY", "urn:synthetic")) };
        foreach (var profile in Catalog.Profiles)
        {
            Assert.Equal("Cualitativo", ProtocolExplainer.ResultKind(item, profile)); Assert.Equal("—", ProtocolExplainer.Unit(item, profile));
        }
        var series = Assert.Single(QualitySeriesBuilder.Build([item]));
        Assert.Equal(0, series.NumericCount); Assert.Null(series.Median); Assert.Null(series.Range.Minimum);
        Assert.Equal(1m, item.Value.Numeric); Assert.Equal("1", item.Value.RawText);
    }
}
