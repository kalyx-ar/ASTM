using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Diagnostics;
using Atsm.Plugin.Sysmex.Urinalysis;
using Atsm.Plugins;

namespace Atsm.Tests.Diagnostics;

public sealed class SemanticPresentationTests
{
    private static readonly DiagnosticCatalog Catalog = new();
    private static readonly DiagnosticProfile Profile = Catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
    private static readonly DiagnosticTiming Timing = new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc);
    private static string Result(string test = "WBC", string value = "1.2", string flags = "N", string model = "UF-5000", string kind = "S", string sample = "SYN-SAMPLE") =>
        $"H|\\^&|||U-WAM\rP|1\rO|1|{sample}|||||||||N||||||||||||||F\rR|1|^^^{test}^A^1^{kind}|{value}^RAW|u||{flags}|||||||{model}\rL|1|N\r";
    private static DiagnosticAnalysis Analyze(string raw) => DiagnosticReader.Analyze(DiagnosticReader.Read(Encoding.ASCII.GetBytes(raw), DiagnosticInputFormat.AstmMessage, Profile, "synthetic", TimeSpan.Zero), Profile, Timing);

    [Theory]
    [InlineData(SysmexUrinalysisPlugin.WithChemistryProfile)]
    [InlineData(SysmexUrinalysisPlugin.WithoutChemistryProfile)]
    public void Chemistry_9902_is_an_order_error_with_its_original_00CC_code(string profile)
    {
        var selected = Catalog.Resolve(profile); var plugin = (SysmexUrinalysisPlugin)selected.AstmPlugin!;
        var fact = plugin.Lookup(new(profile, "R", "C-Error Code"), "9902")!;
        Assert.Equal("00CC", fact.SourceCode); Assert.Contains("Error de solicitud", fact.Text.Fallback);
        Assert.Null(plugin.Lookup(new(profile, "R", "WBC"), "9902"));
        var input = DiagnosticReader.Read(Encoding.ASCII.GetBytes(Result("C-Error Code", "9902", model: "UC-3500")), DiagnosticInputFormat.AstmMessage, selected, "synthetic", TimeSpan.Zero);
        var result = DiagnosticReader.Analyze(input, selected, Timing); var finding = Assert.Single(result.Diagnostics, d => d.Code.StartsWith("ATSM-UWAM-CHEMISTRY-", StringComparison.Ordinal));
        Assert.Equal("Error de solicitud (9902)", DiagnosticText.Finding(finding, selected));
        Assert.Contains("00CC", finding.Message); Assert.Contains("00CC", DiagnosticText.FindingEvidence(finding, selected));
        Assert.Equal("9902", Assert.Single(result.Observations).Value.RawText);
    }

    [Theory]
    [InlineData("9001", "AA00", "Boquilla obstruida")]
    [InlineData("9002", "BB00", "Extremo de la tira reactiva no detectado")]
    [InlineData("9901", "00BB", "Error de lectura del código de barras")]
    [InlineData("9903", "00DD", "Tira reactiva vencida")]
    public void Appendix_K_codes_are_contextual_and_do_not_use_the_direct_ER_table(string code, string original, string label)
    {
        var plugin = (SysmexUrinalysisPlugin)Profile.AstmPlugin!;
        var fact = plugin.Lookup(new(Profile.Id, "R", "C-Error Code", "UC-3500"), code)!;
        Assert.Equal(original, fact.SourceCode); Assert.Equal(label + " (" + code + ")", fact.Text.Fallback);
        Assert.Null(plugin.Lookup(new(SysmexUrinalysisPlugin.Uc3500Profile, "ER", "code"), code));
    }

    [Fact]
    public void Model_specific_errors_are_not_silently_applied_to_an_unidentified_analyzer()
    {
        var plugin = (SysmexUrinalysisPlugin)Profile.AstmPlugin!;
        Assert.Contains("Fallo mecánico", plugin.Lookup(new(Profile.Id, "R", "C-Error Code", "UC-3500"), "1100")!.Text.Fallback);
        Assert.Contains("Tiempo de análisis agotado", plugin.Lookup(new(Profile.Id, "R", "C-Error Code", "UC-1000"), "1100")!.Text.Fallback);
        Assert.Contains("o tiempo de análisis", plugin.Lookup(new(Profile.Id, "R", "C-Error Code"), "1100")!.Text.Fallback);
        var unknown = Assert.Single(Analyze(Result("C-Error Code", "9999")).Diagnostics, d => d.Code.StartsWith("ATSM-UWAM-CHEMISTRY-", StringComparison.Ordinal));
        Assert.Equal("Error químico sin descripción documentada (9999)", DiagnosticText.Finding(unknown, Profile));
        Assert.DoesNotContain(Analyze(Result("C-Error Code", "0000")).Diagnostics, d => d.Code.StartsWith("ATSM-UWAM-CHEMISTRY-", StringComparison.Ordinal));
        var normal = plugin.Lookup(new(Profile.Id, "R", "C-Error Code"), "0000")!;
        Assert.Equal("Sin error (0000)", normal.Text.Fallback); Assert.Null(normal.FindingCode);
    }

    [Fact]
    public void Patient_and_order_summary_preserves_useful_records_without_header_or_terminator_noise()
    {
        var raw = "H|\\^&\rP|1\rO|1|SYN-SAMPLE\rL|1|N\r"; var result = Analyze(raw);
        var summary = ProtocolExplainer.MessageContent(Assert.Single(result.Messages), Profile, result.Observations);
        Assert.Equal("Identificación del paciente · Muestra, tipo de análisis y estado de la solicitud", summary);
        Assert.Equal(raw, result.Messages[0].Text);
        Assert.Contains("Cabecera", ProtocolExplainer.RecordNames(raw, Profile)); // Detail keeps the full structure.
    }

    [Fact]
    public void Queries_show_the_requested_sample_and_group_summary_stays_semantic()
    {
        var result = Analyze("H|\\^&\rQ|1|SYN-123\rL|1|N\r"); var message = Assert.Single(result.Messages);
        Assert.Equal("Consulta SYN-123 por número de muestra", ProtocolExplainer.MessageContent(message, Profile, []));
        Assert.Equal("Consulta de solicitudes por número de muestra", ProtocolExplainer.MessageSummary(message, Profile));
    }

    [Fact]
    public void Message_errors_are_local_and_do_not_mark_the_following_normal_message()
    {
        var result = Analyze(Result("C-Error Code", "9902", model: "UC-3500", sample: "SYN-1") + Result(sample: "SYN-2"));
        Assert.Equal(2, result.Messages.Count);
        Assert.EndsWith(" - Con error", ProtocolExplainer.MessageContent(result.Messages[0], Profile, result.Observations));
        Assert.DoesNotContain("Con error", ProtocolExplainer.MessageContent(result.Messages[1], Profile, result.Observations));
        Assert.Single(result.Messages[0].Diagnostics); Assert.Empty(result.Messages[1].Diagnostics);
    }

    [Fact]
    public void Normal_N_is_just_Normal_and_never_counts_as_an_alarm()
    {
        var result = Analyze(Result()); var observation = Assert.Single(result.Observations);
        Assert.Equal("Normal", ProtocolExplainer.Status(observation, Profile));
        Assert.False(ProtocolExplainer.RequiresAttention(observation, (IInstrumentFlagKnowledge)Profile.AstmPlugin!));
        var qc = observation with { Specimen = observation.Specimen with { Role = SpecimenRole.Control }, Quality = new(new(QualityMaterialKind.Control, "SYN-QC")) };
        Assert.Equal(0, Assert.Single(QualitySeriesBuilder.Build([qc], (IInstrumentValueKnowledge)Profile.AstmPlugin!)).Flagged);
        Assert.False(ProtocolExplainer.MessageHasError(result.Messages[0], Profile, result.Observations));
        Assert.Equal("N", observation.Flags.Single().Code);
    }

    [Theory]
    [InlineData("UF-5000", "H", "Juicio de error", true)]
    [InlineData("UC-3500", "H", "Positivo", false)]
    [InlineData("UF-5000", "W", "Baja fiabilidad", true)]
    [InlineData("UC-3500", "W", "Baja fiabilidad: tira vencida", true)]
    [InlineData("UF-5000", "^H", "Por encima del intervalo normal", false)]
    [InlineData("UF-5000", "N^L", "Por debajo del intervalo normal", false)]
    public void The_same_flag_is_interpreted_in_its_analyzer_and_component_context(string model, string flags, string label, bool error)
    {
        var result = Analyze(Result(flags: flags, model: model)); var observation = Assert.Single(result.Observations);
        Assert.Equal(label, ProtocolExplainer.Status(observation, Profile));
        Assert.Equal(error, ProtocolExplainer.MessageHasError(result.Messages[0], Profile, result.Observations));
        if (flags.StartsWith('^')) Assert.EndsWith(":lh-flag", Assert.Single(observation.Flags).System);
    }

    [Fact]
    public void IF_is_an_image_in_type_and_value_but_preserves_the_external_reference()
    {
        var raw = "SYN-DATE&PNG&R&[SYN-MODULE]&R&file.png";
        var observation = Assert.Single(Analyze(Result("SYN-SCATTER", raw, kind: "IF")).Observations);
        Assert.Equal("Imagen", ProtocolExplainer.ResultKind(observation, Profile));
        Assert.Equal("Imagen", ProtocolExplainer.ResultValue(observation, Profile));
        Assert.Equal("SYN-DATE&PNG\\[SYN-MODULE]\\file.png", observation.Value.RawText);
        Assert.Equal(Result("SYN-SCATTER", raw, kind: "IF"), Assert.Single(Analyze(Result("SYN-SCATTER", raw, kind: "IF")).Messages).Text);
        Assert.Equal("S/RAW", Assert.Single(Analyze(Result()).Observations).ResultKind);
        var numericReference = Assert.Single(Analyze(Result("SYN-IMAGE", "12345", kind: "IF")).Observations);
        numericReference = numericReference with { Specimen = numericReference.Specimen with { Role = SpecimenRole.Control }, Quality = new(new(QualityMaterialKind.Control, "SYN-QC")) };
        var series = Assert.Single(QualitySeriesBuilder.Build([numericReference], (IInstrumentValueKnowledge)Profile.AstmPlugin!));
        Assert.Equal(0, series.NumericCount); Assert.Null(series.Median); Assert.Null(series.Range.Minimum); Assert.Null(series.Range.Maximum);
        Assert.Equal("Imagen", ProtocolExplainer.ResultValue(numericReference, Profile)); Assert.Equal("12345", numericReference.Value.RawText);
    }

    [Fact]
    public void Unknown_binary_control_payload_is_explicit_without_fabricating_ASTM_observations()
    {
        var log = "2026/10/25 10:00:00.000 R 008{[STX][NUL][SOH][NUL][NUL][ACK][NUL][ETX]}\r\n"u8.ToArray();
        var input = DiagnosticReader.Read(log, DiagnosticInputFormat.Auto, Profile, "synthetic", TimeSpan.Zero);
        Assert.Equal("binary", input.Header.Properties!["unsupportedPayload"]);
        var result = DiagnosticReader.Analyze(input, Profile, Timing);
        Assert.Empty(result.Observations); Assert.Empty(result.Messages);
        var finding = Assert.Single(result.Diagnostics, d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL");
        Assert.Equal("Protocolo binario sin intérprete documentado; bytes disponibles en Trama.", DiagnosticText.Finding(finding, Profile));
        Assert.Equal(new byte[] { 2, 0, 1, 0, 0, 6, 0, 3 }, Assert.Single(result.Entries).Data.ToArray());
    }

    [Fact]
    public void Complete_appendix_K_and_result_type_tables_are_exposed_by_the_plugin()
    {
        var plugin = (SysmexUrinalysisPlugin)Profile.AstmPlugin!;
        string[] common = ["0000", "1100", "2200", "9001", "9002", "9003", "4020", "5010", "5020", "5030", "5040", "6010", "6020", "6030", "6040", "6050", "7010", "7020", "7030", "7050", "8010", "8030", "8040", "0301", "0302", "0303", "0304", "0305", "0401", "0402", "0403", "0501", "0502", "9901", "9902", "9903"];
        foreach (var profile in new[] { SysmexUrinalysisPlugin.WithChemistryProfile, SysmexUrinalysisPlugin.WithoutChemistryProfile })
        {
            foreach (var model in new[] { "UC-3500", "UC-1000" })
            foreach (var code in common) Assert.NotNull(plugin.Lookup(new(profile, "R", "C-Error Code", model), code));
            foreach (var code in new[] { "8050", "9010" })
            {
                Assert.NotNull(plugin.Lookup(new(profile, "R", "C-Error Code", "UC-1000"), code));
                Assert.Null(plugin.Lookup(new(profile, "R", "C-Error Code", "UC-3500"), code));
            }
            foreach (var code in new[] { "S", "RC", "RP", "IF" }) Assert.NotNull(plugin.Lookup(new(profile, "R", "3.7"), code));
            foreach (var code in new[] { "A", "M" }) Assert.NotNull(plugin.Lookup(new(profile, "R", "3.5"), code));
            foreach (var code in new[] { "RAW", "MAINFORMAT", "RAW_Q", "RAW_C", "RAW_R1", "RAW_R2" }) Assert.NotNull(plugin.Lookup(new(profile, "R", "4.2"), code));
        }
        Assert.Equal("Parámetro de sensibilidad", ProtocolExplainer.ResultKind(Analyze(Result(kind: "RP")).Observations.Single(), Profile));
    }
}
