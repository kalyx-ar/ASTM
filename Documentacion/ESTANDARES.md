# Estándares de referencia

Estado: propuesta, 24 de octubre de 2026. ATSM es neutral respecto del LIS/LIMS que lo aloja. Los estándares fijan el vocabulario común; los manuales de cada fabricante fijan el dialecto efectivo.

## Posición

1. **El núcleo implementa estándares, no fabricantes.** Todo lo que un manual describe como desviación se resuelve en el plugin o en su perfil declarativo.
2. **Se nombra la edición vigente y se aceptan las anteriores como perfiles.** Los equipos declaran `E1381-91/95/02`, `LIS1-A`, `LIS2-A2` o `E1394-91/97`; el motor las trata como variantes de un mismo mecanismo con diferencias acotadas.
3. **HL7 v2.5.1 / IHE LAW es el camino moderno para instrumentos que lo soporten.** ASTM/LIS02 se mantiene porque es lo que hablan los equipos instalados.
4. **La semántica sale normalizada:** códigos del fabricante preservados y, cuando haya mapeo, LOINC, UCUM y SNOMED CT.

## Capa por capa

| Capa ATSM | Estándar vigente | Ediciones/variantes aceptadas | Uso |
|---|---|---|---|
| Enlace seguro | TLS 1.3 (RFC 8446), BCP 195 (RFC 9325) | TLS 1.2 como mínimo | Agente ↔ host y TCP nativo cuando el extremo lo soporte |
| Contrato agente–host | ATSM Link v1 (propio), con control en CBOR (RFC 8949, codificación determinista §4.2) | — | Ver [INTERFAZ-COMUNICACION](INTERFAZ-COMUNICACION.md) |
| Capa baja serial/TCP | CLSI LIS01-A2 | ASTM E1381-91/95/02, CLSI LIS1-A | ENQ/STX/ETB/ETX/EOT, frames 0–7, checksum, timers, contención |
| Capa baja HL7 | MLLP (HL7 v2, `0x0B … 0x1C 0x0D`) | MLLP sobre TLS | Instrumentos y middleware HL7 |
| Mensajes ASTM | CLSI LIS02-A2 | ASTM E1394-91/97, LIS2-A | Registros H, P, O, R, C, Q, M, S, L y delimitadores declarados en H |
| Mensajes HL7 | HL7 v2.5.1, perfil IHE PaLM **LAW** (CLSI AUTO16) | HL7 v2.3.1/2.4 como dialectos | OUL^R22, OML^O33, QBP^Q11/RSP^K11, ACK |
| Point of care | CLSI POCT1-A2 | — | Fuera de la primera entrega; se reserva el punto de extensión |
| Semántica de tests | LOINC + IHE/IICC **LIVD** (mapeo LOINC↔IVD del fabricante) | Tablas locales de equivalencias | El plugin publica códigos del equipo; el mapeo es un artefacto versionado |
| Unidades | UCUM | Unidad original preservada | La unidad original nunca se reemplaza |
| Muestra / resultados cualitativos | SNOMED CT (cuando el host lo use) | Códigos locales | Opcional, por mapeo |
| Proyección a sistemas clínicos | HL7 FHIR R4/R5: `ServiceRequest`, `Specimen`, `Observation`, `DiagnosticReport`, `Device` | — | Adaptador opcional; el modelo canónico no depende de FHIR |
| Identificación de dispositivos | UDI (cuando el equipo lo informe) | Número de serie del registro H | Se conserva como dato, no se infiere |
| Observabilidad técnica | OpenTelemetry (trazas/métricas/logs) | — | Salud y latencia; **no** sustituye la traza binaria de evidencia |

Los nombres de mensaje HL7 de la tabla son los de LAW. Su lista final se cierra con el manual del primer instrumento HL7.

## Evidencia disponible en el repositorio

| Documento local | Fabricante / equipo | Protocolo declarado en el documento |
|---|---|---|
| `manuales/91407-108 (1).pdf` | Abbott Standard Interface RS-232 (ARCHITECT) | E1381-91 / LIS1-A, E1394-91 / LIS2-A2 |
| `manuales/ASTM Alinity 206797-101.pdf` | **ARCHITECT System HL7 Interface Manual** (el nombre del archivo no corresponde al contenido) | HL7 v2.5.1, MLLP, TCP/IP, LAW |
| `manuales/CS-2500_ASTM_Host_Interface_…pdf` | Sysmex CS-2500 (coagulación) | E1381-02/95, E1394-97/95, TCP/IP |
| `manuales/R315168_U-WAM_Host Interface…pdf` | Sysmex U-WAM (UF-5000 / UC-3500) | E1381-02/95, LIS2-A2, TCP/IP |
| `manuales/ilide.info-xn-l-lis-interface…pdf` | Sysmex XN-L | E1394-97, TCP/IP |
| `manuales/SUIT Protocol Description v8.0*.pdf`, `.docx` | Sysmex Universal Interface (SUIT) | Basado en E1381-91/E1394-91 con extensiones propias |
| `manuales/SYSMEX RECORDS.xlsx` | Sysmex | Definición de registros |
| `manuales/[ LIS] ASTM Connection Manual QWALYS 3.pdf` | Diagast QWALYS 3 (inmunohematología) | E1381-95, E1394-95 |
| `manuales/876_HL7 V2X - introduccion - Mandirola.pdf` | Material introductorio | HL7 v2.x |

Las trazas confirman lo mismo en campo: CELL-DYN Ruby declara `LIS2-A` en H; UF-5000 declara `E1394-97`; ARCHITECT i1000SR envía `H|\^&|||ARCHITECT^9.45^…^H1P1O1R1C1Q1L1`.

Los manuales tienen derechos de sus fabricantes y uno está marcado *Classified as Internal*. Se usan como referencia local y **no se publican** en el repositorio ATSM; ver [DATOS-DE-PRUEBA](DATOS-DE-PRUEBA.md).

## Lo que ATSM no promete

- Certificación o conformidad formal frente a CLSI, IHE o HL7. Se publica una **declaración de conformidad por perfil** con los casos realmente probados.
- Validez clínica de una interpretación. El estado del instrumento (`F`, `R`, `C`, `W`…) se entrega tal cual con su significado según el dialecto; la validación clínica es del host.
- Valores universales de timers: LIS01-A2 fija valores por defecto que el perfil puede ajustar cuando el manual lo exige.

Las normas CLSI/ASTM se adquieren a sus editores; el repositorio cita su identificación y no reproduce su texto.
