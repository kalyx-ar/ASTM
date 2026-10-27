using Astm.Abstractions.Model;
using Astm.Plugins;

namespace Astm.Plugin.Diagast.Qwalys;

public sealed partial class QwalysPlugin : IInstrumentKnowledge, IInstrumentFlagKnowledge, IInstrumentValueKnowledge
{
    public InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code)
    {
        if (context.Profile != Profiles[0].Id || context.Model is not (null or "QWALYS 3")) return null;
        var prefix = (context.Record, context.Field) switch
        {
            ("record", "type") => "record.", ("R", "3.4") => "valueKind.", ("R", "5") => "support.",
            ("R", "9") => "state.", ("R", "4.7:A") => "reaction.A.", ("R", "4.7:W") => "reaction.W.", _ => null,
        };
        return prefix is not null && DiagnosticVocabulary.TryGetValue(prefix + code, out var text) ? new(text) : null;
    }
    public InstrumentFlagMeaning? DescribeFlag(InstrumentObservation observation, CodedValue flag) => null; // R.7 not used by this interface.
    public ProtocolText? DescribeState(InstrumentObservation observation) => observation.Test.Vendor.System == CodeSystems.Vendor("diagast.qwalys3")
        ? DiagnosticVocabulary.GetValueOrDefault("state." + observation.InstrumentStatus) : null;
    public InstrumentValueMeaning? DescribeValue(InstrumentObservation observation)
    {
        if (observation.Test.Vendor.System != CodeSystems.Vendor("diagast.qwalys3")
            || !DiagnosticVocabulary.TryGetValue("valueKind." + observation.ResultKind, out var kind)) return null;
        var code = observation.Value.Coded?.Code;
        return new(kind, "diagast.qwalys3:" + observation.ResultKind, false)
        {
            DisplayText = DiagnosticVocabulary.GetValueOrDefault("reaction." + observation.ResultKind + "." + code),
            DisplayValue = code,
        };
    }
}
