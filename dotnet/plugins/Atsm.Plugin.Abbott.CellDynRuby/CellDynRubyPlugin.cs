using System.Globalization;
using System.Text;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugin.Abbott.CellDynRuby;

/// <summary>
/// CELL-DYN Ruby. No vendor manual is available in this repository yet: the profile and the dialect are derived from
/// production traces and from the legacy host plugin, and every point not confirmed by the manual is marked as such.
/// </summary>
public sealed class CellDynRubyPlugin : IInstrumentPlugin
{
    public const string PluginId = "abbott.celldyn-ruby";
    public const string ProfileId = "abbott.celldyn-ruby.lis2a";

    public IReadOnlyList<AstmProfile> Profiles { get; } =
    [
        new AstmProfile(
            ProfileId,
            "0.1.0",
            Lis01Options.Default,
            Encoding.Latin1,
            new HashSet<AstmWorkflow> { AstmWorkflow.UnsolicitedResults, AstmWorkflow.HostQuery, AstmWorkflow.OrderDownload },
            TimeSpan.FromSeconds(10),
            OwnershipEnforcement.Advisory,
            [
                "Trace: the analyzer declares LIS2-A in H.13 and sends each whole message in one frame chain",
                "Trace: idle link checks as empty transmissions (ENQ, EOT)",
                "Trace: every host message framed one record per frame (legacy host) was answered with C|1|I|Parsing Error^RecordID|G and L|1|E; host framing per message is a hypothesis to confirm with the manual or a test analyzer",
                "Timers: CLSI LIS01-A2 defaults, not confirmed by the vendor manual",
            ])
        {
            InstrumentFraming = AstmFraming.FramePerMessage,
            HostFraming = AstmFraming.FramePerMessage,
        },
    ];

    public PluginDescriptor Describe() => new(
        PluginId,
        "0.1.0",
        PluginApi.Version,
        "Abbott",
        ["CELL-DYN Ruby"],
        ["Production traces (local, anonymized for the corpus)", "Legacy host Plugin_Celldyn_Ruby 1.0.0.0 (legacy reference)"]);

    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context)
    {
        if (profile.Id != ProfileId)
            throw new ArgumentException($"Unknown profile {profile.Id}.", nameof(profile));
        return new CellDynRubyDialect(context);
    }
}

/// <summary>
/// Differences with the standard observed in traces: H.5 is serial^model^software^version (serial padded with spaces);
/// R.3 is ^^^panel^^^parameter; R.4 = FLAG marks an instrument alarm named by the parameter component; units are not sent;
/// the result time is in R.12 and R.14 repeats the serial; R.9 is F, W, X or empty; Q.3 = ^ALL asks for every pending order.
/// </summary>
public sealed class CellDynRubyDialect(DialectContext context) : StandardAstmDialect(context, CodeSystems.Vendor(CellDynRubyPlugin.PluginId))
{
    public const string InstrumentAlarm = "ATSM-RUBY-ALARM";
    public const string RejectedByAnalyzer = "ATSM-RUBY-REJECTED";

    public override AstmInterpretation Interpret(Lis02Message message, TraceRef trace)
    {
        var interpretation = base.Interpret(message, trace);
        var diagnostics = interpretation.Diagnostics.ToList();

        // Alarms travel as result records whose value is FLAG: they qualify the sample, they are not measurements.
        var alarms = interpretation.Observations.Where(o => o.Value.RawText.Trim().Equals("FLAG", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var alarm in alarms)
            diagnostics.Add(new AtsmDiagnostic(InstrumentAlarm, AtsmSeverity.Warning, $"Analyzer alarm {alarm.Test.Vendor.Code}", alarm.Specimen.SpecimenId));
        var alarmsBySpecimen = alarms.ToLookup(a => a.Specimen.SpecimenId, a => new CodedValue(a.Test.Vendor.Code, null, VendorSystem + ":alarm"));
        var observations = interpretation.Observations.Except(alarms)
            .Select(o => alarmsBySpecimen.Contains(o.Specimen.SpecimenId) ? o with { Flags = [.. o.Flags, .. alarmsBySpecimen[o.Specimen.SpecimenId]] } : o)
            .ToList();

        // The analyzer answers a message it could not parse with a comment and termination code E.
        var comment = message.OfType('C').FirstOrDefault();
        if (message.Records.Count > 0 && message.Records[^1].Type == 'L' && message.Records[^1].Field(3) == "E")
            diagnostics.Add(new AtsmDiagnostic(RejectedByAnalyzer, AtsmSeverity.Error,
                $"Analyzer rejected the previous host message: {comment?.Text(4) ?? "(no comment)"}"));

        return interpretation with { Observations = observations, Diagnostics = diagnostics };
    }

    protected override InstrumentRef? ReadInstrument(Lis02Record? header)
    {
        if (header is null || header.Text(5, 2).Length == 0)
            return null;
        return Context.Instrument with
        {
            Manufacturer = "Abbott",
            Model = header.Text(5, 2).Trim(),
            SerialNumber = NullIfEmpty(header.Text(5, 1).Trim()),
            SoftwareVersion = NullIfEmpty(header.Text(5, 3).Trim()),
        };
    }

    protected override string TestCode(Lis02Record result)
    {
        var parameter = result.Text(3, 7).Trim();
        return parameter.Length > 0 ? parameter : base.TestCode(result);
    }

    /// <summary>Panel of the parameter (for example CBC), from R.3 component 4.</summary>
    protected override string? ResultKind(Lis02Record result) => NullIfEmpty(result.Text(3, 4).Trim());

    protected override Dictionary<string, string> Identity(Lis02Record result, OrderContext order)
    {
        var identity = base.Identity(result, order);
        if (identity.TryGetValue("instrument.id", out var serial))
            identity["instrument.id"] = serial.Trim();
        return identity;
    }

    protected override InstrumentObservation ReadResult(InstrumentRef instrument, OrderContext order, Lis02Record result, TraceRef trace,
        List<string> comments, List<AtsmDiagnostic> diagnostics)
    {
        var observation = base.ReadResult(instrument, order, result, trace, comments, diagnostics);
        // R.13 is empty in the traces; the analyzer reports the time in R.12.
        return observation.ResultTime is null && result.Field(12).Length > 0
            ? observation with { ResultTime = ParseTimestamp(result.Field(12), diagnostics) }
            : observation;
    }

    protected override OrderQuery? ReadQuery(InstrumentRef instrument, Lis02Record record, TraceRef trace)
    {
        var id = record.Text(3, 2).Trim();
        if (id.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return new OrderQuery(Guid.CreateVersion7(), instrument, [], "ALL", trace);
        return base.ReadQuery(instrument, record, trace);
    }

    public override IReadOnlyList<Lis02Record> BuildQueryAnswer(OrderQuery query, OrderQueryAnswer answer) =>
        // Not confirmed by the manual: standard LIS02-A2 answers (orders, or termination code I when there is nothing).
        base.BuildQueryAnswer(query, answer);

    protected override Lis02Record OrderRecord(int sequence, WorkOrder order, string reportType) =>
        new Lis02RecordBuilder('O', Delimiters)
            .SetText(2, sequence.ToString(CultureInfo.InvariantCulture))
            .SetText(3, order.Specimen.SpecimenId)
            .SetRepeats(5, order.Tests.Select(t => new[] { null, null, null, t.Vendor.Code }))
            .SetText(6, order.Priority == OrderPriority.Stat ? "S" : "R")
            .SetText(12, order.Action == OrderAction.Cancel ? "C" : "N")
            .SetText(16, order.SpecimenType)
            .SetText(26, reportType)
            .Build();
}
