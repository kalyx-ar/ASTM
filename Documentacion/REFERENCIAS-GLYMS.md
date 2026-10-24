# Referencias a Glyms: ensamblados y lógica productiva

**El usuario informa que estos desarrollos están en uso productivo.** Su código es legacy y de baja calidad. Se utiliza como evidencia para descubrir reglas y particularidades reales, y debe caracterizarse al migrar; no se adopta automáticamente su estructura ni toda rama como política deseada.

Las rutas absolutas siguientes corresponden al workspace `H:/Github/Glyms`. El manifiesto [referencias-glyms.json](referencias-glyms.json) también contiene rutas relativas a esa raíz, versiones de ensamblado, hashes y puntos de lectura. Al mover el material, resolverlas contra la nueva raíz Glyms.

Los originales son ensamblados .NET, incluidos los archivos `.deploy`; `GlymsWeb` es un bundle .NET Linux sin extensión, con tres ensamblados propios recuperados. Los archivos C# son decompilados para consulta y no constituyen la solución fuente original.

## Ensamblados originales

| ID | Pieza | Ruta absoluta al original | Uso en la migración |
|---|---|---|---|
| G01 | RS232 | [H:/Github/Glyms/Original/GlymsInterfaces/RS232.dll.deploy](H:/Github/Glyms/Original/GlymsInterfaces/RS232.dll.deploy) | Transporte serial reutilizable; referencia de parámetros, buffers, timeouts y Win32. |
| G02 | Contrato de plugins | [H:/Github/Glyms/Original/GlymsInterfaces/Plugin.dll.deploy](H:/Github/Glyms/Original/GlymsInterfaces/Plugin.dll.deploy) | Contrato de extensión del host legacy. |
| G03 | Architect | [H:/Github/Glyms/Original/Plugins ATSM/Plugin_Architect.dll](H:/Github/Glyms/Original/Plugins%20ATSM/Plugin_Architect.dll) | Dialectos y particularidades de un instrumento en producción según el usuario. |
| G04 | CELL-DYN Ruby | [H:/Github/Glyms/Original/Plugins ATSM/Plugin_Celldyn_Ruby.dll](H:/Github/Glyms/Original/Plugins%20ATSM/Plugin_Celldyn_Ruby.dll) | Segundo dialecto para contrastar qué puede generalizarse en el motor. |
| G05 | Host de interfaces | [H:/Github/Glyms/Original/GlymsInterfaces/GlymsInter.exe.deploy](H:/Github/Glyms/Original/GlymsInterfaces/GlymsInter.exe.deploy) | Host legacy: coordinación serial/TCP, carga de plugins y relación con el LIMS. |
| G06 | Reglas de negocio y equivalencias | [H:/Github/Glyms/Original/GlymsWin/GlymsBiz.dll.deploy](H:/Github/Glyms/Original/GlymsWin/GlymsBiz.dll.deploy) | Equivalencias, valores, rangos, flags y estados que se mezclan con integración. |
| G07 | Acceso a datos | [H:/Github/Glyms/Original/GlymsWin/GlymsDal.dll.deploy](H:/Github/Glyms/Original/GlymsWin/GlymsDal.dll.deploy) | Contratos PostgreSQL y persistencia; seguir también funciones y triggers. |
| G08 | Entidades y catálogos | [H:/Github/Glyms/Original/GlymsWin/GlymsEnt.dll.deploy](H:/Github/Glyms/Original/GlymsWin/GlymsEnt.dll.deploy) | Conceptos legacy de resultados y configuración de instrumentos. |
| G09 | Escritorio y operación | [H:/Github/Glyms/Original/GlymsWin/GlymsWin.exe.deploy](H:/Github/Glyms/Original/GlymsWin/GlymsWin.exe.deploy) | Validaciones y decisiones de operación presentes en formularios. |
| G10 | Web y módulos de aplicación | [H:/Github/Glyms/Original/GlymsWeb/GlymsWeb](H:/Github/Glyms/Original/GlymsWeb/GlymsWeb) | Bundle .NET Linux; contiene GlymsWeb, GlymsShared y GlymsShared2. |
| G11 | Utilidades propias | [H:/Github/Glyms/Original/GlymsWin/GlymsUtils.dll.deploy](H:/Github/Glyms/Original/GlymsWin/GlymsUtils.dll.deploy) | Helpers propios y convenciones transversales; revisar consumidores antes de portar. |
| G12 | Servicios administrativos | [H:/Github/Glyms/Original/GlymsWin/GlymsFact.dll.deploy](H:/Github/Glyms/Original/GlymsWin/GlymsFact.dll.deploy) | Contexto de proveedores y circuitos empresariales fuera del transporte ASTM. |
| G13 | Utilidades QR | [H:/Github/Glyms/Original/GlymsWin/CryptoQrUtilities.dll.deploy](H:/Github/Glyms/Original/GlymsWin/CryptoQrUtilities.dll.deploy) | Contexto auxiliar de publicación/informes; no es parte del motor ASTM. |

