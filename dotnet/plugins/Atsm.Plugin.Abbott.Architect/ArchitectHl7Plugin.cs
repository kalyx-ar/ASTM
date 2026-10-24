using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Hl7v2;

namespace Atsm.Plugin.Abbott.Architect;

public sealed class ArchitectHl7Plugin(TimeSpan queryAnswerBudget, TimeSpan responseTimeout, TimeZoneInfo instrumentTimeZone) : IHl7InstrumentPlugin
{
    public const string ProfileId = "abbott.architect.hl7-2.5.1";
    public IReadOnlyList<Hl7Profile> Profiles { get; } =
    [new(ProfileId, "1.0.0", new UTF8Encoding(false, true), Positive(queryAnswerBudget), Positive(responseTimeout),
        ["Abbott 206797-101 (2012) §1-2 MLLP, §1-5 original ACK, §2-8/2-9 ZOS queries, §2-13 OUL^R22, §2-3 OML^O33",
         "Budgets must match deployed instrument settings; this manual does not define universal timeout defaults."])];

    private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.Zero && value.TotalMilliseconds <= uint.MaxValue - 1
        ? value : throw new ArgumentOutOfRangeException(nameof(value));
    public PluginDescriptor Describe() => new("abbott.architect.hl7", "0.1.0", PluginApi.Version, "Abbott", ["ARCHITECT System"],
        ["ARCHITECT System HL7 Interface Manual 206797-101, January 2012; manufacturer profile, not a complete IHE LAW conformance claim"]);
    public IHl7Dialect CreateDialect(Hl7Profile profile, DialectContext context) => profile.Id == ProfileId
        ? new ArchitectHl7Dialect(context, instrumentTimeZone) : throw new ArgumentException("Unknown HL7 profile.", nameof(profile));
}

