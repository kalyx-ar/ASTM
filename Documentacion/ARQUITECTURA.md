# Arquitectura de integración de instrumentos

> **Actualización 2026-10-26:** el contrato `IInstrumentChannel` y `Atsm.Channels` se retiraron. ATSM recibe el transporte final como un `Stream` abierto por el anfitrión; los plugins declaran `TransportKnowledge`; `Atsm.Transport` y `PortGate.Client` son opcionales. Ver [TRANSPORTE](TRANSPORTE.md). Lo que sigue se conserva como antecedente de diseño.

## Componentes y responsabilidades

| Componente | Responsabilidades |
|---|---|
| Agente C++ | Apertura/configuración del puerto, lectura/escritura binaria, señales seriales, TCP/TLS, buffers acotados, arbitraje y diagnóstico técnico |
| Canal común del host | Adaptar agente remoto, TCP nativo, serial local, carpeta o traza a lectura/escritura y eventos; retirar la envoltura de transporte antes de entregar los bytes al motor ([INTERFAZ-COMUNICACION](INTERFAZ-COMUNICACION.md)) |
| Motor genérico C# | Framing incremental, checksum, numeración, estados de conversación, temporizadores y mecanismos genéricos de intercambio ([MOTOR-GENERICO](MOTOR-GENERICO.md)) |
| Plugin del instrumento | Perfil y extensiones del dialecto, registros propietarios, interpretación de órdenes/resultados, correlación y política de recuperación del proceso ([PLUGINS](PLUGINS.md)) |
| Runtime ATSM | Alojar motor/plugins, resolver dependencias, coordinar ingestión/órdenes y entregar hechos canónicos al anfitrión |
| Anfitrión (LIS/LIMS, p. ej. EDC) | OT, muestra, analitos, equivalencias, validación, persistencia, permisos e historial de negocio |

El agente no inspecciona registros ASTM ni genera confirmaciones ASTM. Las confirmaciones que emita sobre una escritura describen etapas técnicas, como aceptación del buffer o escritura al driver. El motor y el plugin interpretan las respuestas del instrumento.

## Canal común

El motor ASTM recibe `IInstrumentByteChannel`: un flujo ordenado de bytes en ambas direcciones, con propiedad del canal, capacidades y eventos de desconexión. Una lectura puede contener parte de una trama, una trama o varias. El framing vive en el motor.

Se proponen dos adaptadores:

- `ManagedSerialTunnelChannel`: conexión TCP al agente C++, con o sin TLS según el endpoint configurado. Incluye el control de propiedad y el acceso a diagnóstico.
- `NativeTcpInstrumentChannel`: conexión TCP del instrumento, usando TLS sólo cuando ese instrumento o su gateway lo admita. Sus capacidades de propiedad y diagnóstico se declaran explícitamente.

La transparencia cubre bytes y contrato de canal, no igualdad de capacidades: un instrumento TCP puede no permitir controlar RTS/DTR, obtener trazas locales o excluir por sí mismo a otros clientes.

Si el failover exige exclusión efectiva para TCP nativo y el instrumento no la proporciona, todos los candidatos deben acceder por un árbitro/proxy común. Un lock entre procesos EDC no bloquea a un cliente que pueda acceder directamente al socket del instrumento.

## Empaquetado neutral

