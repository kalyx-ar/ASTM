using System.Text.Json.Serialization;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Model;

namespace Astm.Runtime.Tracing;

// Closed metadata used by traces, diagnostic custody and NativeAOT consumers. The wire format
// keeps the previous camelCase/null/enum behavior; no reflection fallback is enabled.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TraceHeader))]
[JsonSerializable(typeof(TraceLine))]
[JsonSerializable(typeof(InstrumentObservationBatch))]
[JsonSerializable(typeof(InstrumentObservation))]
[JsonSerializable(typeof(QualityResourceReference))]
[JsonSerializable(typeof(AstmDiagnostic))]
public partial class AstmJsonContext : JsonSerializerContext;
