using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Diagnostics;

namespace Atsm.Tests.Diagnostics;

public sealed class QualitySeriesTests
{
    private static InstrumentObservation Item(int index, decimal? value, string lot = "LOTE-LITERAL", string unit = "mg/dL", string kind = "CONCENTRATION") => new(
        new Guid(index, 0, 0, new byte[8]), new("I-SYN", SerialNumber: "SER-SYN"), new("CONTROL-LITERAL", Role: SpecimenRole.Control),
        CodeSet.Of(new("TEST", "Prueba sintética", "urn:synthetic")), new(value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "No informado", value, RawUnit: unit),
        null, [], "0", InstrumentStatusMeaning.Unknown, kind, null, DateTimeOffset.UnixEpoch.AddMinutes(index), null, [], [], VendorIdentity.Empty,
        new(Guid.Empty, index, index)) { Quality = new(new(QualityMaterialKind.Control, "MATERIAL-LITERAL", lot)) };

    [Fact]
    public void Median_and_sample_SD_describe_the_actual_measurements_without_replacing_the_originals()
    {
        var items = new[] { Item(1, 2), Item(2, 4), Item(3, 6), Item(4, 8) };
        var series = Assert.Single(QualitySeriesBuilder.Build(items));
        Assert.Equal(4, series.Count); Assert.Equal(2m, series.Minimum); Assert.Equal(5m, series.Median); Assert.Equal(8m, series.Maximum);
        Assert.Equal(5, series.Mean); Assert.Equal(Math.Sqrt(20d / 3), series.StandardDeviation!.Value, 12);
        Assert.Equal("MATERIAL-LITERAL", series.Material); Assert.Equal("LOTE-LITERAL", series.Lot);
        Assert.Equal(items, series.Records); Assert.Same(items[^1], series.Last);
    }

    [Fact]
    public void A_possible_outlier_is_highlighted_but_is_never_removed_or_called_a_QC_failure()
    {
        var items = new[] { Item(1, 1), Item(2, 1), Item(3, 1), Item(4, 1), Item(5, 100) };
        var series = Assert.Single(QualitySeriesBuilder.Build(items));
        Assert.Equal(1m, series.Median); Assert.Equal(20.8, series.Mean!.Value, 12);
        Assert.Equal(items[^1].ObservationId, Assert.Single(series.PotentialOutliers));
        Assert.Equal(5, series.Records.Count); Assert.Equal(100m, series.Maximum);
    }

    [Fact]
    public void Lots_levels_units_types_instruments_and_reagents_cannot_share_a_statistical_series()
    {
        var first = Item(1, 1);
        var records = new[] { first, Item(2, 1, lot: "OTHER"), Item(3, 1, unit: "g/L"), Item(4, 1, kind: "REFLECTANCE1"),
            Item(5, 1) with { Instrument = new("OTHER") }, Item(6, 1) with { Quality = first.Quality! with { Material = first.Quality.Material! with { Level = "2" } } },
            Item(7, 1) with { Reagents = [new("REAGENT-LOT")] } };
        Assert.Equal(records.Length, QualitySeriesBuilder.Build(records).Count);
    }

    [Fact]
    public void Censored_missing_unperformed_and_duplicate_records_remain_visible_but_do_not_inflate_n()
    {
        var first = Item(1, 4);
        var items = new[] { first, first, Item(2, null), Item(3, 99) with { InstrumentStatusMeaning = InstrumentStatusMeaning.NotPerformed },
            Item(4, 10) with { Value = new("<10", 10, "<", RawUnit: "mg/dL") } };
        var series = Assert.Single(QualitySeriesBuilder.Build(items));
        Assert.Equal(1, series.Count); Assert.Equal(2, series.NonNumeric); Assert.Equal(1, series.NotPerformed); Assert.Equal(1, series.Repeated);
        Assert.Equal(5, series.Records.Count); Assert.Null(series.StandardDeviation); Assert.Equal(4m, series.Median);
    }

    [Fact]
    public void Reported_statistics_are_not_replicates_and_patient_data_are_excluded()
    {
        var first = Item(1, 4);
        var control = first with { Quality = first.Quality! with { Statistics = [new(ReportedStatisticKind.CqMean, new("50", 50), StatisticScope.ReplicateGroup, "SYN")] } };
        var patient = Item(2, 100) with { Specimen = new("PATIENT-SYN", Role: SpecimenRole.Patient) };
        var series = Assert.Single(QualitySeriesBuilder.Build([control, patient]));
        Assert.Equal(1, series.Count); Assert.Equal(4m, series.Median); Assert.Single(series.Records[0].Quality!.Statistics);
    }

    [Fact]
    public void Extreme_decimal_values_do_not_overflow_median_or_standard_deviation()
    {
        var series = Assert.Single(QualitySeriesBuilder.Build([Item(1, decimal.MinValue), Item(2, decimal.MaxValue)]));
        Assert.Equal(0m, series.Median); Assert.True(double.IsFinite(series.StandardDeviation!.Value));
        var empty = Assert.Single(QualitySeriesBuilder.Build([Item(1, null)]));
        Assert.Null(empty.Median); Assert.Null(empty.StandardDeviation); Assert.Empty(empty.PotentialOutliers);
    }

    [Fact]
    public void A_single_record_can_have_a_summary_while_keeping_its_exact_child()
    {
        var row = new DiagnosticRow(["RAW"], "ORIGINAL", "key");
        var summary = new DiagnosticRow(["MEDIAN"], "STATISTICS", "key");
        var group = new DiagnosticRowGroup("key", [row]) { SummaryRow = summary };
        Assert.Same(summary, group.Summary(_ => "")); Assert.Same(row, Assert.Single(group.Children));
    }
}