## Código decompilado y puntos de lectura

### G01 — RS232

- Identidad: `RS232, Version=1.24.0.0, Culture=neutral, PublicKeyToken=515d87df384dcc81`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Integraciones/RS232/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Integraciones/RS232/GsmComm/RS232/Rs232.cs](H:/Github/Glyms/Decompilado/Codigo/Integraciones/RS232/GsmComm/RS232/Rs232.cs): Leer puerto, paridad, stop bits, buffers, eventos y llamadas nativas. Leer `Rs232` (línea aproximada 14), `DllImport` (línea aproximada 417).

### G02 — Contrato de plugins

- Identidad: `Plugin, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin/PlugIn/IPlugin.cs](H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin/PlugIn/IPlugin.cs): Contrato actual, parsing, upload y caracteres de control. Leer `IPlugin` (línea aproximada 4), `ProcesarMenaje` (línea aproximada 14), `UploadMuestra` (línea aproximada 16).
- [H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin/PlugIn/IPluginHost.cs](H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin/PlugIn/IPluginHost.cs): Registro de plugins en el host. Leer `IPluginHost` (línea aproximada 4), `Register` (línea aproximada 6).

### G03 — Architect

- Identidad: `Plugin_Architect, Version=1.0.0.1, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin_Architect/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin_Architect/Plugin_Architect/PlugIn.cs](H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin_Architect/Plugin_Architect/PlugIn.cs): Parsing H/P/O/R/C/Q/L, muestra, variante de analito, órdenes salientes, framing y checksum. Leer `ProcesarMenaje` (línea aproximada 53), `UploadMuestra` (línea aproximada 135), `FinMensaje` (línea aproximada 38), `checksum` (línea aproximada 148).
  Cambio previo detectado: el hash actual del C# difiere del manifiesto de extracción. Se conserva el archivo; contrastar el binario original. Ambos hashes están en el JSON.

### G04 — CELL-DYN Ruby

- Identidad: `Plugin_Celldyn_Ruby, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin_Celldyn_Ruby/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin_Celldyn_Ruby/Plugin_Celldyn_Ruby/PlugIn.cs](H:/Github/Glyms/Decompilado/Codigo/Integraciones/Plugin_Celldyn_Ruby/Plugin_Celldyn_Ruby/PlugIn.cs): Estados F/W, comparadores, alarmas FLAG, tramas salientes y terminador. Leer `ProcesarMenaje` (línea aproximada 54), `UploadMuestra` (línea aproximada 129), `FinMensaje` (línea aproximada 38), `checksum` (línea aproximada 140).

### G05 — Host de interfaces

- Identidad: `GlymsInter, Version=3.1.3.1, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Integraciones/GlymsInter/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Integraciones/GlymsInter/GlymsInter/FormInter.cs](H:/Github/Glyms/Decompilado/Codigo/Integraciones/GlymsInter/GlymsInter/FormInter.cs): Seguir coordinación del host; parte del código conserva ofuscación. Leer `FormInter` (línea aproximada 54).

