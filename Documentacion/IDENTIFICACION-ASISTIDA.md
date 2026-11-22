# Identificación asistida del modelo del equipo (análisis)

Análisis del 2026-11-22, a pedido del responsable. **No se implementa.** Pregunta:
¿puede el servicio neutral identificar el equipo, o sugerir el perfil, a partir de
lo que manda, en lugar de que el administrador lo elija?

## Lo que ya existe

| Pieza | Qué hace | Dónde |
|---|---|---|
| `IInstrumentHeaderProbe.RecognizesHeader` | El plugin reconoce el registro H de ASTM/LIS2 (nombre del emisor, H.5). Contrato opcional: "identifica un dialecto, no opciones instaladas, firmware homologado ni permiso para mandar comandos" | ARCHITECT ASI, CELL-DYN Ruby, CS-2500, XN-L, U-WAM/UC |
| `IInstrumentTracePlugin.Recognizes` | Reconoce trazas de los lectores propios (UC directo, UF binario) | Sysmex Urinalysis |
| `DiagnosticCatalog.ResolveAstmInterpretation` | Diagnostics ya elige el dialecto por el H de lo capturado y falla con `ASTM-DIAG-AMBIGUOUS-INPUT` si dos plugins lo reconocen; conserva la opción elegida dentro del mismo dialecto | `tools/Astm.Diagnostics.Core` |
| `DiagnosticInputDetector` | Detecta el formato de un archivo con ambigüedad explícita | `tools/Astm.Diagnostics.Core` |
| Inspección de mensajes | Mapas por perfil de P/O/R y PID/OBR/OBX | [INSPECCION-DE-MENSAJES](INSPECCION-DE-MENSAJES.md) |
| PortGate | Capta el tráfico de un puerto compartido (`Observe`) y opcionalmente una captura sellada; nunca interpreta | spec/admin-v1.md |

Por regla de AGENTS (11), Diagnostics ya hace "detección por contenido con
ambigüedad explícita". Falta llevarlo al servicio y a un flujo de alta.

## Qué se puede identificar y con qué certeza

| Señal | Perfiles | Certeza | Límite |
|---|---|---|---|
| H.5 de ASTM (`ARCHITECT^...`, `ASI`, `CS-2500`, `XN-350/450/550`, `CDRuby`, U-WAM/UC) | ARCHITECT ASI, CS-2500, XN-L, CELL-DYN Ruby (hipótesis de trazas), U-WAM | Alta para la **familia/dialecto** | No distingue opciones del mismo dialecto (configuraciones U-WAM, química instalada), firmware ni número de serie confiable |
| MSH.3/MSH.4 de HL7 y la versión de MSH.12 | ARCHITECT HL7 | Media: el campo lo configura el equipo/middleware y puede venir vacío o reescrito por un middleware | ARCHITECT ZOS y LAW son perfiles distintos (AGENTS, regla 9): MSH no basta para elegir entre ellos |
| Estructura sin H con nombre (CLSI genérico, QWALYS) | CLSI LIS02, QWALYS | Baja: el genérico acepta casi todo | Un H genérico sólo dice "habla LIS2" |
| Formato propio (UC directo, UF binario) | Sysmex UC/UF | Alta con suficiente tráfico (`Recognizes` de su lector) | Necesita varios paquetes; un fragmento no alcanza |
| Archivos (CFX CSV/RDML) | Bio-Rad CFX | Alta por formato de archivo | No hay canal vivo: es `messages` |
| Parámetros serie | Ninguno | — | La velocidad/paridad no se infiere del contenido; si son incorrectos no hay contenido legible |

## Riesgos

1. **Falsos positivos.** Un middleware o un concentrador reescribe H.5/MSH.3; un
   equipo de otra marca configurado para emular a otro. Una sugerencia aceptada
   sin revisar aplicaría reglas de catálogo, restricciones y respuestas a
   consultas de otro dialecto, y podría **mandar órdenes** con un formato ajeno.
