ATSM Diagnóstico · MAUI · Windows x64

Ejecutar atsm-diagnostics.exe conservando toda esta carpeta.
Incluye .NET 10 y Windows App SDK; no requiere instalarlos por separado.
Publicación self-contained/ReadyToRun, no NativeAOT de MAUI Windows .NET 10.

Fuente / conexión muestra los ajustes en ventanas pequeñas. Ejemplo CFX carga
datos sintéticos. Las vistas conservan trama, mensajes, observaciones, hallazgos,
calidad, eventos y catálogo. Seleccionar una fila muestra el detalle.

Los casos .atsmcase mantienen DPAPI CurrentUser y compatibilidad con los casos
Windows anteriores. No son portables entre usuarios por copiar la carpeta.
Antes de conectar se prepara el caso protegido. PortGate toma control
exclusivo; no es un observador de otra sesión. Las acciones disponibles las
declara el plugin; el envío requiere una sesión de anfitrión activa.
Probar PortGate ensaya una traza ASTM con un puerto serie simulado propio,
sin instalar drivers: orden, respuesta, checksum/reintento y tiempos.
Los originales no se modifican ni se generan trazas sensibles en texto plano.

MAUI es la presentación vigente. WPF está retirado. Núcleo/plugins neutrales
reutilizables por EDC y catálogo estático de mantenimiento. Otras plataformas
requieren su anfitrión y adaptador de archivos/custodia; no están verificadas.
Authenticode e instalador corresponden al release.

Interpretación y grupos activados al iniciar; desactive para vista técnica.
Formato de archivo: Detectar automáticamente. Selección manual si es ambiguo.
Una entrada por equipo; protocolos/configuraciones en el selector correspondiente.
La tabla ajusta sus anchos a contenido/ventana; borde de encabezado para arrastre,
doble clic para autoajustar una columna. No hay botón de ajuste.
El tema claro mantiene contraste y los campos tienen borde/fondo propios.
La carpeta incluye sólo idiomas españoles y fallback en-US.
No confundir monitor Sysmex con protocolo interno: si no hay intérprete, se informa.
