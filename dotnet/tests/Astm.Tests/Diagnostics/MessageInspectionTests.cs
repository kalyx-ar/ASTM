using Astm.Diagnostics;
using Astm.Plugins;
using Astm.Plugin.Abbott.Architect;
using Astm.Protocol.Lis02;

namespace Astm.Tests.Diagnostics;

public sealed class MessageInspectionTests
{
    private static readonly DiagnosticCatalog Catalog = new();

    [Fact]
    public void Repeated_sequence_numbers_do_not_merge_patients_orders_or_messages()
    {
        var original = "H|\\^&\rP|1|PAT-SYN-A\rO|1|SYN-A\rR|1|^^^A|1\rO|1|SYN-B\rR|1|^^^B|2\r"
            + "P|1|PAT-SYN-B\rR|1|^^^ORPHAN|3\rL|1|N\rH|\\^&\rR|1|^^^ORPHAN2|4\rL|1|N\r";
        var inspected = Catalog.Resolve(StandardAstmPlugin.ProfileId).InspectMessage!(original);
        Assert.Equal(original, inspected.Original);
        var results = inspected.Records.Where(r => r.Type == "R").ToArray();
        Assert.Equal(new[] { "P@2/O@3/R@4", "P@2/O@5/R@6", "P@7/R@8", "R@11" }, results.Select(r => r.Context));
        Assert.False(results[0].UnassociatedResult); Assert.False(results[1].UnassociatedResult);
        Assert.True(results[2].UnassociatedResult); Assert.True(results[3].UnassociatedResult);
        Assert.Contains(inspected.Diagnostics, d => d.Code == Lis02Codes.UnexpectedRecordOrder);
    }

    [Fact]
    public void Declared_delimiters_repetitions_empty_components_and_escapes_are_preserved_before_decoding()
    {
        const string original = "H!%$?\rP!1!SYN!!!FAMILY?F?NAME$GIVEN%OTHER$SECOND$\rO!1!SYN\rR!1!$$$CODE!2?S?3!!\rL!1!N\r";
        var inspected = Catalog.Resolve(StandardAstmPlugin.ProfileId).InspectMessage!(original);
        var header = inspected.Records[0];
        Assert.Equal("%$?", Assert.Single(header.Fields, f => f.Location == "H.2").Raw);
        Assert.DoesNotContain(header.Fields, f => f.Location.StartsWith("H.2.", StringComparison.Ordinal));
        var patient = inspected.Records.Single(r => r.Type == "P");
        var family = Assert.Single(patient.Fields, f => f.Location == "P.6[1].1");
        Assert.Equal("FAMILY?F?NAME", family.Raw); Assert.Equal("FAMILY!NAME", family.Decoded);
        Assert.Equal("", Assert.Single(patient.Fields, f => f.Location == "P.6[2].3").Raw);
        var result = inspected.Records.Single(r => r.Type == "R");
        Assert.Equal("2$3", Assert.Single(result.Fields, f => f.Location == "R.4").Decoded);
        Assert.DoesNotContain(result.Fields, f => f.Location.StartsWith("R.4.", StringComparison.Ordinal));
        Assert.Equal("", Assert.Single(result.Fields, f => f.Location == "R.6").Raw);
        Assert.Equal(original, inspected.Original);
    }

    [Fact]
    public void Patient_maps_are_owned_by_each_profile_and_unknown_fields_are_not_guessed()
    {
        var uwam = Catalog.Resolve(Astm.Plugin.Sysmex.Urinalysis.SysmexUrinalysisPlugin.WithChemistryProfile);
        var cs = Catalog.Resolve("sysmex.cs2500.astm02.results");
        var qwalys = Catalog.Resolve("diagast.qwalys3.astm.results");
        Assert.Contains("paciente", uwam.MessageSchema!.Fields["P.4"].Fallback);
        Assert.DoesNotContain("P.3", cs.MessageSchema!.Fields.Keys);
        Assert.Equal("Nombre", cs.MessageSchema.Fields["P.6.2"].Fallback);
        Assert.Equal("Apellido", cs.MessageSchema.Fields["P.6.3"].Fallback);
        Assert.Contains("muestra", qwalys.MessageSchema!.Fields["P.3"].Fallback);
        var text = MessageInspectionPresentation.Describe("H|\\^&\rP|1|SYN-UNMAPPED\rL|1|N\r", cs);
        Assert.Contains("P.3 | Sin interpretación documentada | \"SYN-UNMAPPED\" → \"SYN-UNMAPPED\"", text);
        Assert.Contains("Fuente:", text);
    }

