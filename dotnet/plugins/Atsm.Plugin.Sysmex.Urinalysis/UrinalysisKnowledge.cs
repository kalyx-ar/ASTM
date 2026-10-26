using Atsm.Abstractions.Diagnostics;
using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Urinalysis;

/// <summary>Compiled, evidence-backed knowledge. A code is meaningful only in its decoded context.</summary>
internal sealed class UrinalysisKnowledge : IInstrumentKnowledge
{
    private readonly Dictionary<(InstrumentKnowledgeContext Context, string Code), InstrumentKnowledgeFact> facts = [];
    public IReadOnlyDictionary<string, ProtocolText> Vocabulary { get; }
    public UrinalysisKnowledge()
    {
        var vocabulary = new Dictionary<string, ProtocolText>(UwamInterpreter.CreateVocabulary(), StringComparer.Ordinal);
        foreach (var (key, value) in Uc3500Interpreter.CreateVocabulary()) vocabulary.Add(key, value);
        foreach (var (key, value) in UfBinaryInterpreter.Vocabulary) vocabulary.Add(key, value);
        Vocabulary = vocabulary;
        foreach (var code in new[] { "DBS_LANG_01746", "DBS_LANG_02215" })
            facts.Add((new(UfBinaryInterpreter.ProfileId, "NOTICE", "resource"), code), new(vocabulary["uf.need." + code]));
        foreach (var profile in new[] { UwamInterpreter.WithChemistryProfile, UwamInterpreter.WithoutChemistryProfile })
        foreach (var entry in UwamChemistryErrors.Definitions)
        {
            var finding = UwamChemistryErrors.FindingCode(entry);
            facts.Add((new(profile, "R", UwamDialect.ChemistryErrorParameter, entry.Model), entry.Code),
                new(vocabulary["finding." + finding], entry.Code == "0000" ? null : finding,
                    entry.Code == "0000" ? AtsmSeverity.Information : AtsmSeverity.Warning, entry.SourceCode));
        }
        foreach (var profile in new[] { UwamInterpreter.WithChemistryProfile, UwamInterpreter.WithoutChemistryProfile })
        {
            foreach (var (parameter, code, _) in UwamIndicators.Research)
                facts.Add((new(profile, "R", "4.1:" + parameter), code), new(vocabulary["research." + parameter + "." + code]));
            foreach (var (code, _) in UwamIndicators.Reviews)
                facts.Add((new(profile, "R", "3.4:RC"), code), new(vocabulary["review." + code]));
            foreach (var code in new[] { "A", "M" }) facts.Add((new(profile, "R", "3.5"), code), new(vocabulary["method." + code]));
            foreach (var code in new[] { "S", "RC", "RP", "IF" }) facts.Add((new(profile, "R", "3.7"), code), new(vocabulary[code == "IF" ? "value.Image" : "resultKind." + code]));
            foreach (var code in new[] { "RAW", "MAINFORMAT", "RAW_Q", "RAW_C", "RAW_R1", "RAW_R2" }) facts.Add((new(profile, "R", "4.2"), code), new(vocabulary["resultFormat." + code]));
        }
        foreach (var profile in new[] { UwamInterpreter.WithChemistryProfile, UwamInterpreter.WithoutChemistryProfile })
        foreach (var (key, value) in vocabulary.Where(p => p.Key.StartsWith("record.", StringComparison.Ordinal)))
            facts.Add((new(profile, "record", "type"), key[7..]), new(value));
        foreach (var code in new[] { "RQ02", "CM00" })
            facts.Add((new(Uc3500Interpreter.ProfileId, "command", "code"), code), new(vocabulary[code]));
        facts.Add((new(Uc3500Interpreter.ProfileId, "CM00", "strip"), ""), new(vocabulary["CM00.EMPTY"]));
        facts.Add((new(Uc3500Interpreter.ProfileId, "record", "type"), "RESULT"), new(vocabulary["RESULT"]));
        foreach (var code in new[] { "ACK", "NAK", "EOT" })
            facts.Add((new(Uc3500Interpreter.ProfileId, "control", "code"), code), new(vocabulary[code]));
        foreach (var code in new[] { "ER00", "ER01", "ER03", "ER04", "ER05" })
            facts.Add((new(Uc3500Interpreter.ProfileId, "ER", "code"), code), new(vocabulary[code],
                code == "ER00" ? null : "ATSM-UC3500-" + code, code == "ER00" ? AtsmSeverity.Information : AtsmSeverity.Warning));
        foreach (var code in new[] { "00BB", "00CC", "00DD" })
        {
            var finding = "ATSM-UC3500-ANALYSIS-" + code;
            facts.Add((new(Uc3500Interpreter.ProfileId, "RESULT", "error"), code), new(vocabulary["finding." + finding], finding, AtsmSeverity.Warning));
        }
    }
    public InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code)
    {
        if (context.Profile == UfBinaryInterpreter.ProfileId)
        {
            if (context.Model is not (null or "UF-5000")) return null;
            context = context with { Model = null };
        }
        if (context.Profile is UwamInterpreter.WithChemistryProfile or UwamInterpreter.WithoutChemistryProfile
            && (context.Field.StartsWith("4.1:", StringComparison.Ordinal) || context.Field == "3.4:RC"))
        {
            if (context.Model is not (null or "U-WAM" or "UF-3000" or "UF-3000X" or "UF-4000" or "UF-5000")) return null;
            if (code == "RC_MNPMN" && context.Model is "UF-3000" or "UF-3000X") return null;
            context = context with { Model = null };
        }
        if (context.Record == "R" && context.Field == UwamDialect.ChemistryErrorParameter)
            context = context with { Model = UwamChemistryErrors.Model(context.Model) };
        if (facts.TryGetValue((context, code), out var exact)) return exact;
        if (context != new InstrumentKnowledgeContext(Uc3500Interpreter.ProfileId, "RESULT", "error") || code == "0000") return null;
        // Table 7-1 categories apply to result errors, never to ER response records or arbitrary text.
        var category = code switch
        {
            _ when code.Length == 4 && code[1] == '0' && code[3] == '0' && code[0] is >= '1' and <= '9' && code[2] is >= '1' and <= '9' => "STRIP",
            _ when code.Length == 4 && code[0] == '0' && code[2] == '0' && code[1] is >= '1' and <= '9' && code[3] is >= '1' and <= '9' => "LIGHT",
            _ when code.Length == 4 && code.EndsWith("00", StringComparison.Ordinal) => "MEASUREMENT",
            _ => null,
        };
        if (category is null) return null;
        var finding = "ATSM-UC3500-ANALYSIS-" + category;
        return new(Vocabulary["finding." + finding], finding, AtsmSeverity.Warning);
    }
}
