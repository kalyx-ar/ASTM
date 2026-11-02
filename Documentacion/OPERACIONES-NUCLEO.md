# Núcleo neutral de operaciones — primer bloque #416

Implementación inicial, 2026-11-01. [Issue EDC #416](https://github.com/kalyx-ar/EDC/issues/416), abierto. El usuario pidió avanzar con una entrega acotada y dejar continuidad para otro agente. [Diseño completo](OPERACIONES-Y-CONTRATOS.md).

## Código entregado

Nuevo ensamblado **Astm.Operations 0.1.0** en `dotnet/src/Astm.Operations`, sólo BCL, sin referencias a otros proyectos o paquetes. No conoce pacientes, laboratorio, HL7, EDC, transporte ni bases de datos. Los modelos clínicos existentes no se movieron todavía; la extracción/migración completa del diseño sigue pendiente.

- `OperationDefinition`: ID/versión, propósito/tipo, referencias de contratos de entrada/salida, perfil/revisión, capacidades independientes y alcance de finalización. Es metadata: aún no implementa negociación, admisión de catálogos o validación de las capacidades anunciadas.
- `IOperationCatalog`: contrato para descubrir operaciones y resolver contratos de datos. La implementación concreta sólo existe como fixture sintética en pruebas.
- `IOperationBinding<TInput>` y `PreparedOperation<TInput>`: preparación tipada sin transmisión. El binding deberá validar y crear una instantánea propia de datos/metadata mutables antes de entregarla. Un record genérico no congela profundamente sus referencias. No hay ejecutor físico en este bloque.
- `OperationDataContract`, `OperationDataSchema` y campos: objetos/listas/texto/número/Int64/booleano; cardinalidades, nullabilidad, códigos y origen del valor. La fase Input distingue datos del consumidor de los que completa el host; Prepared valida el objeto ya completado.
- `OperationDataValidator.ValidateSchema` permite verificar metadata antes de recibir datos. `Validate` valida primero el esquema y después el JSON, sin I/O ni efectos de negocio.
- `OperationJsonContext` contiene metadata generada para serializar/deserializar los descriptores sin reflexión dinámica. Es formato propio, **no** exportación ni implementación completa de JSON Schema 2020-12. La prueba de metadata no equivale a publicar un ejecutable NativeAOT.

## Reglas del subconjunto inicial

| Regla | Comportamiento |
|---|---|
| Optional / Required / Conditional / Forbidden | Required exige presencia; nullable decide si null es válido; texto vacío depende de MinLength. Forbidden rechaza la presencia incluso si el valor es null. |
| RequiredWhen | Igualdad ordinal de texto contra un path absoluto declarado en el schema, separado por puntos. No hay scripts, paths relativos o selectores por fila de un array. Selector ausente/null/no textual da `data.condition-unresolved`. Si la condición no se cumple el campo es opcional, no prohibido. |
| Origin | Input, HostContext, Configuration, Generated, Fixed o Derived. En fase Input un campo gestionado no se exige; sólo puede aportarlo el consumidor si AllowOverride. Si se permite override se valida su tipo/límites, sin hacerlo obligatorio en la entrada. |
| Prepared | Todos los campos Required y condiciones se verifican sobre el objeto completo. No verifica por sí solo la procedencia real de un valor: el binding reúne los valores confiables del host. |
| Texto | Longitud en escalares Unicode y AllowedValues ordinal. No valida bytes/encoding de cable, fechas, unidades ni vocabularios externos. Eso requiere el binding/reglas posteriores. |
| Números | Number admite un número JSON; Integer exige signed Int64. No hay aún límites numéricos ni unidades/precisión semántica. |
| Objetos | Rechaza propiedades no declaradas por defecto y detecta duplicadas en el nivel validado. AllowAdditionalProperties permite contenido extra sin validar su semántica; no usarlo para ignorar requisitos de fabricante. |
| Metadata | Kind omitido es Unspecified y falla. Restricciones JSON desconocidas se rechazan al deserializar con OperationJsonContext. Schemas incompletos, tipos inválidos, reglas sobre tipos incompatibles y ciclos se diagnostican antes de mirar datos. |
| Diagnóstico | Código y path del schema; no reproduce valores ni nombres de propiedades desconocidas del payload. Se limita a 100 incidencias por validación y profundidad de 64 niveles. |

Las colecciones de los descriptores son IReadOnlyList/IReadOnlyDictionary para consumo; eso no garantiza inmutabilidad profunda del objeto aportado por el productor. El catálogo/binding debe mantener metadata estable durante validación/uso, sin mutarla concurrentemente.

## Evidencia reproducible

`dotnet/tests/Astm.Tests/Operations/OperationContractTests.cs` aporta 25 casos. El fixture describe/prepara una receta térmica sintética no médica con modo, lote, setpoint y canales; no abre ni controla un horno. Comprueba consumidor tipado, revisión/contrato, condiciones, origen de campos, JSON estricto, cardinalidad/Int64, null/vacío, corrupción de schema/ciclos y ausencia de dependencias clínicas en el ensamblado. Los codecs de los DTOs de prueba también usan metadata generada.

Comandos:

```powershell
dotnet test dotnet/tests/Astm.Tests/Astm.Tests.csproj --no-restore -m:1 -v quiet --filter FullyQualifiedName~OperationContractTests
dotnet test dotnet/Astm.slnx --no-restore -m:1 -v quiet
ctest --test-dir PortGate/build/win-x64 -C Release --output-on-failure
dotnet build dotnet/src/Astm.Operations/Astm.Operations.csproj -c Release --no-restore -p:AstmRequireStrongName=true -m:1 -v quiet
```

Build Release correcto, cero errores/advertencias. DLL 0.1.0.0, Strong Name de distribución token `066190a23251f202`; **Authenticode NotSigned**, pendiente del release. Sin claves nuevas, paquetes publicados, cambios de confianza o instalación. Los resultados finales de la suite repetida y commits quedan en #416 y el changelog del host.

## Traspaso: siguiente bloque concreto

1. Conservar este núcleo como proyecto independiente. Completar `OperationDefinition` con roles, validación/admisión y negociación real de contratos/perfiles; no considerar un descriptor prueba de capacidad. Definir snapshot de metadata/input en bindings y contratos de decoder/sesión/observación antes de ejecutar efectos.
2. Diseñar los DTOs médicos comunes en un proyecto opcional, a partir de los modelos actuales; mantener fuera del núcleo Patient/Specimen/solicitudes/observaciones. Evitar aliases perpetuos: planificar migración de consumidores y compatibilidad de paquetes explícitamente.
3. Construir un formato médico genérico y los pilotos ARCHITECT ASI / U-WAM sobre los mismos DTOs. Publicar campos/requisitos por operación/equipo contrastados con evidencia. Agregar límites de bytes/encoding, reglas complejas y códigos en el binding, sin anunciar soporte inexistente del validador estructural.
4. Adaptar el estado de operaciones concurrentes y sus scopes de evidencia/canal. `AstmOperationObserver` de #414 permanece sin cambios y todavía no se deriva de este catálogo; no migrarlo por sustitución de un nombre ni confundir fin de mensaje con fin de proceso.
5. Continuar con otros perfiles/protocolos y un adaptador médico EDC/ETL común. EDC conserva paquetes 0.3; esta biblioteca no está desplegada ni referenciada desde sus módulos.

No cambiar las reglas de soft/hard ni interpretar protocolos en PortGate. #404 resuelto; firma/validación VCOM instalada sigue en #408. Mantener #416 abierto y actualizar pruebas, matriz del ecosistema, commits y changelog de cada incremento. No hay trabajo pendiente oculto en un binario o instalación local: este bloque se entrega en fuente y pruebas.
