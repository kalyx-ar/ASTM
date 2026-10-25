using System.Data.Common;
using System.Globalization;

namespace Atsm.Diagnostics.Maui;

/// <summary>An embedding host may supply its already-open translation connection before creating the app.</summary>
public static class DiagnosticsStartup
{
    public static DbConnection? TranslationDatabase { get; set; }
    public static string TranslationTarget { get; set; } = "es-AR";
}

internal static class Globals
{
    internal static readonly Dictionary<string, string> lang = new(StringComparer.Ordinal)
    {
        ["App.Title"] = "ATSM · Diagnóstico de mantenimiento", ["App.Banner"] = "ATSM / Diagnóstico",
        ["App.Subtitle"] = "Interfaces, logs y datos de calidad", ["App.Loading"] = "Preparando el diagnóstico…",
        ["App.LanguageError"] = "No se pudieron preparar las traducciones. Verifique la conexión suministrada por el anfitrión.",
        ["App.StartError"] = "No se pudo preparar la aplicación. Código: ATSM-STARTUP-FAILED.",
        ["App.Error"] = "No se completó la operación ({0}). Consulte el detalle original.", ["App.ErrorTitle"] = "Diagnóstico", ["Close"] = "Cerrar",
        ["Source.Title"] = "FUENTE Y EQUIPO", ["Source.Plugin"] = "Marca y modelo", ["Source.Configuration"] = "Configuración de la interfaz",
        ["Source.Format"] = "Formato del archivo", ["Source.Open"] = "Abrir archivo", ["Source.Save"] = "Guardar caso",
        ["Source.ProtectedCase"] = "Caso ATSM protegido",
        ["Source.Custody"] = "Los casos se protegen para el usuario Windows actual. Los archivos originales se conservan.",
        ["Source.Recorder"] = "Quién registró el log", ["Source.RecorderAuto"] = "Inferir por el contenido", ["Source.RecorderHost"] = "El anfitrión (LIS o software de conexión)",
        ["Source.RecorderInstrument"] = "El instrumento", ["Source.Offset"] = "Offset UTC de logs (±hh:mm)",
        ["Live.Title"] = "INTERFAZ EN VIVO", ["Live.Transport"] = "Transporte", ["Live.Mode"] = "Modo",
        ["Live.TcpClient"] = "TCP · conectar al instrumento", ["Live.TcpServer"] = "TCP · esperar al instrumento", ["Live.Tunnel"] = "SerialTunnel · agente ATSM",
        ["Live.Host"] = "Host / IP local al escuchar", ["Live.Port"] = "Puerto TCP", ["Live.AgentPort"] = "Puerto del agente",
        ["Live.Controller"] = "Identidad del controlador", ["Live.Tls"] = "TLS · sin degradación automática",
        ["Live.Certificate"] = "Certificado PFX / mTLS", ["Live.NoCertificate"] = "Sin certificado seleccionado", ["Live.Password"] = "Contraseña del PFX (no se guarda)",
        ["Live.Capture"] = "Capturar sin responder", ["Live.Simulate"] = "Simular anfitrión: ACK, sin solicitudes",
        ["Live.Ownership"] = "Conexión propia. SerialTunnel toma control exclusivo; no observa otra sesión.",
        ["Live.QueryBudget"] = "Presupuesto de consulta HL7 (segundos)", ["Live.ResponseBudget"] = "Presupuesto de respuesta HL7 (segundos)",
        ["Live.Connect"] = "Conectar", ["Live.Stop"] = "Detener", ["Live.Opening"] = "Abriendo interfaz · Caso protegido preparado antes de conectar.",
        ["Live.Simulating"] = "Simulación de anfitrión · Custodia de diagnóstico, consultas sin solicitudes.",
        ["Live.Connected"] = "Captura conectada sin respuestas · Conexión propia.", ["Live.Closed"] = "Interfaz cerrada · Caso protegido conservado.",
        ["Live.Stopped"] = "Captura detenida · Caso protegido conservado.",
        ["View.Source"] = "Fuente / conexión", ["View.Demo"] = "Ejemplo CFX", ["View.Interpret"] = "Interpretar comandos y mensajes",
        ["View.Group"] = "Agrupar repeticiones", ["View.Hex"] = "Hexadecimal",
        ["View.Wire"] = "Trama", ["View.Messages"] = "Mensajes", ["View.Observations"] = "Observaciones", ["View.Findings"] = "Hallazgos",
        ["View.Quality"] = "Calidad", ["View.Events"] = "Eventos", ["View.Catalog"] = "Catálogo",
        ["View.Empty"] = "Sin datos para esta vista.", ["View.NoCase"] = "Sin caso abierto", ["View.Prompt"] = "Seleccione un equipo y abra un archivo o conecte una interfaz.",
        ["View.DetailPrompt"] = "Seleccione una fila para consultar el detalle. Los grupos conservan todos sus registros.",
        ["View.DetailLimit"] = "Detalle visual limitado a 64 KiB; el caso conserva el contenido completo.",
        ["View.Summary"] = "{0:N0} entradas · {1:N0} mensajes · {2:N0} observaciones · {3:N0} hallazgos",
        ["View.Ready"] = "Listo · Diagnóstico sin publicación en LIMS.", ["View.FileRead"] = "Archivo leído · Sin acciones sobre instrumentos ni LIMS.",
        ["View.Incomplete"] = "Caso incompleto: puede faltar el tramo final. Registros disponibles conservados.",
        ["View.Saved"] = "Caso protegido guardado: {0} · Usuario Windows actual.", ["View.DemoTitle"] = "Ejemplo sintético · Bio-Rad CFX",
        ["View.DemoStatus"] = "Ejemplo sintético · Estadísticas reportadas por el software, sin decisión QC ni datos de pacientes.",
        ["Grid.Count"] = "Cantidad", ["Grid.Values"] = "{0} valores distintos", ["Grid.Rows"] = "{0:N0} filas originales · {1:N0} grupos · Vista {2:N0}–{3:N0} de {4:N0}",
        ["Grid.Previous"] = "Anterior", ["Grid.Next"] = "Siguiente", ["Grid.Resize"] = "Arrastre para cambiar el ancho. Doble clic para ajustar al contenido.",
        ["Grid.Expand"] = "Desplegar {0} registros", ["Grid.Collapse"] = "Contraer {0} registros",
        ["Column.Sequence"] = "#", ["Column.Milliseconds"] = "ms", ["Column.Direction"] = "Dirección", ["Column.Bytes"] = "Bytes",
        ["Column.Content"] = "Contenido", ["Column.Command"] = "Comando / significado", ["Column.Specimen"] = "Muestra", ["Column.Position"] = "Posición",
        ["Column.Test"] = "Prueba", ["Column.Type"] = "Tipo", ["Column.Value"] = "Valor original", ["Column.Unit"] = "Unidad",
        ["Column.Status"] = "Estado / alarmas", ["Column.Severity"] = "Nivel", ["Column.Code"] = "Código", ["Column.Finding"] = "Hallazgo",
        ["Column.Material"] = "Material / lote", ["Column.Statistic"] = "Dato reportado", ["Column.Scope"] = "Ámbito", ["Column.SourceField"] = "Campo fuente",
        ["Column.Layer"] = "Capa", ["Column.Detail"] = "Detalle", ["Column.Manufacturer"] = "Marca", ["Column.Profile"] = "Equipo / configuración",
        ["Column.Format"] = "Formato", ["Column.Evidence"] = "Evidencia / alcance", ["Column.Configuration"] = "Configuraciones",
        ["Quality.External"] = "Referencia externa", ["Quality.Targets"] = "Objetivos / límites", ["Quality.RequiresHost"] = "Requiere anfitrión / ETL",
        ["Quality.Batch"] = "Lote", ["Quality.Individual"] = "Individual", ["Quality.Observation"] = "Observación", ["Quality.Measurement"] = "Medición · {0}",
        ["Statistic.CqMean"] = "Cq · media", ["Statistic.CqStandardDeviation"] = "Cq · desvío estándar", ["Statistic.EfficiencyPercent"] = "Eficiencia (%)",
        ["Statistic.Slope"] = "Pendiente", ["Statistic.YIntercept"] = "Ordenada al origen", ["Statistic.RSquared"] = "R²",
        ["Statistic.StartingQuantity"] = "Cantidad inicial (SQ)", ["Statistic.LogStartingQuantity"] = "Log cantidad inicial",
        ["Statistic.StartingQuantityMean"] = "SQ · media", ["Statistic.StartingQuantityStandardDeviation"] = "SQ · desvío estándar",
        ["Scope.ReplicateGroup"] = "Réplicas", ["Scope.StandardCurve"] = "Curva", ["Scope.Well"] = "Pozo",
        ["Validation.Hl7Timing"] = "Ingrese los plazos HL7 configurados en el equipo, en segundos y con punto decimal.",
        ["Validation.Profile"] = "El perfil guardado no está disponible en este catálogo.", ["Validation.Offset"] = "Offset UTC inválido (±hh:mm).",
        ["Validation.InputLimit"] = "Límite de entrada: 32 MiB.", ["Validation.Port"] = "Ingrese el puerto TCP configurado en el instrumento o agente.",
        ["Validation.Host"] = "Complete el host y la identidad del controlador.", ["Validation.Certificate"] = "El servidor TLS requiere un PFX con clave privada.",
        ["Error.ATSM-DIAG-HL7-PROFILE-REQUIRED"] = "Se reconoció contenido HL7. Seleccione una configuración HL7 compatible con el equipo; no se asume la marca por el formato del archivo.",
    };
    public static string T(string key) => lang.GetValueOrDefault(key, key);
    public static string F(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, T(key), values);
    public static async Task InitializeAsync()
    {
        if (DiagnosticsStartup.TranslationDatabase is not { } database) return;
        await DiagnosticText.InitializeAsync(database, new DiagnosticCatalog(), DiagnosticsStartup.TranslationTarget);
#if KALYX_LANG
        var runtime = await Kalyx.Globalization.Lang.InitializeAsync(lang, database, source: typeof(Globals).Assembly.GetName().Name!, target: DiagnosticsStartup.TranslationTarget);
        await runtime.FlushMissingAsync();
#endif
    }
}
