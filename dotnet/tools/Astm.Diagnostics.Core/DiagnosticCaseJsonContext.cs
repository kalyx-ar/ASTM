using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Astm.Runtime.Tracing;

namespace Astm.Diagnostics;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProtectedCaseJournal.Envelope))]
[JsonSerializable(typeof(ProtectedCaseJournal.Closure))]
internal partial class DiagnosticCaseJsonContext : JsonSerializerContext;

internal static class DiagnosticCaseJson
{
    internal static readonly JsonSerializerOptions Options = new(TraceJson.Options)
    { TypeInfoResolver = JsonTypeInfoResolver.Combine(DiagnosticCaseJsonContext.Default, AstmJsonContext.Default) };
    internal static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
}
