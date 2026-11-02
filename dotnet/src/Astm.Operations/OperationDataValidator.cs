using System.Text.Json;

namespace Astm.Operations;

/// <summary>Side-effect-free structural validation. Issues contain schema paths/codes, never input values.
/// The instrument binding remains authoritative for semantic and protocol constraints.</summary>
public static class OperationDataValidator
{
    private const int MaxDepth = 64, MaxIssues = 100;

    public static IReadOnlyList<OperationValidationIssue> ValidateSchema(OperationDataContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var issues = new List<OperationValidationIssue>();
        if (contract.Reference is not { } reference || string.IsNullOrWhiteSpace(reference.Id) || reference.Major < 1 || reference.Minor < 0)
            Add("schema.reference", "$");
        Check(contract.Root, "$", 0, new(ReferenceEqualityComparer.Instance));
        return issues.AsReadOnly();

        void Add(string code, string path) { if (issues.Count < MaxIssues) issues.Add(new(code, path)); }
        void Check(OperationDataSchema? schema, string path, int depth, HashSet<OperationDataSchema> parents)
        {
            if (issues.Count >= MaxIssues) return;
            if (schema is null) { Add("schema.missing", path); return; }
            if (depth > MaxDepth || !parents.Add(schema)) { Add("schema.recursion", path); return; }
            if (!Enum.IsDefined(schema.Kind) || schema.Kind == OperationDataKind.Unspecified ||
                schema.MinLength is < 0 || schema.MaxLength is < 0 || schema.MinItems is < 0 || schema.MaxItems is < 0 ||
                schema.MinLength > schema.MaxLength || schema.MinItems > schema.MaxItems)
                Add("schema.constraint", path);
            if (schema.Fields is null || schema.AllowedValues is null)
            { Add("schema.missing", path); parents.Remove(schema); return; }
            if (schema.Kind != OperationDataKind.Object && (schema.Fields.Count > 0 || schema.AllowAdditionalProperties) ||
                schema.Kind != OperationDataKind.Array && (schema.Items is not null || schema.MinItems is not null || schema.MaxItems is not null) ||
                schema.Kind != OperationDataKind.String && (schema.MinLength is not null || schema.MaxLength is not null || schema.AllowedValues.Count > 0))
                Add("schema.constraint-kind", path);
            if (schema.AllowedValues.Any(v => v is null)) Add("schema.code", path);
            if (schema.Kind == OperationDataKind.Array && schema.Items is null) Add("schema.items-required", path);
            foreach (var (name, field) in schema.Fields)
            {
                if (!ValidPathPart(name)) { Add("schema.field", path); continue; }
                var child = path + "." + name;
                if (field is null) { Add("schema.missing", child); continue; }
                if (!Enum.IsDefined(field.Requirement) || !Enum.IsDefined(field.Origin)) Add("schema.field", child);
                if ((field.Requirement == OperationFieldRequirement.Conditional) != (field.RequiredWhen is not null))
                    Add("schema.condition", child);
                if (field.RequiredWhen is { } condition && (condition.EqualsValue is null || !IsStringPath(contract.Root, condition.Path)))
                    Add("schema.condition-path", child);
                Check(field.Schema, child, depth + 1, parents);
                if (issues.Count >= MaxIssues) break;
            }
            if (schema.Items is { } items) Check(items, path + "[]", depth + 1, parents);
            parents.Remove(schema);
        }
    }

