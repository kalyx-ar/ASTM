# Configuraciones ilustrativas

[agente.json](agente.json) y [instrumento.json](instrumento.json) describen el mismo puerto e instrumento sintéticos. No son archivos consumibles por un agente existente ni acreditan soporte de una plataforma. Ambos están deshabilitados y utilizan placeholders de certificados, almacenamiento y políticas.

El endpoint TCP del agente escucha y el ETL se conecta. `Tls` y `Plain` son modos explícitos por endpoint; para ilustrar el modo claro se cambiaría la configuración de ambos extremos y se definiría su autorización. No se utiliza fallback automático.

Los valores de baud, buffers, memoria, retención y lease son ejemplos. Deben sustituirse por los del perfil validado. En la cola, un número menor indica mayor prioridad; a igual prioridad se conserva FIFO. `preemptActiveOwner=false` evita interpretar la prioridad como expulsión inmediata.

`expectedServerIdentity` expresa la identidad que presentará el agente; el ETL la valida mediante `expectedPeerIdentity`. La implementación final resolverá los placeholders y el vínculo certificado–controllerId usando sus almacenes y políticas.

`Durable` representa una capacidad exigida en este ejemplo. Si el dispositivo sólo ofrece RAM, el perfil debe declararlo y el ETL debe resolver esa incompatibilidad de requisitos. Cambiar a Linux/Raspberry requiere cambiar `device`, el perfil de plataforma y las referencias de almacenamiento, además de validar el hardware.

Para TCP nativo se sustituye el adaptador por `NativeTcpInstrumentChannel` y se declaran las capacidades reales del instrumento. No se envía el contrato gestionado del agente a un instrumento TCP que sólo espera su protocolo propietario.

## Configuración consumible del agente

[agente-tls.json](agente-tls.json) sí sigue el esquema implementado (`atsm-agent/1`) y se valida con `atsm-agent --check agente-tls.json`. Las rutas de certificados son relativas al archivo. Los controladores se identifican por el CN de su certificado o, como `edc-cliente-local`, por la huella SHA-256 configurada (el valor del ejemplo es un marcador). Para texto claro se usa `"tls": "Plain"`, sólo en redes de confianza.
