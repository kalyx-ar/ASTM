using Astm.Plugins;
using static Astm.Plugins.InstrumentProfileKnowledge;

namespace Astm.Plugin.Abbott.CellDynRuby;

public sealed partial class CellDynRubyPlugin : IInstrumentProfileKnowledge
{
    public IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId) => profileId == ProfileId ? Bind(typeof(CellDynRubyPlugin), profileId,
        Fact("ruby.calibration-evidence", "calibration", "lis02", InstrumentKnowledgeSupport.Unconfirmed,
            "Calibración pendiente de manual de interfaz", "El perfil deriva de trazas locales y del host anterior. Sin manual del fabricante no se confirma consulta de estado, informe de curva ni comandos de calibración; no se transfieren capacidades de ARCHITECT por compartir marca.",
            "CellDynRubyPlugin.Describe y REFERENCIAS-LEGACY; manual del fabricante ausente en el corpus local"),
        Fact("ruby.order-boundary", "interoperability", "lis02", InstrumentKnowledgeSupport.Implemented,
            "Consultas y órdenes con evidencia de trazas", "Se conservan resultados, alarmas, consultas ALL y órdenes New/Cancel. ACK e intercambio no certifican ejecución física; encuadre y timers pendientes de confirmar con manual/equipo permanecen identificados como hipótesis.",
            "Trazas locales de CELL-DYN Ruby y perfil abbott.celldyn-ruby.lis2a; sin homologación física")) : [];
}
