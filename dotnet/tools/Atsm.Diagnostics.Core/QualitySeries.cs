using Atsm.Abstractions.Model;

namespace Atsm.Diagnostics;

public sealed record QualityValueFrequency(string Value, int Count, bool Numeric, bool NotPerformed);

public sealed record QualitySeries(string Key, string Material, string? Lot, string? Level, string Test, string Kind, string Unit,
    IReadOnlyList<InstrumentObservation> Records, int NumericCount, int NonNumeric, int NotPerformed, int Repeated, int Flagged,
    decimal? Minimum, decimal? Median, decimal? Maximum, double? StandardDeviation, double? Mean,
    IReadOnlySet<Guid> PotentialOutliers)
{
    public int MeasurementCount => Records.Count;
    public IReadOnlyList<QualityValueFrequency> ValueFrequencies { get; init; } = [];
    public InstrumentObservation Last => Records[^1];
}

/// <summary>Descriptive statistics of actual observations, never a QC acceptance decision.
/// Reported means/SDs in Quality.Statistics stay metadata; they are not additional replicates.</summary>
public static class QualitySeriesBuilder
{
    public static IReadOnlyList<QualitySeries> Build(IEnumerable<InstrumentObservation> observations)
    {
        return observations.Where(o => o.Specimen.Role != SpecimenRole.Patient && (o.Quality is not null || o.Specimen.Role is SpecimenRole.Control or SpecimenRole.Calibrator))
            .GroupBy(Key, StringComparer.Ordinal).Select(group =>
            {
                var records = group.OrderBy(o => o.Trace.FirstSequence).ToArray(); var first = records[0];
                var unique = records.DistinctBy(o => o.ObservationId).ToArray();
                var measured = unique.Where(o => o.InstrumentStatusMeaning != InstrumentStatusMeaning.NotPerformed).ToArray();
                var numeric = measured.Where(IsNumeric).ToArray(); var sorted = numeric.Select(o => o.Value.Numeric!.Value).Order().ToArray();
                double? mean = null, deviation = null;
                if (sorted.Length > 0)
                {
                    double center = 0, m2 = 0; var n = 0;
                    foreach (var value in sorted) { var x = (double)value; n++; var delta = x - center; center += delta / n; m2 += delta * (x - center); }
                    mean = center; if (n > 1) deviation = Math.Sqrt(Math.Max(0, m2 / (n - 1)));
                }
                var outliers = new HashSet<Guid>();
                if (sorted.Length >= 4)
                {
                    try
                    {
                        var q1 = Quantile(sorted, 0.25m); var q3 = Quantile(sorted, 0.75m); var iqr = checked(q3 - q1);
                        var lower = checked(q1 - 1.5m * iqr); var upper = checked(q3 + 1.5m * iqr);
                        foreach (var item in numeric.Where(o => o.Value.Numeric < lower || o.Value.Numeric > upper)) outliers.Add(item.ObservationId);
                    }
                    catch (OverflowException) { /* No representable fences; no invented outlier classification. */ }
                }
                return new QualitySeries(group.Key, first.Quality?.Material?.Identifier ?? first.Specimen.SpecimenId,
                    first.Quality?.Material?.Lot, first.Quality?.Material?.Level, first.Test.Vendor.Display ?? first.Test.Vendor.Code,
                    first.ResultKind ?? "", first.Value.RawUnit ?? first.Value.Unit?.Code ?? "", records, numeric.Length,
                    measured.Length - numeric.Length, unique.Length - measured.Length, records.Length - unique.Length,
                    unique.Count(o => o.Flags.Count > 0 || o.InstrumentStatusMeaning == InstrumentStatusMeaning.Warning),
                    sorted.Length == 0 ? null : sorted[0], sorted.Length == 0 ? null : Quantile(sorted, 0.5m),
                    sorted.Length == 0 ? null : sorted[^1], deviation, mean, outliers)
                {
                    // Frequencies describe every communicated record, including retransmissions and
                    // non-performed results. Only the numeric statistics deduplicate observations.
                    ValueFrequencies = records.GroupBy(o => (Value: string.IsNullOrWhiteSpace(o.Value.RawText)
                            ? o.Value.Coded?.Display ?? o.Value.Coded?.Code ?? "" : o.Value.RawText.Trim(),
                        Numeric: IsNumeric(o) && o.InstrumentStatusMeaning != InstrumentStatusMeaning.NotPerformed,
                        NotPerformed: o.InstrumentStatusMeaning == InstrumentStatusMeaning.NotPerformed))
                        .Select(g => new QualityValueFrequency(g.Key.Value, g.Count(), g.Key.Numeric, g.Key.NotPerformed))
                        .OrderByDescending(f => f.Count).ThenBy(f => f.Value, StringComparer.Ordinal).ToArray(),
                };
            }).ToArray();
    }
    private static bool IsNumeric(InstrumentObservation o) => o.Value.Numeric is not null && string.IsNullOrEmpty(o.Value.Comparator)
        && !o.Value.RawText.TrimStart().StartsWith('<') && !o.Value.RawText.TrimStart().StartsWith('>');
    private static string Key(InstrumentObservation o) => DiagnosticGrouping.Key(o.Instrument.InstrumentId, o.Instrument.SerialNumber,
        o.Specimen.Role.ToString(), o.Quality?.Material?.Kind.ToString(), o.Quality?.Material?.Identifier ?? o.Specimen.SpecimenId,
        o.Quality?.Material?.Lot, o.Quality?.Material?.Level, o.Quality?.Material?.TypeRaw, o.Quality?.Method, o.Quality?.ModuleId,
        o.Quality?.Calibration?.PerformedAtRaw, string.Join(";", o.Reagents.Select(r => DiagnosticGrouping.Key(r.Lot, r.Serial, r.Kind)).Order(StringComparer.Ordinal)),
        o.Test.Vendor.System, o.Test.Vendor.Code, o.ResultKind, o.Value.RawUnit, o.Value.Unit?.System, o.Value.Unit?.Code);
    // R7: linear interpolation at (n-1)p. Median is exact decimal; SD uses Welford in double.
    private static decimal Quantile(decimal[] sorted, decimal fraction)
    {
        var position = (sorted.Length - 1) * fraction; var index = (int)position;
        if (index == sorted.Length - 1) return sorted[index];
        var part = position - index; var a = sorted[index]; var b = sorted[index + 1];
        try { return checked(a + (b - a) * part); }
        catch (OverflowException) { return a * (1 - part) + b * part; }
    }
}
