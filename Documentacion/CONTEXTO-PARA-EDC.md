# Contexto para implementar en EDC

Copiar el texto siguiente junto con el paquete documental y acceso al material de referencia necesario.

> ASTM es un repositorio propio y neutral (agente, motor genérico, plugins); EDC lo consume como paquetes mediante un adaptador `ILabWorkflowHost` en `LIMS.Infrastructure.Astm`, sin que Domain/Application vean tipos ASTM. Leer primero README, INTEGRACION-EDC, MODELO-CANONICO, INTERFAZ-COMUNICACION, MOTOR-GENERICO, PLUGINS, ARQUITECTURA, CONTRATOS, TRAZAS-Y-RECUPERACION, IMPLEMENTACION, DATOS-DE-PRUEBA y REFERENCIAS-LEGACY de este paquete. Inspeccionar las instrucciones y las cuatro capas del repositorio EDC antes de modificarlo; adoptar ASTM y sus paquetes requiere un Issue de decisión con aprobación de dependencias. Las trazas reales contienen datos de pacientes: usar sólo corpus anonimizado. El sistema legacy está en `EDC/LIMS/LEGACY`.
>
> La decisión acordada es agente C++ nativo para RS232–TCP, TLS configurable por endpoint y arbitraje exclusivo por instrumento. No hay UDP. Las plataformas objetivo incluyen XP/7/8/10/11, Linux, Raspberry y un primer perfil ESP32; requieren builds y comprobaciones por plataforma.
>
> El ETL puede estar en un cliente o servidor EDC. El motor ASTM genérico y los plugins por fabricante/modelo son C#. El motor recibe un canal binario bidireccional y eventos, tanto desde el túnel serial como desde TCP nativo. El agente conserva independencia del protocolo del equipo.
>
> El motor implementa mecanismos genéricos de conversación y framing; el plugin adapta el dialecto, correlaciona órdenes/resultados y evalúa recuperación por proceso. Application coordina los casos de uso y Domain conserva las reglas LIMS. Los adaptadores técnicos pertenecen a Infrastructure; la configuración, monitor y herramientas de trazas pertenecen a Presentation.
>
> El semáforo es un permiso exclusivo renovable, con identidad y generación para rechazar controladores anteriores. La cola contiene solicitudes de control. El cambio de propietario debe exponer el estado de bytes ya escritos/pendientes; no implica por sí solo continuidad de la conversación ASTM.
>
> Las herramientas transversales registran bytes, eventos, mensajes, operaciones y checkpoints correlacionados. La recuperación depende del instrumento y de cada operación. Hay operaciones reversibles, irreversibles o que no conviene revertir dentro de una misma traza. No imponer una política global de cancelar/repetir ni reinyectar automáticamente una traza al instrumento. Reproducción diagnóstica y recuperación operativa son modos distintos.
>
> Los ensamblados del sistema legacy referenciados, según informa el usuario, son desarrollos en uso productivo. Su código es legacy y de baja calidad; extraer comportamiento y conocimiento de negocio, sin adoptar automáticamente su estructura o anomalías. REFERENCIAS-LEGACY y manifiesto local de referencias contienen rutas originales, código decompilado, métodos y hashes.
>
> Conservar procedencia por regla migrada y decidir explícitamente conservar/corregir/retirar. En Architect hay una modificación previa del C# decompilado respecto del manifiesto; contrastar el ensamblado original. El host de interfaces legacy conserva ofuscación residual. Los catálogos recuperados son de febrero de 2026 y el esquema más reciente es de octubre; no asumir que los valores de configuración quedaron actualizados.
>
> Empezar por una funcionalidad acotada con simulador y trazas sintéticas, después caracterizar Architect y Ruby. No ejecutar órdenes sobre instrumentos productivos para validar la propuesta. Documentar resultados y compatibilidad realmente probados. Esta carpeta entrega una propuesta, no una implementación ya validada.
