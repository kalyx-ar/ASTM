using Atsm.Plugins;

namespace Atsm.Plugin.Abbott.CellDynRuby;

public sealed partial class CellDynRubyPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = StandardDiagnosticVocabulary.Create(
        "CLSI LIS02-A2");
}
