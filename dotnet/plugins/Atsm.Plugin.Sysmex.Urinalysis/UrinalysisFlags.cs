using Atsm.Abstractions.Model;
using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Urinalysis;

internal static class UrinalysisFlags
{
    internal static InstrumentFlagMeaning? Describe(InstrumentObservation observation, CodedValue flag, IReadOnlyDictionary<string, ProtocolText> vocabulary, IInstrumentKnowledge knowledge)
    {
        if (observation.Test.Vendor.System == CodeSystems.Vendor(Uc3500Interpreter.PluginId) && flag.System == CodeSystems.Vendor(Uc3500Interpreter.PluginId))
        {
            if (vocabulary.TryGetValue("flag.UC3500." + flag.Code, out var text)) return new(text, true, flag.Code != "*");
            var fact = knowledge.Lookup(new(Uc3500Interpreter.ProfileId, "RESULT", "error"), flag.Code);
            return fact is null ? null : new(fact.Text, fact.Severity != Atsm.Abstractions.Diagnostics.AtsmSeverity.Information, true);
        }
        if (observation.Test.Vendor.System != CodeSystems.Vendor(UwamInterpreter.PluginId)) return null;
        var code = flag.Code.Trim(); var system = CodeSystems.Vendor(UwamInterpreter.PluginId);
        if (flag.System == system + ":lh-flag")
        {
            var key = observation.Specimen.Role == SpecimenRole.Control && code is "L" or "H" ? "QC." + code : "LH." + code;
            return vocabulary.TryGetValue("flag." + key, out var lh) ? new(lh, true) : null;
        }
        if (flag.System != system + ":analyzer-flag") return null;
        var model = (observation.VendorIdentity["instrument.id"] ?? observation.Instrument.Model)?.Split('^')[0].Trim().ToUpperInvariant();
        var chemistry = UwamChemistryErrors.Model(model) is not null;
        var quantitative = model is "UF-3000" or "UF-3000X" or "UF-4000" or "UF-5000";
        if (code == "HH" && !chemistry) return null;
        var keyName = code switch
        {
            "W" when chemistry => "W.Chemistry", "H" when chemistry => "H.Chemistry", ">" when chemistry => ">.Chemistry",
            "H" when !quantitative => "H.Unknown", ">" when !quantitative => ">.Unknown", _ => code,
        };
        return vocabulary.TryGetValue("flag." + keyName, out var meaning)
            ? new(meaning, code != "N", code is "A" or "W" or "@" or "HH" || quantitative && (code is "H" or ">")) : null;
    }
}
