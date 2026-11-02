using System.Text.Json;
using System.Text.Json.Serialization;
using Astm.Operations;

namespace Astm.Tests.Operations;

public class OperationContractTests
{
    private static readonly OperationDataSchema Text = new(OperationDataKind.String) { MinLength = 1, MaxLength = 16 };
    private static OperationDataContract Recipe() => new(new("example.thermal.recipe", 1), new(OperationDataKind.Object)
    {
        Fields = new Dictionary<string, OperationDataField>
        {
            ["mode"] = new(new(OperationDataKind.String) { AllowedValues = ["production", "calibration"] }, OperationFieldRequirement.Required),
            ["batchId"] = new(Text, OperationFieldRequirement.Conditional, RequiredWhen: new("mode", "production")),
            ["setpoint"] = new(new(OperationDataKind.Number), OperationFieldRequirement.Required),
            ["channels"] = new(new(OperationDataKind.Array) { Items = new(OperationDataKind.Integer), MinItems = 1, MaxItems = 2 }),
            ["note"] = new(Text with { Nullable = true }),
            ["requestId"] = new(Text, OperationFieldRequirement.Required, OperationFieldOrigin.Generated),
            ["operatorId"] = new(Text, OperationFieldRequirement.Required, OperationFieldOrigin.HostContext, AllowOverride: true),
            ["rawCommand"] = new(Text, OperationFieldRequirement.Forbidden),
        },
    });

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static IReadOnlyList<OperationValidationIssue> Validate(string json, OperationValidationPhase phase = OperationValidationPhase.Input) =>
        OperationDataValidator.Validate(Recipe(), Json(json), phase);

    [Fact]
    public void A_nonmedical_host_can_discover_validate_and_prepare_a_typed_operation_without_a_transport()
    {
        var binding = new ThermalBinding();
        IOperationCatalog catalog = binding;
        var operation = Assert.Single(catalog.Describe("oven.simulated"));
        Assert.Equal(Recipe().Reference, operation.Input);
        Assert.Empty(OperationDataValidator.ValidateSchema(catalog.Resolve(operation.Input!)));
        var input = new ThermalRecipe("production", "B-1", 37.5m);
        Assert.Empty(binding.Validate(input));
        var operationId = Guid.NewGuid();
        var prepared = binding.Prepare(operationId, input);
        Assert.Equal(operationId, prepared.OperationId);
        Assert.Equal(input, prepared.Input);
        Assert.Equal("1", prepared.Definition.ProfileRevision);
        Assert.Equal(OperationCapabilities.Prepare, prepared.Definition.Capabilities);
        Assert.Throws<ArgumentException>(() => binding.Prepare(operationId, input with { BatchId = null }));
        Assert.Throws<ArgumentException>(() => binding.Prepare(Guid.Empty, input));
        Assert.Throws<KeyNotFoundException>(() => catalog.Resolve(operation.Input! with { Major = 2 }));
        Assert.Empty(catalog.Describe("unknown-profile"));
    }

    [Theory]
    [InlineData("{}", "data.required", "$.mode")]
    [InlineData("{\"mode\":\"production\",\"setpoint\":1}", "data.required", "$.batchId")]
    [InlineData("{\"mode\":\"calibration\",\"setpoint\":null}", "data.type", "$.setpoint")]
    [InlineData("{\"mode\":\"production\",\"batchId\":\"\",\"setpoint\":1}", "data.length", "$.batchId")]
    [InlineData("{\"mode\":\"other\",\"setpoint\":1}", "data.code", "$.mode")]
    [InlineData("{\"mode\":\"calibration\",\"setpoint\":\"1\"}", "data.type", "$.setpoint")]
    [InlineData("{\"mode\":\"calibration\",\"setpoint\":1,\"rawCommand\":null}", "data.forbidden", "$.rawCommand")]
    public void Invalid_data_reports_contract_paths_without_values(string json, string code, string path)
    {
        Assert.Contains(new OperationValidationIssue(code, path), Validate(json));
    }

