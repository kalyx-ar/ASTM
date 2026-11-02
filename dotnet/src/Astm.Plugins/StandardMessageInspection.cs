namespace Astm.Plugins;

public sealed partial class StandardAstmPlugin : IInstrumentMessageInspection
{
    private static readonly InstrumentMessageSchema InspectionSchema = InstrumentMessageSchema.Create<StandardAstmPlugin>(
        InstrumentMessageSyntax.Lis02, "CLSI LIS02-A2: patient, order and result record field positions; StandardAstmDialect.",
        ("P.3", "Identificador de paciente del consultorio"),
        ("P.4", "Identificador de paciente del laboratorio"),
        ("P.5", "Identificador de paciente"),
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
        ("O.12", "Acción"),
        ("O.16", "Tipo de muestra"),
        ("O.26", "Tipo de informe"),
        ("R.3", "Identificador de ensayo"),
        ("R.3.4", "Código de ensayo"),
        ("R.4", "Resultado"),
        ("R.5", "Unidad"),
        ("R.6", "Intervalo de referencia"),
        ("R.7", "Indicadores"),
        ("R.9", "Estado del resultado"),
        ("R.11", "Operador"),
        ("R.13", "Fecha y hora de medición"),
        ("R.14", "Instrumento"));

    public InstrumentMessageSchema MessageSchema(string profileId) => profileId == ProfileId
        ? InspectionSchema : throw new ArgumentException("Unknown message inspection profile.", nameof(profileId));
}
