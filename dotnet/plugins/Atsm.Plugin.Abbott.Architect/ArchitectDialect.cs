using System.Globalization;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugin.Abbott.Architect;

/// <summary>
/// ARCHITECT dialect. Field numbers follow the ASI manual tables (CLSI field n.m = record field m).
/// The vendor test code is the assay number; the result type (F, P, I, Favg...) travels separately in
/// <see cref="InstrumentObservation.ResultKind"/> instead of being appended to the code as the legacy plugin did.
/// </summary>
public sealed class ArchitectDialect(DialectContext context, ArchitectDialectOptions options) : IAstmDialect
{
    public static readonly string VendorSystem = CodeSystems.Vendor(ArchitectPlugin.PluginId);

    public const string UnknownRecord = "ATSM-ARC-UNKNOWN-RECORD";
    public const string ResultWithoutOrder = "ATSM-ARC-RESULT-WITHOUT-ORDER";
    public const string InvalidTimestamp = "ATSM-ARC-INVALID-TIMESTAMP";
    public const string QueryCancelled = "ATSM-ARC-QUERY-CANCELLED";

    private static readonly Lis02Delimiters Delimiters = Lis02Delimiters.Default;

    public AstmInterpretation Interpret(Lis02Message message, TraceRef trace)
    {
        var diagnostics = new List<AtsmDiagnostic>();
        var observations = new List<InstrumentObservation>();
        var queries = new List<OrderQuery>();
        var instrument = ReadInstrument(message.Header) ?? context.Instrument;

        OrderState? order = null;
        List<string>? lastComments = null;
        foreach (var record in message.Records)
        {
            switch (record.Type)
            {
                case 'H':
                case 'P':
                case 'L':
                    order = record.Type == 'P' ? null : order;
                    lastComments = null;
                    break;

                case 'O':
                    order = ReadOrder(record);
                    lastComments = null;
                    if (record.Field(26) == "X")
                    {
                        var notPerformed = NotPerformed(instrument, order, record, trace);
                        observations.Add(notPerformed);
                        lastComments = (List<string>)notPerformed.Comments;
                    }
                    break;

                case 'R':
                    if (order is null)
                    {
                        diagnostics.Add(new(ResultWithoutOrder, AtsmSeverity.Warning, "Result record without a preceding order record.", record.Field(2)));
                        break;
                    }
                    var observation = ReadResult(instrument, order, record, trace, diagnostics);
                    observations.Add(observation);
                    lastComments = (List<string>)observation.Comments;
                    break;

                case 'C':
                    // C|seq|source|text|type: attaches to the result or order it follows.
                    lastComments?.Add(record.Text(4));
                    break;

                case 'Q':
                    if (record.Field(13) == "X")
                    {
                        diagnostics.Add(new(QueryCancelled, AtsmSeverity.Information, "Instrument cancelled its previous query.", record.Field(3)));
                        break;
                    }
                    queries.Add(ReadQuery(instrument, record, trace));
                    break;

                default:
                    diagnostics.Add(new(UnknownRecord, AtsmSeverity.Information, $"Record {record.Type} preserved without interpretation.", record.Field(2)));
                    break;
            }
        }
        return new AstmInterpretation(observations, queries, instrument, diagnostics);
    }

    public IReadOnlyList<Lis02Record> BuildQueryAnswer(OrderQuery query, OrderQueryAnswer answer)
    {
        if (answer.State == QueryAnswerState.Orders && answer.Orders.Count > 0)
            return BuildMessage(answer.Orders, reportType: "Q");

        // Negative query response (ASI table 3.5): one Q record with status X per queried specimen.
        var records = new List<Lis02Record> { Header() };
        var sequence = 1;
        foreach (var specimen in query.Specimens)
        {
            records.Add(new Lis02RecordBuilder('Q', Delimiters)
                .SetText(2, (sequence++).ToString(CultureInfo.InvariantCulture))
                .SetComponents(3, null, specimen.SpecimenId)
                .SetComponents(5, null, null, null, query.Scope.Length == 0 ? "ALL" : query.Scope)
                .SetText(13, "X")
                .Build());
        }
        records.Add(Terminator());
        return records;
    }

    public IReadOnlyList<Lis02Record> BuildOrders(IReadOnlyList<WorkOrder> orders) => BuildMessage(orders, reportType: "O");