El agente, el motor, los plugins y las herramientas viven en el repositorio ATSM y no referencian ningún LIS/LIMS, UI ni base de datos. El anfitrión implementa `ILabWorkflowHost` ([MODELO-CANONICO](MODELO-CANONICO.md)); la estructura de proyectos está en el [README](README.md#estructura-propuesta-del-repositorio).

En un anfitrión por capas, ATSM pertenece a Infrastructure: Domain y Application no dependen de COM, sockets, framing ni tipos ATSM, y ATSM no constituye una capa de negocio adicional. El encaje concreto en EDC está en [INTEGRACION-EDC](INTEGRACION-EDC.md).

## Motor genérico y plugin

El motor ofrece mecanismos comunes y puntos de extensión. Cada perfil fija delimitadores, encoding, límites, checksum, temporizadores, numeración, reglas de conversación y capacidades efectivamente comprobadas. No se fijan constantes de tiempo universales en esta propuesta.

El plugin proporciona identificación y versión, perfil compatible, adaptación de registros, construcción de mensajes, interpretación de respuestas y evaluación de recuperación. Puede extender el comportamiento genérico cuando exista una variante propietaria demostrada, sin duplicar el motor completo.

El resultado del plugin se convierte al modelo canónico: equipo, muestra/orden, analito, valor original, comparador, unidad, flags, estado del equipo, fecha y referencias de traza. La asociación con una determinación y su estado clínico se resuelve con las reglas del anfitrión.

Un estado `F` de un instrumento no se transforma automáticamente en Validado Final del LIMS. El significado y la transición se comprueban por perfil y política de negocio.

## Despliegues

| Modalidad | Ubicación del agente | Ubicación del runtime y plugins |
|---|---|---|
| Servidor central | PC próximo al equipo, Raspberry o ESP32 | Servidor del anfitrión (p. ej. EDC) |
| Resolución local | Equipo compatible próximo al instrumento | Cliente del anfitrión con motor/plugins instalados |
| Primario/respaldo | Un agente compartido por los candidatos | Servidor A, servidor B o cliente local, con un único controlador |
| Instrumento TCP nativo | Puede prescindir de puente serial | ETL directo o proxy común si se necesita arbitraje externo |

Tener un cliente conectado al agente no lo convierte en controlador. La propiedad se obtiene con el contrato de [CONTRATOS](CONTRATOS.md). Los perfiles de controlador determinan candidatos autorizados, prioridades y solicitudes de cesión; un heartbeat del agente no demuestra que el ETL o el plugin estén resolviendo trabajo.

La continuidad local requiere acceso a las órdenes/configuraciones y credenciales necesarias, además de un mecanismo de conciliación al volver al servidor. Esta capacidad se declara y prueba por despliegue: la adquisición del puerto no acredita disponibilidad de datos de negocio.

## Plataformas y builds

| Perfil | Adaptador y validación requerida |
|---|---|
| Windows XP | Win32 y toolset compatible, como `v141_xp`; comprobar SP/arquitectura, runtime, drivers y backend TLS concretos |
| Windows 7/8/10/11 | Win32; builds y dependencias TLS comprobados por versión objetivo |
| Linux x64 / Raspberry Linux ARM | POSIX/termios y sockets; verificar ABI, bibliotecas y adaptador físico |
| ESP32 | UART, red y TLS de ESP-IDF; comprobar memoria, firmware, límites de buffers y almacenamiento disponible |

Se comparte el núcleo C++; existen binarios o firmware distintos. La documentación de Microsoft describe el [toolset para XP](https://learn.microsoft.com/en-us/cpp/build/configuring-programs-for-windows-xp). El SDK de Espressif ofrece [ESP-TLS](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-reference/protocols/esp_tls.html). Son referencias de implementación, no certificación de un build de este proyecto.

El soporte de otros ESP se incorpora cuando se verifique ese modelo. El hardware requiere una interfaz eléctrica RS232 compatible; los perfiles seriales se prueban con el adaptador instalado.

## TCP y TLS

Cada endpoint declara modo `Plain` o `Tls`, rol de conexión, identidad esperada y autorización por instrumento. TLS no se autodetecta ni se desactiva porque falle la validación de un certificado. Para el modo TLS se propone mínimo 1.2 y uso de 1.3 cuando los extremos lo soporten, con certificados de servidor y opción mTLS según el despliegue. Referencia: [RFC 9325](https://www.rfc-editor.org/rfc/rfc9325.html).

Las claves se referencian mediante el almacén o mecanismo seguro de la plataforma. La elección de biblioteca TLS debe demostrar interoperabilidad en XP y ESP32, incluyendo validación, reloj, certificados y renovación. TLS protege el enlace; la implementación C++ debe controlar memoria, tamaños y errores de sus adaptadores.

En la topología inicial el ETL conecta al endpoint del agente. Cuando un sitio necesite conexión saliente desde el agente, se podrá invertir el establecimiento conservando mensajes, autorización y arbitraje. La primera implementación debe cerrar y probar un único rol antes de incorporar el otro.

El TCP nativo del instrumento se configura independientemente: que el túnel serial soporte TLS no implica que un instrumento legado lo soporte.

## Latencia y presión de buffers

La sesión TCP/TLS se mantiene abierta durante el intercambio. La lectura serial no espera reunir una trama ASTM. El transporte entrega bytes disponibles dentro del presupuesto configurado, con tratamiento de lecturas/escrituras parciales y buffers acotados.

Se medirá el recorrido `serial + agente/TLS + red + motor/plugin + escritura de respuesta`. Registrar percentiles y máximos observados, número de timeouts, uso de CPU/memoria, pérdida reportada y latencia durante renovación TLS/reconexión. El presupuesto se deriva de las especificaciones y trazas de cada instrumento.

Si se agota un buffer, la política técnica declarada puede aplicar control de flujo compatible, detener la admisión o informar una interrupción. Toda discontinuidad queda registrada. Un perfil no puede prometer conservar tráfico ilimitado si su almacenamiento es limitado.

## Decisiones pendientes de la prueba de concepto

- SDK/TLS concretos y builds reproducibles para XP y ESP32.
- Encoding definitivo del contrato lógico agente–ETL, límites y compatibilidad de versiones.
- Perfiles de timing y flujo de los primeros instrumentos.
- Datos disponibles y conciliación cuando el ETL funciona en un cliente local.
- Capacidad de exclusión y recuperación del primer instrumento TCP nativo.

Las decisiones pendientes no cambian la frontera acordada entre transporte, ASTM, plugin y negocio.
