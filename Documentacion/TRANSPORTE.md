# Transporte: ASTM sobre Stream, conocimiento de transporte y PortGate

Pruebas sin hardware ni driver y acciones definidas por los plugins: [Pruebas de comunicación con PortGate](PRUEBAS-SERIE.md).

Decisiones humanas del 2026-10-26:

1. **ASTM es un protocolo de nivel lenguaje.** El anfitrión entrega el transporte final ya abierto como un `Stream` bidireccional; ASTM interpreta y comunica en ambos sentidos sobre él. Quien usa ASTM mantiene la infraestructura (COM, red, TLS, reconexión, archivos).
2. **Los plugins aportan conocimiento de transporte**, no transporte: la especificación teórica del equipo (parámetros serie, rol TCP, puerto, MLLP, señales) con su evidencia del manual. ASTM nunca abre un puerto con ese conocimiento.
3. **Biblioteca de transporte opcional (`Astm.Transport`)** para que el anfitrión resuelva rápido qué entregar (COM local, TCP/TLS cliente o servidor, socket existente). Su configurador está abstraído: la misma configuración se entrega a un COM local o a un puerto detrás de PortGate.
4. **PortGate** (antes RemoteUSB) es la puerta de puertos serie y Ethernet: semáforo, kill switch, impugnación y administración remota; puede vivir en una red IoT aislada como túnel seguro de administración. Su cliente .NET (`PortGate.Client`) se queda en .NET: a velocidades serie el costo de .NET es despreciable y un cliente C++ sumaría interop y riesgo de memoria.
5. **`System.IO.Ports` aprobado** (paquete oficial de Microsoft) para el COM local de `Astm.Transport`, previendo Linux y macOS. Registro en [DEPENDENCIAS](DEPENDENCIAS.md).

## Capas

| Capa | Ensamblado | Conoce | No conoce |
|---|---|---|---|
| Protocolos | `Astm.Protocol.*` | LIS01/LIS02, MLLP, HL7 v2 (máquinas de estado sin E/S) | Transporte |
| Sesiones | `Astm.Runtime` | Un `Stream` abierto, reloj y `ITraceSink` | Cómo se abrió, semáforos, COM, TLS |
| Conocimiento | `Astm.Plugins` (+ plugins) | `TransportKnowledge` por perfil, con evidencia | Abrir puertos |
| Transporte opcional | `Astm.Transport` | `TransportTarget`, configurador, COM local (`System.IO.Ports`), TCP/TLS | Protocolos de laboratorio |
| Túnel | `PortGate.Client` | PortGate Link y administración; entrega un `Stream` por puerto tomado | Protocolos de laboratorio |

Una sesión trata el cierre o el fallo del `Stream` como desconexión. El semáforo, la tenencia del puerto y su pérdida son del anfitrión y del túnel: al perder la tenencia, `PortGate.Client` cierra el `Stream`.

## Conocimiento de transporte

Por perfil, todo opcional y con cita (`Evidence`) o marcado como hipótesis:

- Serie: baudios admitidos y por defecto, bits de datos, paridad, bits de parada, control de flujo, DTR/RTS que el equipo requiere.
- Red: rol del equipo (servidor o cliente TCP), puerto habitual, MLLP.
- Medio: bidireccional o sólo lectura (archivo, exportación, log).

Las señales de módem (DTR/RTS, break) son parte de la configuración del puerto, no del flujo de bytes: un `Stream` no las lleva.

## Configurador abstraído

El anfitrión elige **dónde** está el puerto y **cómo** se configura por separado:

- `SerialSettings` (baudios, datos, paridad, parada, flujo, DTR, RTS) se arma desde el conocimiento del perfil o a mano.
- El destino es un COM local, un endpoint TCP/TLS o un puerto de PortGate.
- `ITransportProvider.OpenAsync(target)` devuelve el `Stream`. Para PortGate, el proveedor del cliente lleva `SerialSettings` hasta el agente: los compara con los anunciados por el puerto y, si difieren y el anfitrión administra ese agente, los aplica con `SetConfig`; si no puede aplicarlos, falla con un error explícito en lugar de usar parámetros distintos.

## PortGate para equipos Ethernet

Además del puerto serie y del cliente TCP hacia un equipo servidor, PortGate escucha a un equipo que actúa como cliente TCP (dispositivo `tcp-listen`). En los tres casos se aplican el semáforo, el kill switch, la impugnación y la administración remota, y la red de los instrumentos puede quedar aislada detrás del agente.
