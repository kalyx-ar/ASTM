using Astm.Formats.Medical;
using Astm.Plugins;
using static Astm.Plugins.InstrumentProfileKnowledge;

namespace Astm.Plugin.Abbott.Architect;

public sealed partial class ArchitectPlugin : IInstrumentProfileKnowledge
{
    public IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId) => profileId == AsiProfileId ? Bind(typeof(ArchitectPlugin), profileId,
        Fact("architect.asi-calibration", "calibration", "lis02", InstrumentKnowledgeSupport.Excluded,
            "Calibradores excluidos de ASI", "ASI suprime resultados y excepciones de calibradores. No se anuncia informe de curva, consulta de estado ni orden de calibración por esta interfaz.",
            "Abbott 91407-108 §2-3; la exclusión ASI no se extiende a HL7"),
        Fact("architect.asi-qc", "quality-control", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Control de calidad según liberación y configuración", "El envío QC requiere resultado liberado, transmisión Single/Collated y conexión habilitada. R.3.10 conserva lote, R.14 módulo y R.6 el rango; un rango no equivale a media objetivo o desvío.",
            "Abbott 91407-108 §2-3/4 y tabla R pp.2-16/17"),
        Fact("architect.asi-signal", "reported-statistics", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Concentración, señal e interpretación separadas", "R.3.11 identifica F/P/I y sus variantes. P puede contener RLU, absorbancia o mV; se conserva como representación distinta de la concentración y de una réplica adicional.",
            "Abbott 91407-108 tabla R pp.2-16/17; ArchitectDialect.ResultKind")) : [];
}

public sealed partial class ArchitectHl7Plugin : IInstrumentProfileKnowledge
{
    public static IReadOnlyList<InstrumentProfileFact> ProfileKnowledge { get; } = Bind(typeof(ArchitectHl7Plugin), ProfileId,
        Fact("architect.hl7-report", "calibration", "hl7v2", InstrumentKnowledgeSupport.Implemented,
            "Informe de calibración al finalizar", "OUL/R24 comunica una calibración exitosa o fallida por ensayo/módulo/lote. Se conservan documento Base64, hash, texto y materiales; no es envío periódico ni descarga histórica y no se supone un modelo matemático fijo.",
            "Abbott 206797-101 §2-17/19 y §3-57/63", MedicalPurposes.ReceiveCalibration),
        Fact("architect.hl7-inventory", "calibration", "hl7v2", InstrumentKnowledgeSupport.Implemented,
            "Consulta de inventario y estado de calibración", "INR/U06 pide todos los elementos de un módulo. ACK sólo confirma recepción; INU/U05 se recibe independientemente. No se inicia físicamente una calibración.",
            "Abbott 206797-101 §2-27/30 y §3-12/22", MedicalPurposes.QueryInventory),
        Fact("architect.hl7-reagents", "reagents", "hl7v2", InstrumentKnowledgeSupport.Implemented,
            "Lotes, cantidades, vencimiento y estabilidad", "Cada INV conserva reactivo/serial, ensayos asociados y campos originales de cantidad, unidades, vencimiento y estabilidad. INV.19 puede describir estabilidad del reactivo o de la calibración según el tipo de estado; no se confunden ambas.",
            "Abbott 206797-101 §3-12/22; InstrumentInventoryStatus.SourceFields", MedicalPurposes.ReceiveInventory),
        Fact("architect.hl7-reference", "calibration", "hl7v2", InstrumentKnowledgeSupport.Implemented,
            "Referencia de calibración usada", "ZCL en R22 identifica ensayo, lote y fecha de la calibración usada. Es una referencia distinta del informe R24 y no se asocia a otro ensayo por proximidad.",
            "Abbott 206797-101 §3-97/98"),
        Fact("architect.hl7-equipment", "instrument-state", "hl7v2", InstrumentKnowledgeSupport.Documented,
            "Estado del módulo y notificaciones documentados", "ESR/U02 y ESU/U01 permiten solicitar/comunicar estado; EAN/U09 informa notificaciones. Son oportunidades documentadas, todavía sin binding, modelo de datos y observador propios implementados.",
            "Abbott 206797-101 §2-21/25 y §2-32/34"),
        Fact("architect.hl7-service", "maintenance", "hl7v2", InstrumentKnowledgeSupport.Documented,
            "Mantenimiento y configuración del fabricante", "El manual define eventos de servicio y consulta de configuración, además de prueba de conexión. Requieren implementación y validación específicas; no se anuncian como comandos disponibles ni conformidad LAW completa.",
            "Abbott 206797-101 §2-35/43"));
    public IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId) => profileId == ProfileId ? ProfileKnowledge : [];
}