2. **Equipos que no hablan primero.** Los que sólo responden a una consulta del
   LIS, o que pasan horas sin resultados, no dan nada que identificar. Mandarles
   un mensaje de prueba sería escribir al instrumento sin perfil confirmado: no
   se debe hacer (sin comandos inventados).
3. **Parámetros serie incorrectos.** Sin línea correcta sólo llega basura; no se
   debe "barrer" velocidades escribiendo al puerto. Sí se puede leer pasivamente.
4. **Sesión sin perfil.** Para leer hay que abrir el canal, y LIS01 exige
   responder ACK/NAK al ENQ: escuchar "sin perfil" con el enlace activo ya es una
   elección de protocolo. Por eso la lectura debe ser **pasiva** (observador en un
   puerto PortGate compartido, captura sellada o un archivo/traza ya capturada),
   o con el perfil LIS01 genérico **sólo en recepción**, sin responder consultas
   ni mandar órdenes.
5. **Datos de pacientes.** Lo que se lee para identificar contiene pacientes. El
   resultado de la identificación debe ser sólo un agregado (perfil sugerido,
   campo y valor del emisor), sin conservar el contenido fuera de la custodia
   del anfitrión (AGENTS, reglas 3 y 9).
6. **Ambigüedad.** Dos plugins que reconocen el mismo H deben responder
   "ambiguo", nunca elegir el primero (ya es la regla de Diagnostics).

## Propuesta

**Sugerir, nunca elegir.** El perfil sigue siendo decisión del administrador.

1. Exponer en el servicio un método de lectura, por ejemplo
   `profiles.suggest {content}` (Base64, acotado por `maxContentBytes`), que corre
   los `IInstrumentHeaderProbe`, los `Recognizes` de los lectores propios y la
   detección HL7 (MSH.3/MSH.4/MSH.12), y devuelve candidatos con su evidencia:
   `[{profileId, family, signal: "H.5", observed: "CS-2500", certainty: "dialect"}]`,
   o `ambiguous`/`unknown`. Sin estado, sin abrir transportes, sin conservar el
   contenido. El anfitrión le pasa contenido que ya tiene en su custodia (un
   mensaje recibido por `messages.process`, una captura PortGate abierta por él o
   un archivo de prueba).
2. Para un equipo nuevo detrás de PortGate, el flujo recomendado es: dar de alta
   el puerto compartido con observación para el anfitrión, leer la captura o el
   flujo observado (pasivo, sin tomar el puerto), pedir `profiles.suggest` y
   mostrar la sugerencia con su evidencia. El administrador confirma y recién ahí
   `instruments.put` con el perfil y los parámetros serie (que siguen sin
   inferirse).
3. En una sesión ya configurada, el servicio podría emitir un diagnóstico
   `profile.mismatch` cuando el H/MSH recibido no corresponde al perfil (por
   ejemplo, un equipo cambiado de puerto). Sólo aviso; no cambia el perfil ni
   corta la sesión.
4. Agregar `IInstrumentMessageHeaderProbe` (o extender el contrato de HL7) para
   que ARCHITECT HL7 declare su reconocimiento de MSH en su plugin, igual que los
   ASTM, sin ramas de fabricante en el motor (AGENTS, regla 1).

Funciona bien para ARCHITECT ASI, CS-2500, XN-L, U-WAM y los lectores UC/UF
(familia/dialecto); parcialmente para ARCHITECT HL7 y CELL-DYN Ruby (hipótesis); no
sirve para CLSI genérico ni QWALYS sin un H distintivo, ni para elegir entre
opciones de un mismo dialecto ni parámetros serie.

Esfuerzo estimado: chico para el punto 1 (reutiliza los contratos de los
plugins; mover la lógica de `DiagnosticCatalog` a una pieza compartida en
`Astm.Plugins` para no duplicarla); el punto 2 es del anfitrión; el 3 y 4 son
incrementos separados. Pruebas con corpus sintético por perfil, incluyendo
ambigüedad, H reescrito y contenido sin H.
