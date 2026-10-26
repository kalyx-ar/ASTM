using Atsm.Abstractions.Model;

namespace Atsm.Plugin.Sysmex.Urinalysis;

internal static class UwamIndicators
{
    internal static IReadOnlyList<(string Parameter, string Code, string Text)> Research { get; } =
    [
        ("RBC-Info.", "0", "Sin evaluación"), ("RBC-Info.", "1", "Posible patrón eritrocitario isomórfico"),
        ("RBC-Info.", "2", "Posible patrón eritrocitario dismórfico"), ("RBC-Info.", "3", "Posible patrón eritrocitario mixto"),
        ("UTI-Info.", "0", "Sin evaluación"), ("UTI-Info.", "1", "Sospecha de infección urinaria"),
        ("BACT-Info.", "0", "Sin evaluación"), ("BACT-Info.", "1", "Posibles bacterias gramnegativas"),
        ("BACT-Info.", "2", "Posibles bacterias grampositivas"), ("BACT-Info.", "3", "Posible mezcla grampositiva/gramnegativa"),
        ("BACT-Info.", "4", "Bacterias sin clasificar"),
    ];
    internal static IReadOnlyList<(string Code, string Text)> Reviews { get; } =
    [
        ("RC_USER_1", "Revisión por regla configurada 1"), ("RC_USER_2", "Revisión por regla configurada 2"),
        ("RC_USER_3", "Revisión por regla configurada 3"), ("RC_USER_4", "Revisión por regla configurada 4"),
        ("RC_USER_5", "Revisión por regla configurada 5"), ("RC_USER_6", "Revisión por regla configurada 6"),
        ("RC_USER_7", "Revisión por regla configurada 7"), ("RC_UNIVERSAL", "Revisión por configuración del equipo"),
        ("RC_DEBRIS", "Detritos elevados"), ("RC_RBCYLC", "Separación eritrocitos/levaduras poco fiable"),
        ("RC_DC", "Conductividad anómala: recuentos posiblemente alterados"),
        ("RC_RBC_Carryover", "Sospecha de arrastre de eritrocitos"), ("RC_WBC_Carryover", "Sospecha de arrastre de leucocitos"),
        ("RC_BACT_Carryover", "Sospecha de arrastre de bacterias"), ("RC_SPERM_Carryover", "Sospecha de arrastre de espermatozoides"),
        ("RC_MeasuringInterval_UF", "Fuera del intervalo de medición"), ("RC_MeasuringInterval_BF", "Fuera del intervalo de medición en líquido corporal"),
        ("RC_WBCLowReliability", "Recuento leucocitario de baja fiabilidad"), ("RC_MNPMN", "Separación mononucleares/polimorfonucleares poco fiable"),
    ];
    internal static bool IsResearch(InstrumentObservation observation) => Context(observation, "S")
        && Research.Any(e => e.Parameter == observation.Test.Vendor.Code);
    internal static bool IsReview(InstrumentObservation observation) => Context(observation, "RC")
        && Reviews.Any(e => e.Code == observation.Test.Vendor.Code)
        && !(observation.Test.Vendor.Code == "RC_MNPMN" && Model(observation) is "UF-3000" or "UF-3000X");
    private static string? Model(InstrumentObservation observation) => (observation.VendorIdentity["instrument.id"] ?? observation.Instrument.Model)?.Split('^')[0].Trim().ToUpperInvariant();
    private static bool Context(InstrumentObservation observation, string type) => observation.Test.Vendor.System == CodeSystems.Vendor(UwamInterpreter.PluginId)
        && observation.ResultKind?.Split('/')[0] == type && Model(observation) is null or "" or "U-WAM" or "UF-3000" or "UF-3000X" or "UF-4000" or "UF-5000";
}
