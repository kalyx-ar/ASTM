using Atsm.Plugins;

namespace Atsm.Plugin.Diagast.Qwalys;

public sealed partial class QwalysPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = StandardDiagnosticVocabulary.Create(
        "QWALYS 3 ASTM Connection Manual §O/R/C",
        ("record.O", "Muestra y prueba de inmunohematología"),
        ("record.R", "Reacción de inmunohematología y soporte"),
        ("record.C", "Comentario de la reacción"),
        ("record.Q", "Consulta del instrumento; este perfil sólo recibe resultados"));
}
