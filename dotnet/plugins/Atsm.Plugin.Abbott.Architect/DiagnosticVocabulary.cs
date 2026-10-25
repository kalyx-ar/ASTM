using Atsm.Plugins;

namespace Atsm.Plugin.Abbott.Architect;

public sealed partial class ArchitectPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = StandardDiagnosticVocabulary.Create(
        "Abbott ARCHITECT ASI 91407-108 tablas de registros",
        ("record.O", "Muestra y solicitud de ensayos ARCHITECT"),
        ("record.R", "Resultado de ensayo ARCHITECT"),
        ("record.Q", "Consulta o cancelación de solicitudes ARCHITECT"));
}
