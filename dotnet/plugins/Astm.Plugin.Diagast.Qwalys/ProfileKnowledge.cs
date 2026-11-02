using Astm.Plugins;
using static Astm.Plugins.InstrumentProfileKnowledge;

namespace Astm.Plugin.Diagast.Qwalys;

public sealed partial class QwalysPlugin : IInstrumentProfileKnowledge
{
    public IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId) => profileId == Profiles[0].Id ? Bind(typeof(QwalysPlugin), profileId,
        Fact("qwalys.calibration", "calibration", "lis02", InstrumentKnowledgeSupport.Unconfirmed,
            "Calibración sin canal documentado", "El manual revisado describe órdenes, resultados y trazabilidad de reacciones; no acredita consulta de calibración, curva ni estadísticas QC universales.",
            "DIAGAST QWALYS 3 Connection Manual §1.1 y §§4.5-4.7"),
        Fact("qwalys.reaction-traceability", "interpretation", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Trazabilidad y modificación de reacciones", "Se conservan ensayo, reactivo/donante, pocillo, nivel, soporte, operador y fecha cruda de modificación. El identificador de reactivo no se convierte en lote; el soporte de reacción no se presenta como unidad.",
            "DIAGAST QWALYS 3 Connection Manual §§4.5-4.7; QwalysDialect.VendorIdentity"),
        Fact("qwalys.append-files", "external-resources", "file-exchange", InstrumentKnowledgeSupport.Documented,
            "Intercambio por archivos append", "La interfaz de red agrega mensajes a archivos. El anfitrión necesita cursor/checkpoint y límites de mensaje; una novedad de archivo no equivale a un mensaje completo. ASTM no vigila ni archiva carpetas.",
            "DIAGAST QWALYS 3 Connection Manual §2.3")) : [];
}
