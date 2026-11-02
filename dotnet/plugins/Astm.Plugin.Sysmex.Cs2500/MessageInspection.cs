using Astm.Plugins;

namespace Astm.Plugin.Sysmex.Cs2500;

public sealed partial class Cs2500Plugin : IInstrumentMessageInspection
{
    private static readonly InstrumentMessageSchema InspectionSchema = InstrumentMessageSchema.Create<Cs2500Plugin>(
        InstrumentMessageSyntax.Lis02, "Sysmex CS-2500 ASTM Host Interface Specifications Rev.2.04, §5.3.3.2/4/5; unused patient fields are not inferred.",
        ("P.6", "Nombre del paciente (vacío en QC)"),
        ("P.6.2", "Nombre"),
        ("P.6.3", "Apellido"),
        ("O.4", "Identificación instrumental de muestra"),
        ("O.4.3", "Identificador de muestra"),
        ("O.5", "Ensayos solicitados"),
        ("O.12", "Acción / control"),
        ("R.3", "Identificación extendida del análisis"),
        ("R.3.4", "Código de análisis"),
        ("R.3.5", "Nombre del análisis"),
        ("R.3.6", "Dilución"),
        ("R.3.7", "Tipo de resultado"),
        ("R.3.8", "Solicitud ampliada"),
        ("R.3.9", "Resultado ampliado"),
        ("R.3.10", "Reflex"),
        ("R.3.11", "Repeticiones comunicadas"),
        ("R.4", "Resultado / máscara de medición"),
        ("R.5", "Unidad"),
        ("R.7", "Indicadores del análisis"),
        ("R.7.1", "Indicador de resultado"),
        ("R.7.2", "Evaluación"),
        ("R.7.3", "Información instrumental"),
        ("R.9", "Estado parcial o completo"),
        ("R.13", "Fecha y hora de medición"));

    public InstrumentMessageSchema MessageSchema(string profileId) => profileId == Profiles[0].Id
        ? InspectionSchema : throw new ArgumentException("Unknown message inspection profile.", nameof(profileId));
}
