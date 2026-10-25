using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Urinalysis;

internal sealed partial class UwamInterpreter : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = CreateVocabulary();
    internal static IReadOnlyDictionary<string, ProtocolText> CreateVocabulary() => StandardDiagnosticVocabulary.Create(
        "Sysmex R315168 rev.22 §5.3.3",
        ("record.S", "Datos del control de calidad y referencia de objetivos/límites"),
        ("record.O", "Muestra, tipo de análisis y estado de la solicitud"),
        ("record.R", "Resultado urinario, método, dilución y formato del valor"),
        ("record.Q", "Consulta de solicitudes por número de muestra"),
        ("resultFormat.RAW", "Original"),
        ("resultFormat.MAINFORMAT", "Formateado"));
}
