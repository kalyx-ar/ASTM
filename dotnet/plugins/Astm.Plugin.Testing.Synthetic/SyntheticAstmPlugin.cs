using System.Text;
using Astm.Abstractions.Model;
using Astm.Abstractions.Transport;
using Astm.Plugins;
using Astm.Protocol.Lis01;
using Astm.Protocol.Lis02;

namespace Astm.Plugin.Testing.Synthetic;

/// <summary>One test of the synthetic menu: code, unit and reference interval. Values are invented, never clinical.</summary>
public sealed record SyntheticTest(string Code, string Unit, decimal Low, decimal High, int Decimals)
{
    public string ReferenceRange => Format(Low) + " to " + Format(High);

    public string Format(decimal value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero)
        .ToString("F" + Decimals, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// ASTM plugin of the synthetic analyzer used by the integration tests. It follows CLSI LIS01-A2/LIS02-A2 field positions
/// (<see cref="StandardAstmDialect"/>) with the three workflows, so the same plugin drives order download, host query and
/// unsolicited results. Profile, menu and serial settings are declared test conditions, not evidence of any device.
/// </summary>
public sealed class SyntheticAstmPlugin : IInstrumentPlugin, IInstrumentCommandPlugin
{
    public const string PluginId = "astm.testing.synthetic";
    public const string ProfileId = "astm.testing.synthetic.analyzer";

    public static IReadOnlyList<SyntheticTest> Menu { get; } =
    [
        new("GLU", "mmol/L", 3.9m, 6.1m, 1),
        new("UREA", "mmol/L", 2.5m, 7.1m, 1),
        new("CREA", "umol/L", 53m, 106m, 0),
        new("NA", "mmol/L", 135m, 145m, 0),
        new("K", "mmol/L", 3.5m, 5.1m, 1),
        new("HGB", "g/dL", 12.0m, 17.5m, 1),
        new("CRP", "mg/L", 0m, 5m, 1),
    ];

    public IReadOnlyList<AstmProfile> Profiles { get; } =
    [
        new AstmProfile(
            ProfileId,
            "1.0.0",
            Lis01Options.Default,
            Encoding.Latin1,
            new HashSet<AstmWorkflow> { AstmWorkflow.UnsolicitedResults, AstmWorkflow.HostQuery, AstmWorkflow.OrderDownload },
            TimeSpan.FromSeconds(10),
            ["Synthetic test analyzer: CLSI LIS01-A2 default timers and LIS02-A2 field positions; menu and values invented for tests"])
        {
            Transport = new TransportKnowledge(TransportDirection.Bidirectional, ["Synthetic test condition, not a device specification"])
            {
                Serial = new SerialKnowledge(new SerialSettings(115200), [9600, 19200, 38400, 57600, 115200]),
                Hypothesis = true,
            },
        },
    ];

    public PluginDescriptor Describe() => new(PluginId, "0.1.0", PluginApi.Version, "(synthetic)", ["ASTM test analyzer"],
        ["Synthetic: no real instrument, manual or trace; used only to verify the host ↔ instrument circuit"]);

    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context) => new StandardAstmDialect(context, CodeSystems.Vendor(PluginId));

    public IReadOnlyList<InstrumentCommandDescriptor> Commands(string profileId) => AstmOrderCommands.Describe(this, profileId);

    public IReadOnlyList<Lis02Record> BuildAstmCommand(string profileId, InstrumentCommandRequest request, DialectContext context) =>
        AstmOrderCommands.Build(this, profileId, request, context);
}
