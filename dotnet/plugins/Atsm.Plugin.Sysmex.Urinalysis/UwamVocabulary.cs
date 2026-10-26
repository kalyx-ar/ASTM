using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Urinalysis;

internal sealed partial class UwamInterpreter : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = CreateVocabulary();
    internal static IReadOnlyDictionary<string, ProtocolText> CreateVocabulary()
    {
        var result = new Dictionary<string, ProtocolText>(StandardDiagnosticVocabulary.Create(
        "Sysmex R315168 rev.22 §5.3.3",
        ("record.S", "Datos del control de calidad y referencia de objetivos/límites"),
        ("resultFormat.RAW", "Original"),
        ("resultFormat.MAINFORMAT", "Formateado")), StringComparer.Ordinal);
        void Add(string key, string text, string evidence) => result[key] = new(key, text, evidence);
        Add("message.QuerySamples", "Consulta {0} por número de muestra", "R315168 rev.22 §5.3.3.5, Q.3");
        result["value.Image"] = StandardDiagnosticVocabulary.Shared["value.Image"];
        Add("unit.NonMeasurement", "—", "R315168 rev.22 §5.3.3.4, IF/RC: imagen o comentario, sin magnitud física");
        Add("value.ResearchIndicator", "Indicador de investigación", "R315168 rev.22 appendices C/F.2 pp.74/80; códigos sin unidad, no concentraciones");
        foreach (var (parameter, code, text) in UwamIndicators.Research)
            Add("research." + parameter + "." + code, text, "R315168 rev.22 appendix C p.74 y F.2 p.80; parámetro reservado y tipo S, UF-3000/3000x/4000/5000");
        foreach (var (code, text) in UwamIndicators.Reviews)
            Add("review." + code, text, "R315168 rev.22 appendix D pp.75-76 y F.3 p.80; comentario RC requiere revisión, no diagnóstico ni error confirmado");
        foreach (var (key, label) in new (string, string)[] { ("S", "Resultado"), ("RC", "Comentario de revisión"), ("RP", "Parámetro de sensibilidad") })
            Add("resultKind." + key, label, "R315168 rev.22 §5.3.3.4, R.3.7");
        Add("method.A", "Equipo", "R315168 rev.22 §5.3.3.4, R.3.5");
        Add("method.M", "Entrada manual", "R315168 rev.22 §5.3.3.4, R.3.5");
        foreach (var (key, label) in new (string, string)[] { ("RAW_Q", "Valor cualitativo"), ("RAW_C", "Concentración"),
            ("RAW_R1", "Reflectancia 1"), ("RAW_R2", "Reflectancia 2") })
            Add("resultFormat." + key, label, "R315168 rev.22 §5.3.3.4, 9.4 p.46; salida química por representación");
        Add("finding.ChemistryUnknown", "Error químico sin descripción documentada ({0})", "R315168 rev.22 appendix K; código/modelo sin correspondencia confirmada");
        foreach (var entry in UwamChemistryErrors.Definitions)
        {
            var code = UwamChemistryErrors.FindingCode(entry);
            Add("finding." + code, entry.Meaning + " (" + entry.Code + ")",
                "R315168 rev.22 appendix K pp.88-89; " + (entry.Model ?? "modelo UC no identificado; interpretación común o alternativas")
                + "; U-WAM " + entry.Code + " → analizador " + entry.SourceCode);
        }
        foreach (var (key, label) in new (string, string)[]
        {
            ("N", "Normal"), ("A", "Error de análisis"), ("W", "Baja fiabilidad"), ("W.Chemistry", "Baja fiabilidad: tira vencida"),
            ("@", "Fuera del intervalo de medición"), (">", "Repetición indicada"), (">.Chemistry", "Coloración anormal alta"),
            ("H", "Juicio de error"), ("H.Chemistry", "Positivo"), ("H.Unknown", "Positivo o juicio de error; modelo no identificado"),
            (">.Unknown", "Repetición o coloración anormal; modelo no identificado"), ("HH", "Coloración anormal baja"),
            ("LH.L", "Por debajo del intervalo normal"), ("LH.H", "Por encima del intervalo normal"),
            ("LH.LL", "Por debajo del límite de anormalidad"), ("LH.HH", "Por encima del límite de anormalidad"),
            ("QC.L", "Por debajo del límite de control"), ("QC.H", "Por encima del límite de control"),
        })
        {
            var shared = key switch { "N" => "state.Normal", "A" => "state.AnalysisError", "W" => "state.LowReliability", _ => null };
            if (shared is not null) result["flag." + key] = StandardDiagnosticVocabulary.Shared[shared];
            else Add("flag." + key, label, "R315168 rev.22 §5.3.3.4, 9.7 pp.46-47; analyzer flag y LH flag son contextos distintos");
        }
        return result;
    }
}
