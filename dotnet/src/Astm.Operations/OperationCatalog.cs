using System.Collections.Frozen;

namespace Astm.Operations;

public sealed class OperationCompatibilityException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

/// <summary>Validated, deeply owned metadata. Selection is explicit: no silent schema conversion or minor-version guess.</summary>
public sealed class OperationCatalog : IOperationCatalog
{
    private readonly IReadOnlyDictionary<OperationContractReference, OperationDataContract> _contracts;
    private readonly IReadOnlyList<OperationDefinition> _definitions;
    public OperationCatalog(IEnumerable<OperationDefinition> definitions, IEnumerable<OperationDataContract> contracts)
    {
        var copy = new Dictionary<OperationContractReference, OperationDataContract>();
        foreach (var contract in contracts)
        {
            if (OperationDataValidator.ValidateSchema(contract).Count > 0) throw new ArgumentException("operation.schema.invalid");
            if (!copy.TryAdd(contract.Reference, contract with { Root = Freeze(contract.Root) })) throw new ArgumentException("operation.schema.duplicate");
        }
        _contracts = copy.ToFrozenDictionary();
        var owned = new List<OperationDefinition>();
        foreach (var d in definitions)
        {
            if (string.IsNullOrWhiteSpace(d.Id) || d.Major < 1 || string.IsNullOrWhiteSpace(d.ProfileId) ||
                string.IsNullOrWhiteSpace(d.ProfileRevision) || !Enum.IsDefined(d.Kind) || !Enum.IsDefined(d.CompletionScope) ||
                !Enum.IsDefined(d.Initiator) || d.Capabilities == OperationCapabilities.None ||
                (d.Capabilities & ~(OperationCapabilities.Prepare | OperationCapabilities.Decode | OperationCapabilities.Execute | OperationCapabilities.Observe | OperationCapabilities.Cancel)) != 0 ||
                d.Evidence is null || d.Evidence.Count == 0 || d.Evidence.Any(string.IsNullOrWhiteSpace) || d.WireBindings is null ||
                d.Input is { } input && !_contracts.ContainsKey(input) || d.Output is { } output && !_contracts.ContainsKey(output) ||
                d.Capabilities.HasFlag(OperationCapabilities.Prepare) && d.Input is null ||
                d.Capabilities.HasFlag(OperationCapabilities.Execute) && !d.Capabilities.HasFlag(OperationCapabilities.Prepare))
                throw new ArgumentException("operation.definition.invalid");
            if (owned.Any(o => o.Id == d.Id && o.ProfileId == d.ProfileId && o.Major == d.Major)) throw new ArgumentException("operation.definition.duplicate");
            owned.Add(d with { Evidence = Array.AsReadOnly(d.Evidence.ToArray()), WireBindings = d.WireBindings.ToFrozenDictionary(StringComparer.Ordinal) });
        }
        _definitions = owned.AsReadOnly();
    }
    public IReadOnlyList<OperationDefinition> Describe(string profileId) => Array.AsReadOnly(_definitions.Where(d => d.ProfileId == profileId).ToArray());
    public OperationDataContract Resolve(OperationContractReference reference) => _contracts.TryGetValue(reference, out var contract)
        ? contract : throw new OperationCompatibilityException("operation.contract.unsupported");
    public OperationDefinition Select(string profileId, string revision, string operationId, int major,
        OperationContractReference? input, OperationCapabilities required)
    {
        var found = _definitions.SingleOrDefault(d => d.ProfileId == profileId && d.Id == operationId && d.Major == major)
            ?? throw new OperationCompatibilityException("operation.unsupported");
        if (found.ProfileRevision != revision) throw new OperationCompatibilityException("operation.profile.changed");
        if (found.Input != input) throw new OperationCompatibilityException("operation.contract.unsupported");
        if ((found.Capabilities & required) != required) throw new OperationCompatibilityException("operation.capability.unsupported");
        return found;
    }
    private static OperationDataSchema Freeze(OperationDataSchema s) => s with
    {
        AllowedValues = Array.AsReadOnly(s.AllowedValues.ToArray()), Items = s.Items is { } i ? Freeze(i) : null,
        Fields = s.Fields.ToFrozenDictionary(p => p.Key, p => p.Value with { Schema = Freeze(p.Value.Schema) }, StringComparer.Ordinal),
    };
}
