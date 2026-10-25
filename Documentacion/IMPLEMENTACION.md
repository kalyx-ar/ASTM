# Plan de implementación y aceptación

## Condición de partida

El paquete contiene una propuesta. No hay agente, motor nuevo o plugin EDC implementado en este workspace. Los ejemplos son sintéticos y están deshabilitados. El primer trabajo en EDC es revisar sus instrucciones, sus cuatro capas, el host ETL existente y sus convenciones de configuración, persistencia e identidad.

## Entregas por etapa

| Etapa | Entrega verificable | Criterio de salida |
|---|---|---|
| 0. Encaje en EDC | Mapa de proyectos, contratos de Application y decisión de despliegue | Dependencias compatibles con las cuatro capas y nombres acordados |
| 1. Contrato y simulador | Encoding agente–ETL cerrado, vectores comunes y canal simulado | Fragmentación/concatenación no altera payload; versiones y límites producen errores definidos |
| 2. Transporte C++ | Puerto serial y TCP, modos claro/TLS por endpoint, eventos y buffers | Prueba de ida/vuelta binaria, escrituras parciales y errores observables |
| 3. Plataformas extremas | Builds reproducibles XP y ESP32, además de entorno de desarrollo | Serial, TLS y capacidades verificadas en hardware/OS objetivo; sin extrapolar soporte |
| 4. Arbitraje | Cola, permisos renovables, generaciones y quiescencia | Exclusión demostrada ante competencia, retrasos, vencimiento y reinicio |
| 5. Motor ASTM | Parser incremental, conversación genérica, perfiles y trazas de protocolo | Casos sintéticos válidos/inválidos y límites temporales caracterizados |
| 6. Primer plugin | Architect adaptado, con corpus de referencia y perfil propio | Registros y mensajes equivalentes a la evidencia; anomalías documentadas con decisión explícita |
| 7. Segundo plugin | CELL-DYN Ruby sobre el mismo motor | Particularidades resueltas por perfil/extensiones; mecanismos comunes sin duplicación |
| 8. Negocio y recuperación | Integración Application, trazas correlacionadas, planes y conciliación | Un flujo completo orden–instrumento–resultado y escenarios de recuperación acordados |
| 9. TCP nativo y failover | Adaptador directo y exclusión declarada; servidor/cliente local | Contrato común y cambio de controlador probado con capacidades reales |
| 10. Puesta en servicio | Perfil por instalación, monitor, operación y soporte | Aceptación por instrumento/proceso y procedimiento de retorno documentado |

Las etapas pueden solaparse cuando no dependan entre sí. Los primeros prototipos deben exponer pronto las restricciones de XP y ESP32, para no basar el diseño en una biblioteca que después no pueda ejecutarse allí.

## Matriz de aceptación propuesta

Todos los casos siguientes están **pendientes de ejecución**. Sus expectativas definen el trabajo a implementar.

| ID | Caso | Resultado esperado |
|---|---|---|
| T01 | Bytes binarios con cero, controles y valores mayores a 127 | Ida/vuelta sin transformación; configuración/encoding sólo en el motor cuando corresponde |
| T02 | Lecturas/escrituras partidas y varias unidades en una lectura | Parser de transporte y ASTM incrementales, con offsets y orden conservados |
| T03 | TLS válido, mTLS y autorización por instrumento | Identidad y derechos correctos antes de conceder control |
| T04 | Certificado vencido/no confiable o identidad distinta | Error explícito; el endpoint TLS no cambia a claro |
| T05 | Dos servidores y cliente local compiten por un puerto | Un propietario; cola/timeout/cancelación de solicitudes comprobados |
| T06 | Escritura tardía de un propietario revocado | Rechazo por generación/arranque/conexión; ningún byte nuevo se admite de ese permiso |
| T07 | Cambio de propietario con escritura parcial | Progreso conocido o incertidumbre declarados; nueva admisión espera el estado de transferencia |
| T08 | Reinicio de agente y reconexión de un controlador viejo | Nuevo `agentEpoch`, permisos anteriores inválidos y rangos de traza disponibles declarados |
| T09 | Cola o almacenamiento de captura saturados | Límites respetados y evento de discontinuidad/fallo, conforme al perfil |
| T10 | Motor bajo carga y conexión cifrada sostenida | Latencia medida dentro del presupuesto del instrumento; CPU/memoria y buffers registrados |
| A01 | Checksum, secuencia o delimitación inválidos | Respuesta y transición configuradas por el perfil, con evidencia de traza |
| A02 | Timeout, repetición y desconexión durante conversación | Estados y acciones coherentes con el perfil; sin completar mensajes inventados |
| P01 | Architect: registros O/R y variante de código de analito | Código de muestra y analito conservados según evidencia; interpretación trazable |
| P02 | Architect: órdenes salientes, componentes, sexo y checksum | Campos y frames caracterizados; comportamiento anómalo del sexo decidido explícitamente |
| P03 | Ruby: F/W, comparadores, FLAG y terminador | Valor, comparador, alarma y fin de mensaje propios del perfil |
| B01 | Analito desconocido, equivalencia inactiva y cambio de dominio | Resolución/rechazo por Application/Domain, preservando el dato original del equipo |
| B02 | Reenvío de mensaje frente a nueva medición legítima | Identidad/idempotencia del proceso; una repetición válida no se pierde por compartir valor |
| R01 | Inicio de análisis con identificador único y respuesta perdida | Plan basado en evidencia y capacidades del equipo, sin repetir automáticamente el inicio |
| R02 | Resultado ya aplicado y confirmación perdida | Conciliación/reconocimiento del reenvío conforme al contrato de negocio |
| R03 | Failover a cliente local y retorno al servidor | Órdenes/configuración disponibles, permiso único y conciliación de efectos probados |
| R04 | Reproducción diagnóstica de una traza | Sin conexión física ni efectos externos; versiones y rangos documentados |
| N01 | TCP nativo sin exclusión propia | No anunciar exclusión fuerte; usar el despliegue con árbitro común cuando sea requisito |