    private IReadOnlyList<Lis02Record> BuildMessage(IReadOnlyList<WorkOrder> orders, string reportType)
    {
        var records = new List<Lis02Record> { Header() };
        var patientSequence = 1;
        foreach (var group in orders.GroupBy(o => o.Specimen.SpecimenId))
        {
            var first = group.First();
            records.Add(PatientRecord(patientSequence++, first));
            var orderSequence = 1;
            foreach (var order in group)
                records.Add(OrderRecord(orderSequence++, order, reportType));
        }
        records.Add(Terminator());
        return records;
    }

    private Lis02Record PatientRecord(int sequence, WorkOrder order)
    {
        var builder = new Lis02RecordBuilder('P', Delimiters).SetText(2, sequence.ToString(CultureInfo.InvariantCulture));
        if (order.Action == OrderAction.Control || order.Patient is not { } patient)
            return builder.Build();
        return builder
            .SetText(3, patient.Id)
            .SetComponents(6, patient.FamilyName, patient.GivenName)
            .SetText(8, patient.BirthDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            .SetText(9, Sex(patient.AdministrativeSex))
            .SetText(14, patient.Physician)
            .SetText(26, patient.Location)
            .Build();
    }

    private static Lis02Record OrderRecord(int sequence, WorkOrder order, string reportType)
    {
        var tests = order.Tests.Select(t => new[] { null, null, null, t.Vendor.Code }).ToList();
        return new Lis02RecordBuilder('O', Delimiters)
            .SetText(2, sequence.ToString(CultureInfo.InvariantCulture))
            .SetText(3, order.Specimen.SpecimenId)
            .SetRepeats(5, tests)
            .SetText(6, order.Priority == OrderPriority.Stat ? "S" : "R")
            .SetText(8, order.CollectedAt?.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture))
            .SetText(12, order.Action switch { OrderAction.Add => "A", OrderAction.Cancel => "C", OrderAction.Control => "Q", _ => "N" })
            .SetText(16, order.SpecimenType)
            .SetText(26, reportType)
            .Build();
    }

    private string Sex(string? value) => options.Sex == SexPolicy.AlwaysUnknown
        ? "U"
        : value?.ToUpperInvariant() switch { "M" => "M", "F" => "F", _ => "U" };

    private static Lis02Record Header() =>
        Lis02RecordBuilder.Header(Delimiters).SetText(12, "P").SetText(13, "1").Build();

    private static Lis02Record Terminator() => new Lis02RecordBuilder('L', Delimiters).SetText(2, "1").Build();

    private InstrumentRef? ReadInstrument(Lis02Record? header)
    {
        if (header is null)
            return null;
        // H.5 sender name: ARCHITECT^version^serial^interface
        var model = header.Text(5, 1);
        if (model.Length == 0)
            return null;
        return context.Instrument with
        {
            Manufacturer = "Abbott",
            Model = model,
            SoftwareVersion = NullIfEmpty(header.Text(5, 2)),
            SerialNumber = NullIfEmpty(header.Text(5, 3)),
        };
    }

    private static OrderState ReadOrder(Lis02Record record) => new(
        Sequence: record.Field(2),
        SpecimenId: record.Text(3),
        InstrumentSpecimenId: record.Text(4, 1),
        Carrier: record.Text(4, 2),
        Position: record.Text(4, 3),
        Role: record.Field(12) == "Q" ? SpecimenRole.Control : SpecimenRole.Patient,
        Priority: record.Field(6));

