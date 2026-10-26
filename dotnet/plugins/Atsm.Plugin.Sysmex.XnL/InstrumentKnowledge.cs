using Atsm.Abstractions.Model;
using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.XnL;

public sealed partial class XnLPlugin : IInstrumentKnowledge, IInstrumentFlagKnowledge, IInstrumentValueKnowledge
{
    public InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code)
    {
        if (context.Profile != Profiles[0].Id || context.Model is not (null or "XN-350" or "XN-450" or "XN-550")) return null;
        var prefix = (context.Record, context.Field) switch
        {
            ("record", "type") => "record.", ("R", "3.5") => "ip.", ("R", "7") => "flag.", ("R", "9") => "state.", _ => null,
        };
        return prefix is not null && DiagnosticVocabulary.TryGetValue(prefix + code, out var text) ? new(text) : null;
    }
    private static bool Own(InstrumentObservation observation) => observation.Test.Vendor.System == CodeSystems.Vendor("sysmex.xnl");
    public InstrumentFlagMeaning? DescribeFlag(InstrumentObservation observation, CodedValue flag)
    {
        if (!Own(observation) || flag.System != CodeSystems.Vendor("sysmex.xnl") + ":flag") return null;
        if (flag.Code == "A" && XnLMessages.Find(observation.Test.Vendor.Code) is { } ip)
            return new(DiagnosticVocabulary["ip." + ip.Code], true, ip.IsError);
        var code = flag.Code == "A" ? observation.Value.RawText switch { "----" => "A.Masked", "++++" => "A.Overflow", _ => "A" } : flag.Code;
        return DiagnosticVocabulary.TryGetValue("flag." + code, out var text) ? new(text, flag.Code != "N", flag.Code is "A" or "W") : null;
    }
    public ProtocolText? DescribeState(InstrumentObservation observation)
    {
        if (!Own(observation)) return null;
        if (XnLMessages.Find(observation.Test.Vendor.Code) is { Table: 8 } && !observation.Flags.Any(f => f.Code == "A"))
            return DiagnosticVocabulary["state.IP.Inactive"];
        return DiagnosticVocabulary.GetValueOrDefault("state." + observation.InstrumentStatus);
    }
    public InstrumentValueMeaning? DescribeValue(InstrumentObservation observation) => Own(observation) && XnLMessages.Find(observation.Test.Vendor.Code) is not null
        ? new(DiagnosticVocabulary["value.IP"], "sysmex.xnl:ip:" + observation.Test.Vendor.Code, false) { IsStructuredContent = true } : null;
    public ProtocolText? DescribeUnit(InstrumentObservation observation) => Own(observation) && XnLMessages.Find(observation.Test.Vendor.Code) is not null
        ? DiagnosticVocabulary["unit.None"] : null;
}
