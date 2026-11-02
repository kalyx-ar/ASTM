using Astm.Plugins;

namespace Astm.Plugin.Abbott.CellDynRuby;

public sealed partial class CellDynRubyPlugin : IInstrumentMessageInspection
{
    private static readonly InstrumentMessageSchema InspectionSchema = InstrumentMessageSchema.Create<CellDynRubyPlugin>(
        InstrumentMessageSyntax.Lis02, "CELL-DYN Ruby: hipótesis del perfil derivada de trazas; posiciones usadas por CellDynRubyDialect/StandardAstmDialect, sin manual de interfaz homologado.",
        ("P.6", "Nombre del paciente (posición LIS02; hipótesis)"),
        ("O.3", "Identificador de muestra"),
        ("O.4", "Identificador instrumental de muestra"),
        ("R.3", "Identificación del parámetro"),
        ("R.3.4", "Panel"),
        ("R.3.7", "Código de parámetro"),
        ("R.4", "Resultado o alarma FLAG"),
        ("R.5", "Unidad"),
        ("R.7", "Indicadores"),
        ("R.9", "Estado del resultado"),
        ("R.13", "Fecha y hora de medición"));

    public InstrumentMessageSchema MessageSchema(string profileId) => profileId == ProfileId
        ? InspectionSchema : throw new ArgumentException("Unknown message inspection profile.", nameof(profileId));
}
