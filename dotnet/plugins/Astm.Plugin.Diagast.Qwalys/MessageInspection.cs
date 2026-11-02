using Astm.Plugins;

namespace Astm.Plugin.Diagast.Qwalys;

public sealed partial class QwalysPlugin : IInstrumentMessageInspection
{
    private static readonly InstrumentMessageSchema InspectionSchema = InstrumentMessageSchema.Create<QwalysPlugin>(
        InstrumentMessageSyntax.Lis02, "DIAGAST ASTM Connection Manual QWALYS 3, §5.1.3 and §5.2: P.3 sample number / O.4, patient demographics and result subfields.",
        ("P.3", "Número de muestra (debe coincidir con O.4)"),
        ("P.4", "Identificador de paciente del laboratorio"),
        ("P.6", "Nombre del paciente"),
        ("P.6.1", "Apellido"),
        ("P.6.2", "Nombre"),
        ("P.8", "Fecha de nacimiento"),
        ("P.9", "Sexo administrativo"),
        ("O.4", "Número de muestra"),
        ("O.5", "Ensayo solicitado"),
        ("O.5.4", "Código de ensayo"),
        ("O.6", "Prioridad"),
        ("R.3", "Identificación de reacción / grupo"),
        ("R.3.4", "Tipo de reacción"),
        ("R.3.5", "Posición de reacción"),
        ("R.3.6", "Nombre de reacción / donante"),
        ("R.3.7", "Código de ensayo"),
        ("R.3.8", "Nivel"),
        ("R.4", "Datos del resultado"),
        ("R.4.7", "Resultado / intensidad de reacción"),
        ("R.5", "Modo del resultado"),
        ("R.9", "Origen del resultado (M: modificado por técnico)"),
        ("R.13", "Fecha y hora de medición"),
        ("C.3", "Comentario"));

    public InstrumentMessageSchema MessageSchema(string profileId) => profileId == Profiles[0].Id
        ? InspectionSchema : throw new ArgumentException("Unknown message inspection profile.", nameof(profileId));
}
