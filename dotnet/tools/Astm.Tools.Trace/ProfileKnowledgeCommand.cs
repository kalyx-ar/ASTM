using System.Text.Json;
using Astm.Diagnostics;
using Astm.Plugins;

namespace Astm.Tools.Trace;

internal static class ProfileKnowledgeCommand
{
    internal static int Run(string profileId)
    {
        var catalog = new DiagnosticCatalog();
        var profiles = profileId == "all" ? catalog.Profiles : [catalog.Resolve(profileId)];
        var facts = profiles.SelectMany(p => p.Knowledge).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(facts, InstrumentKnowledgeJsonContext.Default.InstrumentProfileFactArray));
        return 0;
    }
}
