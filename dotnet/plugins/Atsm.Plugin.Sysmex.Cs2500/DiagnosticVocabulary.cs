using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Cs2500;

public sealed partial class Cs2500Plugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = StandardDiagnosticVocabulary.Create(
        "Sysmex CS-2500 rev.2.04 §5.3.3",
        ("record.O", "Muestra o material de control y posición"),
        ("record.R", "Resultado de coagulación, dilución y alarmas"),
        ("record.Q", "Consulta del instrumento; este perfil sólo recibe resultados"));
}
