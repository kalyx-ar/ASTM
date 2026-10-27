using Astm.Abstractions.Model;
using Astm.Plugins;

namespace Astm.Diagnostics;

public sealed record QualityReportedRange(string? Minimum, string? Median, string? Maximum, int BoundedCount = 0,
    bool Ordinal = false, bool Incomparable = false)
{
    private sealed record Point(decimal Value, string Text, string? Comparator);
    internal static QualityReportedRange Build(IReadOnlyList<InstrumentObservation> measured, IInstrumentValueKnowledge? knowledge)
    {
        if (measured.Count == 0) return new(null, null, null);
        var descriptions = measured.Select(o => knowledge?.DescribeValue(o)).ToArray();
        var ordinal = descriptions.Any(d => d?.IsOrdinal == true);
        if (ordinal)
        {
            // Unknown categories and another scale cannot be silently dropped from an ordinal range.
            if (descriptions.Any(d => d?.OrdinalRank is null) || descriptions.Select(d => d!.Scale).Distinct().Count() != 1)
                return new(null, null, null, Ordinal: true, Incomparable: true);
            return Summarize(measured.Select((o, i) => new Point(descriptions[i]!.OrdinalRank!.Value,
                descriptions[i]!.DisplayValue ?? o.Value.RawText.Trim(), descriptions[i]!.Comparator)).ToArray(), true);
        }
        var points = measured.Select(o =>
        {
            if (InstrumentContent.IsImageReference(o) || o.Value.Coded is not null || knowledge?.DescribeValue(o)?.IsStructuredContent == true) return null;
            var parsed = InstrumentNumericValue.Parse(o.Value.RawText);
            var number = o.Value.Numeric ?? parsed.Numeric;
            var comparison = o.Value.Comparator switch { "≥" => ">=", "≤" => "<=", _ => o.Value.Comparator ?? parsed.Comparator };
            return number is { } value ? new Point(value, comparison is null ? o.Value.RawText.Trim()
                : comparison + value.ToString(System.Globalization.CultureInfo.InvariantCulture), comparison) : null;
        }).OfType<Point>().ToArray();
        return Summarize(points, false);
    }
    private static QualityReportedRange Summarize(Point[] points, bool ordinal)
    {
        if (points.Length == 0) return new(null, null, null, Ordinal: ordinal);
        var bounded = points.Count(p => p.Comparator is not null);
        // A lower-censored observation has no finite upper endpoint, and vice versa.
        // Do not turn a bound into an exact minimum/maximum for the complete set.
        var lower = points.Any(p => p.Comparator is "<" or "<=") ? null
            : points.OrderBy(p => p.Value).ThenBy(p => p.Comparator is null ? 0 : p.Comparator == ">=" ? 1 : 2).First().Text;
        var upper = points.Any(p => p.Comparator is ">" or ">=") ? null
            : points.OrderByDescending(p => p.Value).ThenBy(p => p.Comparator is null ? 0 : p.Comparator == "<=" ? 1 : 2).First().Text;
        string? median = null;
        if (ordinal && bounded == 0)
        {
            var sorted = points.OrderBy(p => p.Value).ToArray(); var right = sorted.Length / 2; var left = (sorted.Length - 1) / 2;
            median = sorted[left].Value == sorted[right].Value ? sorted[left].Text : sorted[left].Text + " – " + sorted[right].Text;
        }
        return new(lower, median, upper, bounded, ordinal);
    }
}
