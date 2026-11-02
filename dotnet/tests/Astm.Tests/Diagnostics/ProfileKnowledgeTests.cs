using System.Text;
using System.Text.Json;
using Astm.Diagnostics;
using Astm.Formats.Medical;
using Astm.Operations;
using Astm.Plugin.Abbott.Architect;
using Astm.Plugin.Sysmex.Cs2500;
using Astm.Plugin.Sysmex.XnL;
using Astm.Plugins;

namespace Astm.Tests.Diagnostics;

public class ProfileKnowledgeTests
{
    [Fact]
    public void Every_real_bundled_profile_has_scoped_knowledge_sources_and_separate_implementation_status()
    {
        var catalog = new DiagnosticCatalog();
        foreach (var profile in catalog.Profiles.Where(p => p.Manufacturer is not ("Generic" or "Testing")
            && p.AstmPlugin is not StandardAstmPlugin && p.AstmPlugin is not Astm.Plugin.Testing.Synthetic.SyntheticAstmPlugin))
        {
            Assert.NotEmpty(profile.Knowledge);
            Assert.Equal(profile.Knowledge.Count, profile.Knowledge.Select(f => f.Id).Distinct().Count());
            IMedicalOperationProvider provider = profile.IsHl7
                ? new ArchitectHl7Plugin(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeZoneInfo.Utc)
                : (IMedicalOperationProvider)((object?)profile.AstmPlugin ?? profile.ContentPlugin ?? (object)profile.TracePlugin!);
            var operations = provider.CreateOperations(profile.Id, new(new("SYN"), TimeProvider.System));
            foreach (var fact in profile.Knowledge)
            {
                Assert.Equal(profile.Id, fact.ProfileId); Assert.NotEmpty(fact.Title.Evidence); Assert.NotEmpty(fact.Detail.Evidence);
                Assert.Equal(fact.Title.SourceAssembly, fact.Detail.SourceAssembly); Assert.StartsWith("Astm.Plugin.", fact.Title.SourceAssembly);
                if (fact.OperationPurpose is { } purpose)
                { Assert.Equal(InstrumentKnowledgeSupport.Implemented, fact.Support); Assert.NotNull(operations.Select(purpose)); }
            }
            Assert.Empty(((IInstrumentProfileKnowledge)provider).DescribeKnowledge("different.profile"));
        }
        var facts = catalog.Profiles.SelectMany(p => p.Knowledge).ToArray();
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(facts, InstrumentKnowledgeJsonContext.Default.InstrumentProfileFactArray),
            InstrumentKnowledgeJsonContext.Default.InstrumentProfileFactArray)!;
        Assert.Equal(facts, copy);
        Assert.All(facts.SelectMany(f => new[] { f.Title, f.Detail }).GroupBy(t => (t.SourceAssembly, t.Key)), g =>
            Assert.Single(g.Select(t => t.Fallback).Distinct()));
    }

    [Fact]
    public void Manufacturer_service_documentation_and_missing_evidence_never_become_executable_commands()
    {
        var catalog = new DiagnosticCatalog();
        var sncs = catalog.Resolve("sysmex.xnl.astm02.results").Knowledge.Single(f => f.Id == "xnl.sncs-calibration");
        Assert.Equal(InstrumentKnowledgeSupport.Documented, sncs.Support); Assert.Equal("manufacturer-service", sncs.Channel); Assert.Null(sncs.OperationPurpose);
        Assert.Equal(InstrumentKnowledgeSupport.Excluded, catalog.Resolve(ArchitectPlugin.AsiProfileId).Knowledge.Single(f => f.Topic == "calibration").Support);
        Assert.Equal(InstrumentKnowledgeSupport.Unconfirmed, catalog.Resolve("sysmex.cs2500.astm02.results").Knowledge.Single(f => f.Id == "cs2500.calibration-query").Support);
        Assert.DoesNotContain(catalog.Profiles, p => p.Id.Contains("suit", StringComparison.OrdinalIgnoreCase));
        foreach (var plugin in new IMedicalOperationProvider[] { new XnLPlugin(), new Cs2500Plugin() })
        {
            var operations = plugin.CreateOperations(plugin.MedicalProfiles[0], new(new("SYN"), TimeProvider.System));
            Assert.DoesNotContain(operations.Describe(operations.Profile.Id), o => o.Id == MedicalPurposes.QueryInventory || o.Id == MedicalPurposes.ReceiveCalibration);
        }
        Assert.Contains("SNCS", DiagnosticText.Knowledge(sncs));
        Assert.Contains(sncs.Detail.Evidence, DiagnosticText.Knowledge(sncs));
    }

    [Fact]
    public void Knowledge_catalog_rejects_undocumented_or_duplicate_entries_and_cannot_link_an_unimplemented_operation()
    {
        var fact = InstrumentProfileKnowledge.Fact("test", "calibration", "lis02", InstrumentKnowledgeSupport.Documented,
            "Título", "Detalle", "Manual §1");
        Assert.Throws<ArgumentException>(() => InstrumentProfileKnowledge.Bind(typeof(Cs2500Plugin), "test", fact, fact));
        Assert.Throws<ArgumentException>(() => InstrumentProfileKnowledge.Bind(typeof(Cs2500Plugin), "test", fact with { OperationPurpose = "calibrate" }));
        Assert.Throws<ArgumentException>(() => InstrumentProfileKnowledge.Bind(typeof(Cs2500Plugin), "test", fact with { Detail = fact.Detail with { Evidence = "" } }));
        var bound = InstrumentProfileKnowledge.Bind(typeof(Cs2500Plugin), "test", fact);
        Assert.True(((IList<InstrumentProfileFact>)bound).IsReadOnly);
        Assert.Equal("Astm.Plugin.Sysmex.Cs2500", bound[0].Title.SourceAssembly);
    }

    private static DiagnosticAnalysis ReadCs(string raw, string kind = "1", string flags = "N", string replication = "")
    {
        var profile = new DiagnosticCatalog().Resolve("sysmex.cs2500.astm02.results");
        var text = $"H|\\^&|||CS-2500\rP|1\rO|1||01^02^SYN\rR|1|^^^041^SYN^100^{kind}^^^^{replication}|{raw}|s||{flags}||F\rL|1|N\r";
        return DiagnosticReader.Analyze(DiagnosticReader.Read(Encoding.ASCII.GetBytes(text), DiagnosticInputFormat.AstmMessage, profile,
            "synthetic", TimeSpan.Zero), profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("XXXXXX", "CALIBRATION", "Sin curva de calibración validada")]
    [InlineData("12X.34", "CALIBRATION", "Sin curva de calibración validada")]
    [InlineData("******", "ANALYSIS", "Error de análisis")]
    [InlineData("//////", "AVERAGE", "Error al calcular el promedio")]
    [InlineData("++++++", "OVERFLOW", "Desbordamiento de visualización")]
    [InlineData("------", "CALCULATION", "Error de cálculo")]
    public void Documented_measurement_masks_are_visible_when_extended_error_information_is_disabled(string raw, string code, string text)
    {
        var analysis = ReadCs(raw, replication: "3"); var observation = Assert.Single(analysis.Observations);
        Assert.Null(observation.Value.Numeric); Assert.Equal(raw, observation.Value.RawText); Assert.Equal("N", observation.VendorIdentity["flags.raw"]);
        Assert.Equal("3", observation.VendorIdentity["analysis.replications"]);
        Assert.Contains(analysis.Diagnostics, d => d.Code == "ASTM-CS2500-MASK-" + code);
        Assert.Equal(text, ProtocolExplainer.Status(observation, new DiagnosticCatalog().Resolve("sysmex.cs2500.astm02.results")));
        Assert.True(ProtocolExplainer.RequiresAttention(observation, new Cs2500Plugin()));
        Assert.DoesNotContain(observation.Flags, f => f.Code == "0000.4000.0030");
    }

    [Theory]
    [InlineData("-12.3", "1")]
    [InlineData("+12.3", "1")]
    [InlineData("0.0", "1")]
    [InlineData("X*", "1")]
    [InlineData("file-X.png", "1")]
    [InlineData("X", "IMAGE")]
    [InlineData("0.X", "1")]
    public void Signed_numbers_images_mixed_masks_and_unrecognized_contexts_are_not_misclassified(string raw, string kind)
    {
        var analysis = ReadCs(raw, kind);
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Code.StartsWith("ASTM-CS2500-MASK-", StringComparison.Ordinal));
        Assert.DoesNotContain(analysis.Observations[0].Flags, f => f.System.EndsWith(":measurement-mask", StringComparison.Ordinal));
    }

    [Fact]
    public void Detailed_error_codes_keep_precedence_over_the_fallback_mask_and_lookup_is_contextual()
    {
        var source = ReadCs("XXXXXX", flags: "N^[0000.4000.0030 SOURCE]");
        Assert.DoesNotContain(source.Diagnostics, d => d.Code.StartsWith("ASTM-CS2500-MASK-", StringComparison.Ordinal));
        var plugin = new Cs2500Plugin();
        Assert.NotNull(plugin.Lookup(new(plugin.Profiles[0].Id, "R", "4"), "X"));
        Assert.Null(plugin.Lookup(new(plugin.Profiles[0].Id, "R", "7.1"), "X"));
        Assert.Null(plugin.Lookup(new(plugin.Profiles[0].Id, "R", "4", "XN-550"), "X"));
    }

    [Fact]
    public void An_unrelated_instrument_error_does_not_hide_a_specific_calibration_mask()
    {
        var source = ReadCs("XXXXXX", flags: "N^^[34422 LOW-REAGENT]");
        Assert.Contains(source.Diagnostics, d => d.Code == "ASTM-CS2500-MASK-CALIBRATION");
        Assert.Contains(source.Observations[0].Flags, f => f.Code == "34422");
        Assert.Contains(source.Observations[0].Flags, f => f.Code == "X" && f.System.EndsWith(":measurement-mask", StringComparison.Ordinal));
    }
}