### G06 — Reglas de negocio y equivalencias

- Identidad: `GlymsBiz, Version=1.0.0.1, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsBiz/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsBiz/GlymsBiz/_lims/Inter_Equival.cs](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsBiz/GlymsBiz/_lims/Inter_Equival.cs): Separar normalización del mensaje de las políticas del LIMS. Leer `VerificaEquivalenciasEstadoRes` (línea aproximada 18).

### G07 — Acceso a datos

- Identidad: `GlymsDal, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsDal/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsDal/GlymsDal/_lims/_Resultado.cs](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsDal/GlymsDal/_lims/_Resultado.cs): Inserción y lectura de resultados; referencia para los adaptadores de Application. Leer `Insert` (línea aproximada 24).

### G08 — Entidades y catálogos

- Identidad: `GlymsEnt, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Resultado.cs](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Resultado.cs): Campos y convenciones del resultado original. Leer `_Resultado` (línea aproximada 16).
- [H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Interface_analito.cs](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Interface_analito.cs): Equivalencias de analitos del equipo. Leer `_Interface_analito` (línea aproximada 16).
- [H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Interface_regla.cs](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Interface_regla.cs): Reglas asociadas a equivalencias. Leer `_Interface_regla` (línea aproximada 16).
- [H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Interface_robot.cs](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Interface_robot.cs): Configuración del instrumento/robot. Leer `_Interface_robot` (línea aproximada 17).
- [H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Deter_numerico.cs](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsEnt/GlymsEnt/_lims/_Deter_numerico.cs): Rangos y fórmulas del LIMS. Leer `_Deter_numerico` (línea aproximada 16).

### G09 — Escritorio y operación

- Identidad: `GlymsWin, Version=1.6.7.32, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsWin/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsWin/GlymsWin/_lims/Interface_Lista.cs](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsWin/GlymsWin/_lims/Interface_Lista.cs): Punto de lectura de la gestión de interfaces del escritorio. Leer `Interface_Lista` (línea aproximada 21).

### G10 — Web y módulos de aplicación

- Identidad: `GlymsShared, Version=2.4.0.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Web/GlymsShared/_extraccion.json).
- Identidad: `GlymsShared2, Version=2.4.0.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Web/GlymsShared2/_extraccion.json).
- Identidad: `GlymsWeb, Version=2.4.0.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Web/GlymsWeb/_extraccion.json).
- [H:/Github/Glyms/Decompilado/Codigo/Web/GlymsShared2/GlymsShared2/Modulos/ModuloIngresos.cs](H:/Github/Glyms/Decompilado/Codigo/Web/GlymsShared2/GlymsShared2/Modulos/ModuloIngresos.cs): Ingreso de órdenes y coordinación de reglas/persistencia de la web. Leer `ModuloIngresos` (línea aproximada 17).
- [H:/Github/Glyms/Decompilado/Codigo/Web/GlymsShared2/GlymsShared2/GlymsContext.cs](H:/Github/Glyms/Decompilado/Codigo/Web/GlymsShared2/GlymsShared2/GlymsContext.cs): Adaptación PostgreSQL y ejecución de consultas de los módulos. Leer `EjecutarQueryDinamica` (línea aproximada 251).

### G11 — Utilidades propias

- Identidad: `GlymsUtils, Version=1.0.0.1, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsUtils/_extraccion.json).
- [carpeta de código decompilado](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsUtils); referencia complementaria, fuera del recorrido ASTM inicial.

### G12 — Servicios administrativos

