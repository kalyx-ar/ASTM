using System.Globalization;
using Atsm.Abstractions.Model;

namespace Atsm.Plugins;

/// <summary>Ordinal ranks express order only, never distances, concentrations or arithmetic values.</summary>
public sealed record InstrumentValueMeaning(ProtocolText Kind, string Scale, bool IsOrdinal,
    decimal? OrdinalRank = null, string? Comparator = null)
{
    /// <summary>Contextual presentation only; never replaces RawText or creates a quantitative result.</summary>
    public string? DisplayValue { get; init; }
}
public interface IInstrumentValueKnowledge
{
    InstrumentValueMeaning? DescribeValue(InstrumentObservation observation);
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
