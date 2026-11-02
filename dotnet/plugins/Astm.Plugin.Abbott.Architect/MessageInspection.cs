using Astm.Plugins;

namespace Astm.Plugin.Abbott.Architect;

public sealed partial class ArchitectPlugin : IInstrumentMessageInspection
{
    private static readonly InstrumentMessageSchema InspectionSchema = InstrumentMessageSchema.Create<ArchitectPlugin>(
        InstrumentMessageSyntax.Lis02, "Abbott 91407-108 (2012), §2 outgoing / §3 incoming ARCHITECT messages: Patient, Test Order and Result; ArchitectDialect.",
        ("P.3", "Identificador de paciente"),
        ("P.6", "Nombre del paciente"),
        ("P.6.1", "Apellido"),
        ("P.6.2", "Nombre"),
        ("P.8", "Fecha de nacimiento"),
        ("P.9", "Sexo administrativo"),
        ("P.14", "Médico"),
        ("P.26", "Ubicación"),
        ("O.3", "Identificador de muestra"),
        ("O.4", "Identificador instrumental de muestra"),
        ("O.5", "Ensayos solicitados"),
        ("O.5.4", "Código de ensayo"),
        ("O.6", "Prioridad"),
        ("O.12", "Acción / control"),
        ("R.3", "Identificación extendida del ensayo"),
        ("R.3.4", "Código de ensayo"),
        ("R.3.5", "Nombre del ensayo"),
        ("R.3.6", "Dilución"),
        ("R.3.7", "Estado del ensayo"),
        ("R.3.8", "Lote de reactivo"),
        ("R.3.9", "Número de serie del reactivo"),
        ("R.3.10", "Lote de control"),
        ("R.3.11", "Tipo de resultado"),
        ("R.4", "Resultado"),
        ("R.5", "Unidad"),
        ("R.6", "Intervalo de referencia"),
        ("R.7", "Indicadores"),
        ("R.9", "Estado del resultado"),
        ("R.11", "Operador"),
        ("R.13", "Fecha y hora de medición"),
        ("R.14", "Módulo"));

    public InstrumentMessageSchema MessageSchema(string profileId) => profileId == AsiProfileId
        ? InspectionSchema : throw new ArgumentException("Unknown message inspection profile.", nameof(profileId));
}
