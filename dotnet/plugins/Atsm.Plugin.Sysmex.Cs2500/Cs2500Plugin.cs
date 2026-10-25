using System.Text;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Model;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugin.Sysmex.Cs2500;

/// <summary>Documented receive-only E1381-02 / E1394-97 profile. No speculative order handling.</summary>
public sealed partial class Cs2500Plugin : IInstrumentPlugin
{
    public IReadOnlyList<AstmProfile> Profiles { get; } = [new("sysmex.cs2500.astm02.results", "1.1.0", Lis01Options.Default,
        Encoding.ASCII, new HashSet<AstmWorkflow> { AstmWorkflow.UnsolicitedResults }, TimeSpan.Zero, OwnershipEnforcement.Advisory,
        ["Sysmex CS-2500 ASTM Host Interface Specifications for US rev.2.04 (2019), §5.2.2/5.2.5: 15/15/30 s, busy 10 s; §5.3.3.4 O.4, §5.3.3.5 R.3/R.7.",
         "rev.2.04 p.28/30: QC control identifier QC + name(7) + lot(6), O.12=Q; unknown level remains absent. Receive-only; no E1381-95 mode, orders, external-path dereferencing or hardware qualification."])];
    public PluginDescriptor Describe() => new("sysmex.cs2500", "0.1.0", PluginApi.Version, "Sysmex", ["CS-2500"], Profiles[0].Evidence);
    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context) => profile.Id == Profiles[0].Id ? new Cs2500Dialect(context) : throw new ArgumentException("Unknown profile.", nameof(profile));
}

public sealed class Cs2500Dialect(DialectContext context) : StandardAstmDialect(context, CodeSystems.Vendor("sysmex.cs2500"))
{
    public override AstmInterpretation Interpret(Lis02Message message, Atsm.Abstractions.Tracing.TraceRef trace)
    {
        var result = base.Interpret(message, trace);
        return message.Records.Any(r => r.Type == 'Q') ? result with { Diagnostics = result.Diagnostics.Append(
            new("ATSM-CS2500-QUERY-UNSUPPORTED", Atsm.Abstractions.Diagnostics.AtsmSeverity.Warning, "Receive-only profile: query preserved without an application response.")).ToArray() } : result;
    }
    protected override OrderContext ReadOrder(Lis02Record record) => new(record.Field(2), record.Text(4, 3).Trim(), record.Field(4), record.Field(12) == "Q" ? SpecimenRole.Control : SpecimenRole.Unknown, record.Field(26))
    { Rack = record.Text(4), Position = record.Text(4, 2) };
    protected override SpecimenRef Specimen(OrderContext order) => base.Specimen(order) with { Position = order.Rack + "^" + order.Position };
    protected override string? ResultKind(Lis02Record result) => result.Text(3, 7);
    protected override InstrumentObservation ReadResult(InstrumentRef instrument, OrderContext order, Lis02Record result, Atsm.Abstractions.Tracing.TraceRef trace,
        List<string> comments, List<Atsm.Abstractions.Diagnostics.AtsmDiagnostic> diagnostics)
    {
        var observation = base.ReadResult(instrument, order, result, trace, comments, diagnostics);
        // rev.2.04 p.28: fixed-width QC + control name (7) + lot (6); no level is separately transmitted.
        var encoded = order.SpecimenId;
        var material = order.Role == SpecimenRole.Control ? new QualityMaterial(QualityMaterialKind.Control,
            encoded.Length == 15 && encoded.StartsWith("QC", StringComparison.Ordinal) ? encoded.Substring(2, 7).Trim() : encoded,
            encoded.Length == 15 && encoded.StartsWith("QC", StringComparison.Ordinal) ? NullIfEmpty(encoded[9..].Trim()) : null) : null;
        return observation with { Test = CodeSet.Of(new(result.Text(3, 4), result.Text(3, 5), VendorSystem)), Dilution = result.Text(3, 6),
            Quality = material is null ? null : new(material, ModuleId: instrument.SerialNumber)
            { SourceFields = new Dictionary<string, string> { ["O.4"] = order.InstrumentSpecimenId ?? "", ["O.12"] = "Q", ["R.3"] = result.Field(3) } } };
    }
    protected override Dictionary<string, string> Identity(Lis02Record result, OrderContext order)
    {
        var identity = base.Identity(result, order);
        identity["dilution.percent"] = result.Text(3, 6); identity["analysis.resultType"] = result.Text(3, 7);
        identity["extended.request"] = result.Text(3, 8); identity["extended.result"] = result.Text(3, 9);
        identity["reflex.request"] = result.Text(3, 10); identity["flags.raw"] = result.Field(7);
        return identity;
    }
    protected override Atsm.Abstractions.Model.OrderQuery? ReadQuery(InstrumentRef instrument, Lis02Record record, Atsm.Abstractions.Tracing.TraceRef trace) => null;
    public override IReadOnlyList<Lis02Record> BuildOrders(IReadOnlyList<WorkOrder> orders) => throw new NotSupportedException("Receive-only CS-2500 profile.");
}
