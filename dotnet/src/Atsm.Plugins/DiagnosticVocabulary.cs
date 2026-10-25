using Atsm.Plugins;

namespace Atsm.Plugins;

public sealed partial class StandardAstmPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = StandardDiagnosticVocabulary.Create(
        "CLSI LIS02-A2");
}
