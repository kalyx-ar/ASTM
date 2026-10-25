using System.Globalization;
using Atsm.Abstractions.Model;

namespace Atsm.Plugins;

/// <summary>Ordinal ranks express order only, never distances, concentrations or arithmetic values.</summary>
public sealed record InstrumentValueMeaning(ProtocolText Kind, string Scale, bool IsOrdinal,
    decimal? OrdinalRank = null, string? Comparator = null)
{
    /// <summary>Contextual presentation only; never replaces RawText or creates a quantitative result.</summary>
    public string? DisplayValue { get; init; }
    /// <summary>Images/references are not numeric measurements, even if their identifier is all digits.</summary>
    public bool IsStructuredContent { get; init; }
}
public interface IInstrumentValueKnowledge
{
    InstrumentValueMeaning? DescribeValue(InstrumentObservation observation);
    /// <summary>Unit presentation is separate from the value's type. Never infers a configurable unit from its magnitude.</summary>
    ProtocolText? DescribeUnit(InstrumentObservation observation) => null;
}

public static class InstrumentUnits
{
    /// <summary>Collapse only empty/duplicated components; preserve genuine expressions such as 10^3/uL.</summary>
    public static string? Symbol(ObservationValue value)
    {
        if (value.Unit?.Display is { Length: > 0 } display) return display;
        if (value.Unit?.Code is { Length: > 0 } code) return code;
        var raw = value.RawUnit?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        var parts = raw.Split('^').Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return parts.Length switch { 0 => null, 1 => parts[0], _ => raw };
    }
}

public readonly record struct InstrumentNumericValue(decimal? Numeric, string? Comparator)
{
    /// <summary>Retains a reported bound as a bound; RawText is owned by the caller and remains intact.</summary>
    public static InstrumentNumericValue Parse(string raw)
    {
        var text = raw.Trim(); string? comparison = null;
        foreach (var prefix in new[] { ">=", "<=", ">", "<", "≥", "≤" })
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            { comparison = prefix switch { "≥" => ">=", "≤" => "<=", _ => prefix }; text = text[prefix.Length..].TrimStart(); break; }
        return new(decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number) ? number : null, comparison);
    }
}
