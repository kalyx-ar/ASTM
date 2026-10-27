using System.Text.Json;
using System.Text.Json.Serialization;
using Astm.Tracing;

namespace Astm.Tools.Trace;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(StackEvent))]
[JsonSerializable(typeof(TraceMetrics))]
internal partial class TraceToolJsonContext : JsonSerializerContext;

// anonymize historically emits PascalCase, unlike the camelCase trace/metrics format.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AnonymizationReport))]
internal partial class AnonymizationJsonContext : JsonSerializerContext;
