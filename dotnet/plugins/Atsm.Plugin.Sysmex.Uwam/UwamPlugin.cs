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

namespace Atsm.Plugin.Sysmex.Uwam;

public sealed class UwamPlugin : IInstrumentPlugin, ITraceLogAdapterProvider
{
    public const string PluginId = "sysmex.uwam";
    public const string WithChemistryProfile = "sysmex.uwam.lis2a2.with-chemistry";
    public const string WithoutChemistryProfile = "sysmex.uwam.lis2a2.without-chemistry";

    private static readonly string[] Evidence =
    [
        "Sysmex R315168 U-WAM ASTM Host Online Specifications rev. 22, 5.2.4: one record per message, at most 240 characters per frame text",
        "R315168 5.2.5: 15 s establishment, 30 s receiver; order inquiry answered within 8 s (with chemistry analyzer) or 18 s (without)",
        "R315168 5.2.5: in E1381-95 mode U-WAM performs no timeout processing (not covered by these profiles)",
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
        ["U-WAM (UF-5000, UC-3500, UD-10)"],
        ["Sysmex R315168 U-WAM ASTM Host Online Specifications, revision 22"]);

    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context)
    {
        if (profile.Id is not (WithChemistryProfile or WithoutChemistryProfile))
            throw new ArgumentException($"Unknown profile {profile.Id}.", nameof(profile));
        return new UwamDialect(context);
    }

    private static AstmProfile Profile(string id, TimeSpan budget) => new(
        id,
        "1.0.0",
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
public sealed class UwamDialect(DialectContext context) : StandardAstmDialect(context, CodeSystems.Vendor(UwamPlugin.PluginId))
{
    protected override OrderContext ReadOrder(Lis02Record record) =>
        base.ReadOrder(record) with { Rack = NullIfEmpty(record.Text(3, 2)), Position = NullIfEmpty(record.Text(3, 3)) };

    protected override SpecimenRef Specimen(OrderContext order) =>
        base.Specimen(order) with { Position = order.Rack is null ? null : $"{order.Rack}^{order.Position}" };

    protected override string? ResultKind(Lis02Record result)
    {
        var type = result.Text(3, 7);
        var format = result.Text(4, 2);
        return format.Length == 0 ? NullIfEmpty(type) : $"{type}/{format}";
    }

    protected override string InstrumentStatus(Lis02Record result, OrderContext order) => order.ReportType;

    protected override IReadOnlyList<CodedValue> Flags(Lis02Record result) =>
        result.Components(7).Where(f => f.Trim().Length > 0)
            .Select((f, i) => new CodedValue(f.Trim(), null, VendorSystem + (i == 0 ? ":analyzer-flag" : ":lh-flag")))
            .ToList();

    protected override Dictionary<string, string> Identity(Lis02Record result, OrderContext order)
    {
        var identity = base.Identity(result, order);
        identity["analysis.method"] = result.Text(3, 5);
        identity["dilution"] = result.Text(3, 6);
        if (result.Field(9).Length > 0)
            identity["ud10.cell"] = result.Field(9);
        return identity;
    }

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
