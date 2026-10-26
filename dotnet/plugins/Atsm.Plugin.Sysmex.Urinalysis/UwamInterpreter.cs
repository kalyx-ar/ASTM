using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Plugins.Tracing;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugin.Sysmex.Urinalysis;

internal sealed partial class UwamInterpreter(IInstrumentKnowledge knowledge) : IInstrumentPlugin, ITraceLogAdapterProvider
{
    public const string PluginId = "sysmex.uwam";
    public const string WithChemistryProfile = "sysmex.uwam.lis2a2.with-chemistry";
    public const string WithoutChemistryProfile = "sysmex.uwam.lis2a2.without-chemistry";

    private static readonly string[] Evidence =
    [
        "Sysmex R315168 U-WAM ASTM Host Online Specifications rev.22 §5.2.4–5.2.5",
        "R315168 §5.3.3.7; appendices B, J, K",
    ];

    // The host answer budget leaves margin below the instrument limit for framing and transfer.
    public IReadOnlyList<AstmProfile> Profiles { get; } =
    [
        Profile(WithChemistryProfile, TimeSpan.FromSeconds(6)),
        Profile(WithoutChemistryProfile, TimeSpan.FromSeconds(15)),
    ];

    public IReadOnlyList<ITraceLogAdapter> LogAdapters { get; } = [new SysmexPortMonitorAdapter()];

    public PluginDescriptor Describe() => new(
        PluginId,
        "0.1.0",
        PluginApi.Version,
        "Sysmex",
        ["U-WAM (UF-3000/3000x/4000/5000, UC-1000/3500, UD-10)"],
        ["Sysmex R315168 U-WAM ASTM Host Online Specifications, revision 22"]);

    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context)
    {
        if (profile.Id is not (WithChemistryProfile or WithoutChemistryProfile))
            throw new ArgumentException($"Unknown profile {profile.Id}.", nameof(profile));
        return new UwamDialect(context, profile.Id, knowledge);
    }

    private static AstmProfile Profile(string id, TimeSpan budget) => new(
        id,
        "1.1.0",
        Lis01Options.Default,
        Encoding.Latin1,
        new HashSet<AstmWorkflow> { AstmWorkflow.UnsolicitedResults, AstmWorkflow.HostQuery, AstmWorkflow.OrderDownload },
        budget,
        OwnershipEnforcement.Advisory,
        Evidence);
}

/// <summary>
/// Sysmex port monitor log written by Sysmex software for each line: <c>yyyy/MM/dd HH:mm:ss.fff R|S NNN{payload}</c>,
/// with control characters as <c>[STX]</c>, <c>[CR]</c>… and <c>OPEN</c>/<c>CLOSE</c> lines. R and S are seen from the
/// writing system, so pass <see cref="RecordedBy.Instrument"/> for logs of the port towards the LIS (U-WAM is the
/// instrument there) and <see cref="RecordedBy.Host"/> for logs where the Sysmex software acts as host of an analyzer.
/// </summary>
public sealed partial class SysmexPortMonitorAdapter() : RegexLogAdapter(LogFormat)
{
    public override LogExtraction Extract(ReadOnlySpan<byte> log, LogAdapterOptions options)
    {
        var extraction = base.Extract(NormalizeBlocks(log), options);
        var framed = new HashSet<TraceDirection>();
        var segments = extraction.Segments.Select(segment =>
        {
            if (segment.Direction == TraceDirection.Event) return segment;
            // Each line is a read/write. A continuation without STX is still wire data,
            // never a standalone application record. LIS01 ends with EOT, MLLP with FS.
            var bytes = segment.Data.Span;
            if (bytes.Contains((byte)2) || bytes.Contains((byte)11)) framed.Add(segment.Direction);
            var result = framed.Contains(segment.Direction) ? segment with { Content = SegmentContent.Wire } : segment;
            if (bytes.Contains((byte)4) || bytes.Contains((byte)28)) framed.Remove(segment.Direction);
            return result;
        }).ToArray();
        return extraction with { Segments = segments };
    }

