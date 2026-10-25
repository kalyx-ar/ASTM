using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Uwam;

public sealed partial class UwamPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = StandardDiagnosticVocabulary.Create(
        "Sysmex R315168 rev.22 §5.3.3",
        ("record.S", "Datos del control de calidad y referencia de objetivos/límites"),
        ("record.O", "Muestra, tipo de análisis y estado de la solicitud"),
        ("record.R", "Resultado urinario, método, dilución y formato del valor"),
        ("record.Q", "Consulta de solicitudes por número de muestra"),
        ("resultFormat.RAW", "Valor sin formato del equipo"),
        ("resultFormat.MAINFORMAT", "Valor con el formato principal del equipo"));
}
