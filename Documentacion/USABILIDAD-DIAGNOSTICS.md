# Interpretación y uso de Diagnostics

Decisiones humanas del 2026-10-25, [EDC#205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6398309342). Esta ampliación corrige la presentación MAUI y la selección/detección de entradas, sin modificar protocolos ni la custodia de EDC.

## Vista predeterminada

**Interpretar comandos y mensajes** y **Agrupar repeticiones** están activados al iniciar. La primera opción muestra significado de controles del enlace (inicio, aceptación, rechazo, fin), tipo de mensaje, vocabulario de registros del plugin y resultados interpretados. Se puede desactivar para inspección técnica. Hexadecimal tiene su propia casilla, siempre disponible, para el detalle de bytes; no requiere desactivar la interpretación.

`IInstrumentDiagnosticVocabulary` permite que cada plugin aporte claves, fallback español y referencia documental. La descripción de un registro S de U-WAM, por ejemplo, corresponde a datos de control y objetivos/límites (R315168); no se extrapola ese significado al ASTM genérico. Lo desconocido se identifica como **sin interpretación documentada**. Estado del instrumento no equivale a validación clínica; no se presenta F/P de XN-L como final/preliminar clínico.

Los originales, códigos, posiciones y datos del fabricante permanecen en el caso y en el detalle. La traducción afecta la presentación; no altera `AtsmDiagnostic.Message`, bytes, hallazgos persistidos ni el contrato del motor. En vista técnica se conservan mensajes originales aunque estén en inglés.

## Grupos, contenido y columnas

Se agrupan comandos, mensajes, eventos y hallazgos por significado/código, y mediciones por muestra/prueba/tipo. **Cantidad** indica el número de filas; la flecha despliega subfilas con cada valor y detalle original. Si una columna tiene valores diferentes, el resumen indica cuántos; no muestra como común el valor de la primera fila. Desactivar la agrupación devuelve cada fila.

Se eliminó el recorte de las últimas 2.000 filas. La vista pagina 300 filas visibles para mantener la navegación; los grupos y sus hijos siguen completos. Se prueba un grupo con 10.001 registros sin pérdida.

Los mensajes de la lista se presentan en una línea, con separadores visibles entre registros. El detalle conserva el texto original multilínea. El ajuste de columnas es automático al contenido y ancho de la ventana, con muestreo de hasta 1.500 filas y un ancho mínimo legible para encabezados; si éstos no caben, hay desplazamiento horizontal. La tabla ocupa el ancho disponible desde la izquierda. El usuario puede arrastrar el borde de un encabezado para personalizar un ancho; doble clic reajusta esa columna. Los anchos manuales se conservan durante actualizaciones de esa vista. No hay botón «Ajustar columnas».

Encabezados con fondo claro, regla inferior y agarre de redimensionado discreto al pasar el puntero, sin barras gruesas. Casillas junto a su etiqueta, etiquetas pulsables y tema claro coherente con los fondos. El panel lateral identifica cada selector/campo con etiqueta, borde y fondo suave.

## Una entrada U-WAM y configuraciones de comunicación

La lista presenta **8 equipos/familias**, que conservan **12 perfiles técnicos**. CFX agrupa CSV/RDML/curva; ARCHITECT agrupa ASTM/HL7. U-WAM aparece una vez, con las variantes en **Configuración de la interfaz**:

| Configuración | Límite de consulta según R315168 §5.2.5 | Presupuesto del anfitrión ATSM |
|---|---|---|
| Con analizador químico | 8 s | 6 s |
| Sin analizador químico | 18 s | 15 s |

Ambas utilizan el mismo plugin, dialecto, encuadre y comunicación. No se abren conexiones adicionales. La diferencia implementada es el presupuesto de consulta; el operador confirma la configuración real antes de conectar. Un archivo no demuestra por sí solo qué configuración de hardware está instalada.

Si la cabecera permite sugerir U-WAM y todavía estaba seleccionado otro equipo, la importación propone la variante de presupuesto más corto (6 s, compatible con el límite más exigente). Es una selección conservadora del intérprete, no una detección de hardware instalado. Si el operador ya había seleccionado una variante U-WAM, se conserva.

## Detección por contenido

**Detectar automáticamente** es el formato predeterminado. Se reconocen firmas de los adaptadores Sysmex/legacy, cabecera JSON de traza ATSM, magic de caso protegido, cabeceras CSV CFX, raíz RDML (también en ZIP acotado), mensajes ASTM y HL7/MLLP. La extensión por sí sola no elige el parser. Si no se reconoce el contenido o coincide con varios formatos, se informa en español y se conserva la selección manual.

En Sysmex se infiere quién registró la comunicación a partir de registros R/Q que origina el instrumento. Esto corrige el sentido instrumento→anfitrión entre un monitor del puerto LIS de U-WAM y un monitor donde el software actúa como anfitrión del analizador. La inferencia puede reemplazarse en **Quién registró el log**. Una captura sin evidencia suficiente muestra una advertencia; no se infiere la dirección por ACK o solicitudes O solamente.

Reconocer el formato de un monitor **no** acredita el protocolo de su contenido. Si un log binario interno no contiene cabeceras ASTM compatibles, se conserva en Trama y se indica que requiere otro perfil. No se lo interpreta como una secuencia de registros ASTM ni se inventan observaciones. Un fragmento de captura sin cabecera puede requerir intervención técnica; no se declara compatible automáticamente.

Regresión local, sólo agregados y sin publicar los archivos:

| Fuente | Resultado de la nueva lectura |
|---|---|
| Log UF-5000 mostrado por el usuario | 7.089 entradas, 425 mensajes, 5.006 observaciones; registrador inferido: anfitrión. Antes la GUI invertía su sentido y mostraba cero observaciones. |
| Log UC-3500 mostrado por el usuario | 2.006 entradas conservadas; sin mensajes/observaciones ASTM inferidos. 6 hallazgos en 2 grupos: límites de la fuente y protocolo sin intérprete; desaparece la cascada de falsos registros/errores. |

`atsm-trace inspect-input <archivo> [--profile <id>]` utiliza el mismo detector/lector y emite únicamente cantidades y perfil; no imprime nombres de archivo, muestras, pacientes ni resultados. No abre casos DPAPI sin su lector específico. No crea archivos de intercambio ni vigila directorios.

## Idiomas publicados y Lang

`WindowsLanguages.targets` filtra MUI nativos de WinUI en los elementos de MSBuild y satélites administrados. Se conservan español y fallback `en-US`, sin borrar DLL/PRI ni carpetas arbitrarias después de publicar. Paquete comprobado: carpetas de idioma `es`, `es-ES` y `en-us`, 4 MUI nativos. `Microsoft.UI.Xaml` y `NpuDetect` son carpetas funcionales, no idiomas.

La distribución independiente usa seeds de **español neutral**. UI en `Atsm.Diagnostics.Maui/Lang.cs`; diagnósticos/composición en `Atsm.Diagnostics.Core/DiagnosticText.cs`; vocabulario de cada plugin en su ensamblado. Claves estables, placeholders y `source = AssemblyName`, `target = es-AR` conforme la [guía de Kalyx.Globalization](../../../Documentacion/Bibliotecas/Globalization/Lang-para-agentes.md) del checkout EDC.

El anfitrión puede habilitar la referencia compilada a la biblioteca propia existente:

```powershell
dotnet build dotnet/tools/Atsm.Diagnostics.Maui/Atsm.Diagnostics.Maui.csproj -c Release `
  -p:EnableKalyxLang=true `
  -p:KalyxGlobalizationProject=H:/Github/EDC/Kalyx.Globalization/Kalyx.Globalization.csproj
```

Antes de crear la aplicación, el anfitrión asigna su conexión abierta a `DiagnosticsStartup.TranslationDatabase` y, si corresponde, `TranslationTarget`. La inicialización es asíncrona y anterior a crear MainPage. También se ofrece `DiagnosticText.InitializeAsync` para otros anfitriones. Lang registra sus tablas/seeds y traducciones; no se bloquea un constructor estático ni se crea otra clase Lang. Sin la compilación opcional, pasar una conexión produce una incompatibilidad explícita; no se simula inicialización.

La publicación independiente no incluye esa referencia ni sus dependencias SQLite: no necesita otra base para funcionar en español. La prueba opcional ejecutó **el Lang real** con una conexión DbConnection sintética, verificando aplicación de traducciones, seed por plugin y conservación de la conexión prestada. No acredita una integración SQLCipher/UI de EDC completa ni sincronización/descarga de traducciones. Inventario y firma: [DEPENDENCIAS](DEPENDENCIAS.md).

## Verificación y límites

181 pruebas .NET y 542 verificaciones nativas, corridas completas repetidas; 19 pruebas nuevas de detección/dirección, ambigüedad, agrupación sin pérdida, vocabulario y columnas automáticas. Prueba opcional Lang adicional: 1 correcta. Se verifican idiomas, dependencias y arranque/DPAPI en la salida directa; recorre las siete vistas con y sin interpretación, comprueba handlers y alineación de la tabla en la ventana real.

Se inspeccionó una ventana del primer candidato con accesibilidad y captura. El usuario detuvo Computer Use con Esc antes de probar el arrastre; no se realizaron más acciones de control visual. Los ajustes posteriores de encabezados/espaciado/contraste se verificaron por compilación y pruebas de la aplicación, sin afirmar inspección visual manual ni arrastre manual del diseño final. No se interrumpe la instancia abierta del usuario para sustituirla.

## Destino único y retiro de compatibilidad

Decisión humana posterior: sólo se mantiene legacy por traspaso activo o fuerza mayor (ejemplo Windows XP). La salida única es dotnet/tools/Atsm.Diagnostics.Maui/bin/Release/atsm-diagnostics.exe. Se eliminaron el alias Windows, salidas anteriores, los comprimidos de entrega y tools/Publish-Diagnostics.ps1. Se compila directamente con .NET y se verifica el ejecutable resultante; sin mover, duplicar ni comprimir salidas. No se eliminan datos de usuario ni se reescribe el historial de changelogs.

La detección preserva también HL7/MLLP dentro de un log: infiere el lado del registrador por OUL/QBP cuando hay evidencia, conserva el perfil HL7 seleccionado y nunca lo reemplaza por U-WAM por encontrar un nombre de equipo. Reconocer MSH no elige una marca: sin configuración HL7 compatible del equipo se solicita seleccionarla, en vez de interpretar con otro fabricante. La prueba adicional cubre ambas direcciones y el rechazo de esa suposición.
