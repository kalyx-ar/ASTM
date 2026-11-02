using Astm.Plugins;
using static Astm.Plugins.InstrumentProfileKnowledge;

namespace Astm.Plugin.Sysmex.Urinalysis;

public sealed partial class SysmexUrinalysisPlugin : IInstrumentProfileKnowledge
{
    public IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId)
    {
        if (profileId is WithChemistryProfile or WithoutChemistryProfile) return Bind(typeof(SysmexUrinalysisPlugin), profileId,
            Fact("uwam.qc-context", "quality-control", "lis02", InstrumentKnowledgeSupport.Implemented,
                "Identificación y representaciones de QC", "El identificador puede combinar material/lote/nivel/método según equipo y configuración. Se conservan método, formato y serial; no se separan componentes ambiguos ni se convierte el rango en media/desvío.",
                "Sysmex U-WAM R315168 rev.22 pp.39-40,43,47-49; UwamDialect y AnalyticalQualityContext"),
            Fact("uwam.qc-reference", "external-resources", "lis02", InstrumentKnowledgeSupport.Implemented,
                "Referencia al archivo del lote QC", "S.7 entrega el localizador como QualityResourceReference en el mismo lote, incluso sin observaciones. Recibir la referencia no acredita contenido adquirido ni permite abrir una ruta UNC/URL automáticamente.",
                "Sysmex U-WAM R315168 rev.22 §5.3.3.7 p.52 y apéndice J p.84"),
            Fact("uwam.qc-target-import", "quality-control", "csv", InstrumentKnowledgeSupport.Documented,
                "Objetivos y límites del lote en CSV", "Los archivos QC difieren por familia. Su importación tipada y asociación a lote requieren custodia, autorización y formato/versión comprobados; el plugin conserva la referencia pero todavía no importa esos objetivos.",
                "Sysmex U-WAM R315168 rev.22 apéndice J"),
            Fact("uwam.calibration-query", "calibration", "lis02", InstrumentKnowledgeSupport.Unconfirmed,
                "Consulta de calibración sin evidencia", "La interfaz revisada no documenta una consulta de estado o curva equivalente a ARCHITECT. QC y sus límites no certifican calibración vigente; el soporte U-WAM no se generaliza a toda conexión directa UF/UC/UD.",
                "Sysmex U-WAM R315168 rev.22; matriz por interfaz CALIDAD-Y-CALIBRACION"),
            Fact("uwam.reviews-images", "interpretation", "lis02", InstrumentKnowledgeSupport.Implemented,
                "Revisiones, indicadores y referencias de imágenes", "Se conservan RC/RP, método, representación e indicadores por modelo. Una referencia de imagen es contenido estructurado, no una medición numérica ni permiso para acceder a la ruta del fabricante.",
                "Sysmex U-WAM R315168 rev.22 registros R, indicadores y formatos; UrinalysisKnowledge"));
        if (profileId == Uc3500Profile) return Bind(typeof(SysmexUrinalysisPlugin), profileId,
            Fact("uc3500.reflectance", "quality-control", "serial-passive", InstrumentKnowledgeSupport.Implemented,
                "Controles y reflectancias originales", "La serie C identifica material de control. Se conservan R%(1)/R%(2), tira, número/serie y errores; no se inventan lote/nivel ni unidades ausentes. El perfil directo es pasivo.",
                "UC3500-PROTOCOLO; especificación serial local y tabla 7-1; Uc3500Interpreter"),
            Fact("uc3500.calibration-query", "calibration", "serial-passive", InstrumentKnowledgeSupport.Unconfirmed,
                "Calibración no identificada en el protocolo directo", "Los comandos/resultados conocidos no acreditan consulta de calibración, curva ni una operación física de calibrar. QC y aceptación ER00 son evidencias distintas.",
                "UC3500-PROTOCOLO; UC-3500 serial RQ02/CM00/ER, sin evidencia de comando de calibración"));
        if (profileId == UfBinaryProfile) return Bind(typeof(SysmexUrinalysisPlugin), profileId,
            Fact("uf.service-notices", "maintenance", "proprietary-passive", InstrumentKnowledgeSupport.Implemented,
                "Avisos de preparación y limpieza", "Se reconocen los avisos conocidos de limpieza/preparación. Una necesidad informada no demuestra que la tarea se ejecutó ni finalizó; se conserva el código de origen.",
                "UF-CONTROL-Y-TIEMPOS; DBS_LANG_01746/DBS_LANG_02215 y UfBinaryInterpreter"),
            Fact("uf.counter-limits", "instrument-state", "proprietary-passive", InstrumentKnowledgeSupport.Unconfirmed,
                "Contadores y calibración sin semántica completa", "Se reconstruyen contadores/niveles, pero su asignación física y unidades no están confirmadas. No se publican porcentajes de reactivo, disponibilidad física ni calibración a partir de esos números.",
                "UF-CONTROL-Y-TIEMPOS, hipótesis de remanentes y límites P-15"));
        return [];
    }
}
