using Astm.Abstractions.Tracing;
using Astm.Diagnostics;
using Astm.Plugin.Sysmex.Urinalysis;
using System.Text;

namespace Astm.Tests.Diagnostics;

public sealed class UrinalysisKnowledgeTests
{
    [Fact]
    public void Codes_only_resolve_inside_their_documented_protocol_record_and_field()
    {
        var plugin = new SysmexUrinalysisPlugin();
        Assert.Equal("ASTM-UC3500-ER04", plugin.Lookup(new(SysmexUrinalysisPlugin.Uc3500Profile, "ER", "code"), "ER04")!.FindingCode);
        Assert.Null(plugin.Lookup(new(SysmexUrinalysisPlugin.WithChemistryProfile, "ER", "code"), "ER04"));
        Assert.Null(plugin.Lookup(new(SysmexUrinalysisPlugin.Uc3500Profile, "RESULT", "error"), "ER04"));
        Assert.Null(plugin.Lookup(new(SysmexUrinalysisPlugin.Uc3500Profile, "ER", "code"), "8010"));
        Assert.Equal("Error relacionado con las tiras reactivas", plugin.Lookup(new(SysmexUrinalysisPlugin.Uc3500Profile, "RESULT", "error"), "8010")!.Text.Fallback);
        Assert.Null(plugin.Lookup(new(SysmexUrinalysisPlugin.Uc3500Profile, "ER", "code"), "ER99"));
    }

    [Fact]
    public void Selecting_UWAM_is_enough_to_resolve_direct_UC_and_can_switch_back_to_ASTM_in_the_same_family()
    {
        var catalog = new DiagnosticCatalog(); var uwam = catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var direct = DiagnosticReader.Read("RQ02,SYN-0001      01\r\n"u8.ToArray(), DiagnosticInputFormat.WireBytes, uwam, "SYN", TimeSpan.Zero);
        var resolved = catalog.ResolveInterpretation(direct, uwam);
        Assert.Equal(SysmexUrinalysisPlugin.Uc3500Profile, resolved.Id); Assert.Equal(uwam.InstrumentId, resolved.InstrumentId);
        var astm = DiagnosticReader.Read("H|\\^&|||UF-5000\rP|1\rL|1\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, uwam, "SYN", TimeSpan.Zero);
        Assert.Equal(uwam.Id, catalog.ResolveInterpretation(astm, resolved).Id);
        Assert.Single(catalog.Instruments, i => i.Id == SysmexUrinalysisPlugin.PluginId);
        Assert.Same(uwam.AstmPlugin, resolved.TracePlugin);
    }

    [Fact]
    public void A_capture_with_multiple_protocols_is_ambiguous_instead_of_discarding_one_part()
    {
        var catalog = new DiagnosticCatalog(); var uwam = catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var data = Encoding.ASCII.GetBytes("RQ02,SYN-0001      01\r\nH|\\^&|||UF-5000\rP|1\rL|1\r");
        var capture = DiagnosticReader.Read(data, DiagnosticInputFormat.WireBytes, uwam, "SYN", TimeSpan.Zero);
        Assert.Throws<InvalidDataException>(() => catalog.ResolveInterpretation(capture, uwam));
    }
}
