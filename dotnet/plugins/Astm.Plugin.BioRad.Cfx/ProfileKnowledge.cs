using Astm.Plugins;
using static Astm.Plugins.InstrumentProfileKnowledge;

namespace Astm.Plugin.BioRad.Cfx;

public sealed partial class CfxPlugin : IInstrumentProfileKnowledge
{
    public IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId)
    {
        if (!Profiles.Any(p => p.Id == profileId)) return [];
        var facts = new List<InstrumentProfileFact>
        {
            Fact("cfx.control-boundary", "calibration", "export", InstrumentKnowledgeSupport.Unconfirmed,
                "Exportación de análisis, sin control de calibración instrumental", "Una curva estándar qPCR no es la calibración física del instrumento ni de sus colorantes. Los perfiles de exportación no ofrecen consulta de estado ni ejecución de calibración.",
                "Bio-Rad CFX Maestro 2.3 10000126764B pp.77-80,199-204; soporte limitado a exportaciones"),
            Fact("cfx.rfu-opportunity", "reaction-series", "export", InstrumentKnowledgeSupport.Documented,
                "Series RFU disponibles en el software", "El fabricante documenta exportaciones de fluorescencia por pozo/ciclo y datos de fusión. El plugin actual importa cuantificación/RDML y curva estándar; incorporar esas series requiere corpus, ejes/unidades y un contrato específico.",
                "Bio-Rad CFX Maestro 2.3 10000126764B pp.206,212-213; https://www.bio-rad.com/sites/default/files/webroot/web/pdf/lsr/literature/10000126764.pdf"),
        };
        if (profileId == CurveProfileId) facts.Add(Fact("cfx.standard-curve", "reported-statistics", "csv", InstrumentKnowledgeSupport.Implemented,
            "Parámetros reportados de curva estándar", "Se importan eficiencia porcentual, pendiente, ordenada y R² por ensayo/objetivo. No se recalcula el ajuste ni se convierte en calibración del hardware.",
            "Bio-Rad CFX Maestro 2.3 10000126764B p.204 tabla 20"));
        if (profileId == CsvProfileId) facts.Add(Fact("cfx.replicate-statistics", "reported-statistics", "csv", InstrumentKnowledgeSupport.Implemented,
            "Cuantificación y estadísticas de réplicas", "Se conservan Cq, cantidades iniciales, medias y desvíos reportados con su ámbito. La estadística repetida en filas no es otra réplica ni una media objetivo de control.",
            "Bio-Rad CFX Maestro 2.3 10000126764B pp.202-203 tabla 19"));
        if (profileId == RdmlProfileId) facts.Add(Fact("cfx.rdml-material", "quality-control", "rdml", InstrumentKnowledgeSupport.Implemented,
            "Estándares, controles y exclusiones RDML", "Se preservan tipo de material, corrida, reacción, Cq y exclusiones del subconjunto RDML 1.0/1.1. Un estándar no se etiqueta como calibrador instrumental; no se afirma interpretar todo el esquema o curvas RFU.",
            "Bio-Rad CFX Maestro 2.3 10000126764B p.192; esquemas RDML 1.0/1.1 y CfxPlugin"));
        return Bind(typeof(CfxPlugin), profileId, facts.ToArray());
    }
}