    private byte[] NormalizeBlocks(ReadOnlySpan<byte> log)
    {
        // The monitor splits long blocks into reads with separate counters, but braces span those reads.
        // A brace can also be a real binary byte: use decoded lengths, never Trim('{', '}') or NUL removal.
        var output = new StringBuilder(); var open = new Dictionary<string, bool?>();
        foreach (var line in LogFormat.Encoding.GetString(log).Split('\n'))
        {
            var complete = EntryPattern().Match(line);
            if (complete.Success && complete.Groups["label"].Success)
            {
                if (complete.Groups["label"].Value is "OPEN" or "CLOSE") { open["R"] = false; open["S"] = false; }
                output.Append(line).Append('\n'); continue;
            }
            if (complete.Success && int.TryParse(complete.Groups["length"].Value, out var completeLength)
                && Decode(complete.Groups["payload"].Value).Length == completeLength)
            {
                open[complete.Groups["dir"].Value] = false;
                output.Append(line).Append('\n'); continue;
            }
            var match = BlockLine().Match(line);
            if (!match.Success) { output.Append(line).Append('\n'); continue; }
            var raw = match.Groups["payload"].Value; var direction = match.Groups["dir"].Value;
            if (!int.TryParse(match.Groups["length"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            { output.Append(line).Append('\n'); continue; }
            var candidates = new List<(string Text, bool Start, bool End)> { (raw, false, false) };
            if (raw.StartsWith('{')) candidates.Add((raw[1..], true, false));
            if (raw.EndsWith('}')) candidates.Add((raw[..^1], false, true));
            if (raw.StartsWith('{') && raw.EndsWith('}') && raw.Length > 1) candidates.Add((raw[1..^1], true, true));
            var valid = candidates.Where(c => Decode(c.Text).Length == length).ToArray();
            if (valid.Length == 0 && complete.Success) { output.Append(line).Append('\n'); continue; }
            var state = open.GetValueOrDefault(direction);
            if (valid.Length > 1 && !state.HasValue && complete.Success) { output.Append(line).Append('\n'); continue; }
            (string Text, bool Start, bool End) unchanged = (raw, false, false);
            var selected = valid.Length == 1 ? valid[0] : valid.Length > 1 && state.HasValue
                ? valid.FirstOrDefault(c => state.Value ? c.End && !c.Start : c.Start && !c.End, unchanged) : unchanged;
            if (selected.Start || selected.End) open[direction] = selected.Start && !selected.End;
            output.Append(match.Groups["prefix"].Value).Append('{').Append(selected.Text).Append('}').Append('\n');
        }
        return LogFormat.Encoding.GetBytes(output.ToString());
    }
    // The split-block form uses the monitor's three-digit, zero-padded byte counter (512-byte lines in the capture).
    // Complete braced records above continue to accept other counter widths.
    [GeneratedRegex(@"^(?<prefix>(?<time>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<dir>[RS]) +(?<length>\d{3}))(?<payload>[^\r\n]*)\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockLine();

    public static readonly RegexLogFormat LogFormat = new(
        "sysmex.port-monitor",
        "Sysmex port monitor (wire bytes or records, one line per read/write)",
        EntryPattern(),
        "yyyy/MM/dd HH:mm:ss.fff",
        Encoding.Latin1,
        new Dictionary<string, TraceDirection> { ["R"] = TraceDirection.Received, ["S"] = TraceDirection.Transmitted },
        [
            "Timestamps are local times of the Sysmex PC without offset.",
            "Each line is one read or write of the Sysmex software, not necessarily one frame.",
            "Bytes outside ASCII (for example 0xB5, micro sign in units) are written as spaces: frames containing them fail the checksum in the log although the receiver accepted them.",
        ])
    {
        Tokens = AsciiControlTokens.Bracketed,
    };

    [GeneratedRegex(@"^(?<time>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?:(?<dir>[RS]) +(?<length>\d+)\{(?<payload>.*)\}|(?<label>[A-Z]+))[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EntryPattern();
}

/// <summary>
/// U-WAM → host messages (R315168 5.3.3). Differences with the standard: R.3 is parameter^method^dilution^result type^rack^position,
/// R.4 is value^format (RAW / MAINFORMAT, one record each), R.7 is analyzer flag^LH flag, R.9 carries the UD-10 cell,
/// R.14 names the analyzer, Q.3 is the sample number alone, Q.13 the device type, and O.26 is always F (final).
/// </summary>
public sealed class UwamDialect(DialectContext context, string profile = SysmexUrinalysisPlugin.WithChemistryProfile, IInstrumentKnowledge? knowledge = null) : StandardAstmDialect(context, CodeSystems.Vendor(UwamInterpreter.PluginId))
{
    /// <summary>Parameter through which U-WAM reports errors of the urine chemistry analyzer (UC-3500 / UC-1000), R315168 appendix B.1.</summary>
    public const string ChemistryErrorParameter = "C-Error Code";
    private readonly IInstrumentKnowledge chemistryKnowledge = knowledge ?? new UrinalysisKnowledge();

    public override AstmInterpretation Interpret(Lis02Message message, TraceRef trace)
    {
        var interpretation = base.Interpret(message, trace);
        interpretation = interpretation with { QualityResources = message.Records.Where(r => r.Type == 'S' && r.Field(7).Length > 0)
            .Select(r => new QualityResourceReference(Guid.CreateVersion7(), interpretation.Instrument ?? Context.Instrument,
                QualityResourceKind.ControlTargetLimits, r.Field(7), "S.7 (R315168 §5.3.3.7, Appendix J)", trace)).ToArray() };
        // A chemistry error may leave the results of that sample blank (R315168 B.1 note *2); the code maps through appendix K.
        var errors = interpretation.Observations
            .Where(o => o.Test.Vendor.Code == ChemistryErrorParameter && o.Value.RawText.Trim() is { Length: > 0 } code && code != "0000")
            .Select(o =>
            {
                var code = o.Value.RawText.Trim();
                var fact = chemistryKnowledge.Lookup(new(profile, "R", ChemistryErrorParameter, o.VendorIdentity["instrument.id"] ?? o.Instrument.Model), code);
                return new Atsm.Abstractions.Diagnostics.AtsmDiagnostic(fact?.FindingCode ?? UwamChemistryErrors.Prefix + code,
                    Atsm.Abstractions.Diagnostics.AtsmSeverity.Warning,
                    $"Urine chemistry analyzer error {code}" + (fact?.SourceCode is { } original && original != code ? $"; analyzer code {original}" : "")
                    + " (R315168 appendix K)", o.Specimen.SpecimenId);
            })
            .ToList();
        return errors.Count == 0 ? interpretation : interpretation with { Diagnostics = [.. interpretation.Diagnostics, .. errors] };
    }

    protected override OrderContext ReadOrder(Lis02Record record)
    {
        var order = base.ReadOrder(record);
        if (record.Field(3).Length == 0 && record.Text(4, 3).Trim().Length > 0)
            return order with { SpecimenId = record.Text(4, 3).Trim(), InstrumentSpecimenId = record.Field(4),
                Rack = NullIfEmpty(record.Text(4, 1)), Position = NullIfEmpty(record.Text(4, 2)) };
        return order with { Rack = NullIfEmpty(record.Text(3, 2)), Position = NullIfEmpty(record.Text(3, 3)) };
    }

    protected override SpecimenRef Specimen(OrderContext order) =>
        base.Specimen(order) with { Position = order.Rack is null ? null : $"{order.Rack}^{order.Position}" };

    protected override InstrumentObservation ReadResult(InstrumentRef instrument, OrderContext order, Lis02Record result, TraceRef trace,
        List<string> comments, List<Atsm.Abstractions.Diagnostics.AtsmDiagnostic> diagnostics)
    {
        var observation = base.ReadResult(instrument, order, result, trace, comments, diagnostics);
        if (UwamIndicators.IsResearch(observation)) observation = observation with
        { Value = observation.Value with { Numeric = null, Comparator = null, Coded = new(observation.Value.RawText, null, VendorSystem + ":research:" + observation.Test.Vendor.Code) } };
        if (UwamIndicators.IsReview(observation)) observation = observation with
        { Flags = [.. observation.Flags, new(observation.Test.Vendor.Code, null, VendorSystem + ":review-comment")] };
        var symbol = InstrumentUnits.Symbol(observation.Value);
        if (symbol is not null) observation = observation with { Value = observation.Value with { Unit = new(symbol, symbol, VendorSystem + ":unit") } };
        // R315168 pp.39-40: identifier combines lot/level/method and qualitative output can omit level.
        // Keep it whole until external lot metadata supplied by the host resolves the components.
        return order.Role != SpecimenRole.Control ? observation : observation with { Quality = new(
            new(QualityMaterialKind.Control, order.SpecimenId), ModuleId: NullIfEmpty(result.Text(14)), Method: AnalysisMethod(result))
        { SourceFields = new Dictionary<string, string> { ["O.12"] = "Q", ["O.3"] = order.SpecimenId, ["R.3"] = result.Field(3), ["R.4"] = result.Field(4), ["R.14"] = result.Field(14) } } };
    }

    protected override string? ResultKind(Lis02Record result)
    {
        var type = result.Text(3, 7);
        var format = result.Text(4, 2);
        return format is "RAW" or "MAINFORMAT" or "RAW_Q" or "RAW_C" or "RAW_R1" or "RAW_R2" ? $"{type}/{format}" : NullIfEmpty(type);
    }

    protected override string InstrumentStatus(Lis02Record result, OrderContext order) => order.ReportType;

    protected override IReadOnlyList<CodedValue> Flags(Lis02Record result) =>
        result.Components(7).Select((f, i) => new CodedValue(f.Trim(), null, VendorSystem + (i == 0 ? ":analyzer-flag" : ":lh-flag")))
            .Where(f => f.Code.Length > 0)
            .ToList();

    protected override Dictionary<string, string> Identity(Lis02Record result, OrderContext order)
    {
        var identity = base.Identity(result, order);
        identity["analysis.method"] = AnalysisMethod(result) ?? "";
        identity["result.identifier.raw"] = result.Field(3);
        identity["result.value.raw"] = result.Field(4);
        identity["result.unit.raw"] = result.Field(5);
        identity["dilution"] = result.Text(3, 6);
        if (result.Field(9).Length > 0)
            identity["ud10.cell"] = result.Field(9);
        return identity;
    }
    private static string? AnalysisMethod(Lis02Record result) => result.Text(3, 5) is "A" or "M" ? result.Text(3, 5) : null;

    protected override OrderQuery? ReadQuery(InstrumentRef instrument, Lis02Record record, TraceRef trace)
    {
        var sample = record.Text(3).Trim();
        return sample.Length == 0 ? null : new OrderQuery(Guid.CreateVersion7(), instrument, [new SpecimenRef(sample)], record.Field(13), trace);
    }

    public override IReadOnlyList<Lis02Record> BuildQueryAnswer(OrderQuery query, OrderQueryAnswer answer)
    {
        if (answer.State == QueryAnswerState.Orders && answer.Orders.Count > 0)
            return base.BuildQueryAnswer(query, answer);
        // R315168 5.3.2.2: no order → O record with the sample number and no analysis parameters.
        var records = new List<Lis02Record> { Header() };
        var sequence = 1;
        foreach (var specimen in query.Specimens)
        {
            records.Add(new Lis02RecordBuilder('P', Delimiters).SetText(2, sequence.ToString(CultureInfo.InvariantCulture)).Build());
            records.Add(new Lis02RecordBuilder('O', Delimiters).SetText(2, "1").SetText(3, specimen.SpecimenId).SetText(12, "N").SetText(26, "O").Build());
            sequence++;
        }
        records.Add(new Lis02RecordBuilder('L', Delimiters).SetText(2, "1").SetText(3, "N").Build());
        return records;
    }

    protected override Lis02Record OrderRecord(int sequence, WorkOrder order, string reportType) =>
        // U-WAM accepts only O (new order) as report type from the host and keys orders by sample number and order number (O.19).
        new Lis02RecordBuilder('O', Delimiters)
            .SetText(2, sequence.ToString(CultureInfo.InvariantCulture))
            .SetText(3, order.Specimen.SpecimenId)
            .SetRepeats(5, order.Tests.Select(t => new[] { null, null, null, t.Vendor.Code }))
            .SetText(6, order.Priority == OrderPriority.Stat ? "S" : "R")
            .SetText(12, order.Action switch { OrderAction.Add => "A", OrderAction.Cancel => "C", _ => "N" })
            .SetText(19, order.OrderId)
            .SetText(26, "O")
            .Build();
}
