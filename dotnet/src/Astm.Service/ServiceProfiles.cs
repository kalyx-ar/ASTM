using Astm.Abstractions.Transport;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Model;
using Astm.Plugins;

namespace Astm.Service;

/// <summary>Compiled registration supplied by the executable. No vendor code or dynamic DLL loading here.</summary>
public sealed record ServiceProfile(ProfileDescription Description, Func<InstrumentConfiguration, ProfileBinding> Bind);
public sealed record ProfileBinding(IMedicalOperationProvider Plugin, IMedicalOperations Operations, TimeZoneInfo TimeZone)
{
    public IInstrumentPlugin? AstmPlugin { get; init; }
    public AstmProfile? Astm { get; init; }
    public IHl7InstrumentPlugin? Hl7Plugin { get; init; }
    public Hl7Profile? Hl7 { get; init; }
    public IInstrumentTracePlugin? TracePlugin { get; init; }
    public InstrumentTraceProfile? TraceProfile { get; init; }
    public TransportKnowledge? Transport => Astm?.Transport ?? Hl7?.Transport;
}

public static class ServiceProfiles
{
    public static ServiceProfile Astm(IInstrumentPlugin plugin, AstmProfile profile) => Register(plugin, profile.Id, profile.Version, "astm",
        plugin.Describe(), config => Create(plugin, config) with { AstmPlugin = plugin, Astm = profile });
    public static ServiceProfile Content(IInstrumentContentPlugin plugin, InstrumentContentProfile profile) => Register(plugin, profile.Id,
        profile.Version, "content", plugin.Describe(), config => Create(plugin, config));
    public static ServiceProfile Trace(IInstrumentTracePlugin plugin, InstrumentTraceProfile profile) => Register(plugin, profile.Id, "1", "trace",
        plugin.Describe(), config => Create(plugin, config) with { TracePlugin = plugin, TraceProfile = profile });
    public static ServiceProfile Hl7(string id, string revision, PluginDescriptor descriptor, IReadOnlyList<InstrumentProfileFact> facts,
        Func<InstrumentConfiguration, IHl7InstrumentPlugin> factory) => new(new(id, revision, "hl7", descriptor, facts, true), config =>
        {
            if (config.QueryBudgetMs is not > 0 || config.ResponseTimeoutMs is not > 0) throw new ServiceException("profile.timing.required");
            var plugin = factory(config);
            return Create(plugin, config) with { Hl7Plugin = plugin, Hl7 = plugin.Profiles.Single(p => p.Id == id) };
        });
    private static ServiceProfile Register(IMedicalOperationProvider plugin, string id, string version, string kind,
        PluginDescriptor descriptor, Func<InstrumentConfiguration, ProfileBinding> factory) => new(new(id, version, kind, descriptor,
            (plugin as IInstrumentProfileKnowledge)?.DescribeKnowledge(id) ?? [], false), factory);
    private static ProfileBinding Create(IMedicalOperationProvider plugin, InstrumentConfiguration config)
    {
        var context = new MedicalOperationContext(new InstrumentRef(config.Id), TimeProvider.System);
        return new(plugin, plugin.CreateOperations(config.ProfileId, context), TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone));
    }
}
