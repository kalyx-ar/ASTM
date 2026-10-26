# ATSM

Neutral integration of clinical laboratory instruments: PortGate (a protocol-agnostic serial ↔ TCP agent, administered by ATSM), a generic engine for the laboratory protocols (CLSI LIS01-A2 / LIS02-A2, a.k.a. ASTM E1381 / E1394; HL7 v2 MLLP) and vendor plugins. Any LIS/LIMS hosts it by implementing one port, `ILabWorkflowHost`.

Agents: [AGENTS.md](AGENTS.md) · pending work: [Documentacion/CONTINUACION.md](Documentacion/CONTINUACION.md) · documentation (Spanish): [Documentacion/README.md](Documentacion/README.md) · status: [Documentacion/ESTADO.md](Documentacion/ESTADO.md) · wire protocol: [spec/link-v1.md](spec/link-v1.md).

Aplicación gráfica MAUI (entrega Windows autónoma): [diagnóstico de mantenimiento](Documentacion/DIAGNOSTICO-MANTENIMIENTO.md), con catálogo integrado, lectura de logs/exportaciones y captura TCP/SerialTunnel. Incluye Bio-Rad CFX (CSV/RDML), Sysmex U-WAM/XN-L/CS-2500, QWALYS y Abbott; cada perfil declara su evidencia y límites, sin atribuir homologación en hardware.

## Layout

```text
dotnet/      engine, channels, runtime, plugin SDK, plugins, tools and tests (net10.0)
PortGate/   portgate agent, C++17 (Windows, POSIX; ESP32 planned)
spec/        normative wire formats
conformance/ vectors shared by the C++ and .NET implementations
```

## Build and test

```bash
dotnet test dotnet/Atsm.slnx
```

```bash
cmake -S PortGate -B PortGate/build/win-x64 -G "Visual Studio 18 2026" -A x64
```

```bash
cmake --build PortGate/build/win-x64 --config Release
```

```bash
ctest --test-dir PortGate/build/win-x64 -C Release
```

The first CMake configure downloads Mbed TLS 4.1.1 and checks its SHA-256 (offline: `-DPORTGATE_MBEDTLS_SOURCE_DIR=<folder>`; without TLS: `-DPORTGATE_ENABLE_TLS=OFF`). The .NET suite also runs interoperability tests (plain and mutual TLS) against `portgate` when it has been built (or when `PORTGATE_EXE` points to it); otherwise they are reported as skipped.

## Data

Instrument traces may contain patient data. Real traces and vendor manuals are kept out of version control (`Documentacion/trazas_externas/`, `Documentacion/manuales/`); only anonymized corpora are published. Use `atsm-trace anonymize` and review its report before sharing a trace.

Distribución verificada: la herramienta estándar .NET; ejecutable `dotnet/tools/Atsm.Diagnostics.Maui/bin/Release/atsm-diagnostics.exe`. [MAUI y NativeAOT](Documentacion/MAUI-Y-NATIVEAOT.md): WPF retirado, CLI NativeAOT y catálogo compilado; carga en caliente sólo en EDC.

Mejoras de Diagnostics: interpretación por defecto, grupos completos, detección de logs, columnas automáticas/redimensionables, equipos con configuraciones internas y Lang opcional. Ver [usabilidad](Documentacion/USABILIDAD-DIAGNOSTICS.md).
