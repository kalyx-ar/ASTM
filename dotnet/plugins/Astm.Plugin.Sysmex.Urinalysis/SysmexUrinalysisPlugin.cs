using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;
using Astm.Plugins;
using Astm.Protocol.Lis02;
using Astm.Abstractions.Diagnostics;

namespace Astm.Plugin.Sysmex.Urinalysis;

/// <summary>One equipment-family plugin, with shared knowledge and separate protocol decoders.</summary>
public sealed class SysmexUrinalysisPlugin : IInstrumentPlugin, IInstrumentTracePlugin, ITraceLogAdapterProvider, IInstrumentKnowledge, IInstrumentValueKnowledge, IInstrumentHeaderProbe, IInstrumentMessageKnowledge, IInstrumentFlagKnowledge, IInstrumentCommandPlugin
{
    public IReadOnlyList<InstrumentCommandDescriptor> Commands(string profileId) => AstmOrderCommands.Describe(this, profileId);
    public IReadOnlyList<Lis02Record> BuildAstmCommand(string profileId, InstrumentCommandRequest request, DialectContext context) => AstmOrderCommands.Build(this, profileId, request, context);
    public const string PluginId = "sysmex.urinalysis";
    public const string WithChemistryProfile = UwamInterpreter.WithChemistryProfile;
    public const string WithoutChemistryProfile = UwamInterpreter.WithoutChemistryProfile;
    public const string Uc3500Profile = Uc3500Interpreter.ProfileId;
    public const string UfBinaryProfile = UfBinaryInterpreter.ProfileId;
    private readonly UwamInterpreter astm;
    private readonly UrinalysisKnowledge knowledge = new();
    private readonly Uc3500Interpreter serial;
    public SysmexUrinalysisPlugin(Uc3500CheckMode check = Uc3500CheckMode.Auto) { astm = new(knowledge); serial = new(check, knowledge); }
    public PluginDescriptor Describe() => new(PluginId, "0.1.0", PluginApi.Version, "Sysmex",
        ["U-WAM (UF-3000/3000x/4000/5000, UC-1000/3500, UD-10)"], [.. astm.Describe().Evidence, .. serial.Describe().Evidence]);
    public IReadOnlyList<AstmProfile> Profiles => astm.Profiles;
    public IReadOnlyList<InstrumentTraceProfile> TraceProfiles => [.. serial.Profiles, UfBinaryInterpreter.Profile];
    IReadOnlyList<InstrumentTraceProfile> IInstrumentTracePlugin.Profiles => TraceProfiles;
    public IReadOnlyList<ITraceLogAdapter> LogAdapters => astm.LogAdapters;
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary => knowledge.Vocabulary;
    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context) => astm.CreateDialect(profile, context);
    public bool Recognizes(InstrumentTraceProfile profile, IReadOnlyList<TraceSegment> segments) => profile.Id == UfBinaryProfile
        ? UfBinaryInterpreter.Recognizes(segments) : serial.Recognizes(profile, segments);
    public RecordedBy? InferRecorder(InstrumentTraceProfile profile, IReadOnlyList<TraceSegment> segments) => profile.Id == UfBinaryProfile ? null : serial.InferRecorder(profile, segments);
    public TraceInterpretation Interpret(InstrumentTraceProfile profile, IReadOnlyList<TraceEntry> entries,
        InstrumentRef instrument, Guid traceId, TimeZoneInfo timeZone) => profile.Id == UfBinaryProfile
        ? UfBinaryInterpreter.Interpret(entries, instrument, traceId) : serial.Interpret(profile, entries, instrument, traceId, timeZone);
    public InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code) => knowledge.Lookup(context, code);
    public InstrumentValueMeaning? DescribeValue(InstrumentObservation observation) => observation.Test.Vendor.System == CodeSystems.Vendor(UfBinaryInterpreter.VendorId)
        ? new(DiagnosticVocabulary["uf.value.Kind"], "device-notice", false) { DisplayText = observation.Value.Coded?.System.EndsWith(":need", StringComparison.Ordinal) == true
            ? DiagnosticVocabulary.GetValueOrDefault("uf.need." + observation.Test.Vendor.Code) : null, DisplayValue = observation.Value.RawText, IsStructuredContent = true }
        : UrinalysisValueKnowledge.Describe(observation, DiagnosticVocabulary);
    public ProtocolText? DescribeUnit(InstrumentObservation observation) => observation.Test.Vendor.System == CodeSystems.Vendor(UfBinaryInterpreter.VendorId)
        ? DiagnosticVocabulary["uf.unit.None"] : UrinalysisValueKnowledge.Unit(observation, DiagnosticVocabulary);
    public ProtocolExplanation? ExplainFinding(AstmDiagnostic finding)
    {
        if (finding.Code == "ASTM-UF-DEVICE-NOTICE") return new(DiagnosticVocabulary["uf.notice"], [finding.Message]);
        if (DiagnosticVocabulary.TryGetValue("finding." + finding.Code, out var text)) return new(text, []);
        return finding.Code.StartsWith(UwamChemistryErrors.Prefix, StringComparison.Ordinal)
            ? new(DiagnosticVocabulary["finding.ChemistryUnknown"], [finding.Code[UwamChemistryErrors.Prefix.Length..]]) : null;
    }
    public ProtocolExplanation? ExplainMessage(Lis02Message message)
    {
        var samples = message.OfType('Q').SelectMany(q => q.Repeats(3)).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        return samples.Length == 0 ? null : new(DiagnosticVocabulary["message.QuerySamples"], [string.Join(", ", samples)]);
    }
    public InstrumentFlagMeaning? DescribeFlag(InstrumentObservation observation, CodedValue flag) => observation.Test.Vendor.System == CodeSystems.Vendor(UfBinaryInterpreter.VendorId)
        && flag.Code == observation.Test.Vendor.Code && (flag.System == CodeSystems.Vendor(UfBinaryInterpreter.VendorId) + ":need" || flag.System == CodeSystems.Vendor(UfBinaryInterpreter.VendorId) + ":notice")
        ? new(DiagnosticVocabulary[flag.System.EndsWith(":need", StringComparison.Ordinal) ? "uf.state.Need" : "uf.state.Notice"], true)
        : UrinalysisFlags.Describe(observation, flag, DiagnosticVocabulary, knowledge);
    public ProtocolText? DescribeState(InstrumentObservation observation) => observation.Test.Vendor.System == CodeSystems.Vendor(Uc3500Interpreter.PluginId)
        && observation.VendorIdentity["analysis.state"] == "0" ? DiagnosticVocabulary["state.UC3500.Analysed"] : null;
    // R315168 rev.22, §5.3.3.1 and appendices B/K: sender identity on the U-WAM link.
    // This does not claim that every analyzer's direct serial protocol is LIS02.
    public bool RecognizesHeader(Lis02Record header) => header.Type == 'H' && header.Text(5, 1).Trim().ToUpperInvariant()
        is "U-WAM" or "UF-3000" or "UF-3000X" or "UF-4000" or "UF-5000" or "UC-1000" or "UC-3500" or "UD-10";
}
