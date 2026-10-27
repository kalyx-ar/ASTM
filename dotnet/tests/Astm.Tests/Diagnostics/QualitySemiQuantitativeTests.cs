using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;
using Astm.Diagnostics;
using Astm.Plugin.Sysmex.Urinalysis;
using Astm.Plugins;

namespace Astm.Tests.Diagnostics;

public sealed class QualitySemiQuantitativeTests
{
    private static readonly SysmexUrinalysisPlugin Plugin = new();
    private static InstrumentObservation Item(int index, string raw, string test = "BLD", string kind = "QUALITATIVE")
    {
        var parsed = InstrumentNumericValue.Parse(raw);
        return new(new Guid(index, 0, 0, new byte[8]), new("SYN"), new("CONTROL-SYN", Role: SpecimenRole.Control),
            CodeSet.Of(new(test, null, "urn:astm:vendor:sysmex.uc3500")), new(raw, parsed.Numeric, parsed.Comparator), null, [], "0",
            InstrumentStatusMeaning.Unknown, kind, null, null, null, [], [], VendorIdentity.Empty, new(Guid.Empty, index, index))
        { Quality = new(new(QualityMaterialKind.Control, "CONTROL-SYN", "LOT-SYN")) };
    }

    [Fact]
    public void Crosses_are_ordered_categories_and_do_not_become_a_concentration_or_arithmetic_SD()
    {
        var items = new[] { Item(1, "-     "), Item(2, "2+    "), Item(3, "-     "),
            Item(4, "      ") with { InstrumentStatusMeaning = InstrumentStatusMeaning.NotPerformed } };
        var series = Assert.Single(QualitySeriesBuilder.Build(items, Plugin));
        Assert.Equal("0", series.Range.Minimum); Assert.Equal("0", series.Range.Median); Assert.Equal("2+", series.Range.Maximum);
        Assert.Equal(4, series.MeasurementCount); Assert.Equal(1, series.NotPerformed); Assert.Equal(0, series.NumericCount);
        Assert.Null(series.StandardDeviation); Assert.Null(series.Mean); Assert.Null(items[1].Value.Numeric);
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        Assert.Equal("Semicuantitativo (cruces)", ProtocolExplainer.ResultKind(items[0], profile));
        Assert.Equal("Semicuantitativo (cruces)", ProtocolExplainer.ResultKind(items[1], profile));
        Assert.Equal("2+    ", items[1].Value.RawText);
    }

