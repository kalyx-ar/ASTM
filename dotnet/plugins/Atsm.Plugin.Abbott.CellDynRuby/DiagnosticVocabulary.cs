using Atsm.Plugins;

namespace Atsm.Plugin.Abbott.CellDynRuby;

public sealed partial class CellDynRubyPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = StandardDiagnosticVocabulary.Create(
        "Trazas locales CELL-DYN Ruby; manual pendiente",
        ("record.R", "Resultado hematológico CELL-DYN Ruby"),
        ("record.C", "Comentario o alarma del analizador"),
        ("record.Q", "Consulta de paneles o determinaciones al anfitrión"));
}