    [Theory]
    [InlineData("clsi.lis01-a2.lis02-a2", 4)]
    [InlineData("abbott.architect.asi-rs232", 4)]
    [InlineData("abbott.celldyn-ruby.lis2a", 7)]
    [InlineData("sysmex.cs2500.astm02.results", 4)]
    [InlineData("sysmex.xnl.astm02.results", 5)]
    [InlineData("sysmex.uwam.lis2a2.with-chemistry", 4)]
    [InlineData("sysmex.uwam.lis2a2.without-chemistry", 4)]
    [InlineData("diagast.qwalys3.astm.results", 7)]
    public void Diagnostic_fields_can_be_compared_to_the_same_plugins_canonical_result(string profileId, int codeComponent)
    {
        var profile = Catalog.Resolve(profileId);
        var d = Lis02Delimiters.Default;
        var order = new Lis02RecordBuilder('O', d).SetText(2, "1").SetText(3, "SYN-SAMPLE");
        if (profileId.StartsWith("sysmex.cs2500", StringComparison.Ordinal) || profileId.StartsWith("sysmex.xnl", StringComparison.Ordinal))
            order.SetRaw(3, "").SetComponents(4, "RACK", "01", "SYN-SAMPLE");
        if (profileId.StartsWith("diagast", StringComparison.Ordinal)) order.SetRaw(3, "").SetText(4, "SYN-SAMPLE");
        var components = new string[11]; Array.Fill(components, ""); components[codeComponent - 1] = "SYN-TEST";
        var result = new Lis02RecordBuilder('R', d).SetText(2, "1").SetComponents(3, components).SetText(4, "42").SetText(5, "SYN-UNIT").SetText(9, "F");
        if (profileId.StartsWith("diagast", StringComparison.Ordinal))
        { result.SetRaw(3, "^^^W^1^SYN-REACTION^SYN-TEST^1").SetRaw(4, "^^^^^^2+"); }
        var message = Lis02Codec.Parse(Lis02Codec.Serialize([Lis02RecordBuilder.Header(d).Build(),
            new Lis02RecordBuilder('P', d).SetText(2, "1").SetText(4, "PAT-SYN").SetComponents(6, "FAMILY-SYN", "GIVEN-SYN").Build(),
            order.Build(), result.Build(), new Lis02RecordBuilder('L', d).SetText(2, "1").SetText(3, "N").Build()])).Messages.Single();
        var original = Lis02Codec.Serialize(message.Records);
        var inspected = profile.InspectMessage!(original);
        var observation = Assert.Single(profile.AstmPlugin!.CreateDialect(profile.AstmProfile!, new(new("SYN"), TimeProvider.System))
            .Interpret(message, new(Guid.Empty, 1, 1)).Observations);
        Assert.Equal("SYN-SAMPLE", observation.Specimen.SpecimenId);
        var fields = inspected.Records.Single(r => r.Type == "R").Fields;
        Assert.Equal("SYN-TEST", Assert.Single(fields, f => f.Location == "R.3." + codeComponent).Decoded);
        if (profileId.StartsWith("diagast", StringComparison.Ordinal))
            Assert.Equal(observation.Value.Coded!.Code, Assert.Single(fields, f => f.Location == "R.4.7").Decoded);
        else
        {
            Assert.Equal(observation.Test.Vendor.Code, Assert.Single(fields, f => f.Location == "R.3." + codeComponent).Decoded);
            Assert.Equal(observation.Value.RawText, Assert.Single(fields, f => f.Location == "R.4").Decoded);
            Assert.Equal(observation.Value.RawUnit, Assert.Single(fields, f => f.Location == "R.5").Decoded);
        }
        Assert.All(fields.Where(f => f.Meaning is not null), f => Assert.False(string.IsNullOrWhiteSpace(f.Meaning!.Evidence)));
        Assert.Equal(original, inspected.Original);
    }

