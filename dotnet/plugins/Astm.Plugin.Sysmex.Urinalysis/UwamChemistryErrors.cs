namespace Astm.Plugin.Sysmex.Urinalysis;

/// <summary>U-WAM codes are not the analyzer's codes. R315168 rev.22 appendix K, pp.88-89.</summary>
internal static class UwamChemistryErrors
{
    internal const string Prefix = "ASTM-UWAM-CHEMISTRY-";
    internal sealed record Definition(string Code, string SourceCode, string Meaning, string? Model = null);
    internal static string? Model(string? value) => value?.Split('^')[0].Trim().ToUpperInvariant() switch
    { "UC-3500" => "UC-3500", "UC-1000" => "UC-1000", _ => null };
    internal static readonly Definition[] Definitions = Build();
    private static Definition[] Build()
    {
        Definition[] common =
        [
            new("0000", "0000", "Sin error"),
            new("1100", "1100", "Fallo mecánico"), new("2200", "2200", "Error de muestreo"),
            new("9001", "AA00", "Boquilla obstruida"), new("9002", "BB00", "Extremo de la tira reactiva no detectado"),
            new("9003", "EE00", "No se recibió la solicitud de error de muestreo"),
            new("4020", "4020", "Objeto extraño en el patrón interno"), new("5010", "5010", "Tira reactiva mal posicionada"),
            new("5020", "5020", "Falta la tira reactiva en el soporte"), new("5030", "5030", "Iluminación insuficiente en la base"),
            new("5040", "5040", "Extremo de la tira reactiva no detectado"), new("6010", "6010", "Almohadillas demasiado próximas"),
            new("6020", "6020", "Separación excesiva en el extremo de la tira"), new("6030", "6030", "Almohadillas demasiado separadas"),
            new("6040", "6040", "Tira reactiva invertida y mal posicionada"), new("6050", "6050", "Tipo de tira reactiva incorrecto"),
            new("7010", "7010", "Almohadilla reactiva no detectada"), new("7020", "7020", "Tipo de tira reactiva incorrecto"),
            new("7030", "7030", "Tira reactiva invertida"), new("7050", "7050", "Marca de línea no detectada"),
            new("8010", "8010", "Dispensación insuficiente"), new("8030", "8030", "Coloración desigual de la almohadilla"),
            new("8040", "8040", "Arrastre de líquido viscoso"), new("0301", "0301", "Iluminación roja insuficiente"),
            new("0302", "0302", "Iluminación verde insuficiente"), new("0303", "0303", "Iluminación azul insuficiente"),
            new("0304", "0304", "Iluminación infrarroja insuficiente"), new("0305", "0305", "Error de cálculo del color"),
            new("0401", "0401", "Iluminación insuficiente para densidad"), new("0402", "0402", "Volumen insuficiente para densidad"),
            new("0403", "0403", "Error de cálculo de densidad"), new("0501", "0501", "Iluminación insuficiente del blanco"),
            new("0502", "0502", "Volumen insuficiente del blanco"), new("9901", "00BB", "Error de lectura del código de barras"),
            new("9902", "00CC", "Error de solicitud"), new("9903", "00DD", "Tira reactiva vencida"),
        ];
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1100"] = "Tiempo de análisis agotado", ["4020"] = "Iluminación insuficiente",
            ["5010"] = "Tira reactiva mal posicionada o no reconocida", ["6040"] = "Tira reactiva invertida",
            ["7010"] = "Código de barras de la tira no reconocido", ["7020"] = "Tira reactiva no reconocida", ["7030"] = "Tira reactiva no reconocida",
        };
        var ambiguous = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1100"] = "Fallo mecánico o tiempo de análisis agotado", ["4020"] = "Error del patrón interno o de iluminación",
            ["5010"] = "Tira reactiva mal posicionada o no reconocida", ["6040"] = "Tira reactiva invertida o mal posicionada",
            ["7010"] = "Almohadilla o código de barras de la tira no reconocido", ["7020"] = "Tipo de tira incorrecto o tira no reconocida",
            ["7030"] = "Tira reactiva invertida o no reconocida",
        };
        var result = new List<Definition>();
        foreach (var entry in common)
        {
            result.Add(entry with { Meaning = ambiguous.GetValueOrDefault(entry.Code, entry.Meaning) });
            result.Add(entry with { Model = "UC-3500" });
            result.Add(entry with { Model = "UC-1000", Meaning = overrides.GetValueOrDefault(entry.Code, entry.Meaning) });
        }
        result.Add(new("8050", "8050", "Error de autenticidad de la tira reactiva", "UC-1000"));
        result.Add(new("9010", "9010", "Control de calidad fuera de límites", "UC-1000"));
        return result.ToArray();
    }
    internal static string FindingCode(Definition entry) => Prefix + (entry.Model is null ? "" : entry.Model.Replace("-", "", StringComparison.Ordinal) + "-") + entry.Code;
}