    [Theory]
    [InlineData("production", true)]
    [InlineData("calibration", false)]
    public void Conditional_requirements_are_resolved_from_the_root(string mode, bool requiresBatch)
    {
        var issues = Validate("{\"mode\":\"" + mode + "\",\"setpoint\":1}");
        Assert.Equal(requiresBatch, issues.Any(i => i.Code == "data.required" && i.Path == "$.batchId"));
        if (!requiresBatch) Assert.Empty(issues);
        Assert.Empty(Validate("{\"mode\":\"" + mode + "\",\"batchId\":\"B-1\",\"setpoint\":1}"));
    }

    [Fact]
    public void An_unresolved_selector_does_not_silently_disable_a_condition()
    {
        Assert.Contains(Validate("{\"setpoint\":1,\"batchId\":\"B-1\"}"), i => i.Code == "data.condition-unresolved");
        Assert.Contains(Validate("{\"mode\":null,\"setpoint\":1}"), i => i.Code == "data.condition-unresolved");
    }

    [Fact]
    public void Required_presence_nullability_and_empty_text_are_different_rules()
    {
        var fields = new Dictionary<string, OperationDataField> { ["value"] = new(Text with { Nullable = true }, OperationFieldRequirement.Required) };
        var schema = new OperationDataContract(new("example.nullable", 1), new(OperationDataKind.Object) { Fields = fields });
        Assert.Contains(OperationDataValidator.Validate(schema, Json("{}")), i => i.Code == "data.required");
        Assert.Empty(OperationDataValidator.Validate(schema, Json("{\"value\":null}")));
        Assert.Contains(OperationDataValidator.Validate(schema, Json("{\"value\":\"\"}")), i => i.Code == "data.length");
        Assert.Empty(Validate("{\"mode\":\"calibration\",\"setpoint\":1,\"note\":null}"));
    }

    [Fact]
    public void Input_cannot_forge_generated_data_and_prepared_data_must_include_managed_requirements()
    {
        const string input = "{\"mode\":\"calibration\",\"setpoint\":1}";
        Assert.Empty(Validate(input));
        Assert.Contains(Validate(input, OperationValidationPhase.Prepared), i => i.Code == "data.required" && i.Path == "$.requestId");
        Assert.Contains(Validate(input, OperationValidationPhase.Prepared), i => i.Code == "data.required" && i.Path == "$.operatorId");
        const string prepared = "{\"mode\":\"calibration\",\"setpoint\":1,\"requestId\":\"R-1\",\"operatorId\":\"O-1\"}";
        Assert.Empty(Validate(prepared, OperationValidationPhase.Prepared));
        Assert.Contains(Validate(prepared), i => i.Code == "data.managed-field" && i.Path == "$.requestId");
        Assert.Empty(Validate("{\"mode\":\"calibration\",\"setpoint\":1,\"operatorId\":\"O-1\"}"));
        Assert.Contains(Validate("{\"mode\":\"calibration\",\"setpoint\":1,\"operatorId\":\"\"}"), i => i.Code == "data.length");
        Assert.Throws<ArgumentOutOfRangeException>(() => Validate(input, (OperationValidationPhase)999));
    }

    [Theory]
    [InlineData("[]", "data.cardinality")]
    [InlineData("[1,2,3]", "data.cardinality")]
    [InlineData("[1.5]", "data.type")]
    [InlineData("[9223372036854775808]", "data.type")]
    [InlineData("[null]", "data.type")]
    public void Arrays_keep_cardinality_and_Int64_type_limits(string channels, string code)
    {
        Assert.Contains(Validate("{\"mode\":\"calibration\",\"setpoint\":1,\"channels\":" + channels + "}"), i => i.Code == code);
    }