- Identidad: `GlymsFact, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsFact/_extraccion.json).
- [carpeta de código decompilado](H:/Github/Glyms/Decompilado/Codigo/Desktop/GlymsFact); referencia complementaria, fuera del recorrido ASTM inicial.

### G13 — Utilidades QR

- Identidad: `CryptoQrUtilities, Version=0.9.1.0, Culture=neutral, PublicKeyToken=null`; [manifiesto de extracción](H:/Github/Glyms/Decompilado/Codigo/Integraciones/CryptoQrUtilities/_extraccion.json).
- [carpeta de código decompilado](H:/Github/Glyms/Decompilado/Codigo/Integraciones/CryptoQrUtilities); referencia complementaria, fuera del recorrido ASTM inicial.

## SQL, configuración y documentación relacionados

- [lims._resultado_insert: una sobrecarga; seguir sus triggers y otras firmas](H:/Github/Glyms/Decompilado/BaseDatos/Actual/Esquemas/lims/Funciones/_resultado_insert__2199.sql): `H:/Github/Glyms/Decompilado/BaseDatos/Actual/Esquemas/lims/Funciones/_resultado_insert__2199.sql`.
- [cruce C#/SQL actual para localizar sobrecargas](H:/Github/Glyms/Decompilado/BaseDatos/Actual/Inventario/cruce-csharp-sql.json): `H:/Github/Glyms/Decompilado/BaseDatos/Actual/Inventario/cruce-csharp-sql.json`.
- [esquema PostgreSQL del 24 de octubre de 2026](H:/Github/Glyms/Decompilado/BaseDatos/Actual/README.md): `H:/Github/Glyms/Decompilado/BaseDatos/Actual/README.md`.
- [equivalencias de analitos recuperadas](H:/Github/Glyms/Decompilado/BaseDatos/Catalogos/lims.interface_analito.json): `H:/Github/Glyms/Decompilado/BaseDatos/Catalogos/lims.interface_analito.json`.
- [reglas recuperadas de interfaces](H:/Github/Glyms/Decompilado/BaseDatos/Catalogos/lims.interface_regla.json): `H:/Github/Glyms/Decompilado/BaseDatos/Catalogos/lims.interface_regla.json`.
- [parámetros de robots seleccionados, sin credenciales](H:/Github/Glyms/Decompilado/BaseDatos/Catalogos/lims.interface_robot.json): `H:/Github/Glyms/Decompilado/BaseDatos/Catalogos/lims.interface_robot.json`.
- [análisis previo de integraciones y plugins](H:/Github/Glyms/Decompilado/Documentacion/04-Integraciones.md): `H:/Github/Glyms/Decompilado/Documentacion/04-Integraciones.md`.
- [reglas SQL de resultados y circuitos relacionados](H:/Github/Glyms/Decompilado/Documentacion/10-ReglasSQLLims.md): `H:/Github/Glyms/Decompilado/Documentacion/10-ReglasSQLLims.md`.
- [verificación general de la extracción y diferencias registradas](H:/Github/Glyms/Decompilado/Inventario/verificacion.json): `H:/Github/Glyms/Decompilado/Inventario/verificacion.json`.

Los catálogos con datos son del 6 de febrero de 2026; el SQL de octubre contiene sólo esquema. Sus valores no quedaron actualizados por ese export.

## Calidad y decisiones de migración

- Architect y Ruby son los puntos iniciales más legibles para caracterizar dialectos. La generación de tramas/checksum está mezclada con el parsing y debe repartirse entre motor y plugin.
- En Architect hay una condición de sexo `!= M || != F` siempre verdadera: preservar la evidencia y acordar el comportamiento de EDC; no corregir o reproducirla sin registrar la decisión.
- Architect contiene además un cambio previo del C# decompilado respecto de la extracción. El original está verificado contra el inventario; el C# existente se conservó.
- GlymsInter tiene ofuscación residual y una referencia opaca sin resolver. Sus nombres y caminos incompletos requieren contraste con plugins, trazas y equipos.
- Inter_Equival mezcla equivalencias, rangos, cultura decimal, estados y configuración. Es referencia de negocio para Domain/Application, con adaptación en Infrastructure.
- La existencia del ensamblado en producción no prueba que todas sus ramas se usen en todas las instalaciones. Cada regla se acompaña de proceso, versión, configuración y evidencia de caracterización.

Los hashes de los 13 originales enlazados se compararon con el inventario existente al generar este mapa. Esta comprobación es documental y de integridad; no ejecuta protocolos ni acredita compatibilidad del nuevo sistema.