    public static IReadOnlyList<OperationValidationIssue> Validate(OperationDataContract contract, JsonElement input,
        OperationValidationPhase phase = OperationValidationPhase.Input)
    {
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        var schemaIssues = ValidateSchema(contract);
        if (schemaIssues.Count > 0) return schemaIssues;
        var issues = new List<OperationValidationIssue>();
        Visit(contract.Root, input, "$", 0);
        return issues.AsReadOnly();

        void Add(string code, string path) { if (issues.Count < MaxIssues) issues.Add(new(code, path)); }
        void Visit(OperationDataSchema schema, JsonElement value, string path, int depth)
        {
            if (issues.Count >= MaxIssues) return;
            if (depth > MaxDepth) { Add("data.depth", path); return; }
            if (value.ValueKind == JsonValueKind.Null && schema.Nullable) return;
            var matches = schema.Kind switch
            {
                OperationDataKind.Object => value.ValueKind == JsonValueKind.Object,
                OperationDataKind.Array => value.ValueKind == JsonValueKind.Array,
                OperationDataKind.String => value.ValueKind == JsonValueKind.String,
                OperationDataKind.Number => value.ValueKind == JsonValueKind.Number,
                OperationDataKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                OperationDataKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false,
            };
            if (!matches) { Add("data.type", path); return; }
            if (schema.Kind == OperationDataKind.String)
            {
                var text = value.GetString()!;
                var length = 0;
                foreach (var unused in text.EnumerateRunes()) length++;
                if (schema.MinLength is { } min && length < min || schema.MaxLength is { } max && length > max) Add("data.length", path);
                if (schema.AllowedValues.Count > 0 && !schema.AllowedValues.Contains(text, StringComparer.Ordinal)) Add("data.code", path);
            }
            if (schema.Kind == OperationDataKind.Array)
            {
                var count = value.GetArrayLength();
                if (schema.MinItems is { } min && count < min || schema.MaxItems is { } max && count > max) Add("data.cardinality", path);
                var index = 0;
                foreach (var item in value.EnumerateArray())
                { Visit(schema.Items!, item, path + "[" + index++ + "]", depth + 1); if (issues.Count >= MaxIssues) break; }
            }
            if (schema.Kind != OperationDataKind.Object) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                // Unknown names may contain sensitive data; do not echo them in diagnostics.
                if (!seen.Add(property.Name)) Add("data.duplicate-field", path);
                if (!schema.AllowAdditionalProperties && !schema.Fields.ContainsKey(property.Name)) Add("data.unknown-field", path);
                if (issues.Count >= MaxIssues) break;
            }
            foreach (var (name, field) in schema.Fields)
            {
                if (issues.Count >= MaxIssues) break;
                var child = path + "." + name;
                var present = value.TryGetProperty(name, out var member);
                if (field.Requirement == OperationFieldRequirement.Forbidden)
                { if (present) Add("data.forbidden", child); continue; }
                var managedInput = phase == OperationValidationPhase.Input && field.Origin != OperationFieldOrigin.Input;
                if (managedInput && !field.AllowOverride)
                { if (present) Add("data.managed-field", child); continue; }
                var required = field.Requirement == OperationFieldRequirement.Required;
                if (field.RequiredWhen is { } condition && !managedInput)
                {
                    if (!TryString(input, condition.Path, out var selector)) Add("data.condition-unresolved", child);
                    else required = string.Equals(selector, condition.EqualsValue, StringComparison.Ordinal);
                }
                if (!present) { if (required && !managedInput) Add("data.required", child); continue; }
                Visit(field.Schema, member, child, depth + 1);
            }
        }
    }

    private static bool ValidPathPart(string part) => !string.IsNullOrWhiteSpace(part) &&
        !part.Any(c => char.IsControl(c) || c is '.' or '[' or ']' or '$');

    private static bool IsStringPath(OperationDataSchema? root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var part in path.Split('.'))
        {
            if (!ValidPathPart(part) || root is null || root.Kind != OperationDataKind.Object ||
                root.Fields is null || !root.Fields.TryGetValue(part, out var field) || field is null) return false;
            root = field.Schema;
        }
        return root?.Kind == OperationDataKind.String;
    }

    private static bool TryString(JsonElement root, string path, out string? value)
    {
        value = null;
        foreach (var part in path.Split('.'))
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(part, out root)) return false;
        if (root.ValueKind != JsonValueKind.String) return false;
        value = root.GetString(); return true;
    }
}
