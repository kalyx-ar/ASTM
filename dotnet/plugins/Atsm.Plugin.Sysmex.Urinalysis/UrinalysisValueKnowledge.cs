using Atsm.Abstractions.Model;
using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Urinalysis;

internal static class UrinalysisValueKnowledge
{
    // UC table 6-18, and table 6-17 for turbidity. An index expresses order only.
    // Unknown levels are not assigned a rank or converted to a concentration.
    private static readonly Dictionary<string, string[]> CrossScales = new(StringComparer.Ordinal)
    {
        ["URO"] = ["normal", "1+", "2+", "3+", "4+"],
        ["BLD"] = ["-", "+", "1+", "2+", "3+"],
        ["BIL"] = ["-", "1+", "2+", "3+"], ["KET"] = ["-", "1+", "2+", "3+"],
        ["GLU"] = ["-", "+", "1+", "2+", "3+", "4+"], ["PRO"] = ["-", "+", "1+", "2+", "3+", "4+"],
        ["NIT"] = ["-", "+"], ["LEU"] = ["-", "1+", "2+", "3+"],
        ["P/C"] = ["normal", "1+", "2+"], ["A/C"] = ["normal", "1+", "2+"], ["TURBIDITY"] = ["-", "1+", "2+"],
    };
    public static InstrumentValueMeaning? Describe(InstrumentObservation observation, IReadOnlyDictionary<string, ProtocolText> vocabulary)
    {
        if (observation.Test.Vendor.System != CodeSystems.Vendor(Uc3500Interpreter.PluginId)) return null;
        var text = observation.Value.RawText.Trim();
        if ((observation.ResultKind == "QUALITATIVE" || observation.ResultKind == "TURBIDITY")
            && CrossScales.TryGetValue(observation.Test.Vendor.Code, out var levels))
        {
            string? comparison = null;
            foreach (var prefix in new[] { ">=", "<=", ">", "<" })
                if (text.StartsWith(prefix, StringComparison.Ordinal)) { comparison = prefix; text = text[prefix.Length..].TrimStart(); break; }
            // A numeric threshold in this field keeps its own numeric scale, not a cross rank.
            if (InstrumentNumericValue.Parse(observation.Value.RawText) is { Numeric: not null, Comparator: not null })
                return new(vocabulary["value.SemiQuantitative"], "numeric:" + observation.Test.Vendor.Code, false);
            var index = Array.IndexOf(levels, text);
            return new(vocabulary[observation.Test.Vendor.Code == "NIT" ? "value.BinaryCrosses" : "value.Crosses"],
                "crosses:" + observation.Test.Vendor.Code, true, index < 0 ? null : index, comparison);
        }
        var parsed = InstrumentNumericValue.Parse(observation.Value.RawText);
        return parsed is { Numeric: not null, Comparator: not null }
            ? new(vocabulary["value.SemiQuantitative"], "numeric:" + observation.Test.Vendor.Code, false) : null;
    }
}
