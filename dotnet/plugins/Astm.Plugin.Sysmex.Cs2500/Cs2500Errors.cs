namespace Astm.Plugin.Sysmex.Cs2500;

internal sealed record Cs2500Error(string Code, string Text, int Page, bool Attention = true, bool IsError = true)
{
    public string Evidence => $"Sysmex CS-2500 Host Interface rev.2.04, appendix C, tabla 19 p.{Page}; R.7.2, no R.7.1 ni R.7.3";
}

internal static class Cs2500Errors
{
    // Table 19 includes conditions whose R.7.1 is N despite a masked result. Never infer validity from N alone.
    internal static IReadOnlyList<Cs2500Error> Definitions { get; } =
    [
        new("0000.0000.0000", "Dilución modificada", 53, false, false),
        new("0000.0000.0340", "Longitud de onda cambiada a 340 nm", 53, false, false),
        new("0000.0000.0405", "Longitud de onda cambiada a 405 nm", 53, false, false),
        new("0000.0000.0575", "Longitud de onda cambiada a 575 nm", 53, false, false),
        new("0000.0000.0660", "Longitud de onda cambiada a 660 nm", 53, false, false),
        new("0000.0000.0800", "Longitud de onda cambiada a 800 nm", 53, false, false),
        new("0000.0000.1100", "Control del vial sin realizar", 53),
        new("0000.4000.0010", "Desbordamiento de visualización", 53),
        new("0000.4000.0020", "Error de cálculo", 53),
        new("0000.4000.0030", "Sin curva de calibración validada", 53),
        new("0000.4000.0041", "Extrapolación por encima del límite", 53),
        new("0000.4000.0042", "Extrapolación por debajo del límite", 53),
        new("0000.4000.0050", "Cálculo diferido", 53, true, false),
        new("0000.5000.0010", "Error de cálculo de fórmula", 53),
        new("0000.5000.0020", "Error de cálculo de fórmula", 53),
        new("0000.5000.0050", "Problema en la fórmula de cálculo", 53),
        new("0000.6000.0100", "Por encima del límite de reporte", 53, true, false),
        new("0000.6000.0200", "Por debajo del límite de reporte", 53, true, false),
        new("0000.6000.0300", "Por encima del límite del paciente", 53, true, false),
        new("0000.6000.0400", "Por debajo del límite del paciente", 53, true, false),
        new("0000.6000.0500", "Relación de pendientes MDA fuera de límite", 53),
        new("0000.6000.0600", "Diferencia entre réplicas", 53),
        new("0000.7000.0020", "Error al calcular el promedio", 53),
        new("0002.0000.0000", "Coagulación leve", 53),
        new("0004.0000.0000", "Tiempo de análisis excedido", 53),
        new("0008.0001.0000", "Descenso por fluctuación inicial", 53),
        new("0008.0002.0000", "Curva de coagulación: descenso brusco", 53),
        new("0008.0004.0000", "Curva de coagulación: caída transitoria", 54),
        new("0008.0008.0000", "Curva de coagulación: salto ascendente", 54),
        new("0008.0016.0000", "Curva de coagulación escalonada", 54),
        new("0008.0032.0000", "Error en la curva de fibrinógeno", 54),
        new("0008.0064.0000", "Curva de coagulación: meseta", 54),
        new("0008.0128.0001", "Reacción temprana: reacción lenta", 54),
        new("0008.0128.0002", "Reacción temprana: ángulo inicial 1", 54),
        new("0008.0128.0004", "Reacción temprana: ángulo inicial 2", 54),
        new("0008.0128.0008", "Reacción temprana: deriva", 54),
        new("0008.0128.0016", "Reacción temprana: porcentaje inicial", 54),
        new("0008.0256.0000", "Ruido en la medición", 54),
        new("0016.0000.0000", "Turbidez excesiva", 54),
        new("0032.0000.0000", "Sin coagulación detectada", 54),
        new("0032.0002.0000", "Curva plana", 54),
        new("0064.0000.0000", "Muestra envejecida", 54),
        new("0128.0000.0000", "Fuera de intervalo", 54),
        new("0256.0000.0000", "Luz transmitida alta en coagulación", 54),
        new("1000.1000.0000", "Muestra hemolizada", 54, true, false),
        new("1000.2000.0000", "Muestra ictérica", 54, true, false),
        new("1000.3000.0000", "Muestra lipémica", 54, true, false),
        new("1100.4000.0000", "Comprobación del volumen de muestra", 54, false, false),
        new("4001.0000.0000", "Luz transmitida baja", 54),
        new("4002.0000.0000", "Luz transmitida alta en cromogénico/inmunoensayo", 54),
        new("4004.0000.0000", "Sin linealidad", 54),
        new("4008.0000.0000", "Error en la curva de reacción", 54),
        new("4016.0000.0000", "Exceso de antígeno", 54),
        new("4032.0000.0000", "Pico en la curva de reacción", 54),
        new("4128.0000.0000", "Sin ajuste polinómico", 54),
        new("4256.0000.0000", "Intervalo no lineal", 55),
        new("9999.0000.0000", "Fallo de medición", 55),
        // The manual supplies internal symbolic messages only; do not invent their underlying cause.
        new("9999.0000.9010", "Error interno de prueba", 55),
        new("9999.0000.9020", "Error de parámetros de calibración", 55),
        new("9999.0000.9030", "Error de información de calibración", 55),
        new("9999.0000.9040", "Error de cálculo del ensayo", 55),
        new("9999.0000.9050", "Error interno del ensayo", 55),
        new("9999.0000.9060", "Error interno de configuración del ensayo", 55),
        new("9999.0000.9070", "Resultado del ensayo ausente", 55),
        new("9999.0000.9080", "Error de dilución del ensayo", 55),
        new("9999.0000.9090", "Datos de calibración ausentes", 55),
        new("9999.0000.9100", "Resultado inválido para promedio", 55),
        new("9999.0000.9110", "Error de lote de reactivo", 55),
    ];
    internal static string FindingCode(string code) => "ASTM-CS2500-EVALUATION-" + code.Replace('.', '-');
}
