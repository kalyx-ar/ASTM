using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.XnL;

public sealed partial class XnLPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = StandardDiagnosticVocabulary.Create(
        "Sysmex XN-L 1273-MKT rev.3 §7/8",
        ("record.O", "Muestra o material de control; posición en gradilla"),
        ("record.R", "Resultado hematológico y juicio del instrumento"),
        ("record.Q", "Consulta del instrumento; este perfil sólo recibe resultados"));
}