public sealed class ArchitectHl7Dialect(DialectContext context, TimeZoneInfo timeZone) : IHl7Dialect
{
    public Hl7Interpretation Interpret(Hl7Message message, TraceRef trace)
    {
        var findings = new List<AtsmDiagnostic>();
        Hl7Interpretation Invalid(string code) => new([], null, [new(code, AtsmSeverity.Error, "Unsupported or incomplete ARCHITECT HL7 message.")]);
        if (message.Header.Text(12) != "2.5.1") return Invalid("ATSM-HL7-UNSUPPORTED-VERSION");
        if (message.Type == "QBP" && message.Trigger == "ZOS")
        {
            var parameters = message.OfType("QPD").ToArray();
            if (parameters.Length != 1 || parameters[0].Text(1) != "ZOS" || parameters[0].Text(3).Length == 0
                || !message.OfType("RCP").Any()) return Invalid("ATSM-HL7-INVALID-QUERY");
            return new([], new(Guid.CreateVersion7(), context.Instrument, [new(parameters[0].Text(3))], "ALL", trace), findings);
        }
        if (message.Type != "OUL" || message.Trigger != "R22") return Invalid("ATSM-HL7-UNSUPPORTED-MESSAGE");
        var specimens = message.OfType("SPM").ToArray();
        if (specimens.Length != 1 || specimens[0].Text(2).Length == 0 || !message.OfType("OBX").Any()) return Invalid("ATSM-HL7-INVALID-RESULT-GROUP");
        var specimen = new SpecimenRef(specimens[0].Text(2), Role: specimens[0].Text(11) switch
        { "Q" => SpecimenRole.Control, "C" => SpecimenRole.Calibrator, "P" => SpecimenRole.Patient, _ => SpecimenRole.Unknown });
        var observations = new List<InstrumentObservation>();
        List<string>? comments = null;
        List<ReagentLot>? reagents = null;
        Hl7Segment? order = null;
        var seenSpecimen = false;
        foreach (var segment in message.Segments)
        {
            if (segment.Name == "SPM") { seenSpecimen = true; order = null; comments = null; reagents = null; }
            if (segment.Name == "OBR") { order = segment; comments = null; reagents = null; }
            if (segment.Name == "OBX")
            {
                if (!seenSpecimen || order is null || segment.Text(3).Length == 0 || segment.Field(5).Length == 0 || segment.Field(11).Length == 0)
                    return Invalid("ATSM-HL7-INVALID-RESULT-GROUP");
                if (segment.Text(2) is not ("NM" or "SN" or "ST" or "CE")) return Invalid("ATSM-HL7-UNSUPPORTED-VALUE-TYPE");
                comments = []; reagents = [];
                var code = segment.Text(3, subcomponent: 1);
                var kind = segment.Text(3, subcomponent: 3);
                var value = segment.Text(5);
                string? comparator = null;
                decimal? numeric = null;
                if (segment.Text(2) == "SN")
                {
                    comparator = value.Length == 0 ? null : value;
                    if (segment.Text(5, 3).Length > 0 || segment.Text(5, 4).Length > 0) return Invalid("ATSM-HL7-UNSUPPORTED-STRUCTURED-NUMERIC");
                    value = segment.Text(5, 2);
                }
                else foreach (var candidate in new[] { "<=", ">=", "<", ">" })
                    if (value.StartsWith(candidate, StringComparison.Ordinal)) { comparator = candidate; value = value[candidate.Length..].Trim(); break; }
                // Invariant decimal syntax with optional properly grouped thousands. Decimal.TryParse
                // alone accepts malformed grouping such as "1,20" and would silently turn it into 120.
                value = value.Trim();
                if (Regex.IsMatch(value, @"^[+-]?(?:(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]*)?|\.[0-9]+)$", RegexOptions.CultureInvariant)
                    && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) numeric = number;
                if (segment.Text(2) is "NM" or "SN" && numeric is null) return Invalid("ATSM-HL7-INVALID-NUMERIC");
                DateTimeOffset? resultTime = null;
                var timestamp = segment.Text(19);
                if (timestamp.Length > 0)
                {
                    if (!DateTime.TryParseExact(timestamp, ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                        || timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local)) return Invalid("ATSM-HL7-INVALID-TIMESTAMP");
                    resultTime = new(local, timeZone.GetUtcOffset(local));
                }
                var status = segment.Text(11);
                observations.Add(new(Guid.CreateVersion7(), context.Instrument, specimen,
                    CodeSet.Of(new(code, segment.Text(3, 2), ArchitectDialect.VendorSystem)),
                    new(segment.Field(5), numeric, comparator,
                        segment.Text(2) == "CE" ? new(segment.Text(5), segment.Text(5, 2), ArchitectDialect.VendorSystem + ":exception")
                            : kind == "I" ? new(segment.Text(5), segment.Text(5), ArchitectDialect.VendorSystem + ":interpretation") : null,
                        segment.Text(6), null), segment.Text(7),
                    segment.Repetitions(8).Where(f => f.Length > 0).Select(f => new CodedValue(message.Delimiters.Decode(f), null, ArchitectDialect.VendorSystem + ":flag")).ToArray(),
                    status, status switch { "F" => InstrumentStatusMeaning.Final, "P" => InstrumentStatusMeaning.Preliminary, "C" => InstrumentStatusMeaning.Correction,
                        "R" => InstrumentStatusMeaning.PreviouslyTransmitted, "X" => InstrumentStatusMeaning.NotPerformed, _ => InstrumentStatusMeaning.Unknown },
                    kind, segment.Text(3, subcomponent: 2), resultTime, segment.Text(16), reagents, comments,
                    new(new Dictionary<string, string> { ["message.controlId"] = message.ControlId, ["result.sequence"] = segment.Text(1),
                        ["order.sequence"] = order.Text(1), ["equipment"] = segment.Field(18) }), trace));
            }
            if (segment.Name == "NTE") comments?.Add(segment.Text(3));
            if (segment.Name == "SID") reagents?.Add(new(segment.Text(2), segment.Text(3), segment.Field(1)));
        }
        return new(observations, null, findings);
    }

