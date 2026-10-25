using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;
using Atsm.Abstractions.Diagnostics;

namespace Atsm.Plugin.Sysmex.Urinalysis;

/// <summary>One equipment-family plugin, with shared knowledge and separate protocol decoders.</summary>
public sealed class SysmexUrinalysisPlugin : IInstrumentPlugin, IInstrumentTracePlugin, ITraceLogAdapterProvider, IInstrumentKnowledge, IInstrumentValueKnowledge, IInstrumentHeaderProbe, IInstrumentMessageKnowledge, IInstrumentFlagKnowledge
{
    public const string PluginId = "sysmex.urinalysis";
    public const string WithChemistryProfile = UwamInterpreter.WithChemistryProfile;
    public const string WithoutChemistryProfile = UwamInterpreter.WithoutChemistryProfile;
    public const string Uc3500Profile = Uc3500Interpreter.ProfileId;
    private readonly UwamInterpreter astm;
    private readonly UrinalysisKnowledge knowledge = new();
    private readonly Uc3500Interpreter serial;
    public SysmexUrinalysisPlugin(Uc3500CheckMode check = Uc3500CheckMode.Auto) { astm = new(knowledge); serial = new(check, knowledge); }
    public PluginDescriptor Describe() => new(PluginId, "0.1.0", PluginApi.Version, "Sysmex",
        ["U-WAM (UF-3000/3000x/4000/5000, UC-1000/3500, UD-10)"], [.. astm.Describe().Evidence, .. serial.Describe().Evidence]);
    public IReadOnlyList<AstmProfile> Profiles => astm.Profiles;
    public IReadOnlyList<InstrumentTraceProfile> TraceProfiles => serial.Profiles;
    IReadOnlyList<InstrumentTraceProfile> IInstrumentTracePlugin.Profiles => TraceProfiles;
    public IReadOnlyList<ITraceLogAdapter> LogAdapters => astm.LogAdapters;
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary => knowledge.Vocabulary;
    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context) => astm.CreateDialect(profile, context);
    public bool Recognizes(IReadOnlyList<TraceSegment> segments) => serial.Recognizes(segments);
    public RecordedBy? InferRecorder(IReadOnlyList<TraceSegment> segments) => serial.InferRecorder(segments);
    public TraceInterpretation Interpret(InstrumentTraceProfile profile, IReadOnlyList<TraceEntry> entries,
        InstrumentRef instrument, Guid traceId, TimeZoneInfo timeZone) => serial.Interpret(profile, entries, instrument, traceId, timeZone);
    public InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code) => knowledge.Lookup(context, code);
    public InstrumentValueMeaning? DescribeValue(InstrumentObservation observation) => UrinalysisValueKnowledge.Describe(observation, DiagnosticVocabulary);
    public ProtocolText? DescribeUnit(InstrumentObservation observation) => UrinalysisValueKnowledge.Unit(observation, DiagnosticVocabulary);
    public ProtocolExplanation? ExplainFinding(AtsmDiagnostic finding)
    {
        if (DiagnosticVocabulary.TryGetValue("finding." + finding.Code, out var text)) return new(text, []);
        return finding.Code.StartsWith(UwamChemistryErrors.Prefix, StringComparison.Ordinal)
            ? new(DiagnosticVocabulary["finding.ChemistryUnknown"], [finding.Code[UwamChemistryErrors.Prefix.Length..]]) : null;
    }
    public ProtocolExplanation? ExplainMessage(Lis02Message message)
    {
        var samples = message.OfType('Q').SelectMany(q => q.Repeats(3)).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        return samples.Length == 0 ? null : new(DiagnosticVocabulary["message.QuerySamples"], [string.Join(", ", samples)]);
    }
    public InstrumentFlagMeaning? DescribeFlag(InstrumentObservation observation, CodedValue flag) => UrinalysisFlags.Describe(observation, flag, DiagnosticVocabulary, knowledge);
    public ProtocolText? DescribeState(InstrumentObservation observation) => observation.Test.Vendor.System == CodeSystems.Vendor(Uc3500Interpreter.PluginId)
        && observation.VendorIdentity["analysis.state"] == "0" ? DiagnosticVocabulary["state.UC3500.Analysed"] : null;
    // R315168 rev.22, §5.3.3.1 and appendices B/K: sender identity on the U-WAM link.
    // This does not claim that every analyzer's direct serial protocol is LIS02.
    public bool RecognizesHeader(Lis02Record header) => header.Type == 'H' && header.Text(5, 1).Trim().ToUpperInvariant()
        is "U-WAM" or "UF-3000" or "UF-3000X" or "UF-4000" or "UF-5000" or "UC-1000" or "UC-3500" or "UD-10";
}
