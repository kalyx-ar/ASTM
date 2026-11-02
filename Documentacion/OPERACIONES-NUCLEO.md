# Núcleo neutral de operaciones (#416)

Implementación 0.6.3, 2026-11-01. [Issue EDC #416](https://github.com/kalyx-ar/EDC/issues/416). [Especialización médica y consumo](OPERACIONES-Y-CONTRATOS.md).

Astm.Operations utiliza sólo BCL: no referencia proyectos, pacientes, laboratorio, protocolos, EDC, transporte ni bases. Astm.Abstractions conserva transporte/trazas/diagnósticos neutrales. Los tipos clínicos y el puerto ILabWorkflowHost están en el proyecto opcional Astm.Formats.Medical, namespaces .Model y .Hosting, sin aliases antiguos.

## Catálogo y preparación

OperationDefinition describe propósito/versión, iniciador, entrada/salida, capacidades independientes, alcance, perfil/revisión, evidencia y mapeos de cable. OperationCatalog valida y toma copia profunda inmutable de definiciones/schemas; rechaza metadata inválida, contratos ausentes, duplicados y Execute sin Prepare. Select exige coincidencia explícita de operación, major, contrato completo, perfil/revisión y capacidades; no supone compatibilidad entre minors distintos.

OperationEnvelope fija OperationId, propósito/versiones, perfil y datos JSON. PreparedOperationMessage conserva su propio JSON y bytes; Bytes devuelve una copia. Preparar no transmite: la sesión valida el envelope antes del handshake. IOperationBinding<TInput> sigue disponible para otros dominios; un record genérico no congela las colecciones de TInput.

## Datos y validación

| Regla | Comportamiento |
|---|---|
| Optional / Required / Conditional / Forbidden | Presencia, nullabilidad y vacío son independientes; Forbidden rechaza incluso null. |
| RequiredWhen | Igualdad ordinal de texto contra path absoluto declarado, separado por puntos. No scripts ni selectores relativos por fila. Selector ausente/null/no textual produce data.condition-unresolved. |
| Origin | Input, HostContext, Configuration, Generated, Fixed y Derived. Input no exige/admite datos gestionados salvo AllowOverride; Prepared valida el objeto completo. El binding reúne valores confiables. |
| Texto | Longitud en escalares Unicode y AllowedValues ordinal. Encoding, controles, fechas tipadas y reglas del equipo se validan en el binding. Sin máximos de cable inventados. |
| Números/listas | Integer exige Int64; Number admite número JSON. Cardinalidades explícitas; sin vocabulario universal de unidades/precisión. |
| Objetos | Campos desconocidos/duplicados rechazados; AdditionalProperties sólo en mapas explícitos. |
| Metadata | Kind omitido/Unspecified, schemas incompatibles/cíclicos y reglas desconocidas fallan. Profundidad 64 y máximo 100 incidencias, sin reproducir valores sensibles. |

OperationJsonContext ofrece codecs generados para catálogo, datos, envelope y snapshot. Es metadata propia, no una implementación completa de JSON Schema 2020-12. Las condiciones médicas complejas se validan en el binding y se publican mediante MedicalOperationRules.

## Estado y evidencia

OperationReceipt registra identidad, propósito, estado, scope, evidencia, necesidad de canal, secuencia y código técnico. OperationTracker mantiene ledger bajo lock y snapshots inmutables. Retiene 64 terminales; capacidad configurable, 4096 entradas por defecto. Agotarla invalida cobertura y falla explícitamente: no elimina operaciones pendientes para declarar libre.

OperationInProgress considera todos los scopes. ChannelOperationInProgress considera operaciones que requieren canal y actividad del enlace, con cobertura suficiente. NoPendingOperations equivale a ChannelOperationInProgress == false. Unknown, pérdida o cierre nunca prueban libre; una operación física puede seguir activa sin retener canal.

Transport, MessageAcknowledged, ApplicationAccepted, DeviceReported y ContentValidated son evidencias diferentes. El núcleo no interpreta tráfico ni solicita control. Los observadores médicos pertenecen a Astm.Plugins.

## Evidencia y distribución

OperationContractTests aporta 25 casos no médicos (receta térmica sintética) con condiciones/origen/versiones/ciclos y ausencia de dependencias clínicas. MedicalOperationTests verifica perfiles, negociación, reglas, correlación, scopes y ETL. El self-test CLI NativeAOT ejercita codecs médicos/neutrales generados.

Paquetes propios 0.6.3 y PluginApi 3; consumidores migran juntos. Sin dependencias nuevas ni cambios de confianza/bases/hardware. Strong Name no equivale a Authenticode ni a firma NuGet; firmas de release conservan su proceso. #404 sigue resuelto y firma/ensayo instalado VCOM permanece en #408.
