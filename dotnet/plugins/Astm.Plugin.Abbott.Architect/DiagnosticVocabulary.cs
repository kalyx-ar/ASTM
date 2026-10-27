using Astm.Plugins;

namespace Astm.Plugin.Abbott.Architect;

public sealed partial class ArchitectPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = CreateVocabulary();
    private static IReadOnlyDictionary<string, ProtocolText> CreateVocabulary()
    {
        var values = new Dictionary<string, ProtocolText>(StandardDiagnosticVocabulary.Create(
        "Abbott ARCHITECT ASI 91407-108 tablas de registros"), StringComparer.Ordinal);
        foreach (var (key, text) in new (string, string)[]
        {
            ("valueKind.F", "Concentración"), ("valueKind.P", "Respuesta instrumental"), ("valueKind.I", "Interpretación"),
            ("assay.P", "Ensayo primario"), ("assay.C", "Ensayo de correlación"),
            ("flag.LOW", "Por debajo del intervalo de referencia"), ("flag.HIGH", "Por encima del intervalo de referencia"),
            ("flag.LL", "Por debajo del intervalo extremo"), ("flag.HH", "Por encima del intervalo extremo"),
            ("flag.<", "Por debajo del intervalo de medición"), ("flag.>", "Por encima del intervalo de medición"),
            ("flag.EXP", "Reactivo vencido"),
        }) values.Add(key, new(key, text, "Abbott ARCHITECT ASI 91407-108, tabla 2.4 pp.2-16/2-17 y tabla 1.13; tipo R.3.11, ensayo R.3.7, flags R.7"));
        return values;
    }
}
