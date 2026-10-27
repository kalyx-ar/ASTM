# Pruebas de comunicación con PortGate

Decisión humana de [EDC#205](https://github.com/kalyx-ar/EDC/issues/205), 2026-10-27: basta un COM de prueba para comprobar lectura y escritura de PortGate, con un instrumento simulado que interprete las instrucciones de ASTM y responda. No requiere un COM registrado en Windows ni instalar controladores.

## Circuito ejecutado

`AstmInstrumentSession → PortGate.Client → agente nativo PortGate → Device serie de prueba → instrumento simulado → respuesta por el mismo recorrido`.

El agente abre un dispositivo serie con ruta `test://127.0.0.1:<puerto>`. Usa la misma interfaz `platform::Device`, arbitraje, permisos y protocolo Link que los dispositivos físicos. Su backend local conecta al instrumento simulado. Sólo acepta loopback y control de flujo `None`; emula el tiempo de serialización según baudios, datos, paridad y bits de parada, y limita los bits de datos. El cierre interrumpe las escrituras pendientes. No simula señales eléctricas, errores de paridad ni las llamadas del driver Windows.

`PortGateBench` inicia un agente temporal propio, configura su puerto, toma el Stream y ejecuta el escenario. Al terminar, cierra únicamente su proceso y retira su directorio de estado temporal. No instala servicios ni drivers. La traza del ensayo y los datos fuente permanecen en memoria; no se crean logs planos. La administración sin TLS se limita al loopback de este proceso de prueba aislado.

## Escenarios desde trazas

`CommunicationScenario.FromTrace` utiliza mensajes completos recibidos del equipo. No reenvía indiscriminadamente los ACK capturados de ambos extremos. El plugin interpreta muestras, pruebas y resultados; el ensayo verifica su equivalencia semántica al recibirlos por el recorrido real de PortGate.

Cuando el perfil ofrece `order.send`, se construye una orden con el plugin. El instrumento simulado decodifica la muestra y las pruebas antes de devolver el mensaje de resultados seleccionado. Admite respuestas con varias muestras. También se ejecutan consultas del equipo, respuestas del anfitrión y una inyección de checksum incorrecto seguida del reintento correcto (NAK/ACK). La comparación usa firmas en memoria; la consola muestra sólo agregados.

Es un escenario funcional derivado de la traza: prioriza el intercambio orden/respuesta y conserva los payloads seleccionados. No reproduce sus pausas históricas ni certifica el funcionamiento clínico del equipo. Los presupuestos ENQ/ACK y frame/ACK proceden del perfil; las distribuciones de tiempos quedan disponibles en Comunicación con formato `HH:mm:ss.fff`.

## Uso

Compilar primero el agente nativo y después la GUI/CLI; ambas copian `portgate.exe` a su salida:

```powershell
cmake --build PortGate/build/win-x64 --config Release
dotnet publish dotnet/tools/Astm.Diagnostics.Maui -c Release -p:AstmRequireStrongName=true
```

En Diagnostics, abrir una traza ASTM y pulsar **Probar PortGate**. Ejecuta hasta cinco mensajes, muestra el ensayo y selecciona Comunicación. El límite global suma 60 segundos al tiempo mínimo de serialización de los payloads; los timeouts del protocolo permanecen sin cambios. **Detener** cancela la prueba. El self-test de la GUI realiza un intercambio sintético por este puerto.

```powershell
astm-trace test-portgate <traza> --max 3
```

Opciones: `--portgate <exe>`, `--profile <id>`, `--baud <n>` y `--timeout <segundos>` (60 por defecto; salida 124 al expirar). Primero informa bytes del payload y tiempo mínimo de serialización para dimensionar el ensayo. El COM de prueba usa los parámetros del perfil si existen; en perfiles TCP usa 9600/8N1 como condición del ensayo, sin atribuir esos parámetros al equipo. `--baud` cambia únicamente esa condición de prueba. Salida 0: presupuestos cumplidos y al menos una orden interpretada/respondida; salida 2: recorrido parcial sin esa cobertura. Los perfiles pasivos o propietarios sin acciones documentadas no se convierten en emisores activos por este ensayo.

## Instrumento simulado rígido y aleatorizado

Pedido humano del 2026-10-27: un instrumento simulado rígido y otro aleatorizado, con un plugin ASTM de prueba, para verificar el circuito completo con datos de prueba. A diferencia de los escenarios desde trazas, no depende de capturas privadas: corre en cualquier máquina y en CI.

`Astm.Plugin.Testing.Synthetic` (sólo pruebas, fuera del motor neutral):

- **`SyntheticAstmPlugin`**: perfil `astm.testing.synthetic.analyzer`, CLSI LIS01-A2/LIS02-A2 con resultados no solicitados, consulta del equipo y descarga de órdenes, y la acción `order.send`. Menú inventado (GLU, UREA, CREA, NA, K, HGB, CRP) con unidades e intervalos. Serie 115200/8N1 declarada como condición de ensayo (`Hypothesis`), no como especificación de un equipo.
- **`SimulatedAnalyzer`**: lado instrumento sobre cualquier `Stream` (par dúplex, COM de prueba de PortGate o un par COM real). Decodifica órdenes y respuestas a consultas, envía `Q`, "procesa" y devuelve `H/P/O/R/C/L`, y lleva un **libro** de cada resultado entregado. Un código fuera del menú vuelve como no realizado (`X`) con comentario.
- **`RigidSimulation`**: los mismos datos producen siempre los mismos bytes. El valor es el punto medio del intervalo, el estado es final, va una muestra por mensaje, se usa el encuadre del perfil y no hay fallas. Sirve como valores dorados de regresión.
- **`RandomizedSimulation(seed)`**: reproducible por su semilla. Produce valores normales, altos (`H`), bajos (`L`), bajo el límite (`<0.3`), no realizados, preliminares y comentarios con delimitadores y escape LIS02. Puede agrupar hasta tres muestras por mensaje. El encuadre es por registro o por mensaje con frames de 32 a 240 caracteres (ETB) y demoras de hasta 25 ms. Alrededor del 15 % de los frames se envía una vez con el checksum mal, lo que fuerza NAK y retransmisión, nunca dos veces seguidas. Las fallas usan un generador propio, así que la temporización del enlace no altera los valores.

`InstrumentSimulator.FrameFault` es el gancho que corrompe un frame una sola vez. La retransmisión siempre sale correcta, desde el propio enlace.

Pruebas (`tests/Astm.Tests/Integration/SyntheticCircuitTests.cs`): `InMemoryLabHost → AstmInstrumentSession + plugin sintético → (par dúplex | agente PortGate real con COM de prueba) → SimulatedAnalyzer` y regreso. Cada resultado del libro llega al anfitrión una sola vez, con valor, unidad, intervalo, flags, estado, comentarios y hora iguales.

- La versión rígida comprueba los valores dorados con descarga de órdenes, consulta con y sin información, y resultados no solicitados.
- La aleatorizada corre cinco semillas y exige que haya habido NAK/retransmisión.
- Dos corridas con la misma semilla dan el mismo libro.
- La de PortGate ejecuta ambos modos por el COM de prueba a 115200 baudios.

Para reproducir una falla basta la semilla que figura en el mensaje del assert.

No certifica ningún equipo real ni su dialecto: verifica el motor, el plugin estándar de campos, PortGate y el anfitrión con datos sintéticos.

## Acciones de plugins

`IInstrumentCommandPlugin` aporta catálogo, parámetros, etiquetas traducibles y construcción de registros. `AstmInstrumentSession.SendCommandAsync` utiliza la cola y máquina LIS01 existentes; no agrega otro lector ni escribe por fuera del enlace. La GUI genera sus campos desde ese catálogo y habilita el envío sólo durante una sesión activa.

Este incremento implementa **Enviar orden de análisis** en los perfiles ASTM estándar, ARCHITECT y U-WAM que declaran descarga de órdenes. Las consultas de estado, calibración o mantenimiento requieren la orden documentada del fabricante y una implementación en su plugin; no se inventan códigos. Un ACK confirma recepción de enlace, no ejecución de una operación clínica. La respuesta se interpreta con el dialecto normal del plugin.

## Evidencia local

2026-10-27: suite .NET de 412 pruebas (411 correctas, una omitida porque requiere una traza privada seleccionada), repetida en 5/3/3 s; 746 comprobaciones nativas correctas en dos pasadas (CTest 6,06 s). Lang real: una prueba correcta en 191 ms. Entrega GUI autónoma y CLI NativeAOT `0.2.0+d6f101d0e1d4fa290c92aa6854d0ea51d7cb696a`, self-tests y auditoría de dependencias correctos.

Desde la entrega NativeAOT con el PortGate incluido: UF-5000 (tres mensajes, 9600 baudios), una orden, 44 observaciones, dos consultas/respuestas, 63 frames confirmados, 11,656 s; AMS (tres mensajes, 115200 baudios de ensayo), una orden, 252 observaciones, 276 frames confirmados, 37,268 s. Presupuestos de ambos perfiles cumplidos. Cancelación por límite global verificada: salida 124 y ningún proceso hijo nuevo restante. EDC: cinco pruebas correctas repetidas de su adaptador sobre los paquetes 0.2.0. No se publica el contenido de las trazas privadas.

Límites de entrega: 21 ensamblados propios con token de distribución; cinco advertencias CS8002 conocidas de MAUI oficial sin Strong Name. Authenticode propio y firma NuGet pendientes. `System.IO.Ports.dll` original Microsoft tiene Authenticode válido; la copia transformada a ReadyToRun conserva el token `cc7b13ffcd2ddd51`, tiene cabecera nativa administrada y ya no lleva Authenticode. No se altera confianza/admisión para ocultarlo. Hay una intermitencia de cierre/diagnóstico TLS previamente observada en la suite; no se modifica ni se debilita esa verificación para este ensayo.
