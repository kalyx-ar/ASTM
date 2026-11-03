using Astm.Plugin.Abbott.Architect;
using Astm.Plugin.Abbott.CellDynRuby;
using Astm.Plugin.BioRad.Cfx;
using Astm.Plugin.Diagast.Qwalys;
using Astm.Plugin.Sysmex.Cs2500;
using Astm.Plugin.Sysmex.Urinalysis;
using Astm.Plugin.Sysmex.XnL;
using Astm.Plugins;
using Astm.Service;

namespace Astm.Service.Host;

public static class BundledServiceProfiles
{
    public static IReadOnlyList<ServiceProfile> Create()
    {
        var urinalysis = new SysmexUrinalysisPlugin();
        IInstrumentPlugin[] astm = [new StandardAstmPlugin(), new ArchitectPlugin(), new CellDynRubyPlugin(), urinalysis,
            new XnLPlugin(), new Cs2500Plugin(), new QwalysPlugin()];
        var profiles = astm.SelectMany(plugin => plugin.Profiles.Select(profile => ServiceProfiles.Astm(plugin, profile))).ToList();
        IInstrumentTracePlugin trace = urinalysis;
        profiles.AddRange(trace.Profiles.Select(profile => ServiceProfiles.Trace(trace, profile)));
        IInstrumentContentPlugin content = new CfxPlugin();
        profiles.AddRange(content.Profiles.Select(profile => ServiceProfiles.Content(content, profile)));
        profiles.Add(ServiceProfiles.Hl7(ArchitectHl7Plugin.ProfileId, "1.2.0",
            new("abbott.architect.hl7", "0.2.0", PluginApi.Version, "Abbott", ["ARCHITECT System"],
                ["ARCHITECT System HL7 Interface Manual 206797-101; no conformidad LAW completa"]),
            ArchitectHl7Plugin.ProfileKnowledge, config => new ArchitectHl7Plugin(TimeSpan.FromMilliseconds(config.QueryBudgetMs!.Value),
                TimeSpan.FromMilliseconds(config.ResponseTimeoutMs!.Value), TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone))));
        return profiles.AsReadOnly();
    }
}