## Medición de rendimiento

Usar el mismo corpus, configuración serial, hardware, tamaño de buffers y cifrado para comparar builds. Separar arranque/TLS handshake de conexión sostenida. Registrar latencia por dirección, respuesta ASTM, percentiles y máximos observados, CPU, memoria y comportamiento en cortes.

Los límites de aceptación proceden del instrumento y de su perfil validado, no de un número universal ni del lenguaje. Conservar versión del binario, compilador, bibliotecas TLS, firmware, adaptador y condiciones de carga junto con el informe.

## Migración desde el sistema legacy

1. Leer [REFERENCIAS-LEGACY](REFERENCIAS-LEGACY.md) y verificar la procedencia del binario/código utilizado.
2. Seleccionar un proceso de un instrumento y reunir mensajes de referencia y su configuración efectiva.
3. Separar mecanismo de protocolo, particularidades del fabricante y reglas de negocio que hoy están mezclados.
4. Registrar el comportamiento legacy, su evidencia, la decisión de conservar/corregir/retirar y el destino en EDC.
5. Caracterizar en simulador y contrastar con equipos en un entorno de pruebas autorizado.
6. Integrar Application sin reproducir accesos directos a tablas, controles WinForms o arrays posicionales como API del negocio.
7. Validar la funcionalidad completa y sus escenarios de recuperación antes de avanzar a otro instrumento.

Los plugins originales generan también tramas y checksums. Esa lógica debe ubicarse conscientemente entre motor y dialecto. El contrato legacy `IPlugin` contiene UI, strings y arrays por referencia; se conserva como evidencia y se transforma a contratos adecuados para el nuevo host.

El plugin Architect tiene una diferencia previa de hash en su archivo decompilado. La validación de esa pieza debe volver al ensamblado original o a una decompilación aislada, conservando las modificaciones existentes. El host de interfaces legacy mantiene ofuscación residual y no basta para afirmar que se reconstruyeron todos sus caminos.

## Registro por regla o particularidad

```text
ID de regla / proceso / instrumento:
Ensamblado original y SHA-256:
Archivo, clase y método decompilados:
Configuración y versión observadas:
Mensajes/trazas y efectos de referencia:
Comportamiento observado:
Confirmaciones pendientes:
Decisión: conservar / corregir / retirar:
Destino: agente / motor / plugin / Application / Domain / Presentation:
Caso de aceptación y resultado de la prueba:
```

## Aspectos que debe cerrar la implementación

- Ubicación del host ETL y tecnología actual de plugins en EDC.
- Primeros instrumentos/modelos y manuales de comunicación disponibles.
- Matriz de OS, hardware y bibliotecas TLS realmente soportados.
- Contrato binario, política de versiones y límites de cada perfil.
- Datos disponibles sin servidor y estrategia de conciliación del cliente local.
- Persistencia, retención, acceso y exportación de trazas según las políticas EDC.
- Autoridad efectiva para excluir controladores en TCP nativo.

Resolver estos puntos dentro de la implementación no requiere volver a discutir la decisión ya acordada de C++, TCP, TLS configurable y motor/plugins C#.

## Incremento P-01 / HL7 del 2026-10-24

La autorización y el alcance verificable están en [Mensajes, trazas y HL7](MENSAJES-TRAZAS-HL7.md). Pruebas nuevas en `Runtime/CompleteMessageTests`, `Hl7/Hl7Tests` y `Hl7/Hl7SessionTests`: equivalencia ASTM/mensaje completo, IDs de recepción, límites/EOF, fallo de captura anterior a ACK/conexión, MSH/escapes, ACK después de custodia, correlación ORL, timeout incierto, ZOS y corte LAW LAB-27. No se afirma actor LAW completo ni UI/SQLCipher EDC implementados.
