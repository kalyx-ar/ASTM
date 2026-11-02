using Astm.Plugins;

namespace Astm.Plugin.Sysmex.Urinalysis;

public sealed partial class SysmexUrinalysisPlugin : IInstrumentMessageInspection
{
    private static readonly InstrumentMessageSchema InspectionSchema = InstrumentMessageSchema.Create<SysmexUrinalysisPlugin>(
        InstrumentMessageSyntax.Lis02, "Sysmex U-WAM R315168 Rev.22, §5.3.3.2–5.3.3.4: patient, order and result records; UwamDialect.",
        ("P.4", "Identificador de paciente del laboratorio"),
        ("P.6", "Nombre del paciente (con repeticiones)"),
        ("P.6.1", "Apellido"),
        ("P.6.2", "Nombre"),
        ("P.6.3", "Segundo nombre"),
        ("P.8", "Fecha de nacimiento"),
        ("P.9", "Sexo administrativo"),
        ("P.15", "Grupo sanguíneo / RH"),
        ("O.3", "Identificación de muestra / rack / posición"),
        ("O.3.1", "Identificador de muestra"),
        ("O.3.2", "Rack"),
        ("O.3.3", "Posición"),
        ("O.4", "Identificación instrumental alternativa"),
        ("O.4.3", "Identificador alternativo de muestra"),
        ("O.5", "Ensayos solicitados"),
        ("O.26", "Tipo de informe (F)"),
        ("R.3", "Parámetro y contexto instrumental"),
        ("R.3.4", "Parámetro"),
        ("R.3.5", "Método"),
        ("R.3.6", "Dilución / modo de medición"),
        ("R.3.7", "Tipo de resultado"),
        ("R.3.8", "Rack"),
        ("R.3.9", "Posición"),
        ("R.4", "Valor y formato"),
        ("R.4.1", "Resultado"),
        ("R.4.2", "Formato RAW / MAINFORMAT"),
        ("R.5", "Unidad"),
        ("R.7", "Indicadores"),
        ("R.7.1", "Indicador del analizador"),
        ("R.7.2", "Indicador bajo / alto"),
        ("R.9", "Estado / celda UD-10"),
        ("R.13", "Fecha y hora de medición"),
        ("R.14", "Nombre del analizador"));

    public InstrumentMessageSchema MessageSchema(string profileId) => profileId == WithChemistryProfile || profileId == WithoutChemistryProfile
        ? InspectionSchema : throw new ArgumentException("Unknown message inspection profile.", nameof(profileId));
}