    [Fact]
    public void Hl7_inspection_handles_MSH_numbering_subcomponents_repetitions_and_patient_resets()
    {
        var original = "MSH|^~\\&|SYN||||||OUL^R22|SYN-MSG|P|2.5.1\rPID|1||PAT-SYN&ISSUER~ALT-SYN||FAMILY\\F\\SYN^GIVEN\r"
            + "SPM|1|SYN-SAMPLE\rOBR|1|||SYN-TEST\rOBX|1|NM|SYN-TEST||42|SYN-UNIT|||||F\r"
            + "PID|1||PAT-SYN-2\rOBX|1|ST|ORPHAN||SYN\r";
        var profile = Catalog.Resolve(ArchitectHl7Plugin.ProfileId);
        var inspected = profile.InspectMessage!(original);
        Assert.Equal("|", Assert.Single(inspected.Records[0].Fields, f => f.Location == "MSH.1").Raw);
        Assert.Equal("^~\\&", Assert.Single(inspected.Records[0].Fields, f => f.Location == "MSH.2").Raw);
        Assert.DoesNotContain(inspected.Records[0].Fields, f => f.Location.StartsWith("MSH.2[", StringComparison.Ordinal));
        var fields = inspected.Records[1].Fields;
        Assert.Equal("PAT-SYN", Assert.Single(fields, f => f.Location == "PID.3[1].1.1").Decoded);
        Assert.Equal("FAMILY|SYN", Assert.Single(fields, f => f.Location == "PID.5.1").Decoded);
        Assert.Equal("PID@2/SPM@3/OBR@4/OBX@5", inspected.Records[4].Context);
        Assert.Equal("PID@6/OBX@7", inspected.Records[6].Context);
        Assert.True(inspected.Records[6].UnassociatedResult);
        var bad = profile.InspectMessage!(original.Replace("42|", "\\XGG\\|", StringComparison.Ordinal));
        Assert.Contains(bad.Diagnostics, d => d.Code == "ASTM-INSPECTION-ESCAPE");
        Assert.True(Assert.Single(bad.Records[4].Fields, f => f.Location == "OBX.5").DecodeError);
        Assert.Contains("Escape inválido", MessageInspectionPresentation.Describe(bad.Original, profile));
        Assert.Equal(original, inspected.Original);
    }

    [Fact]
    public void Hl7_projection_can_be_compared_without_inventing_a_patient_for_messages_without_PID()
    {
        var original = Astm.Tests.Hl7.Hl7Data.Result();
        var profile = Catalog.Resolve(ArchitectHl7Plugin.ProfileId);
        var inspection = profile.InspectMessage!(original);
        var observations = profile.Hl7Dialect(TimeZoneInfo.Utc).Interpret(Astm.Protocol.Hl7v2.Hl7Codec.Parse(original).Message!, new(Guid.Empty, 1, 1)).Observations;
        var results = inspection.Records.Where(r => r.Type == "OBX").ToArray();
        Assert.Equal(observations.Count, results.Length);
        for (var i = 0; i < results.Length; i++)
        {
            Assert.DoesNotContain("PID@", results[i].Context);
            Assert.False(results[i].UnassociatedResult);
            Assert.Equal(observations[i].Test.Vendor.Code, Assert.Single(results[i].Fields, f => f.Location == "OBX.3.1.1").Decoded);
            Assert.Equal(observations[i].Value.RawText, Assert.Single(results[i].Fields, f => f.Location == "OBX.5").Decoded);
        }
    }

    [Fact]
    public void Inspection_is_available_for_both_directions_and_does_not_require_canonical_observations()
    {
        var profile = Catalog.Resolve(ArchitectPlugin.AsiProfileId);
        const string original = "H|\\^&\rP|1|PAT-SYN|||FAMILY-SYN^GIVEN-SYN\rO|1|SYN-SAMPLE\rL|1|N\r";
        Assert.Contains("FAMILY-SYN", MessageInspectionPresentation.Describe(original, profile));
        Assert.Contains("P.6.1", MessageInspectionPresentation.Describe(original, profile));
        var captured = DiagnosticReader.Read(System.Text.Encoding.ASCII.GetBytes(original), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero);
        foreach (var direction in new[] { Astm.Abstractions.Tracing.TraceDirection.Received, Astm.Abstractions.Tracing.TraceDirection.Transmitted })
        {
            var analysis = DiagnosticReader.Analyze(captured with { Entries = captured.Entries.Select(e => e with { Direction = direction }).ToArray() },
                profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
            var message = Assert.Single(analysis.Messages);
            Assert.Empty(analysis.Observations);
            Assert.Contains("P.6.1", MessageInspectionPresentation.Describe(message.Text, profile));
        }
        Assert.Contains("P.6.1", MessageInspectionPresentation.Describe(original.Replace("L|1|N\r", "", StringComparison.Ordinal), profile));
        Assert.Contains("Sin interpretación documentada", MessageInspectionPresentation.Describe("H|\\^&\rZ|SYN\rL|1|N\r", profile));
        Assert.Empty(MessageInspectionPresentation.Describe("SYN", Catalog.Resolve(Astm.Plugin.Sysmex.Urinalysis.SysmexUrinalysisPlugin.UfBinaryProfile)));
        Assert.Throws<ArgumentException>(() => ((IInstrumentMessageInspection)new ArchitectPlugin()).MessageSchema("unknown"));
    }
}
