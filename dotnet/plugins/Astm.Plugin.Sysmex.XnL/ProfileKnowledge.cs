using Astm.Plugins;
using static Astm.Plugins.InstrumentProfileKnowledge;

namespace Astm.Plugin.Sysmex.XnL;

public sealed partial class XnLPlugin : IInstrumentProfileKnowledge
{
    public IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId) => profileId == Profiles[0].Id ? Bind(typeof(XnLPlugin), profileId,
        Fact("xnl.qc-output", "quality-control", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Dos salidas configurables de control de calidad", "Sample Explorer puede enviar QC-lote y QC Chart un número de archivo/XbarM. Se pueden habilitar ambas, una o ninguna; no se convierten números de archivo en lotes ni dos representaciones en dos mediciones nuevas.",
            "Sysmex XN-L LIS Interface Guide 1273-MKT rev.3 §5 p.14 y ejemplos pp.37-38"),
        Fact("xnl.sncs-calibration", "calibration", "manufacturer-service", InstrumentKnowledgeSupport.Documented,
            "Verificación remota mediante SNCS", "SNCS es software propietario que conecta el analizador con servidores Sysmex y ofrece verificación remota de calibración, inventario y soporte. Es un canal distinto del LIS; este plugin no integra ni acciona SNCS.",
            "Sysmex XN-L LIS Interface Guide 1273-MKT rev.3 §12 p.40; https://www.sysmex.com/en-us/company/service-and-support"),
        Fact("xnl.calibration-query", "calibration", "lis02", InstrumentKnowledgeSupport.Unconfirmed,
            "Estado de calibración por LIS sin confirmar", "La interfaz LIS revisada no documenta una consulta equivalente a INR/U06 ni informe de curva R24. QC y ausencia de flags no certifican calibración vigente.",
            "Sysmex XN-L LIS Interface Guide 1273-MKT rev.3, §5 y registros ASTM"),
        Fact("xnl.ip-messages", "interpretation", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Mensajes interpretativos y acciones del analizador", "Los parámetros IP y los flags se interpretan por su contexto; una A puede señalar una sospecha o una acción pendiente, y no siempre un error de medición. Se conservan originales y autoridad del código.",
            "Sysmex XN-L LIS Interface Guide 1273-MKT rev.3 tablas 6-8; XnLMessages y conocimiento contextual del plugin"),
        Fact("xnl.suit-interface", "interoperability", "separate-interface", InstrumentKnowledgeSupport.Documented,
            "SUIT es otra interfaz", "SUIT v8 documenta órdenes, resultados y mensajes QC propios. No hay plugin SUIT incluido; no se pasan sus registros al parser XN-L/U-WAM ni se anuncia soporte por compartir la marca.",
            "Sysmex SUIT Protocol Description v8.0 §3.3.5, §4.8 y §5.2.6; no acredita calibración remota LIS")) : [];
}
