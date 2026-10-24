using System.Text;
using Atsm.Abstractions.Channels;
using Atsm.Protocol.Lis01;

namespace Atsm.Plugins;

/// <summary>Plugin for instruments that follow CLSI LIS01-A2 / LIS02-A2 without known deviations.</summary>
public sealed class StandardAstmPlugin : IInstrumentPlugin
{
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
            OwnershipEnforcement.Advisory,
            ["CLSI LIS01-A2 default timers", "CLSI LIS02-A2 field positions"]),
    ];

    public PluginDescriptor Describe() => new(PluginId, "0.1.0", PluginApi.Version, "(any)", ["CLSI LIS01-A2 / LIS02-A2 conforming instruments"], ["CLSI LIS01-A2", "CLSI LIS02-A2"]);

    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context) => new StandardAstmDialect(context, Abstractions.Model.CodeSystems.Vendor(PluginId));
}
