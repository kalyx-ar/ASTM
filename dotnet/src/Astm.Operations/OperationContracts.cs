using System.Text.Json.Serialization;

namespace Astm.Operations;

public enum OperationKind { Command, Query, Event, Import }
[Flags]
public enum OperationCapabilities { None = 0, Prepare = 1, Decode = 2, Execute = 4, Observe = 8, Cancel = 16 }
public enum OperationCompletionScope { MessageExchange, ApplicationAcceptance, DeviceExecution, ContentImport }
public enum OperationFieldOrigin { Input, HostContext, Configuration, Generated, Fixed, Derived }
public enum OperationFieldRequirement { Optional, Required, Conditional, Forbidden }
public enum OperationDataKind { Unspecified, Object, Array, String, Number, Integer, Boolean }
public enum OperationValidationPhase { Input, Prepared }

public sealed record OperationContractReference(string Id, int Major, int Minor = 0);
/// <summary>Initial portable condition: equality to a string at an absolute dot-separated input path.</summary>
public sealed record OperationFieldCondition(string Path, string EqualsValue);
public sealed record OperationDataField(OperationDataSchema Schema,
    OperationFieldRequirement Requirement = OperationFieldRequirement.Optional,
    OperationFieldOrigin Origin = OperationFieldOrigin.Input, bool AllowOverride = false,
    OperationFieldCondition? RequiredWhen = null);

/// <summary>Structural subset, not a full JSON Schema implementation. Integer is signed Int64.
/// Additional metadata (wire encoding, code bindings, complex conditions) belongs to subsequent bindings.</summary>
public sealed record OperationDataSchema(OperationDataKind Kind)
{
    public bool Nullable { get; init; }
    public IReadOnlyDictionary<string, OperationDataField> Fields { get; init; } = new Dictionary<string, OperationDataField>();
    public OperationDataSchema? Items { get; init; }
    public bool AllowAdditionalProperties { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }
    public IReadOnlyList<string> AllowedValues { get; init; } = [];
}

public sealed record OperationDataContract(OperationContractReference Reference, OperationDataSchema Root);
public sealed record OperationDefinition(string Id, int Major, OperationKind Kind,
    OperationContractReference? Input, OperationContractReference? Output, OperationCapabilities Capabilities,
    OperationCompletionScope CompletionScope, string ProfileId, string ProfileRevision, IReadOnlyList<string> Evidence);
public sealed record OperationValidationIssue(string Code, string Path);
public interface IOperationCatalog
{
    IReadOnlyList<OperationDefinition> Describe(string profileId);
    OperationDataContract Resolve(OperationContractReference reference);
}

/// <summary>Typed semantic preparation only; it does not transmit, reserve a device or prove acceptance.
/// The binding must snapshot mutable input/metadata before returning; this envelope cannot deep-freeze TInput.</summary>
public sealed record PreparedOperation<TInput>(Guid OperationId, OperationDefinition Definition, TInput Input);
public interface IOperationBinding<TInput>
{
    OperationDefinition Definition { get; }
    IReadOnlyList<OperationValidationIssue> Validate(TInput input);
    PreparedOperation<TInput> Prepare(Guid operationId, TInput input);
}

// Metadata is generated for dynamic ETL/NativeAOT callers; no runtime loading of arbitrary CLR types.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(OperationDefinition))]
[JsonSerializable(typeof(OperationDataContract))]
[JsonSerializable(typeof(OperationValidationIssue[]))]
public partial class OperationJsonContext : JsonSerializerContext;
