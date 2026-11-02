using Astm.Operations;

namespace Astm.Formats.Medical;

/// <summary>Shared shapes; bindings add constraints and verify dates/encoding/correlation before generating wire data.</summary>
public static class MedicalDataContracts
{
    public static readonly OperationContractReference Orders = new("medical.lab.orders", 1),
        Answer = new("medical.lab.worklist-answer", 1), Decoded = new("medical.lab.decoded", 1, 1),
        InventoryRequest = new("medical.lab.inventory-request", 1),
        Content = new("medical.lab.content", 1);
    private static OperationDataSchema Text(bool nullable = false) => new(OperationDataKind.String) { Nullable = nullable };
    private static OperationDataField Field(OperationDataSchema s, bool required = false) => new(s, required ? OperationFieldRequirement.Required : OperationFieldRequirement.Optional);
    private static OperationDataSchema Obj(params (string, OperationDataField)[] fields) => new(OperationDataKind.Object)
        { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal) };
    private static OperationDataSchema List(OperationDataSchema items, int min = 0) => new(OperationDataKind.Array) { Items = items, MinItems = min };
    private static OperationDataSchema Code() => Obj(("code", Field(new(OperationDataKind.String) { MinLength = 1 }, true)), ("display", Field(Text())), ("system", Field(Text(), true)));
    private static OperationDataSchema CodeSet() => Obj(("vendor", Field(Code(), true)), ("mapped", Field(List(Code()), true)));
    public static OperationDataSchema Order(MedicalOperationRules rules)
    {
        var patient = new OperationDataSchema(OperationDataKind.Object)
        {
            Nullable = true,
            Fields = new[] { "id", "familyName", "givenName", "birthDate", "administrativeSex", "physician", "location" }
                .ToDictionary(n => n, n => new OperationDataField(Text(), rules.PatientFields.Contains(n)
                    ? OperationFieldRequirement.Optional : OperationFieldRequirement.Forbidden)),
        };
        var actions = rules.OrderActions.Select(a => a.ToString()).ToArray();
        return Obj(("orderId", Field(new(OperationDataKind.String) { MinLength = 1 }, true)),
            ("specimen", Field(Obj(("specimenId", Field(new(OperationDataKind.String) { MinLength = 1 }, true)),
                ("containerId", Field(Text())), ("type", Field(Text())), ("position", Field(Text())),
                ("role", Field(new(OperationDataKind.String) { AllowedValues = ["Unknown", "Patient", "Control", "Calibrator"] }))), true)),
            ("patient", Field(patient)), ("tests", Field(List(CodeSet(), 1), true)),
            ("priority", Field(new(OperationDataKind.String) { AllowedValues = ["Routine", "Stat"] })),
            ("action", Field(new(OperationDataKind.String) { AllowedValues = actions })),
            ("collectedAt", Field(Text())), ("specimenType", Field(Text())));
    }
    public static IReadOnlyList<OperationDataContract> Create(MedicalOperationRules rules, string format = "lis02")
    {
        var orders = List(Order(rules), 1);
        var query = Obj(("queryId", Field(Text(), true)), ("instrument", Field(Instrument(), true)),
            ("specimens", Field(List(Specimen()), true)), ("scope", Field(Text(), true)), ("trace", Field(Trace(), true)));
        return [new(Orders, Obj(("orders", Field(orders, true)))),
            new(Answer, Obj(("query", Field(query, true)), ("originalMessage", new(Text(), format == "hl7v2" ? OperationFieldRequirement.Required : OperationFieldRequirement.Forbidden)), ("answer", Field(Obj(
                ("state", Field(new(OperationDataKind.String) { AllowedValues = ["Orders", "NoInformation", "Unavailable"] }, true)),
                ("orders", Field(List(Order(rules)), true))), true)))),
            new(Decoded, Obj(("observations", Field(List(Observation()), true)), ("queries", Field(List(query), true)),
                ("diagnostics", Field(List(Diagnostic()), true)), ("qualityResources", Field(List(QualityResource()), true)),
                ("calibrationReports", Field(List(Calibration()), true)), ("inventoryStatuses", Field(List(Inventory()), true)))),
            new(Content, new(OperationDataKind.String))];
    }
    private static OperationDataSchema Instrument() => Obj(("instrumentId", Field(Text(), true)), ("manufacturer", Field(Text())),
        ("model", Field(Text())), ("serialNumber", Field(Text())), ("softwareVersion", Field(Text())), ("udi", Field(Text())));
    private static OperationDataSchema Specimen() => Obj(("specimenId", Field(Text(), true)), ("containerId", Field(Text())),
        ("type", Field(Text())), ("position", Field(Text())), ("role", Field(Text(), true)));
    private static OperationDataSchema Trace() => Obj(("traceId", Field(Text(), true)),
        ("firstSequence", Field(new(OperationDataKind.Integer), true)), ("lastSequence", Field(new(OperationDataKind.Integer), true)));
    private static OperationDataSchema Diagnostic() => Obj(("code", Field(Text(), true)), ("severity", Field(Text(), true)),
        ("message", Field(Text(), true)), ("location", Field(Text())));
    private static OperationDataSchema Value() => Obj(("rawText", Field(Text(), true)), ("numeric", Field(new(OperationDataKind.Number))),
        ("comparator", Field(Text())), ("coded", Field(Code())), ("rawUnit", Field(Text())), ("unit", Field(Code())));
    private static OperationDataSchema Map() => new(OperationDataKind.Object) { AllowAdditionalProperties = true };
    private static OperationDataSchema Calibration() => Obj(("reportId", Field(Text(), true)), ("instrument", Field(Instrument(), true)),
        ("assay", Field(Code(), true)), ("dilution", Field(Text())), ("moduleId", Field(Text(), true)),
        ("systemSerial", Field(Text(), true)), ("performedAtRaw", Field(Text(), true)), ("instrumentStatus", Field(Text(), true)),
        ("successful", Field(new(OperationDataKind.Boolean), true)), ("contentBase64", Field(Text(), true)),
        ("contentSha256", Field(Text(), true)), ("text", Field(Text())),
        ("materials", Field(List(Obj(("kind", Field(Text(), true)), ("identifier", Field(Text(), true)), ("lot", Field(Text())),
            ("level", Field(Text())), ("expirationRaw", Field(Text())), ("typeRaw", Field(Text())))), true)),
        ("reagents", Field(List(Obj(("lot", Field(Text())), ("serial", Field(Text())), ("kind", Field(Text())))), true)),
        ("trace", Field(Trace(), true)), ("sourceFields", Field(Map(), true)));
    private static OperationDataSchema Inventory() => Obj(("statusId", Field(Text(), true)), ("instrument", Field(Instrument(), true)),
        ("moduleId", Field(Text(), true)), ("reportedAtRaw", Field(Text(), true)), ("substanceIdentifier", Field(Text(), true)),
        ("substanceType", Field(Text(), true)), ("statuses", Field(List(Code()), true)), ("calibrationStates", Field(List(Text()), true)),
        ("assays", Field(List(Code()), true)), ("reagentLot", Field(Text())), ("reagentSerial", Field(Text())),
        ("trace", Field(Trace(), true)), ("sourceFields", Field(Map(), true)));
    private static OperationDataSchema QualityResource() => Obj(("resourceId", Field(Text(), true)), ("instrument", Field(Instrument(), true)),
        ("kind", Field(Text(), true)), ("rawLocator", Field(Text(), true)), ("sourceField", Field(Text(), true)), ("trace", Field(Trace(), true)));
    private static OperationDataSchema Quality() => Obj(
        ("material", Field(Obj(("kind", Field(Text(), true)), ("identifier", Field(Text(), true)), ("lot", Field(Text())),
            ("level", Field(Text())), ("expirationRaw", Field(Text())), ("typeRaw", Field(Text()))))),
        ("moduleId", Field(Text())), ("runId", Field(Text())), ("method", Field(Text())),
        ("calibration", Field(Obj(("assayCode", Field(Text(), true)), ("reagentLot", Field(Text())), ("performedAtRaw", Field(Text(), true))))),
        ("statistics", Field(List(Obj(("kind", Field(Text(), true)), ("value", Field(Value(), true)), ("scope", Field(Text(), true)),
            ("sourceField", Field(Text(), true)), ("groupId", Field(Text())))), true)), ("sourceFields", Field(Map(), true)));
    private static OperationDataSchema Observation() => Obj(("observationId", Field(Text(), true)), ("instrument", Field(Instrument(), true)),
        ("specimen", Field(Specimen(), true)), ("test", Field(CodeSet(), true)), ("value", Field(Value(), true)),
        ("referenceRangeText", Field(Text())), ("flags", Field(List(Code()), true)), ("instrumentStatus", Field(Text(), true)),
        ("instrumentStatusMeaning", Field(Text(), true)), ("resultKind", Field(Text())), ("dilution", Field(Text())),
        ("resultTime", Field(Text())), ("operator", Field(Text())),
        ("reagents", Field(List(Obj(("lot", Field(Text())), ("serial", Field(Text())), ("kind", Field(Text())))), true)),
        ("comments", Field(List(Text()), true)), ("vendorIdentity", Field(Obj(("values", Field(Map(), true))), true)),
        ("trace", Field(Trace(), true)), ("quality", Field(Quality())));
}
