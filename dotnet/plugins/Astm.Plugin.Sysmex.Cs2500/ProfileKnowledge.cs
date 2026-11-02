using Astm.Plugins;
using static Astm.Plugins.InstrumentProfileKnowledge;

namespace Astm.Plugin.Sysmex.Cs2500;

public sealed partial class Cs2500Plugin : IInstrumentProfileKnowledge
{
    public IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId) => profileId == Profiles[0].Id ? Bind(typeof(Cs2500Plugin), profileId,
        Fact("cs2500.calibration-errors", "calibration", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Errores de calibración en resultados", "R.7.2 distingue ausencia de curva validada y errores de parámetros/datos. La máscara X en R.4 también indica ausencia de curva validada. La ausencia de error no certifica calibración vigente.",
            "Sysmex CS-2500 ASTM Host Interface rev.2.04 pp.33,53,55; R.4 y R.7.2, apéndice C tabla 19"),
        Fact("cs2500.calibration-query", "calibration", "lis02", InstrumentKnowledgeSupport.Unconfirmed,
            "Consulta independiente de calibración sin confirmar", "El manual revisado no aporta una consulta de estado ni descarga de curva equivalente a ARCHITECT. El perfil recibe resultados; no anuncia comandos de calibración.",
            "Sysmex CS-2500 ASTM Host Interface rev.2.04 §5.3.2/5.3.3; alcance del perfil sysmex.cs2500.astm02.results"),
        Fact("cs2500.qc-material", "quality-control", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Material y lote de control", "O.12=Q y el identificador QC de ancho fijo permiten conservar nombre y lote. No se informa un nivel separado ni se deducen objetivos o desvíos del material.",
            "Sysmex CS-2500 ASTM Host Interface rev.2.04 p.28: QC + nombre(7) + lote(6)"),
        Fact("cs2500.replicates", "reported-statistics", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Réplicas, promedios y reanálisis", "R.3.7 distingue medición, promedio y reanálisis; R.3.11 conserva el número de repeticiones cuando está habilitado. Un promedio no se cuenta como réplica individual; envío manual no implica medición manual.",
            "Sysmex CS-2500 ASTM Host Interface rev.2.04 pp.31-33; Specify Replication y Output Error Information son configurables"),
        Fact("cs2500.reaction-files", "external-resources", "lis02", InstrumentKnowledgeSupport.Documented,
            "Referencias de imágenes y datos de reacción", "El fabricante documenta rutas y archivos asociados a resultados. El original puede conservarse; su adquisición, límites, permisos e importación tipada requieren un adaptador del anfitrión. No se abren rutas del instrumento automáticamente.",
            "Sysmex CS-2500 ASTM Host Interface rev.2.04 §5.3.3.5, tabla 15")) : [];
}
