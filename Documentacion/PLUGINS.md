# Plugins específicos

Estado: contratos de motor implementados; kits formales de conformidad pendientes; catálogo compilado por decisión humana del 2026-10-25. Los ejemplos declarativos siguientes siguen siendo propuesta (contratos reales en `dotnet/src/Atsm.Plugins`). Un plugin encapsula **un dialecto de un fabricante/familia** sobre el motor genérico. No conoce el LIS/LIMS que lo aloja ni el canal físico. Repertorio vigente y aplicación Windows: [DIAGNOSTICO-MANTENIMIENTO](DIAGNOSTICO-MANTENIMIENTO.md).

## Qué hace y qué no hace

SDK vigente: `PluginApi.Version = 2`. `IInstrumentTracePlugin.Recognizes(profile, segments)` e `InferRecorder(profile, segments)` reciben el perfil concreto. La discriminación de UC-3500 y control UF parcial convive en el mismo plugin de familia. Los consumidores se recompilan con este contrato; no se mantienen las firmas anteriores. `TraceInterpretation.Timings` aporta límites explícitos separados en respuesta, duración total de operación y proceso. Véase [Control UF y tiempos](UF-CONTROL-Y-TIEMPOS.md).

| El plugin hace | El plugin no hace |
|---|---|
| Declarar perfiles (capa baja, codec, desviaciones, flujos) | Abrir puertos, sockets o archivos |
| Traducir registros/segmentos del dialecto ↔ modelo canónico | Framing, checksum, timers del estándar |
| Correlacionar consultas, órdenes y resultados del equipo | Mapear a catálogos del laboratorio (lo hace el runtime con `CodeMappingSet`) |
| Interpretar estados/flags del fabricante | Validar clínicamente o publicar resultados |
| Evaluar recuperación con evidencia | Acceder a la base del host |
| Aportar corpus de conformidad | Mostrar UI |

Comparado con el `IPlugin` del sistema legacy (strings, arrays posicionales por `ref`, caracteres de control y checksum dentro del plugin, `About()` con UI), todo lo de framing pasa al motor y la salida pasa a ser tipada.

## Dos niveles

1. **Declarativo (preferido):** perfil JSON + mapa de campos. Cubre la mayoría de equipos ASTM que siguen LIS02-A2 con pocas desviaciones.
2. **Código:** una clase que implementa los puntos de extensión cuando el dialecto tiene lógica (componentes condicionales, registros M, correlación especial).

```json
{
  "dialect": "sysmex.uwam",
  "results": {
    "specimenId": "O.3.3",
    "test":       { "code": "R.3.5", "display": "R.3.5" },
    "value":      "R.4", "unit": "R.5", "flags": "R.7", "status": "R.9",
    "resultTime": "R.13", "operator": "R.11"
  },
  "status": { "F": "Final", "C": "Correction" },
  "comparatorPrefix": true
}
```

El ejemplo es ilustrativo: las posiciones reales de U-WAM salen de su manual y de las trazas. Las rutas usan la notación `Registro.campo.componente` 1-based de LIS02-A2 y se validan contra el corpus del plugin.

## Contrato de código

```csharp
namespace Atsm.Plugins;

public interface IInstrumentPlugin
{
    PluginDescriptor Describe();     // id, versión, versión mínima del motor, modelos/firmware, perfiles
    IReadOnlyList<InstrumentProfile> Profiles { get; }

    IDialect CreateDialect(InstrumentProfile profile, PluginContext context);
}

public interface IDialect
{
    // Entrada: mensaje ya decodificado por el codec del perfil.
    DialectResult Interpret(ProtocolMessage message, DialectContext context);

    // Salida: intención canónica → mensaje del dialecto.
    ProtocolMessage BuildOrders(IReadOnlyList<WorkOrder> orders, DialectContext context);
    ProtocolMessage BuildQueryAnswer(OrderQueryAnswer answer, DialectContext context);

    RecoveryAssessment AssessRecovery(RecoveryEvidence evidence);
}
```

- `DialectResult` contiene hechos canónicos (`InstrumentObservation`, `OrderQuery`, estado del equipo) **y observaciones del dialecto** (campos ignorados, valores fuera de patrón) que no impiden procesar.
- Un plugin no lanza excepciones por datos del instrumento: devuelve problemas con código estable.
- `PluginContext` sólo ofrece reloj, log estructurado y configuración del plugin.

## Empaquetado y aislamiento

- Paquete NuGet `Atsm.Plugin.<Fabricante>.<Familia>` con manifiesto `atsm-plugin.json` (id, versión SemVer, `engineApi` compatible, hash de perfiles, licencia).
- Decisión humana 2026-10-25: ATSM/Diagnostics componen los plugins en compilación y no usan AssemblyLoadContext. La carga/admisión en caliente corresponde al anfitrión EDC; la propuesta previa de loader propio queda retirada.
- Firma: el host puede exigir Strong Name y/o Authenticode y una lista de editores admitidos.
- Versionado: cambiar la interpretación de un campo incrementa la versión del plugin; la traza registra qué versión interpretó cada mensaje para reprocesar si hace falta.

## Kit de conformidad

Cada plugin incluye:

```text
conformance/
  corpus/          trazas *.atsmtrace sintéticas o anonimizadas
  expected/        salida canónica esperada (JSON) por traza
  scenarios.json   casos E/P/R: host query, orden, repetición, corte a mitad de frame…
  CONFORMANCE.md   declaración: qué perfiles, qué flujos, qué quedó sin probar
```

