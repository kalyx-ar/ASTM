# Observación común de operaciones (#414 / #416)

Contrato 0.6.3. #416 integra todos los perfiles médicos mediante IMedicalOperationObserver. #414 aportó el decodificador LIS01; PortGate/VCOM permanece agnóstico.

## Consumo

provider.CreateOperationObserver(profileId, new(instrument, clock), traceId) crea un observador por traza/generación. Alimentar Feed(TraceEntry) con ambos sentidos y entradas técnicas en secuencia contigua. Current es snapshot inmutable consultable desde otro hilo. Complete notifica cierre; Invalidate notifica pérdida. Resynchronize(nextSequence) sólo después de restablecer realmente el contexto.

| Dato | Significado |
|---|---|
| OperationInProgress | Pending/Running en todos los scopes; false sólo con cobertura; null si desconocido. |
| ChannelOperationInProgress | Enlace/conversaciones que necesitan canal; false sólo con cobertura, null ante incertidumbre. |
| NoPendingOperations | ChannelOperationInProgress == false. No prueba éxito clínico ni fin de medición. |
| IsObserving / CoverageComplete | Vigencia y cobertura. Cierre/gap impiden afirmar libre. |
| Operations | Recibos correlacionados: propósito, estado, scope, evidencia, secuencia y código. Sin identificadores de paciente ni resultados. |

Un proceso físico puede seguir activo sin retener COM. Silencio, lista vacía, L/ETX/EOT aislados o un contador no prueban fin. El solicitante conoce el protocolo y conserva su decisión.

## Protocolos

- LIS01 valida frames/secuencia/ACK/cierre y correlaciona consultas según perfil. Astm.Tracing.AstmOperationObserver reside ahora en Astm.Plugins; sin alias de ensamblado anterior.
- Resultados ajenos no terminan consultas. ARCHITECT Q.X identifica muestra; U-WAM usa O; CLSI L.I sin identificador sólo cierra una consulta inequívoca; Ruby ALL no termina con una positiva parcial.
- EOT del receptor se acepta como confirmación sólo cuando el perfil ARCHITECT lo declara.
- HL7 tiene decoder por dirección, MSH-10/MSA-2 y respuesta de consulta pendiente de su propio acuse. ACK desconocido/no correlacionado, control ID pendiente repetido con otro contenido y mensajes no interpretables invalidan cobertura.
- UC-3500 emite recibos RQ02/CM00/ER por ciclo/muestra/posición. ER00 confirma aceptación de respuesta; frontera de medición/disponibilidad no probada permanece Unknown.
- UF conserva mensajes/campos/avisos; operación/disponibilidad Unknown.
- CFX termina ContentImport al completar contenido; no certifica corrida física.

IInstrumentOperationPolicyProvider/astm.results-message conserva su utilidad acotada LIS01 de #414. Consumidores multiformato usan el contrato común.

## Integridad y límites

Primera secuencia arbitraria; después contigua RX/TX. Reintento/NAK no termina operación. Gaps, corrupción, contención y fallo de captura/decoder dejan latch Unknown; nuevos bytes no lo limpian. Resincronizar reinicia decoders/ledger y empieza desconocido. Otra generación requiere nueva observación.

Complete cierra sin certificar pendientes. Buffer LIS01 por transmisión: 1 MiB configurable. Contenido/protocolos propietarios: hasta 16 MiB de evidencia, con invalidación/fallo explícito al excederse. Ledger neutral: 64 terminales y capacidad configurable (4096 por defecto). El intérprete propietario reconstruye el prefijo acotado; no es un monitor ilimitado ni interpreta fronteras desconocidas.

OperationTraceSink conserva primero mediante ITraceSink y después observa/notifica. Escrituras de sesión se registran antes del transporte; fallo invalida/cierra. Para decidir cesión sobre tráfico efectivo se alimentan las copias de observación de PortGate; intención de escritura local no equivale a entrega física.

## PortGate

El cliente obtiene lectura efectiva del flujo, alimenta ObservationsAsync por generación/secuencia, consulta NoPendingOperations y acompaña su declaración con CursorAfter(copy). PortGate revalida condiciones/cursor/arbitraje; la guarda opcional 5000 ms cancela ante tráfico.

ASTM no solicita control ni reintenta/escala automáticamente. El observador declara conocer el protocolo; VCOM sólo recibe desconexión estándar. #404 resuelto; #408 mantiene firma/ensayos instalados.
