# Referencias al sistema legacy

ASTM es neutral y no nombra el LIMS legacy del que se recuperaron reglas y particularidades de equipos (decisión humana del 2026-10-24). El mapa de ensamblados originales, versiones, hashes, código decompilado y puntos de lectura se conserva **fuera de este repositorio**, en el workspace local excluido de git:

`EDC/LIMS/LEGACY/Referencias-ASTM/` (`REFERENCIAS-LEGACY.md` y `referencias-legacy.json`) (desde esta carpeta, `../../LEGACY/Referencias-ASTM/`).

Lo que sí se publica aquí:

- Los plugins de fabricante (`Astm.Plugin.Abbott.Architect`, `Astm.Plugin.Abbott.CellDynRuby`) citan el plugin del host legacy sólo como "referencia legacy" en su evidencia; el manual del fabricante prevalece.
- `Astm.Plugin.Legacy.Testing` lee los logs de interfaz del host legacy como segmentos de traza ASTM, sólo para pruebas y diagnóstico. No forma parte del motor neutral.
- Las trazas reales del host legacy viven en `Documentacion/trazas_externas/legacy/`, ignorada por git.

El código legacy es productivo según informa el usuario, pero de baja calidad: se usa como evidencia para caracterizar comportamiento, no como estructura a adoptar.