`dotnet test` ejecuta `Atsm.Testing.ConformanceRunner` sobre el corpus con el canal `Replay` y compara contra `expected/`. Un plugin no se publica sin pasar su kit.

## Catálogo inicial propuesto

Ordenado por evidencia disponible (manual + traza + plugin legacy):

| Plugin | Equipos | Protocolo | Manual | Trazas | Sistema legacy | Prioridad |
|---|---|---|---|---|---|---|
| `Abbott.Architect` | ARCHITECT i1000SR, c4000 | ASI: LIS1-A / LIS2-A2 (RS232) | 91407-108 | `I1000Interface.log`, `C4000Interface.log`, `Importar/*` | `Plugin_Architect` | **1** |
| `Abbott.CellDynRuby` | CELL-DYN Ruby | LIS2-A | — (falta) | `Celldyn RUBYInterface.log` | `Plugin_Celldyn_Ruby` | 2 |
| `Sysmex.Uwam` | UF-5000, UC-3500 vía U-WAM | E1381-02 / LIS2-A2 (TCP) | R315168 rev. 22, `SYSMEX RECORDS.xlsx` | `Sysmex/*` (con host query) | — | 3 |
| `ArchitectHl7Plugin` (en Abbott.Architect) | ARCHITECT (interfaz HL7) | HL7 v2.5.1 original / MLLP, ZOS; perfil distinto de LAW | 206797-101 | Sólo sintéticas | — | Primer corte implementado |
| `BioRad.Cfx` | Exportaciones Maestro de CFX | CSV y RDML 1.0/1.1 | 10000126764B; esquema RDML | Sintéticas | — | Implementado, validar exportaciones reales |
| `Sysmex.XnL` | XN-L 350/450/550 | E1381-02 / E1394-97 | 1273-MKT rev. 3 | Sintéticas | — | Recepción implementada, pendiente de equipo |
| `Sysmex.Cs2500` | CS-2500 | E1381-02 / E1394-97 | rev. 2.04 | Sintéticas | — | Recepción implementada, pendiente de equipo |
| `Sysmex.Suit` | Middleware SUIT | E1381/E1394-91 + extensiones | v8.0 | — | — | Pendiente de equipo |
| `Diagast.Qwalys` | QWALYS 3 | E1381-95 / ASTM ASCII | Connection Manual | Sintéticas | — | Recepción implementada, pendiente de equipo |
| DeltaLab (fuera de ATSM) | Intercambio con laboratorio cliente | Conector EDC/ETL, decisión 2026-10-24 | — | Referencia local | Host de interfaces legacy | No es un plugin instrumental |

Architect primero: es el único con manual, trazas abundantes y plugin productivo. Ruby segundo: confirma que el motor generaliza (terminador vacío, comparadores, `FLAG`). U-WAM tercero: aporta TCP nativo y host query real.

## Migración desde el sistema legacy, por plugin

1. Convertir las trazas del equipo al formato ATSM con el adaptador de log del plugin (`atsm-trace extract`) o como contenido puro (`from-text`, `from-binary`).
2. Ejecutar el plugin legacy (o leer su código decompilado) para obtener la salida de referencia por mensaje.
3. Escribir el perfil/mapa; contrastar contra la referencia y documentar cada diferencia como **conservar / corregir / retirar** con su evidencia ([IMPLEMENTACION § Registro por regla](IMPLEMENTACION.md)).
4. Anonimizar el corpus resultante y fijarlo como `expected/`.

## Dialecto estándar y adaptadores de log

- `StandardAstmDialect` interpreta LIS02-A2 por posición de la norma. Es el plugin `clsi.lis02` para equipos sin desviaciones conocidas y la base que los dialectos de fabricante extienden: el de U-WAM sobrescribe sólo lo que su manual cambia (componentes de R.3, `valor^formato` en R.4, flags, R.9 como celda UD-10, Q por número de muestra, respuesta sin órdenes).
- Los valores de interfaz que define la documentación de la marca o serie de equipos (límites de respuesta, longitud de frame, página de códigos) van en el **perfil** con su cita. Por ejemplo, U-WAM responde consultas en 8 s con analizador químico o en 18 s sin él, y por eso tiene dos perfiles.
- Un plugin puede implementar `ITraceLogAdapterProvider` para leer los logs de su serie de equipos, configurando un `RegexLogAdapter` ([TRAZAS-Y-RECUPERACION § Reconstrucción](TRAZAS-Y-RECUPERACION.md#reconstrucción-de-trazas-reales-y-teóricas)).

| Plugin | Estado |
|---|---|
| `clsi.lis02` (estándar) | Implementado |
| `abbott.architect` | Implementado (ASI RS-232) |
| `sysmex.uwam` | Implementado: perfiles LIS2-A2 / E1381-02, dialecto (incluye el analizador químico UC-3500/UC-1000) y adaptador `sysmex.port-monitor` |
| `abbott.celldyn-ruby` | Implementado desde trazas: cada mensaje en un frame, `^ALL`, alarmas `FLAG`; pendiente de confirmar con el manual |
| `biorad.cfx` | `IInstrumentContentPlugin`: cuantificación CSV/RDML 1.0/1.1; recibe contenido, sin archivos/control directo ni clasificación clínica |
| `sysmex.xnl`, `sysmex.cs2500`, `diagast.qwalys3` | Recepción según manual con pruebas sintéticas; sin consultas/órdenes, hardware o corpus real verificado |
| `Atsm.Plugin.Legacy.Testing` | Sólo pruebas: adaptador `legacy.interface-log` para los logs del LIMS legacy. Un plugin de integración con el sistema legacy (pedidos desde su interfaz o su log) sería un trabajo aparte |
