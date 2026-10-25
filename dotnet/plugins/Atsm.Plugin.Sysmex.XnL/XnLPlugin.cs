using System.Text;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Model;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugin.Sysmex.XnL;

public sealed partial class XnLPlugin : IInstrumentPlugin, IInstrumentHeaderProbe
{
    // 1273-MKT rev.3, pp.34-38: H.5.1 carries the sender model.
    public bool RecognizesHeader(Lis02Record header) => header.Type == 'H' && header.Text(5, 1).Trim().ToUpperInvariant() is "XN-350" or "XN-450" or "XN-550";
    public IReadOnlyList<AstmProfile> Profiles { get; } = [new("sysmex.xnl.astm02.results", "1.1.0", Lis01Options.Default,
        Encoding.ASCII, new HashSet<AstmWorkflow> { AstmWorkflow.UnsolicitedResults }, TimeSpan.Zero, OwnershipEnforcement.Advisory,
        ["Sysmex XN-L LIS Interface Implementation Guide 1273-MKT rev.3 (2020), §7/8: ASTM1381-02/1394-97; R.3 parameter in component 5, dilution 6, R.9 instrument judgement, R.13 analysis time.",
         "§5/p.14 and pp.37-38: O.12=Q; QC-Lot Number or QC file identifier; no lot/level inferred from file numbers or XbarM identifiers.",
         "CLSI LIS01-A2 defaults for the declared link; non-ASCII encoding and device-specific timer changes are unconfirmed. Receive-only, no hardware qualification."])];
    public PluginDescriptor Describe() => new("sysmex.xnl", "0.1.0", PluginApi.Version, "Sysmex", ["XN-L series (XN-350/450/550 interface profile)"], Profiles[0].Evidence);
    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context) => profile.Id == Profiles[0].Id ? new XnLDialect(context) : throw new ArgumentException("Unknown profile.", nameof(profile));
}

public sealed class XnLDialect(DialectContext context) : StandardAstmDialect(context, CodeSystems.Vendor("sysmex.xnl"))
{
    public override AstmInterpretation Interpret(Lis02Message message, Atsm.Abstractions.Tracing.TraceRef trace)
    {
        var result = base.Interpret(message, trace);
        return message.Records.Any(r => r.Type == 'Q') ? result with { Diagnostics = result.Diagnostics.Append(
            new("ATSM-XNL-QUERY-UNSUPPORTED", Atsm.Abstractions.Diagnostics.AtsmSeverity.Warning, "Receive-only profile: query preserved without an application response.")).ToArray() } : result;
    }
    protected override OrderContext ReadOrder(Lis02Record record)
    {
        var field = record.Field(4).Length > 0 ? 4 : 3;
        return new(record.Field(2), record.Text(field, 3).Trim(), record.Field(field), record.Field(12) == "Q" ? SpecimenRole.Control : SpecimenRole.Unknown, record.Field(26))
        { Rack = record.Text(field), Position = record.Text(field, 2) };
    }
    protected override string TestCode(Lis02Record result) => result.Text(3, 5);
    protected override InstrumentObservation ReadResult(InstrumentRef instrument, OrderContext order, Lis02Record result, Atsm.Abstractions.Tracing.TraceRef trace,
        List<string> comments, List<Atsm.Abstractions.Diagnostics.AtsmDiagnostic> diagnostics)
    {
        var observation = base.ReadResult(instrument, order, result, trace, comments, diagnostics);
        if (order.Role != SpecimenRole.Control) return observation;
        var lot = order.SpecimenId.StartsWith("QC-", StringComparison.Ordinal) ? order.SpecimenId[3..] : null;
        return observation with { Quality = new(new(QualityMaterialKind.Control, order.SpecimenId, string.IsNullOrWhiteSpace(lot) ? null : lot), ModuleId: instrument.SerialNumber)
        { SourceFields = new Dictionary<string, string> { ["O.12"] = "Q", ["O.specimen"] = order.InstrumentSpecimenId ?? "", ["qc.identifierFormat"] = lot is null ? "instrument-defined; lot not inferred" : "QC-Lot Number" } } };
    }
    protected override string? ResultKind(Lis02Record result) => result.Text(3, 9);
    protected override InstrumentStatusMeaning StatusMeaning(string status) => InstrumentStatusMeaning.Unknown; // F/P refer to rerun/reflex rules, not clinical validation.
    protected override Dictionary<string, string> Identity(Lis02Record result, OrderContext order)
    {
        var identity = base.Identity(result, order);
        identity["dilution"] = result.Text(3, 6); identity["instrument.judgement"] = result.Field(9);
        identity["extended.result"] = result.Text(3, 9); return identity;
    }
    protected override OrderQuery? ReadQuery(InstrumentRef instrument, Lis02Record record, Atsm.Abstractions.Tracing.TraceRef trace) => null;
    public override IReadOnlyList<Lis02Record> BuildOrders(IReadOnlyList<WorkOrder> orders) => throw new NotSupportedException("Receive-only XN-L profile.");
}