    private InstrumentObservation ReadResult(InstrumentRef instrument, OrderState order, Lis02Record record, TraceRef trace, List<AtsmDiagnostic> diagnostics)
    {
        // R.3 ^^^assay number^assay name^dilution^assay status^reagent lot^reagent serial^control lot^result type
        var assayCode = record.Text(3, 4);
        var resultKind = record.Text(3, 11);
        var raw = record.Text(4);
        var (comparator, numeric) = ParseValue(raw);
        var status = record.Field(9);
        var flags = record.Components(7)
            .Where(f => f.Length > 0)
            .Select(f => new CodedValue(Delimiters.Decode(f), null, VendorSystem + ":flag"))
            .ToList();
        var identity = new Dictionary<string, string>
        {
            ["order.sequence"] = order.Sequence,
            ["result.sequence"] = record.Field(2),
            ["assay.status"] = record.Text(3, 7),
        };
        AddIfPresent(identity, "control.lot", record.Text(3, 10));
        AddIfPresent(identity, "instrument.specimenId", order.InstrumentSpecimenId);
        AddIfPresent(identity, "module.serial", record.Text(14));
        AddIfPresent(identity, "operator.release", record.Text(11, 2));

        return new InstrumentObservation(
            Guid.CreateVersion7(),
            instrument,
            Specimen(order),
            CodeSet.Of(new CodedValue(assayCode, NullIfEmpty(record.Text(3, 5)), VendorSystem)),
            new ObservationValue(raw, numeric, comparator, resultKind.StartsWith('I') ? new CodedValue(raw, raw, VendorSystem + ":interpretation") : null, NullIfEmpty(record.Text(5))),
            NullIfEmpty(record.Text(6)),
            flags,
            status,
            status switch { "F" => InstrumentStatusMeaning.Final, "R" => InstrumentStatusMeaning.PreviouslyTransmitted, _ => InstrumentStatusMeaning.Unknown },
            NullIfEmpty(resultKind),
            NullIfEmpty(record.Text(3, 6)),
            ParseTimestamp(record.Field(13), diagnostics),
            NullIfEmpty(record.Text(11, 1)),
            [new ReagentLot(NullIfEmpty(record.Text(3, 8)), NullIfEmpty(record.Text(3, 9)), "reagent")],
            new List<string>(),
            new VendorIdentity(identity),
            trace);
    }

    private static InstrumentObservation NotPerformed(InstrumentRef instrument, OrderState order, Lis02Record record, TraceRef trace) => new(
        Guid.CreateVersion7(),
        instrument,
        Specimen(order),
        CodeSet.Of(new CodedValue(record.Text(5, 4), NullIfEmpty(record.Text(5, 5)), VendorSystem)),
        new ObservationValue(string.Empty),
        null,
        [],
        "X",
        InstrumentStatusMeaning.NotPerformed,
        null,
        NullIfEmpty(record.Text(5, 6)),
        null,
        null,
        [],
        new List<string>(),
        new VendorIdentity(new Dictionary<string, string> { ["order.sequence"] = order.Sequence }),
        trace);

    private static OrderQuery ReadQuery(InstrumentRef instrument, Lis02Record record, TraceRef trace)
    {
        // Q.3 ^specimen id (repeatable), Q.4 end of range, Q.5 ^^^ALL
        var specimens = record.Repeats(3)
            .Select((_, i) => record.Text(3, 2, i + 1))
            .Append(record.Text(4, 2))
            .Where(id => id.Length > 0)
            .Select(id => new SpecimenRef(id))
            .ToList();
        return new OrderQuery(Guid.CreateVersion7(), instrument, specimens, record.Text(5, 4), trace);
    }

    private static SpecimenRef Specimen(OrderState order) => new(
        order.SpecimenId,
        NullIfEmpty(order.InstrumentSpecimenId),
        Position: order.Carrier.Length == 0 ? null : $"{order.Carrier}^{order.Position}",
        Role: order.Role);

    private static (string? Comparator, decimal? Numeric) ParseValue(string raw)
    {
        var text = raw.Trim();
        string? comparator = null;
        foreach (var candidate in (ReadOnlySpan<string>)["<=", ">=", "<", ">"])
        {
            if (text.StartsWith(candidate, StringComparison.Ordinal))
            {
                comparator = candidate;
                text = text[candidate.Length..].TrimStart();
                break;
            }
        }
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? (comparator, value)
            : (comparator, null);
    }

    private DateTimeOffset? ParseTimestamp(string value, List<AtsmDiagnostic> diagnostics)
    {
        if (value.Length == 0)
            return null;
        if (!DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            diagnostics.Add(new(InvalidTimestamp, AtsmSeverity.Warning, $"Invalid timestamp '{value}'."));
            return null;
        }
        return new DateTimeOffset(local, options.InstrumentTimeZone.GetUtcOffset(local));
    }

    private static void AddIfPresent(Dictionary<string, string> values, string key, string value)
    {
        if (value.Length > 0)
            values[key] = value;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed record OrderState(string Sequence, string SpecimenId, string InstrumentSpecimenId, string Carrier, string Position, SpecimenRole Role, string Priority);
}
