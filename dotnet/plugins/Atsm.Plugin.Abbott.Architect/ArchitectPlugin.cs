using System.Text;
using Atsm.Abstractions.Channels;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;

namespace Atsm.Plugin.Abbott.Architect;

/// <summary>How the dialect fills the patient sex field of downloaded orders.</summary>
public enum SexPolicy
{
    /// <summary>Send M or F when the host provides it, U otherwise.</summary>
    Send,

    /// <summary>Always U. Reproduces the legacy host plugin, whose condition <c>sex != M || sex != F</c> was always true.</summary>
    AlwaysUnknown,
}

public sealed record ArchitectDialectOptions
{
    public static readonly ArchitectDialectOptions Default = new();

    public SexPolicy Sex { get; init; } = SexPolicy.Send;

    public TimeZoneInfo InstrumentTimeZone { get; init; } = TimeZoneInfo.Local;
}

public sealed class ArchitectPlugin(ArchitectDialectOptions? options = null) : IInstrumentPlugin
{
    public const string PluginId = "abbott.architect";
    public const string AsiProfileId = "abbott.architect.asi-rs232";

    private static readonly Lazy<Encoding> CodePage850 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(850);
    });

    private readonly ArchitectDialectOptions _options = options ?? ArchitectDialectOptions.Default;

    public IReadOnlyList<AstmProfile> Profiles { get; } =
    [
        new AstmProfile(
            AsiProfileId,
            "1.1.0",
            Lis01Options.Default with { BusyRetryDelay = TimeSpan.FromSeconds(15) },
            CodePage850.Value,
            new HashSet<AstmWorkflow> { AstmWorkflow.UnsolicitedResults, AstmWorkflow.HostQuery, AstmWorkflow.OrderDownload },
            QueryAnswerBudget: TimeSpan.FromSeconds(10),
            RequiredOwnership: OwnershipEnforcement.Advisory,
            Evidence:
            [
                "Abbott 91407-108 (2012) §1 Data Link Layer: frames of 247 characters, six retransmissions, timers 15/20/30 s",
                "Abbott 91407-108 §4: 15 s between establishment attempts; EOT reply treated as ACK by the instrument",
                "Abbott 91407-108 §4 Table 4.1: code page subset of MS-DOS 850",
            ]),
    ];

    public PluginDescriptor Describe() => new(
        PluginId,
        "0.1.0",
        PluginApi.Version,
        "Abbott",
        ["ARCHITECT i1000SR", "ARCHITECT i2000SR", "ARCHITECT c4000", "ARCHITECT c8000", "ARCHITECT c16000", "ARCHITECT ci-series"],
        ["Abbott Standard Interface RS-232 Manual, ARCHITECT System Edition, 91407-108, January 2012", "Legacy host Plugin_Architect 1.0.0.1 (legacy reference)"]);

    public IAstmDialect CreateDialect(AstmProfile profile, DialectContext context)
    {
        if (profile.Id != AsiProfileId)
            throw new ArgumentException($"Unknown profile {profile.Id}.", nameof(profile));
        return new ArchitectDialect(context, _options);
    }
}
