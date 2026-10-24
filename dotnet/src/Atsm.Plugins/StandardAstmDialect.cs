using System.Globalization;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugins;

/// <summary>
/// Dialect that follows CLSI LIS02-A2 field positions without vendor knowledge. It interprets any conforming instrument
/// well enough for observability and is the base that vendor dialects override where their manual differs.
/// </summary>
public class StandardAstmDialect(DialectContext context, string vendorSystem) : IAstmDialect
{
    public const string ResultWithoutOrder = "ATSM-STD-RESULT-WITHOUT-ORDER";
    public const string InvalidTimestamp = "ATSM-STD-INVALID-TIMESTAMP";
    public const string UnknownRecord = "ATSM-STD-UNKNOWN-RECORD";

    protected DialectContext Context { get; } = context;

    protected string VendorSystem { get; } = vendorSystem;

    protected static Lis02Delimiters Delimiters => Lis02Delimiters.Default;

    public virtual AstmInterpretation Interpret(Lis02Message message, TraceRef trace)
    {
        var diagnostics = new List<AtsmDiagnostic>();
        var observations = new List<InstrumentObservation>();
        var queries = new List<OrderQuery>();
        var instrument = ReadInstrument(message.Header) ?? Context.Instrument;
        OrderContext? order = null;
        List<string>? comments = null;

        foreach (var record in message.Records)
        {
            switch (record.Type)
            {
                case 'H':
                case 'L':
                    comments = null;
                    break;
                case 'P':
                    order = null;
                    comments = null;
                    break;
                case 'O':
                    order = ReadOrder(record);
                    comments = null;
                    break;
                case 'R':
                    if (order is null)
                    {
                        diagnostics.Add(new(ResultWithoutOrder, AtsmSeverity.Warning, "Result record without a preceding order record.", record.Field(2)));
                        break;
                    }
                    comments = [];
                    observations.Add(ReadResult(instrument, order, record, trace, comments, diagnostics));
                    break;
                case 'C':
                    comments?.Add(record.Text(4));
                    break;
                case 'Q':
                    if (ReadQuery(instrument, record, trace) is { } query)
                        queries.Add(query);
                    break;
                case 'M':
                case 'S':
                    break;
                default:
                    diagnostics.Add(new(UnknownRecord, AtsmSeverity.Information, $"Record {record.Type} preserved without interpretation.", record.Field(2)));
                    break;
            }
        }
        return new AstmInterpretation(observations, queries, instrument, diagnostics);
    }

    public virtual IReadOnlyList<Lis02Record> BuildQueryAnswer(OrderQuery query, OrderQueryAnswer answer) =>
        answer.State == QueryAnswerState.Orders && answer.Orders.Count > 0
            ? BuildMessage(answer.Orders, reportType: "Q")
            // LIS02-A2 termination code I: no information available from the last query.
            : [Header(), new Lis02RecordBuilder('L', Delimiters).SetText(2, "1").SetText(3, "I").Build()];

    public virtual IReadOnlyList<Lis02Record> BuildOrders(IReadOnlyList<WorkOrder> orders) => BuildMessage(orders, reportType: "O");

    protected virtual IReadOnlyList<Lis02Record> BuildMessage(IReadOnlyList<WorkOrder> orders, string reportType)
    {
        var records = new List<Lis02Record> { Header() };
        var patientSequence = 1;
        foreach (var group in orders.GroupBy(o => o.Specimen.SpecimenId))
        {
            records.Add(PatientRecord(patientSequence++, group.First()));
            var orderSequence = 1;
            foreach (var order in group)
                records.Add(OrderRecord(orderSequence++, order, reportType));
        }
        records.Add(new Lis02RecordBuilder('L', Delimiters).SetText(2, "1").SetText(3, "N").Build());
        return records;
    }

    protected virtual Lis02Record Header() => Lis02RecordBuilder.Header(Delimiters).SetText(12, "P").Build();

