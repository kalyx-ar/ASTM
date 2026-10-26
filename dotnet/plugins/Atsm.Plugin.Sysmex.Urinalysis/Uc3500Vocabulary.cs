using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Urinalysis;

internal sealed partial class Uc3500Interpreter
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = CreateVocabulary();
    internal static Dictionary<string, ProtocolText> CreateVocabulary()
    {
        var result = new Dictionary<string, ProtocolText>(StringComparer.Ordinal);
        void Add(string key, string text, string section) => result[key] = new(key, text, Manual + " " + section);
        Add("kind.RQ02", "Consulta de orden", "tabla 6-14");
        Add("kind.CM00", "Orden de análisis", "tabla 6-15");
        Add("kind.ER", "Respuesta a la orden", "tabla 6-16");
        Add("kind.RESULT", "Resultados de análisis", "tabla 6-17");
        Add("value.Crosses", "Semicuantitativo (cruces)", "tabla 6-18: valores cualitativos ordenados en cruces");
        Add("value.BinaryCrosses", "Cualitativo (cruces)", "tabla 6-18: NIT negativo/positivo");
        Add("value.SemiQuantitative", "Valor semicuantitativo", "tabla 6-18: valores con comparadores/límites");
        Add("unit.Percent", "%", "tabla 6-17: reflectancia R%(1)/R%(2)");
        Add("unit.Dimensionless", "Adimensional", "tabla 6-18: pH y densidad relativa");
        Add("unit.None", "—", "tabla 6-17: dato cualitativo/color, sin unidad física");
        Add("unit.NotTransmitted", "No informada", "tabla 6-17: el mensaje no incluye un campo de unidad de concentración");
        Add("state.UC3500.Analysed", "Analizado", "tabla 6-17: estado 0 = analizado; no implica validación clínica");
        Add("flag.UC3500.00BB", "Error de lectura del código de barras (00BB)", "§7, tablas 7-1 y 7-2");
        Add("flag.UC3500.00CC", "Error de solicitud (00CC)", "§7, tablas 7-1 y 7-2");
        Add("flag.UC3500.00DD", "Tira reactiva vencida (00DD)", "§7, tablas 7-1 y 7-2");
        Add("flag.UC3500.*", "Positivo", "tabla 6-17, marca de comentario");
        Add("flag.UC3500.?", "Coloración anormal baja", "tabla 6-17, marca de comentario");
        Add("flag.UC3500.!", "Coloración anormal alta", "tabla 6-17, marca de comentario");
        foreach (var (key, label) in new (string, string)[] { ("QUALITATIVE", "Valor cualitativo"), ("CONCENTRATION", "Concentración informada"),
            ("REFLECTANCE1", "Reflectancia R%(1)"), ("REFLECTANCE2", "Reflectancia R%(2)"), ("COLOR", "Color informado"), ("COLOR-RANK", "Código de evaluación del color"), ("TURBIDITY", "Turbidez informada") })
            Add("resultKind." + key, label, "tabla 6-17");
        Add("RQ02", "Consulta de orden para muestra {0}, posición {1} (RQ02)", "§6.1.2.4, tabla 6-14");
        Add("CM00", "Orden para muestra {0}, posición {1}; tira solicitada: «{2}» (CM00)", "§6.1.2.5, tabla 6-15");
        Add("CM00.EMPTY", "El anfitrión no tiene orden para la muestra {0}, posición {1}; campo de tira vacío (CM00)", "§6.1.2.5, tabla 6-15");
        Add("ER00", "Respuesta a la orden sin error (ER00)", "§6.1.2.6, tabla 6-16");
        Add("ER01", "Error de formato de la orden (ER01)", "§6.1.2.6, tabla 6-16");
        Add("ER03", "La identificación de la orden no coincide con la consulta (ER03)", "§6.1.2.6, tabla 6-16");
        Add("ER04", "La tira solicitada no está colocada en el rack de botellas (ER04)", "§6.1.2.6, tabla 6-16");
        Add("ER05", "La tira solicitada no está registrada en el analizador (ER05)", "§6.1.2.6, tabla 6-16");
        Add("ER.UNKNOWN", "Respuesta de orden con código sin interpretación documentada ({0})", "§6.1.2.6");
        Add("RESULT", "Resultados de muestra {0}, serie {1}, análisis {2}; código del equipo: {3}", "§6.1.2.7, tabla 6-17");
        Add("UNKNOWN", "Registro UC-3500 sin interpretación documentada; se conserva el original", "§6.1.2");
        Add("INVALID", "Registro UC-3500 incompleto o inválido; se conserva el original", "§6.1.1.3-.4 / §6.1.2");
        foreach (var control in new[] { "ACK", "NAK", "EOT" }) result[control] = StandardDiagnosticVocabulary.Shared["Command." + control];
        Add("finding.ATSM-UC3500-ANALYSIS-00BB", "Error de lectura del código de barras (00BB). Puede persistir aunque se corrija manualmente la identificación antes del reenvío.", "§7, tablas 7-1 y 7-2");
        Add("finding.ATSM-UC3500-ANALYSIS-00CC", "Error de orden (00CC). El equipo puede medir con su tira configurada si ORDER ERROR está en MEASURE; el código no acredita la aceptación de la orden del anfitrión.", "§6.1.2.6 / §7, tablas 7-1 y 7-2");
        Add("finding.ATSM-UC3500-ANALYSIS-00DD", "Advertencia de vencimiento de las tiras (00DD).", "§7, tablas 7-1 y 7-2");
        Add("finding.ATSM-UC3500-ANALYSIS-STRIP", "Error relacionado con las tiras reactivas", "§7, tabla 7-1, patrón X0X0");
        Add("finding.ATSM-UC3500-ANALYSIS-MEASUREMENT", "Error de análisis", "§7, tabla 7-1, patrón YY00");
        Add("finding.ATSM-UC3500-ANALYSIS-LIGHT", "Error del sistema óptico", "§7, tabla 7-1, patrón 0X0X");
        Add("finding.ATSM-UC3500-ER01", "El equipo informó un error de formato en la orden (ER01).", "tabla 6-16");
        Add("finding.ATSM-UC3500-ER03", "El equipo informó que la orden no coincide con la consulta (ER03).", "tabla 6-16");
        Add("finding.ATSM-UC3500-ER04", "La tira solicitada no está colocada en el rack de botellas (ER04). Revise la tira pedida por el anfitrión y la configuración/carga del equipo.", "tabla 6-16");
        Add("finding.ATSM-UC3500-ER05", "La tira solicitada no está registrada en el analizador (ER05).", "tabla 6-16");
        foreach (var (key, text) in new (string, string)[]
        {
            ("CHECKSUM", "El checksum UC-3500 no coincide o está incompleto; se excluyeron sus resultados."),
            ("INCOMPLETE", "La captura termina dentro de un registro UC-3500; no se infieren resultados."),
            ("RESTART", "Se encontró STX dentro de un registro abierto; se conservó el fragmento anterior."),
            ("OVERSIZE", "El registro excede las longitudes de campos UC-3500 conocidas."),
            ("LAYOUT", "La estructura o longitud de los campos no coincide con el manual UC-3500."),
            ("UNKNOWN", "El equipo informó un registro o código UC-3500 sin interpretación documentada."),
            ("DIRECTION", "El registro circula en sentido contrario al documentado; confirme quién registró el log."),
            ("CORRELATION", "La orden no coincide con la consulta previa o falta parte del intercambio en la captura."),
            ("PENDING", "La captura no contiene la respuesta final de una consulta u orden UC-3500."),
            ("ANALYSIS-ERROR", "El equipo informó un código de análisis distinto de 0000; se conserva sin decidir validez clínica."),
            ("TIMESTAMP", "La fecha/hora UC-3500 es inválida o su siglo/zona horaria es ambiguo; se conserva el original."),
            ("LENGTH-DISCREPANCY", "La longitud supera los 441 bytes indicados en el texto del manual, pero coincide con la suma de anchos de su tabla de resultados (446)."),
        }) Add("finding.ATSM-UC3500-" + key, text, "§6.1 / §7");
        return result;
    }
    private ProtocolExplanation Explain(string code, params string[] args) => new(DiagnosticVocabulary[code], args);
}
