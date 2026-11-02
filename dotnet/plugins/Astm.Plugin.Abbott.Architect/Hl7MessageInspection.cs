using Astm.Plugins;

namespace Astm.Plugin.Abbott.Architect;

public sealed partial class ArchitectHl7Plugin : IInstrumentMessageInspection
{
    public static InstrumentMessageSchema InspectionSchema { get; } = InstrumentMessageSchema.Create<ArchitectHl7Plugin>(
        InstrumentMessageSyntax.Hl7, "Abbott ARCHITECT HL7 Interface Manual 206797-101: PID/SPM/OBR/OBX definitions and OUL/R22; ArchitectHl7Dialect.",
        ("MSH.1", "Separador de campos"),
        ("MSH.2", "Separadores de codificación"),
        ("MSH.9", "Tipo de mensaje"),
        ("MSH.10", "Identificador del mensaje"),
        ("MSH.12", "Versión HL7"),
        ("PID.3", "Identificadores del paciente"),
        ("PID.3.1", "Identificador de paciente"),
        ("PID.5", "Nombre del paciente"),
        ("PID.5.1", "Apellido"),
        ("PID.5.2", "Nombre"),
        ("PID.7", "Fecha de nacimiento"),
        ("PID.8", "Sexo administrativo"),
        ("SPM.2", "Identificadores de muestra"),
        ("SPM.2.1", "Identificador de muestra"),
        ("SPM.11", "Rol de la muestra"),
        ("SAC.3", "Identificador del contenedor"),
        ("OBR.3", "Identificador instrumental de solicitud"),
        ("OBR.4", "Ensayo"),
        ("OBR.4.1", "Código de ensayo"),
        ("OBX.1", "Secuencia del resultado"),
        ("OBX.2", "Tipo de dato del resultado"),
        ("OBX.3", "Identificación del ensayo"),
        ("OBX.3.1", "Código de ensayo"),
        ("OBX.3.1.1", "Código de ensayo"),
        ("OBX.3.1.2", "Dilución"),
        ("OBX.3.1.3", "Tipo de resultado"),
        ("OBX.3.2", "Nombre del ensayo"),
        ("OBX.4", "Subidentificador de observación"),
        ("OBX.5", "Resultado"),
        ("OBX.6", "Unidad"),
        ("OBX.6.1", "Unidad"),
        ("OBX.7", "Intervalo de referencia"),
        ("OBX.8", "Indicadores"),
        ("OBX.11", "Estado del resultado"),
        ("OBX.14", "Fecha y hora de medición"),
        ("OBX.18", "Identificador del equipo"),
        ("OBX.19", "Fecha y hora del análisis"),
        ("NTE.3", "Comentario"));

    public InstrumentMessageSchema MessageSchema(string profileId) => profileId == ProfileId
        ? InspectionSchema : throw new ArgumentException("Unknown message inspection profile.", nameof(profileId));

    // Offline diagnostics does not require or invent connection timeout settings.
    public static InstrumentMessageInspection InspectDiagnosticMessage(string original) => DelimitedMessageInspector.Inspect(original, InspectionSchema);
}
