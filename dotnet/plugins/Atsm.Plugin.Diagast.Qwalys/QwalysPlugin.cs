using System.Text;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugin.Diagast.Qwalys;

public sealed partial class QwalysPlugin : IInstrumentPlugin
{
    public IReadOnlyList<AstmProfile> Profiles { get; } = [new("diagast.qwalys3.astm.results", "1.0.0", Lis01Options.Default,
        Encoding.ASCII, new HashSet<AstmWorkflow> { AstmWorkflow.UnsolicitedResults }, TimeSpan.Zero, OwnershipEnforcement.Advisory,
        ["DIAGAST QWALYS 3 Connection Manual, §2.2 E1381-95, §4.5 O.4, §4.6 R.3/R.4.7 and R.9, §4.7 C.3.",
         "Receive-only ASCII subset; extended encoding is unconfirmed. Native files are append-based and owned by the host; no hardware qualification."])];
    public PluginDescriptor Describe() => new("diagast.qwalys3", "0.1.0", PluginApi.Version, "DIAGAST", ["QWALYS 3"], Profiles[0].Evidence);
    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context) => profile.Id == Profiles[0].Id ? new QwalysDialect(context) : throw new ArgumentException("Unknown profile.", nameof(profile));
}

public sealed class QwalysDialect(DialectContext context) : IAstmDialect
{
    public AstmInterpretation Interpret(Lis02Message message, TraceRef trace)
    {
        var values = new List<InstrumentObservation>(); var findings = new List<AtsmDiagnostic>();
        string? sample = null; List<string>? comments = null;
        foreach (var record in message.Records)
        {
            if (record.Type is 'P' or 'H') { sample = null; comments = null; }
            if (record.Type == 'O') { sample = record.Text(4); comments = null; }
            if (record.Type == 'R')
            {
                if (string.IsNullOrWhiteSpace(sample)) { findings.Add(new("ATSM-QWALYS-MISSING-ORDER", AtsmSeverity.Error, "Result has no instrument specimen identifier.")); continue; }
                comments = [];
                var type = record.Text(3, 4); var raw = record.Field(4); var value = record.Text(4, 7);
                var identity = new Dictionary<string, string> { ["result.identifier"] = record.Field(3), ["well"] = record.Text(3, 5),
                    ["reagent.orDonor"] = record.Text(3, 6), ["testOrder"] = record.Text(3, 7), ["level"] = record.Text(3, 8),
                    ["reaction.support"] = record.Field(5), ["result.sequence"] = record.Field(2), ["lastModified.raw"] = record.Field(12) };
                values.Add(new(Guid.CreateVersion7(), context.Instrument, new(sample), CodeSet.Of(new(record.Field(3), record.Text(3, 7) + " / " + record.Text(3, 6), CodeSystems.Vendor("diagast.qwalys3"))),
                    new(raw, Coded: new(value, value, CodeSystems.Vendor("diagast.qwalys3") + ":reaction")), null, [], record.Field(9),
                    InstrumentStatusMeaning.Unknown, type, null, null, record.Text(11), [], comments, new(identity), trace));
            }
            if (record.Type == 'C') comments?.Add(record.Text(3));
            if (record.Type == 'Q') findings.Add(new("ATSM-QWALYS-QUERY-UNSUPPORTED", AtsmSeverity.Warning, "Receive-only profile: query preserved without an application response."));
        }
        return new(values, [], context.Instrument, findings);
    }
    public IReadOnlyList<Lis02Record> BuildQueryAnswer(OrderQuery query, Atsm.Abstractions.Hosting.OrderQueryAnswer answer) => throw new NotSupportedException("Receive-only QWALYS profile.");
    public IReadOnlyList<Lis02Record> BuildOrders(IReadOnlyList<WorkOrder> orders) => throw new NotSupportedException("Receive-only QWALYS profile.");
}
