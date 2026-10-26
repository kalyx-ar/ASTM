using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.XnL;

public sealed partial class XnLPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = CreateVocabulary();
    private static IReadOnlyDictionary<string, ProtocolText> CreateVocabulary()
    {
        var values = new Dictionary<string, ProtocolText>(StandardDiagnosticVocabulary.Create(
        "Sysmex XN-L 1273-MKT rev.3 §7/8"), StringComparer.Ordinal);
        void Add(string key, string text, string evidence) => values.Add(key, key == "flag.N" ? StandardDiagnosticVocabulary.Shared["state.Normal"]
            : key == "flag.W" ? StandardDiagnosticVocabulary.Shared["state.LowReliability"] : new(key, text, evidence));
        foreach (var (code, label) in new (string, string)[]
        {
            ("N", "Normal"), ("L", "Por debajo del intervalo de referencia"), ("H", "Por encima del intervalo de referencia"),
            (">", "Fuera de la linealidad garantizada"), ("W", "Baja fiabilidad"),
            ("LL", "Por debajo del límite crítico o de blanco"), ("HH", "Por encima del límite crítico o de blanco"),
            ("A", "Error de análisis o indicador IP"), ("A.Masked", "Error de análisis o del equipo"), ("A.Overflow", "Límite de visualización excedido"),
        }) Add("flag." + code, label, "Sysmex XN-L 1273-MKT rev.3, §8.5 tabla 5 p.24; R.7 y representación de R.4");
        Add("state.F", "Sin regla de repetición aplicable", "1273-MKT rev.3 §8.6 p.25; F no significa validación clínica");
        Add("state.P", "Repetición o prueba refleja pendiente", "1273-MKT rev.3 §8.6 p.25; se enviarán resultados adicionales");
        Add("state.IP.Inactive", "Sin activar", "1273-MKT rev.3 pp.30-31, indicadores de sospecha: presencia indicada por R.7=A, sin umbral universal");
        Add("value.IP", "Indicador IP", "1273-MKT rev.3 §9 pp.29-33; avisos para revisión, no diagnóstico ni concentración");
        Add("unit.None", "—", "1273-MKT rev.3 §9 tablas 7-10; indicadores IP sin unidad física");
        foreach (var entry in XnLMessages.Definitions)
            Add("ip." + entry.Code, entry.Text, $"Sysmex XN-L 1273-MKT rev.3 §9 tabla {entry.Table} pp.29-33; R.3.5, presencia por R.7=A; no diagnóstico clínico");
        return values;
    }
}
