using System.Data.Common;
using System.Globalization;

namespace Astm.Diagnostics.Maui;

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
        ["App.Title"] = "ASTM · Diagnóstico de mantenimiento", ["App.Banner"] = "ASTM / Diagnóstico",
        ["App.Subtitle"] = "Interfaces, logs y datos de calidad", ["App.Loading"] = "Preparando el diagnóstico…",
        ["App.LanguageError"] = "No se pudieron preparar las traducciones. Verifique la conexión suministrada por el anfitrión.",
        ["App.StartError"] = "No se pudo preparar la aplicación. Código: ASTM-STARTUP-FAILED.",
        ["App.Error"] = "No se completó la operación ({0}). Consulte el detalle original.", ["App.ErrorTitle"] = "Diagnóstico", ["Close"] = "Cerrar",
        ["Source.Title"] = "FUENTE Y EQUIPO", ["Source.Plugin"] = "Marca y modelo", ["Source.Configuration"] = "Configuración de la interfaz",
        ["Source.Format"] = "Formato del archivo", ["Source.Open"] = "Abrir archivo", ["Source.Save"] = "Guardar caso",
        ["Source.AutomaticInterpretation"] = "Interpretación automática",
        ["Quality.MaterialLot"] = "Material / lote", ["Quality.NoLot"] = "-", ["Quality.DataType"] = "Tipo", ["Quality.Unit"] = "Unidad", ["Quality.State"] = "Estado",
        ["Quality.Minimum"] = "Mínimo", ["Quality.MedianValue"] = "Mediana / resultado", ["Quality.Median"] = "Mediana", ["Quality.Maximum"] = "Máximo",
        ["Quality.SD"] = "DE", ["Quality.N"] = "Mediciones", ["Quality.Mean"] = "Media",
        ["Quality.NumericCount"] = "Numéricas para estadística", ["Quality.Distribution"] = "Valores comunicados",
        ["Quality.ReportedRange"] = "Extremos/límites comunicados", ["Quality.MixedResults"] = "Mixto",
        ["Quality.Unordered"] = "Orden no definido",
        ["Quality.Frequency"] = "{0} ({1})", ["Quality.NoValue"] = "Sin valor", ["Quality.Unperformed"] = "Sin medir",
        ["Quality.Outliers"] = "{0} posibles atípicos",
        ["Quality.Outlier"] = "Posible atípico", ["Quality.Flagged"] = "{0} con alarmas",
        ["Quality.Repeated"] = "{0} duplicados", ["Quality.Reported"] = "{0} estadísticas informadas en detalle",
        ["Quality.Last"] = "Último registro: {0} · {1}",
        ["Quality.Method"] = "Mediciones: todos los registros comunicados. Sin medir: conteo comunicado separado. Distribución completa en detalle. Cruces: orden documentado, sin media/DE; con dos categorías centrales distintas se muestra su intervalo. Comparadores: conservan el límite y su lado no acotado, sin tratarlos como valores exactos. Mediana/DE numéricas: valores únicos, sin comparadores, cruces ni no realizados. DE muestral (n−1). Posibles atípicos con ≥4 datos: cercas de 1,5·RIC. Señales descriptivas; no son aceptación QC.",
        ["Source.ProtectedCase"] = "Caso ASTM protegido",
        ["Source.Custody"] = "Los casos se protegen para el usuario Windows actual. Los archivos originales se conservan.",
        ["Source.Recorder"] = "Quién registró el log", ["Source.RecorderAuto"] = "Inferir por el contenido", ["Source.RecorderHost"] = "El anfitrión (LIS o software de conexión)",
        ["Source.RecorderInstrument"] = "El instrumento", ["Source.Offset"] = "Offset UTC de logs (±hh:mm)",
        ["Live.Title"] = "INTERFAZ EN VIVO", ["Live.Transport"] = "Transporte", ["Live.Mode"] = "Modo",
        ["Live.TcpClient"] = "TCP · conectar al instrumento", ["Live.TcpServer"] = "TCP · esperar al instrumento", ["Live.Tunnel"] = "PortGate · puerto remoto", ["Live.Serial"] = "COM · puerto local",
        ["Serial.Port"] = "Puerto COM", ["Serial.Baud"] = "Baudios", ["Serial.DataBits"] = "Bits de datos", ["Serial.Parity"] = "Paridad",
        ["Serial.StopBits"] = "Bits de parada", ["Serial.Flow"] = "Control de flujo", ["Serial.Configure"] = "Aplicar configuración serie al puerto remoto",
        ["Serial.Refresh"] = "Consultar puertos disponibles", ["Serial.Available"] = "Puertos: {0}",
        ["Serial.Parity.None"] = "Sin paridad", ["Serial.Parity.Odd"] = "Impar", ["Serial.Parity.Even"] = "Par", ["Serial.Parity.Mark"] = "Marca", ["Serial.Parity.Space"] = "Espacio",
        ["Serial.StopBits.One"] = "1", ["Serial.StopBits.OnePointFive"] = "1,5", ["Serial.StopBits.Two"] = "2",
        ["Serial.Flow.None"] = "Sin control", ["Serial.Flow.RtsCts"] = "RTS/CTS", ["Serial.Flow.XonXoff"] = "XON/XOFF",
        ["Validation.Serial"] = "Revise los baudios y los bits de datos del puerto serie.", ["Validation.SerialProfile"] = "La configuración serie no coincide con la admitida por el perfil del equipo.",
        ["Live.Host"] = "Host / IP local al escuchar", ["Live.Port"] = "Puerto TCP", ["Live.AgentPort"] = "Puerto del agente",
        ["Live.Controller"] = "Identidad del controlador", ["Live.Tls"] = "TLS · sin degradación automática",
        ["Live.Certificate"] = "Certificado PFX / mTLS", ["Live.NoCertificate"] = "Sin certificado seleccionado", ["Live.Password"] = "Contraseña del PFX (no se guarda)",
        ["Live.Capture"] = "Capturar sin responder", ["Live.Simulate"] = "Anfitrión de diagnóstico · habilitar acciones",
        ["Command.Title"] = "ACCIONES DEL PLUGIN", ["Command.Action"] = "Acción", ["Command.Send"] = "Ejecutar acción",
        ["Command.Delivered"] = "Transmisión aceptada por el enlace · {0}", ["Command.Failed"] = "Transmisión sin confirmar · {0}",
        ["Validation.CommandParameter"] = "Complete el campo «{0}».",
        ["Scenario.Run"] = "Probar PortGate", ["Scenario.Title"] = "Prueba de comunicación · Puerto serie simulado",
        ["Scenario.Running"] = "Probando instrucciones y respuestas por PortGate…", ["Scenario.Cancelled"] = "Prueba cancelada.",
        ["Scenario.Passed"] = "Prueba correcta · {0} instrucciones · {1} resultados · {2}",
        ["Scenario.Partial"] = "Prueba parcial · {0} instrucciones · {1} resultados · {2}",
        ["Live.Ownership"] = "Conexión propia. PortGate toma control exclusivo; no observa otra sesión.",
        ["Live.QueryBudget"] = "Presupuesto de consulta HL7 (segundos)", ["Live.ResponseBudget"] = "Presupuesto de respuesta HL7 (segundos)",
        ["Live.Connect"] = "Conectar", ["Live.Stop"] = "Detener", ["Live.Opening"] = "Abriendo interfaz · Caso protegido preparado antes de conectar.",
        ["Live.Simulating"] = "Anfitrión de diagnóstico · Acciones del plugin disponibles.",
        ["Live.Connected"] = "Captura conectada sin respuestas · Conexión propia.", ["Live.Closed"] = "Interfaz cerrada · Caso protegido conservado.",
        ["Live.Stopped"] = "Captura detenida · Caso protegido conservado.",
        ["View.Source"] = "Fuente / conexión", ["View.Demo"] = "Ejemplo CFX", ["View.Interpret"] = "Interpretar comandos y mensajes",
        ["View.Group"] = "Agrupar repeticiones", ["View.Hex"] = "Hexadecimal",
        ["View.Wire"] = "Trama", ["View.Messages"] = "Mensajes", ["View.Observations"] = "Observaciones", ["View.Findings"] = "Hallazgos",
        ["View.Quality"] = "Calidad", ["View.Events"] = "Eventos", ["View.Catalog"] = "Catálogo",
        ["Wire.CompleteBlock"] = "Mensaje completo · entradas {0}–{1} · repeticiones: {2}",
        ["Source.DeviceIdentifier"] = "Equipo: {0}", ["View.Communication"] = "Comunicación", ["Timing.Metric"] = "Métrica / intervalo", ["Timing.N"] = "Intervalos",
        ["Timing.Minimum"] = "Mínimo", ["Timing.Median"] = "Mediana", ["Timing.P95"] = "P95", ["Timing.Maximum"] = "Máximo",
        ["Timing.Pending"] = "Sin respuesta / fin", ["Timing.All"] = "Todas", ["Timing.CaptureRead"] = "Lectura del registrador", ["Timing.CaptureWrite"] = "Escritura del registrador",
        ["Timing.ReadInterval"] = "Intervalo entre lecturas", ["Timing.DirectionTurn"] = "Cambio de sentido", ["Timing.MessageReception"] = "Recepción de mensajes / bloques",
        ["Timing.EnquiryAck"] = "ENQ → ACK", ["Timing.FrameAck"] = "Trama → ACK", ["Timing.Response"] = "Respuesta",
        ["Timing.EnquiryNak"] = "ENQ → NAK", ["Timing.FrameNak"] = "Trama → NAK",
        ["Timing.OperationTotal"] = "Operación completa", ["Timing.OperationProcessing"] = "Proceso de operación",
        ["Timing.Detail"] = "Tiempos de la captura. Los intervalos entre lecturas y cambios de sentido no prueban una respuesta ni un RTT. Respuestas y operaciones usan límites identificados por el protocolo/anfitrión. Cero ms puede reflejar la resolución del registrador. No se infieren timeouts ni finales de operación.",
        ["Timing.Flow"] = "Lecturas: {0:N0} · Bytes: {1:N0} · Intervalos no calculables: {2:N0}",
        ["Timing.Boundaries"] = "Inicio #{0} → fin #{1} · {2}", ["Timing.NoEnd"] = "Sin respuesta/final identificados en la captura",
        ["Timing.NoClock"] = "La fuente no tiene marcas temporales de comunicación; las duraciones no están disponibles.",
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
        ["Column.Sequence"] = "#", ["Column.Time"] = "Tiempo", ["Column.Direction"] = "Dirección", ["Column.Bytes"] = "Bytes",
        ["Column.Content"] = "Contenido", ["Column.Command"] = "Comando / significado", ["Column.Specimen"] = "Muestra", ["Column.Position"] = "Posición",
        ["Column.Test"] = "Prueba", ["Column.Type"] = "Tipo", ["Column.Value"] = "Resultado", ["Column.Unit"] = "Unidad",
        ["Column.Status"] = "Estado", ["Column.Severity"] = "Nivel", ["Column.Code"] = "Código", ["Column.Finding"] = "Hallazgo",
        ["Column.Material"] = "Material / lote", ["Column.Statistic"] = "Dato reportado", ["Column.Scope"] = "Ámbito", ["Column.SourceField"] = "Campo fuente",
        ["Column.Layer"] = "Capa", ["Column.Detail"] = "Detalle", ["Column.Manufacturer"] = "Marca", ["Column.Profile"] = "Equipo / configuración",
        ["Column.Format"] = "Formato", ["Column.Configuration"] = "Configuraciones",
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
        ["Error.ASTM-DIAG-HL7-PROFILE-REQUIRED"] = "Se reconoció contenido HL7. Seleccione una configuración HL7 compatible con el equipo; no se asume la marca por el formato del archivo.",
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
