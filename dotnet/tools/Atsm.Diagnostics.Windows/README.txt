ATSM - Diagnóstico de mantenimiento para Windows (0.1.0)

Requiere .NET 10 Windows Desktop Runtime. Mantener toda esta carpeta junto al EXE.
Abrir atsm-diagnostics.exe; --demo carga un ejemplo CFX sintético.

Elegir perfil y formato antes de abrir el archivo. Las pestañas muestran bytes,
mensajes, observaciones, calidad, hallazgos y catálogo. Seleccionar una fila muestra detalle.

CFX: exportaciones Maestro CSV UTF-8 (Well,Sample,Target,Cq; coma, decimal punto)
o RDML 1.0/1.1. No lee .pcrd ni controla el termociclador.
El perfil CSV de curva estándar lee Target (o Fluor), Efficiency %, Slope,
Y-intercept y R^2. Calidad distingue mediciones y estadísticas reportadas.
Las referencias externas U-WAM se muestran sin abrir archivos automáticamente.
Sysmex XN-L, CS-2500 y QWALYS 3: recepción ASTM; sin consultas ni órdenes.
Los perfiles nuevos requieren validación en equipo. No hay validación clínica.

TCP cliente/servidor y SerialTunnel: preparar caso antes de conectar.
Captura sin respuestas abre una conexión propia; no observa otra sesión.
SerialTunnel toma control exclusivo. Simulación de host envía ACK y custodia
resultados de diagnóstico; no genera órdenes ni publica en un LIMS.

Guardar caso usa DPAPI del usuario Windows actual, sin fallback plano.
No es un paquete de intercambio entre usuarios ni reemplaza SQLCipher de EDC.
Límite 32 MiB/100.000 entradas; CFX 16 MiB. No es captura continua con rotación.
Una captura incompleta se informa al reabrirla. Conservar el original del log.

En el checkout ATSM: Documentacion/DIAGNOSTICO-MANTENIMIENTO.md contiene evidencia
por modelo, instrucciones completas y pendientes. Strong Name de distribución;
Authenticode e instalador quedan pendientes del release.
