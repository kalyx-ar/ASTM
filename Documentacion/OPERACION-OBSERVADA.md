# Estado de operación de una traza (#414)

Solicitud humana 2026-11-01, [EDC #414](https://github.com/kalyx-ar/EDC/issues/414). Implementación incremental pasiva en `Astm.Tracing.AstmOperationObserver`; contratos en Abstractions/Plugins. Fuentes 0.4.0 de Abstractions, Plugins, Protocol.Lis01, Runtime y Tracing. No publica paquetes ni cambia las referencias 0.3 de EDC.

## Consulta simple

`observer.Current` es un snapshot inmutable, legible desde otro hilo. `Feed(TraceEntry)` devuelve el snapshot resultante. Cada traza/generación tiene su propio observador y una instancia independiente de política.

| Dato | Significado |
|---|---|
| `OperationInProgress == true` | Intercambio activo o una operación pendiente según la política. |
| `OperationInProgress == false` | Operación finalizada según el alcance de la política. |
| `OperationInProgress == null` | Desconocido; no permite declarar libre. |
| `NoPendingOperations` | Consulta recomendada: operación Idle, enlace inactivo y observación vigente. |
| `LinkInProgress` | Estado del intercambio LIS01 separado del negocio; nullable si se desconoce. |
| `TraceId`, `PolicyId`, `LastSequence`, `Revision`, `LastObservedAt`, `Code` | Correlación, posición y motivo técnico; sin identificadores de paciente ni resultados. |

No convertir Unknown a false con `GetValueOrDefault()`. El snapshot describe lo procesado; el solicitante interpreta el protocolo y aporta el cursor correspondiente a PortGate. Una nueva lectura puede cambiar el estado. No hay toma de control ni soft/hard automático.

## Fronteras por perfil

`IInstrumentOperationPolicyProvider` es una capacidad opcional del plugin: enumera `InstrumentOperationProfile` con alcance/evidencia y crea `IAstmOperationPolicy` por traza. La política recibe mensajes LIS02 completos de una transmisión confirmada, dirección y `TraceRef`; puede mantener operaciones correlacionadas durante varios intercambios. `InstrumentTraceTiming` conserva sus intervalos históricos; no sustituye este monitor incremental.

StandardAstmPlugin ofrece explícitamente `astm.results-message`: **un mensaje de resultados no solicitado H…R…L es una operación**. Requiere frames válidos/ordenados, ACK explícitos y EOT del emisor. No equivale al proceso completo del equipo, lote multietapa ni sesión clínica. Una Q queda pendiente incluso después de EOT y de resultados ajenos; resolverla requiere una política del equipo que correlacione la respuesta. Un workflow no soportado no se borra con el siguiente resultado. No se asigna el perfil automáticamente a todos los fabricantes.

Sin política se informa el enlace pero no se declara operación libre. La política multi-turno se prueba con datos sintéticos, sin homologar queries por fabricante. HL7/MLLP y protocolos propietarios necesitan monitores/políticas propios, no se interpretan como LIS01.

## Integridad y lifecycle

- Alimentar ambos sentidos desde el punto de vista del host, en secuencia contigua. Una entrada puede tener un byte o varios intercambios. La primera secuencia puede ser arbitraria; el comienzo no certifica libre.
- Un ENQ o fragmento inicia actividad. Silencio, ETX, L o EOT aislados no prueban fin. ACK implícito, checksum/orden inválidos, controles restringidos, contención o interrupción dejan Unknown. El decodificador forense conserva su modo permisivo; este monitor activa validación de caracteres.
- NAK/reintento y ACK de un duplicado válido mantienen la operación hasta su cierre completo. Una lectura COM esperando datos no es una operación de protocolo.
- Pérdida, duplicación o reordenamiento de secuencias e `Invalidate()` dejan Unknown con latch. Nuevos bytes no lo limpian. Tras resincronizar realmente el protocolo/contexto, llamar `Resynchronize(nextSequence)`; vuelve a Unknown hasta una frontera completa. No usarlo para ocultar incertidumbre.
- Cambiar época del agente, generación o traza exige un monitor/política nuevos. Al perder o terminar el canal, llamar `Complete()`: no certifica fin y deshabilita `NoPendingOperations`. Las entradas técnicas no son tráfico; el anfitrión notifica el lifecycle explícitamente.
- Buffer por transmisión de 1 MiB configurable. Desbordamiento invalida y descarta el decoder. Una excepción de política invalida antes de propagarse; no conserva un snapshot libre anterior.

No abre puertos, escribe ACK/NAK, mantiene timers, conserva historial ni solicita control. Custodia/cifrado siguen en el anfitrión. Sin permisos nuevos. PortGate/VCOM conserva su transporte/arbitraje agnósticos.

## Ejemplo PortGate

El consumidor ya conectado tiene observación efectiva y solicita control por el mecanismo existente cuando corresponde. El ejemplo sólo mantiene estado:

```csharp
IInstrumentOperationPolicyProvider plugin = new StandardAstmPlugin();
const string dialect = StandardAstmPlugin.ProfileId;
var operation = plugin.OperationProfiles(dialect).Single(p => p.Id == "astm.results-message");
// Seleccionar ese alcance sólo si coincide con la operación real del consumidor.
var watching = await channel.ObserveAsync(true, cancellationToken);
if (!watching.Traffic) throw new InvalidOperationException("Se requiere observar el flujo compartido.");
AstmOperationObserver? observer = null;
uint? generation = null;
var start = TimeProvider.System.GetTimestamp();
try
{
    await foreach (var copy in channel.ObservationsAsync(cancellationToken))
    {
        if (generation != copy.Generation)
        {
            observer?.Complete(); generation = copy.Generation;
            observer = new(Guid.NewGuid(), Encoding.Latin1,
                plugin.CreateOperationPolicy(dialect, operation.Id));
        }
        var state = observer!.Feed(new TraceEntry(checked((long)copy.Sequence),
            TimeProvider.System.GetElapsedTime(start), TimeProvider.System.GetUtcNow(),
            copy.Direction == 0 ? TraceDirection.Received : TraceDirection.Transmitted, copy.Data));
        if (state.NoPendingOperations)
        {
            var cursor = channel.CursorAfter(copy);
            // Estado y cursor para la decisión explícita del solicitante.
            // PortGate revalida secuencia, generación, cola y observación vigentes.
        }
    }
}
finally { observer?.Complete(); }
```

Una pérdida reportada por el canal exige invalidar/resincronizar además de detectar saltos. Un canal nuevo exige monitor nuevo aunque coincida el número de generación. ASTM no adquiere ni revoca tenencias.

## Evidencia y límites

25 pruebas nuevas: fragmentación por byte, frames por registro/mensaje, ETB y frame wrap, ACK/NAK/duplicados, corrupción, ACK ausente, contención, queries pendientes, chequeo de enlace vacío sin borrar el estado de negocio, correlación multi-turno sintética, secuencias, EOF, memoria y fallo de plugin. JSON con metadata generada para NativeAOT, sin payload clínico; no se afirma publicación de un ejecutable NativeAOT. No acredita interoperabilidad de equipo ni driver instalado; firma/instalación VCOM siguen en [#408](https://github.com/kalyx-ar/EDC/issues/408), abierto.

Verificación final 2026-11-01: solución .NET repetida, 449 correctas/una omitida/cero fallos, 13 s por ejecución; la omitida requiere traza privada seleccionada expresamente. CTest repetido, 855 + 33 comprobaciones sin fallos (7,71/7,69 s). Build Release con `AstmRequireStrongName=true` correcto, cero errores/advertencias, 1,57 s. Metadata de las cinco DLL afectadas: 0.4.0.0, token de distribución `066190a23251f202`; **Authenticode NotSigned**, pendiente de release. No se crean claves, confianza ni baselines nuevas.

Evaluación completa del ecosistema EDC: `Documentacion/Modulos/LIMS/operacion-observada-414.md`. API CLR no acredita IA, UI, OData ni despliegue remoto.
