using System.Text.RegularExpressions;
using Astm.Formats.Medical.Model;

namespace Astm.Plugin.Sysmex.Cs2500;

internal sealed record Cs2500Mask(string Code, string Meaning, string FindingCode, string? DetailedCode = null);
internal static partial class Cs2500Masks
{
    public const string Evidence = "Sysmex CS-2500 ASTM Host Interface rev.2.04 p.33, R.4: máscaras del valor; no son códigos R.7 ni estado global de calibración";
    public static IReadOnlyList<Cs2500Mask> Definitions { get; } =
    [new("X", "Sin curva de calibración validada", "ASTM-CS2500-MASK-CALIBRATION", "0000.4000.0030"),
     new("*", "Error de análisis", "ASTM-CS2500-MASK-ANALYSIS"),
     new("/", "Error al calcular el promedio", "ASTM-CS2500-MASK-AVERAGE", "0000.7000.0020"),
     new("+", "Desbordamiento de visualización", "ASTM-CS2500-MASK-OVERFLOW", "0000.4000.0010"),
     new("-", "Error de cálculo", "ASTM-CS2500-MASK-CALCULATION", "0000.4000.0020")];
    public static Cs2500Mask? Find(InstrumentObservation observation)
    {
        // A signed numeric result stays numeric. References, mixed masks and arbitrary text stay uninterpreted.
        if (observation.Value.Numeric is not null || observation.ResultKind is not ("1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9" or "A")) return null;
        var raw = observation.Value.RawText.Trim();
        if (!MaskValue().IsMatch(raw)) return null;
        var codes = raw.Where(c => !char.IsAsciiDigit(c) && c != '.').Distinct().ToArray();
        return codes.Length == 1 && raw.Split('.')[0].Contains(codes[0])
            ? Definitions.SingleOrDefault(d => d.Code == codes[0].ToString()) : null;
    }
    [GeneratedRegex(@"^[0-9X*/+\-]+(?:\.[0-9X*/+\-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex MaskValue();
}