    [Fact]
    public void Nested_items_use_the_same_schema_and_absolute_conditions()
    {
        var schema = new OperationDataContract(new("example.nested", 1), new(OperationDataKind.Object)
        {
            Fields = new Dictionary<string, OperationDataField>
            {
                ["mode"] = new(Text, OperationFieldRequirement.Required),
                ["jobs"] = new(new(OperationDataKind.Array)
                {
                    Items = new(OperationDataKind.Object) { Fields = new Dictionary<string, OperationDataField>
                    { ["serial"] = new(Text, OperationFieldRequirement.Conditional, RequiredWhen: new("mode", "production")) } },
                }, OperationFieldRequirement.Required),
            },
        });
        Assert.Contains(new OperationValidationIssue("data.required", "$.jobs[0].serial"),
            OperationDataValidator.Validate(schema, Json("{\"mode\":\"production\",\"jobs\":[{}]}")));
        Assert.Empty(OperationDataValidator.Validate(schema, Json("{\"mode\":\"calibration\",\"jobs\":[{}]}")));
    }

    [Fact]
    public void Diagnostics_do_not_echo_unknown_field_names_or_sensitive_values()
    {
        var issues = Validate("{\"mode\":\"calibration\",\"setpoint\":\"SECRET\",\"SENSITIVE-NAME\":\"SECRET\"}");
        Assert.Contains(new OperationValidationIssue("data.unknown-field", "$"), issues);
        Assert.All(issues, i => { Assert.DoesNotContain("SECRET", i.ToString()); Assert.DoesNotContain("SENSITIVE-NAME", i.ToString()); });
        Assert.Contains(Validate("{\"mode\":\"calibration\",\"mode\":\"production\",\"setpoint\":1}"), i => i.Code == "data.duplicate-field");
    }

    [Fact]
    public void Text_lengths_count_Unicode_scalars_and_do_not_claim_wire_byte_limits()
    {
        var schema = new OperationDataContract(new("example.text", 1), Text with { MinLength = 1, MaxLength = 1 });
        Assert.Empty(OperationDataValidator.Validate(schema, Json("\"😀\"")));
        Assert.Contains(OperationDataValidator.Validate(schema, Json("\"😀😀\"")), i => i.Code == "data.length");
    }

    [Fact]
    public void Invalid_contracts_are_reported_before_examining_data()
    {
        var schemas = new OperationDataSchema[]
        {
            new(OperationDataKind.Unspecified), new((OperationDataKind)999),
            new(OperationDataKind.Number) { MinLength = 1 }, new(OperationDataKind.Array),
            Text with { MinLength = 4, MaxLength = 2 }, Text with { Fields = null! },
            Text with { AllowedValues = null! }, new(OperationDataKind.Object) { Fields = new Dictionary<string, OperationDataField> { ["a.b"] = new(Text) } },
            new(OperationDataKind.Object) { Fields = new Dictionary<string, OperationDataField> { ["field"] = null! } },
            new(OperationDataKind.Object) { Fields = new Dictionary<string, OperationDataField> { ["field"] = new(null!) } },
        };
        foreach (var schema in schemas)
        {
            var issues = OperationDataValidator.Validate(new(new("example.invalid", 1), schema), default);
            Assert.NotEmpty(issues); Assert.All(issues, i => Assert.StartsWith("schema.", i.Code));
        }
        Assert.Contains(OperationDataValidator.ValidateSchema(new(new("", 0), Text)), i => i.Code == "schema.reference");
    }

