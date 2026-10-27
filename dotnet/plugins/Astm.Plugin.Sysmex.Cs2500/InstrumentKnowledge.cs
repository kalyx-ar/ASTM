using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Model;
using Astm.Plugins;

namespace Astm.Plugin.Sysmex.Cs2500;

public sealed partial class Cs2500Plugin : IInstrumentKnowledge, IInstrumentFlagKnowledge, IInstrumentValueKnowledge
{
    public InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code)
    {
        if (context.Profile != Profiles[0].Id || context.Model is not (null or "CS-2500")) return null;
        if (context.Record == "record" && context.Field == "type")
            return DiagnosticVocabulary.TryGetValue("record." + code, out var record) ? new(record) : null;
        if (context.Record != "R") return null;
        if (context.Field == "7.2" && Cs2500Errors.Definitions.FirstOrDefault(e => e.Code == code) is { } entry)
            return new(DiagnosticVocabulary["evaluation." + code], entry.Attention ? Cs2500Errors.FindingCode(code) : null,
                entry.Attention ? AstmSeverity.Warning : AstmSeverity.Information, code);
        var key = context.Field switch { "7.1" => "flag.", "7.1:HIL" => "flag.HIL.", "7.1:Volume" => "flag.Volume.",
            "7.3" => "instrument.", "3.7" => "valueKind.", "9" => "state.", _ => null };
        return key is not null && DiagnosticVocabulary.TryGetValue(key + code, out var text) ? new(text) : null;
    }
    public InstrumentFlagMeaning? DescribeFlag(InstrumentObservation observation, CodedValue flag)
    {
        var system = CodeSystems.Vendor("sysmex.cs2500");
        if (observation.Test.Vendor.System != system) return null;
        if (flag.System == system + ":evaluation-error" && Cs2500Errors.Definitions.FirstOrDefault(e => e.Code == flag.Code) is { } entry)
            return new(DiagnosticVocabulary["evaluation." + entry.Code], entry.Attention, entry.IsError);
        if (flag.System == system + ":instrument-error")
            return DiagnosticVocabulary.TryGetValue("instrument." + flag.Code, out var error) ? new(error, true, true) : null;
        if (flag.System != system + ":flag") return null;
        var parameter = observation.Test.Vendor.Display;
        var context = parameter is "Hemolytic Sample" or "Icterus Sample" or "Lipemic Sample" ? "HIL."
            : parameter == "Defective Sample Volume" ? "Volume." : "";
        return DiagnosticVocabulary.TryGetValue("flag." + context + flag.Code, out var text)
            ? new(text, flag.Code != "N", context.Length == 0 && flag.Code == "A") : null;
    }
    public ProtocolText? DescribeState(InstrumentObservation observation) => observation.Test.Vendor.System == CodeSystems.Vendor("sysmex.cs2500")
        ? DiagnosticVocabulary.GetValueOrDefault("state." + observation.InstrumentStatus) : null;
    public InstrumentValueMeaning? DescribeValue(InstrumentObservation observation) => observation.Test.Vendor.System == CodeSystems.Vendor("sysmex.cs2500")
        && DiagnosticVocabulary.TryGetValue("valueKind." + observation.ResultKind, out var text)
        ? new(text, "sysmex.cs2500:" + observation.ResultKind, false) : null;
}