    [Theory]
    [InlineData(">=50", ">=", ">=50", null)]
    [InlineData("<=50", "<=", null, "<=50")]
    [InlineData(">50", ">", ">50", null)]
    [InlineData("<50", "<", null, "<50")]
    [InlineData("≥50", ">=", ">=50", null)]
    [InlineData("≤50", "<=", null, "<=50")]
    public void Censored_values_keep_the_comparator_and_only_the_reported_finite_endpoint(string raw, string comparison, string? minimum, string? maximum)
    {
        var item = Item(1, raw, "P/C", "CONCENTRATION");
        Assert.Equal(comparison, item.Value.Comparator); Assert.Equal(50m, item.Value.Numeric);
        var series = Assert.Single(QualitySeriesBuilder.Build([item], Plugin));
        Assert.Equal(minimum, series.Range.Minimum); Assert.Equal(maximum, series.Range.Maximum);
        Assert.Equal(1, series.Range.BoundedCount); Assert.Equal(0, series.NumericCount); Assert.Null(series.StandardDeviation);
        Assert.Equal("Valor semicuantitativo", ProtocolExplainer.ResultKind(item, new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.Uc3500Profile)));
        Assert.Equal(raw, item.Value.RawText);
    }

    [Fact]
    public void Opposite_unbounded_results_do_not_invent_a_global_interval_or_an_exact_median()
    {
        var series = Assert.Single(QualitySeriesBuilder.Build([Item(1, ">=50", kind: "CONCENTRATION"), Item(2, "<=20", kind: "CONCENTRATION")], Plugin));
        Assert.Null(series.Range.Minimum); Assert.Null(series.Range.Maximum); Assert.Null(series.Median);
        Assert.Equal(2, series.Range.BoundedCount); Assert.Equal(2, series.MeasurementCount);
        Assert.Equal(2, series.ValueFrequencies.Count);
    }

    [Fact]
    public void Even_ordinal_medians_use_the_two_central_categories_without_inventing_a_half_cross()
    {
        var series = Assert.Single(QualitySeriesBuilder.Build([Item(1, "-"), Item(2, "2+")], Plugin));
        Assert.Equal("0 – 2+", series.Range.Median); Assert.Null(series.Median); Assert.Null(series.StandardDeviation);
    }

    [Fact]
    public void Unknown_cross_categories_are_retained_without_guessed_order_or_arithmetic()
    {
        var series = Assert.Single(QualitySeriesBuilder.Build([Item(1, "2+"), Item(2, "10")], Plugin));
        Assert.True(series.Range.Incomparable); Assert.Null(series.Range.Minimum); Assert.Null(series.Range.Median); Assert.Null(series.Range.Maximum);
        Assert.Equal(0, series.NumericCount); Assert.Equal(2, series.MeasurementCount);
    }

    [Fact]
    public void The_unperformed_column_counts_every_communicated_record_including_a_duplicate()
    {
        var omitted = Item(1, "") with { InstrumentStatusMeaning = InstrumentStatusMeaning.NotPerformed };
        var series = Assert.Single(QualitySeriesBuilder.Build([omitted, omitted], Plugin));
        Assert.Equal(2, series.NotPerformed); Assert.Equal(2, series.MeasurementCount); Assert.Null(series.Range.Median);
    }

    [Fact]
    public void Censored_crosses_preserve_the_ordinal_limit_and_plain_positive_is_not_equal_to_one_plus()
    {
        var bound = Assert.Single(QualitySeriesBuilder.Build([Item(1, ">=1+", "A/C")], Plugin));
        Assert.Equal(">=1+", bound.Range.Minimum); Assert.Null(bound.Range.Maximum); Assert.Null(bound.StandardDeviation);
        var ordered = Assert.Single(QualitySeriesBuilder.Build([Item(1, "1+"), Item(2, "+")], Plugin));
        Assert.Equal("+", ordered.Range.Minimum); Assert.Equal("1+", ordered.Range.Maximum);
    }

    [Fact]
    public void Levels_require_the_parameter_context_and_binary_nitrite_is_not_a_multilevel_scale()
    {
        var unknown = Assert.Single(QualitySeriesBuilder.Build([Item(1, "4+", "BLD")], Plugin));
        Assert.True(unknown.Range.Incomparable); Assert.Null(unknown.Range.Minimum);
        var nitrite = Item(1, "+", "NIT");
        Assert.Equal("Cualitativo (cruces)", ProtocolExplainer.ResultKind(nitrite, new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.Uc3500Profile)));
        Assert.Null(Plugin.DescribeValue(Item(2, "2+", "NIT"))!.OrdinalRank);
    }

    [Fact]
    public void Negative_is_zero_crosses_but_is_not_an_unperformed_result_or_zero_concentration()
    {
        var negative = Item(1, "-     ");
        var series = Assert.Single(QualitySeriesBuilder.Build([negative], Plugin));
        Assert.Equal(1, series.MeasurementCount); Assert.Equal(0, series.NotPerformed); Assert.Equal("0", series.Range.Minimum);
        Assert.Equal("0", series.Range.Median); Assert.Equal("0", series.Range.Maximum); Assert.Equal(0, series.NumericCount);
        Assert.Null(negative.Value.Numeric); Assert.Null(series.StandardDeviation); Assert.Equal("-     ", negative.Value.RawText);
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        Assert.Equal("0", ProtocolExplainer.ResultValue(negative, profile));
        Assert.Null(Plugin.DescribeValue(Item(1, "-", kind: "CONCENTRATION")));
    }

    [Fact]
    public void Normal_empty_unknown_and_unperformed_are_not_changed_into_zero()
    {
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        Assert.Equal("normal", ProtocolExplainer.ResultValue(Item(1, "normal", "URO"), profile));
        Assert.Equal("", ProtocolExplainer.ResultValue(Item(2, ""), profile));
        Assert.Equal("-", ProtocolExplainer.ResultValue(Item(3, "-", "UNKNOWN"), profile));
        var omitted = Item(4, "-") with { InstrumentStatusMeaning = InstrumentStatusMeaning.NotPerformed };
        Assert.Null(Plugin.DescribeValue(omitted)!.DisplayValue);
        var series = Assert.Single(QualitySeriesBuilder.Build([omitted], Plugin));
        Assert.Equal(1, series.NotPerformed); Assert.Null(series.Range.Minimum); Assert.Null(series.Range.Median);
    }
}
