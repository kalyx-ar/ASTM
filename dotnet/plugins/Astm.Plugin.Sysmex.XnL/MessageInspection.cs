using Astm.Plugins;

namespace Astm.Plugin.Sysmex.XnL;

public sealed partial class XnLPlugin : IInstrumentMessageInspection
{
    private static readonly InstrumentMessageSchema InspectionSchema = InstrumentMessageSchema.Create<XnLPlugin>(
        InstrumentMessageSyntax.Lis02, "Sysmex XN-L LIS Interface Implementation Guide 1273-MKT Rev.3, ASTM result records; positions implemented by XnLDialect. Patient components without a verified map remain unknown.",
        ("O.3", "Identificación de muestra"),
        ("O.3.3", "Identificador alternativo de muestra"),
        ("O.4", "Identificación instrumental de muestra"),
        ("O.4.3", "Identificador de muestra"),
        ("O.12", "Acción / control"),
        ("R.3", "Identificación del análisis"),
        ("R.3.5", "Código de análisis"),
        ("R.3.6", "Dilución"),
        ("R.3.9", "Resultado ampliado"),
        ("R.4", "Resultado"),
        ("R.5", "Unidad"),
        ("R.7", "Indicadores"),
        ("R.9", "Juicio instrumental F/P (sin validación clínica)"),
        ("R.13", "Fecha y hora de medición"));

    public InstrumentMessageSchema MessageSchema(string profileId) => profileId == Profiles[0].Id
        ? InspectionSchema : throw new ArgumentException("Unknown message inspection profile.", nameof(profileId));
}
