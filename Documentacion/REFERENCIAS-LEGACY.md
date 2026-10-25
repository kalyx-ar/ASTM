# Referencias al sistema legacy

ATSM es neutral y no nombra el LIMS legacy del que se recuperaron reglas y particularidades de equipos (decisión humana del 2026-10-24). El mapa de ensamblados originales, versiones, hashes, código decompilado y puntos de lectura se conserva **fuera de este repositorio**, en el workspace local excluido de git:

`EDC/LIMS/LEGACY/Referencias-ATSM/` (`REFERENCIAS-LEGACY.md` y `referencias-legacy.json`) (desde esta carpeta, `../../LEGACY/Referencias-ATSM/`).

Lo que sí se publica aquí:

- Los plugins de fabricante (`Atsm.Plugin.Abbott.Architect`, `Atsm.Plugin.Abbott.CellDynRuby`) citan el plugin del host legacy sólo como "referencia legacy" en su evidencia; el manual del fabricante prevalece.
- `Atsm.Plugin.Legacy.Testing` lee los logs de interfaz del host legacy como segmentos de traza ATSM, sólo para pruebas y diagnóstico. No forma parte del motor neutral.
- Las trazas reales del host legacy viven en `Documentacion/trazas_externas/legacy/`, ignorada por git.

El código legacy es productivo según informa el usuario, pero de baja calidad: se usa como evidencia para caracterizar comportamiento, no como estructura a adoptar.
