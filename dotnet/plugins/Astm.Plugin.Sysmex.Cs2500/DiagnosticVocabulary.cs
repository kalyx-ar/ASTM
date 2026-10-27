using Astm.Plugins;

namespace Astm.Plugin.Sysmex.Cs2500;

public sealed partial class Cs2500Plugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = CreateVocabulary();
    private static IReadOnlyDictionary<string, ProtocolText> CreateVocabulary()
    {
        var values = new Dictionary<string, ProtocolText>(StandardDiagnosticVocabulary.Create(
        "Sysmex CS-2500 rev.2.04 §5.3.3"), StringComparer.Ordinal);
        void Add(string key, string text, string evidence) => values.Add(key, key == "flag.N" ? StandardDiagnosticVocabulary.Shared["state.Normal"]
            : key == "flag.A" ? StandardDiagnosticVocabulary.Shared["state.AnalysisError"] : new(key, text, evidence));
        foreach (var (code, text) in new (string, string)[]
        {
            ("N", "Normal"), ("A", "Error de análisis"), ("L", "Por debajo del límite del paciente"),
            ("H", "Por encima del límite del paciente"), ("<", "Por debajo del límite de reporte"), (">", "Por encima del límite de reporte"),
            ("HIL.A", "Interferencia HIL"), ("HIL.W", "Fuera del intervalo del detector HIL"),
            ("Volume.A", "Volumen de muestra anómalo"), ("Volume.N", "Volumen de muestra normal"),
        }) Add("flag." + code, text, "CS-2500 rev.2.04 pp.33-35, R.7; HIL/volumen son contextos diferentes del análisis");
        foreach (var (code, text) in new (string, string)[] { ("S", "Resultado parcial"), ("F", "Resultados completos") })
            Add("state." + code, text, "CS-2500 rev.2.04 p.34, R.9; finalización de la muestra, no validación clínica");
        foreach (var (code, text) in new (string, string)[]
        {
            ("1", "Medición automática"), ("2", "Promedio automático"), ("3", "Reanálisis automático"), ("4", "Promedio de reanálisis automático"),
            ("5", "Medición con envío manual"), ("6", "Promedio con envío manual"), ("7", "Reanálisis con envío manual"),
            ("8", "Promedio de reanálisis con envío manual"), ("9", "Información final automática"), ("A", "Información final con envío manual"),
        }) Add("valueKind." + code, text, "CS-2500 rev.2.04 p.32, R.3.7; emisión manual no significa medición manual");
        foreach (var entry in Cs2500Errors.Definitions)
        {
            Add("evaluation." + entry.Code, entry.Text, entry.Evidence);
            Add("finding." + Cs2500Errors.FindingCode(entry.Code), entry.Text, entry.Evidence);
        }
        Add("instrument.34422", "Reactivo insuficiente", "CS-2500 rev.2.04 p.34, ejemplo de R.7.3; superficie de reactivo no detectada");
        return values;
    }
}