    [Fact]
    public void Invalid_conditions_and_schema_cycles_are_rejected_instead_of_ignored()
    {
        foreach (var field in new OperationDataField[]
        {
            new(Text, OperationFieldRequirement.Conditional),
            new(Text, RequiredWhen: new("mode", "production")),
            new(Text, OperationFieldRequirement.Conditional, RequiredWhen: new("absent", "x")),
            new(Text, OperationFieldRequirement.Conditional, RequiredWhen: new("setpoint", "1")),
        })
        {
            var fields = new Dictionary<string, OperationDataField>(Recipe().Root.Fields) { ["extra"] = field };
            Assert.NotEmpty(OperationDataValidator.ValidateSchema(Recipe() with { Root = Recipe().Root with { Fields = fields } }));
        }
        var cycle = new Dictionary<string, OperationDataField>();
        var root = new OperationDataSchema(OperationDataKind.Object) { Fields = cycle };
        cycle["self"] = new(root);
        Assert.Contains(OperationDataValidator.ValidateSchema(new(new("example.cycle", 1), root)), i => i.Code == "schema.recursion");
    }

    [Fact]
    public void Generated_metadata_round_trips_contract_and_operation_and_rejects_unknown_constraints()
    {
        var encoded = JsonSerializer.Serialize(Recipe(), OperationJsonContext.Default.OperationDataContract);
        var decoded = JsonSerializer.Deserialize(encoded, OperationJsonContext.Default.OperationDataContract)!;
        Assert.Empty(OperationDataValidator.ValidateSchema(decoded));
        Assert.Equal(Validate("{}"), OperationDataValidator.Validate(decoded, Json("{}")));
        var definition = new ThermalBinding().Definition;
        Assert.Equal(definition.Id, JsonSerializer.Deserialize(JsonSerializer.Serialize(definition, OperationJsonContext.Default.OperationDefinition),
            OperationJsonContext.Default.OperationDefinition)!.Id);
        const string unsupported = "{\"reference\":{\"id\":\"example\",\"major\":1},\"root\":{\"kind\":\"String\",\"pattern\":\".*\"}}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(unsupported, OperationJsonContext.Default.OperationDataContract));
        var missingKind = JsonSerializer.Deserialize("{\"reference\":{\"id\":\"example\",\"major\":1},\"root\":{}}", OperationJsonContext.Default.OperationDataContract)!;
        Assert.NotEmpty(OperationDataValidator.ValidateSchema(missingKind));
    }

    [Fact]
    public void The_core_assembly_has_no_clinical_protocol_or_host_dependency()
    {
        Assert.DoesNotContain(typeof(OperationDefinition).Assembly.GetReferencedAssemblies(), a =>
            a.Name!.StartsWith("Astm.", StringComparison.Ordinal) || a.Name.StartsWith("Kalyx.", StringComparison.Ordinal));
    }

    private sealed class ThermalBinding : IOperationBinding<ThermalRecipe>, IOperationCatalog
    {
        public OperationDefinition Definition { get; } = new("industry.thermal.recipe.prepare", 1, OperationKind.Command,
            Recipe().Reference, null, OperationCapabilities.Prepare, OperationCompletionScope.DeviceExecution,
            "oven.simulated", "1", ["Synthetic nonmedical test contract; no physical operation"]);
        public IReadOnlyList<OperationDefinition> Describe(string profileId) => profileId == Definition.ProfileId ? [Definition] : [];
        public OperationDataContract Resolve(OperationContractReference reference) => reference == Recipe().Reference ? Recipe() : throw new KeyNotFoundException();
        public IReadOnlyList<OperationValidationIssue> Validate(ThermalRecipe input) => OperationDataValidator.Validate(Recipe(),
            JsonSerializer.SerializeToElement(input, NeutralTestJsonContext.Default.ThermalRecipe));
        public PreparedOperation<ThermalRecipe> Prepare(Guid operationId, ThermalRecipe input)
        {
            if (operationId == Guid.Empty) throw new ArgumentException("operation.id");
            if (Validate(input).Count > 0) throw new ArgumentException("operation.invalid");
            return new(operationId, Definition, input); // Record consists only of immutable scalars.
        }
    }
}

internal sealed record ThermalRecipe(string Mode, string? BatchId, decimal Setpoint);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ThermalRecipe))]
internal partial class NeutralTestJsonContext : JsonSerializerContext;