    public Hl7Message BuildQueryAnswer(Hl7Message request, OrderQueryAnswerContext answer)
    {
        var qpd = request.OfType("QPD").Single();
        var d = request.Delimiters;
        var available = answer.Answer.State != QueryAnswerState.Unavailable;
        var orders = answer.Answer.State == QueryAnswerState.Orders ? answer.Answer.Orders : [];
        if (orders.Any(o => o.Specimen.SpecimenId != qpd.Text(3))) throw new InvalidOperationException("ATSM-HL7-QUERY-SPECIMEN-MISMATCH");
        var segments = new List<Hl7Segment>
        {
            Hl7Acknowledgements.ResponseHeader(request, "RSP", "ZOS", "RSP_K11", answer.ControlId, context.Clock.GetUtcNow()),
            Hl7Segment.Build("MSA", d, (1, available ? "AA" : "AE"), (2, d.Encode(request.ControlId))),
        };
        if (!available) segments.Add(Hl7Segment.Build("ERR", d, (3, "207^Host unavailable^HL70357".Replace('^', d.Component)), (4, "E")));
        segments.Add(Hl7Segment.Build("QAK", d, (1, qpd.Field(2)), (2, !available ? "AE" : orders.Count > 0 ? "OK" : "NF"), (3, qpd.Field(1))));
        segments.Add(qpd);
        if (orders.Count > 0) AppendOrders(segments, orders, d, queryResponse: true);
        return new(d, segments);
    }

    public Hl7Message BuildOrders(IReadOnlyList<WorkOrder> orders, string controlId)
    {
        var d = Hl7Delimiters.Default;
        var header = Hl7Segment.Build("MSH", d, (3, "ATSM"), (7, context.Clock.GetUtcNow().ToString("yyyyMMddHHmmss+0000", CultureInfo.InvariantCulture)),
            (9, "OML^O33^OML_O33"), (10, d.Encode(controlId)), (11, "P"), (12, "2.5.1"), (18, "UNICODE UTF-8"));
        var segments = new List<Hl7Segment> { header };
        AppendOrders(segments, orders, d, queryResponse: false);
        return new(d, segments);
    }

    private static void AppendOrders(List<Hl7Segment> segments, IReadOnlyList<WorkOrder> orders, Hl7Delimiters d, bool queryResponse)
    {
        if (orders.Count == 0 || orders.Select(o => o.Specimen.SpecimenId).Distinct().Count() != 1 || orders.Select(o => o.Action).Distinct().Count() != 1
            || orders.Any(o => o.Tests.Count == 0 || o.Specimen.SpecimenId.Length == 0)) throw new ArgumentException("ARCHITECT requires one specimen and one action per order message.", nameof(orders));
        var first = orders[0];
        var spm = Hl7Segment.Build("SPM", d, (1, "1"), (2, d.Encode(first.Specimen.SpecimenId)),
            (11, first.Specimen.Role switch { SpecimenRole.Control => "Q", SpecimenRole.Calibrator => "C", _ => "P" }));
        if (queryResponse) segments.Add(spm);
        if (first.Patient is { } p && first.Specimen.Role is not (SpecimenRole.Control or SpecimenRole.Calibrator))
            segments.Add(Hl7Segment.Build("PID", d, (1, "1"), (3, d.Encode(p.Id)), (5, d.Encode(p.FamilyName) + d.Component + d.Encode(p.GivenName)),
                (7, p.BirthDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? ""), (8, d.Encode(p.AdministrativeSex))));
        if (!queryResponse) segments.Add(spm);
        var sequence = 0;
        foreach (var order in orders)
        foreach (var test in order.Tests)
        {
            var index = (++sequence).ToString(CultureInfo.InvariantCulture);
            segments.Add(Hl7Segment.Build("ORC", d, (1, order.Action == OrderAction.Cancel ? "CA" : "NW")));
            segments.Add(Hl7Segment.Build("TQ1", d, (1, index), (9, order.Priority == OrderPriority.Stat ? "S" : "R")));
            segments.Add(Hl7Segment.Build("OBR", d, (1, index), (4, d.Encode(test.Vendor.Code) + d.Component + d.Encode(test.Vendor.Display) + d.Component + "ARCHITECT"),
                (11, order.Action == OrderAction.Add ? "A" : "")));
        }
    }
}
