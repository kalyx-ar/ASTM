using System.Text;
using Astm.Protocol.Lis01;

namespace Astm.Plugins;

/// <summary>Plugin for instruments that follow CLSI LIS01-A2 / LIS02-A2 without known deviations.</summary>
public sealed partial class StandardAstmPlugin : IInstrumentPlugin, IInstrumentCommandPlugin, IInstrumentOperationPolicyProvider
{
    public IReadOnlyList<InstrumentOperationProfile> OperationProfiles(string instrumentProfileId)
    {
        if (instrumentProfileId != ProfileId) throw new ArgumentException("Unknown instrument profile.", nameof(instrumentProfileId));
        var policy = new AstmResultsMessageOperationPolicy();
        return [new(policy.Id, "One unsolicited LIS02 results message; queries require a correlated workflow policy", policy.Evidence)];
    }

    public IAstmOperationPolicy CreateOperationPolicy(string instrumentProfileId, string operationProfileId)
    {
        if (!OperationProfiles(instrumentProfileId).Any(p => p.Id == operationProfileId))
            throw new ArgumentException("Unknown operation profile.", nameof(operationProfileId));
        return new AstmResultsMessageOperationPolicy();
    }

    public IReadOnlyList<InstrumentCommandDescriptor> Commands(string profileId) => AstmOrderCommands.Describe(this, profileId);
    public IReadOnlyList<Astm.Protocol.Lis02.Lis02Record> BuildAstmCommand(string profileId, InstrumentCommandRequest request, DialectContext context) => AstmOrderCommands.Build(this, profileId, request, context);
    public const string PluginId = "clsi.lis02";
    public const string ProfileId = "clsi.lis01-a2.lis02-a2";

    public IReadOnlyList<AstmProfile> Profiles { get; } =
    [
        new AstmProfile(
            ProfileId,
            "1.0.0",
            Lis01Options.Default,
            Encoding.Latin1,
            new HashSet<AstmWorkflow> { AstmWorkflow.UnsolicitedResults, AstmWorkflow.HostQuery, AstmWorkflow.OrderDownload },
            TimeSpan.FromSeconds(10),
           
            ["CLSI LIS01-A2 default timers", "CLSI LIS02-A2 field positions"])
        {
            OperationRules = new()
            {
                PatientFields = ["id", "familyName", "givenName", "birthDate", "administrativeSex", "physician", "location"],
                WireBindings = new Dictionary<string, string>
                {
                    ["patient.id"] = "P.3", ["patient.name"] = "P.6", ["patient.physician"] = "P.14",
                    ["patient.location"] = "P.26", ["specimen.specimenId"] = "O.3", ["tests.vendor.code"] = "O.5.4",
                },
            },
        },
    ];

    public PluginDescriptor Describe() => new(PluginId, "0.1.0", PluginApi.Version, "(any)", ["CLSI LIS01-A2 / LIS02-A2 conforming instruments"], ["CLSI LIS01-A2", "CLSI LIS02-A2"]);

    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context) => new StandardAstmDialect(context, Astm.Formats.Medical.Model.CodeSystems.Vendor(PluginId));
}