    protected virtual Lis02Record PatientRecord(int sequence, WorkOrder order)
    {
        var builder = new Lis02RecordBuilder('P', Delimiters).SetText(2, sequence.ToString(CultureInfo.InvariantCulture));
        if (order.Patient is not { } patient)
            return builder.Build();
        return builder
            .SetText(3, patient.Id)
            .SetComponents(6, patient.FamilyName, patient.GivenName)
            .SetText(8, patient.BirthDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            .SetText(9, patient.AdministrativeSex?.ToUpperInvariant() switch { "M" => "M", "F" => "F", _ => "U" })
            .Build();
    }

    protected virtual Lis02Record OrderRecord(int sequence, WorkOrder order, string reportType) =>
        new Lis02RecordBuilder('O', Delimiters)
            .SetText(2, sequence.ToString(CultureInfo.InvariantCulture))
            .SetText(3, order.Specimen.SpecimenId)
            .SetRepeats(5, order.Tests.Select(t => new[] { null, null, null, t.Vendor.Code }))
            .SetText(6, order.Priority == OrderPriority.Stat ? "S" : "R")
            .SetText(12, order.Action switch { OrderAction.Add => "A", OrderAction.Cancel => "C", OrderAction.Control => "Q", _ => "N" })
            .SetText(26, reportType)
            .Build();

    /// <summary>H.5 sender: name^version^serial.</summary>
    protected virtual InstrumentRef? ReadInstrument(Lis02Record? header)
    {
        if (header is null || header.Text(5, 1).Length == 0)
            return null;
        return Context.Instrument with
        {
            Model = header.Text(5, 1).Trim(),
            SoftwareVersion = NullIfEmpty(header.Text(5, 2)),
            SerialNumber = NullIfEmpty(header.Text(5, 3)),
        };
    }

    protected virtual OrderContext ReadOrder(Lis02Record record) => new(
        record.Field(2),
        record.Text(3, 1).Trim(),
        NullIfEmpty(record.Text(4, 1).Trim()),
        record.Field(12) == "Q" ? SpecimenRole.Control : SpecimenRole.Patient,
        record.Field(26));

    protected virtual string TestCode(Lis02Record result)
    {
        // Universal test ID: ^^^local code; fall back to the first non-empty component.
        var local = result.Text(3, 4);
        return local.Length > 0 ? local : result.Components(3).FirstOrDefault(c => c.Length > 0) ?? string.Empty;
    }

    protected virtual string? ResultKind(Lis02Record result) => null;

    protected virtual string ResultValue(Lis02Record result) => result.Text(4, 1);

    protected virtual string InstrumentStatus(Lis02Record result, OrderContext order) => result.Field(9);

    protected virtual InstrumentStatusMeaning StatusMeaning(string status) => status switch
    {
        "F" => InstrumentStatusMeaning.Final,
        "P" => InstrumentStatusMeaning.Preliminary,
        "C" => InstrumentStatusMeaning.Correction,
        "R" => InstrumentStatusMeaning.PreviouslyTransmitted,
        "X" => InstrumentStatusMeaning.NotPerformed,
        "W" => InstrumentStatusMeaning.Warning,
        _ => InstrumentStatusMeaning.Unknown,
    };

    protected virtual IReadOnlyList<CodedValue> Flags(Lis02Record result) =>
        result.Repeats(7).SelectMany(r => r.Split(Delimiters.Component))
            .Where(f => f.Length > 0)
            .Select(f => new CodedValue(Delimiters.Decode(f), null, VendorSystem + ":flag"))
            .ToList();

    protected virtual Dictionary<string, string> Identity(Lis02Record result, OrderContext order)
    {
        var identity = new Dictionary<string, string> { ["order.sequence"] = order.Sequence, ["result.sequence"] = result.Field(2) };
        if (order.InstrumentSpecimenId is { } instrumentSpecimen)
            identity["instrument.specimenId"] = instrumentSpecimen;
        if (result.Text(14).Length > 0)
            identity["instrument.id"] = result.Text(14);
        return identity;
    }

    protected virtual SpecimenRef Specimen(OrderContext order) => new(order.SpecimenId, order.InstrumentSpecimenId, Role: order.Role);

    protected virtual InstrumentObservation ReadResult(InstrumentRef instrument, OrderContext order, Lis02Record result, TraceRef trace,
        List<string> comments, List<AtsmDiagnostic> diagnostics)
    {
        var raw = ResultValue(result);
        var (comparator, numeric) = ParseValue(raw);
        var status = InstrumentStatus(result, order);
        return new InstrumentObservation(
            Guid.CreateVersion7(),
            instrument,
            Specimen(order),
            CodeSet.Of(new CodedValue(TestCode(result), null, VendorSystem)),
            new ObservationValue(raw, numeric, comparator, null, NullIfEmpty(result.Text(5))),
            NullIfEmpty(result.Text(6)),
            Flags(result),
            status,
            StatusMeaning(status),
            ResultKind(result),
            null,
            ParseTimestamp(result.Field(13), diagnostics),
            NullIfEmpty(result.Text(11, 1)),
            [],
            comments,
            new VendorIdentity(Identity(result, order)),
            trace);
    }

    protected virtual OrderQuery? ReadQuery(InstrumentRef instrument, Lis02Record record, TraceRef trace)
    {
        // Q.3 starting range ID: patient^specimen; some instruments send the specimen alone.
        var specimens = record.Repeats(3)
            .Select((r, i) => record.Components(3, i + 1).Count > 1 ? record.Text(3, 2, i + 1) : record.Text(3, 1, i + 1))
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .Select(id => new SpecimenRef(id))
            .ToList();
        return new OrderQuery(Guid.CreateVersion7(), instrument, specimens, record.Text(5, 4), trace);
    }

    protected DateTimeOffset? ParseTimestamp(string value, List<AtsmDiagnostic> diagnostics)
    {
        if (value.Length == 0)
            return null;
        if (!DateTime.TryParseExact(value, ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            diagnostics.Add(new(InvalidTimestamp, AtsmSeverity.Warning, $"Invalid timestamp '{value}'."));
            return null;
        }
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    protected static (string? Comparator, decimal? Numeric) ParseValue(string raw)
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

    protected static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    protected sealed record OrderContext(string Sequence, string SpecimenId, string? InstrumentSpecimenId, SpecimenRole Role, string ReportType)
    {
        public string? Rack { get; init; }

        public string? Position { get; init; }
    }
}
