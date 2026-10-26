using Atsm.Abstractions.Model;
using Atsm.Plugins;

namespace Atsm.Plugin.Abbott.Architect;

public sealed partial class ArchitectPlugin : IInstrumentKnowledge, IInstrumentFlagKnowledge, IInstrumentValueKnowledge
{
    public InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code)
    {
        if (context.Profile != "abbott.architect.asi-rs232"
            || context.Model is { } model && !model.StartsWith("ARCHITECT", StringComparison.OrdinalIgnoreCase)) return null;
        var prefix = (context.Record, context.Field) switch
        {
            ("record", "type") => "record.", ("R", "3.11") => "valueKind.", ("R", "3.7") => "assay.", ("R", "7") => "flag.", _ => null,
        };
        return prefix is not null && DiagnosticVocabulary.TryGetValue(prefix + code, out var text) ? new(text) : null;
    }
    public InstrumentFlagMeaning? DescribeFlag(InstrumentObservation observation, CodedValue flag) => observation.Test.Vendor.System == ArchitectDialect.VendorSystem
        && flag.System == ArchitectDialect.VendorSystem + ":flag" && DiagnosticVocabulary.TryGetValue("flag." + flag.Code, out var text)
        ? new(text, true, flag.Code == "EXP") : null;
    public InstrumentValueMeaning? DescribeValue(InstrumentObservation observation) => observation.Test.Vendor.System == ArchitectDialect.VendorSystem
        && DiagnosticVocabulary.TryGetValue("valueKind." + observation.ResultKind, out var text)
        ? new(text, "abbott.architect:" + observation.ResultKind, false) : null;
}
