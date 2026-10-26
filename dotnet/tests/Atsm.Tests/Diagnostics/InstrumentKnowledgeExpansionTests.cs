using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Diagnostics;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugin.Diagast.Qwalys;
using Atsm.Plugin.Sysmex.Cs2500;
using Atsm.Plugin.Sysmex.XnL;
using Atsm.Plugin.Sysmex.Urinalysis;
using Atsm.Plugins;

namespace Atsm.Tests.Diagnostics;

public sealed class InstrumentKnowledgeExpansionTests
{
    private static readonly DiagnosticCatalog Catalog = new();
    private const string Cs = "sysmex.cs2500.astm02.results", Xn = "sysmex.xnl.astm02.results", Qw = "diagast.qwalys3.astm.results";
    private static DiagnosticAnalysis Read(string profile, string result, string model, string? order = null)
    {
        var selected = Catalog.Resolve(profile);
        var data = Encoding.ASCII.GetBytes($"H|\\^&|||{model}\rP|1\r{order ?? "O|1||01^02^SYN-CONTROL||||||||Q"}\r{result}\rL|1|N\r");
        var input = DiagnosticReader.Read(data, DiagnosticInputFormat.AstmMessage, selected, "synthetic", TimeSpan.Zero);
        return DiagnosticReader.Analyze(input, selected, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
    }
    [Fact]
    public void Manufacturer_tables_are_complete_and_lookup_is_restricted_to_profile_record_field_and_model()
    {
        var cs = new Cs2500Plugin(); var xn = new XnLPlugin(); var qw = new QwalysPlugin(); var arc = new ArchitectPlugin();
        Assert.Equal(68, cs.DiagnosticVocabulary.Keys.Count(k => k.StartsWith("evaluation.", StringComparison.Ordinal)));
        Assert.Equal(49, xn.DiagnosticVocabulary.Keys.Count(k => k.StartsWith("ip.", StringComparison.Ordinal)));
        Assert.Equal("Sin curva de calibración validada", cs.Lookup(new(Cs, "R", "7.2"), "0000.4000.0030")!.Text.Fallback);
        Assert.Null(cs.Lookup(new(Cs, "R", "7.3"), "0000.4000.0030"));
        Assert.Null(cs.Lookup(new(Cs, "R", "7.2", "UF-5000"), "0000.4000.0030"));
        Assert.Null(cs.Lookup(new(Xn, "R", "7.2"), "0000.4000.0030"));
        Assert.Null(cs.Lookup(new(Cs, "R", "7.2"), "7777.0000.0000"));
        Assert.NotNull(xn.Lookup(new(Xn, "R", "3.5"), "PLT_Clumps?"));
        Assert.Null(xn.Lookup(new(Xn, "R", "7"), "PLT_Clumps?"));
        Assert.Null(xn.Lookup(new(Xn, "R", "3.5", "CS-2500"), "PLT_Clumps?"));
        Assert.Null(qw.Lookup(new(Qw, "R", "4.7:W"), "CMP"));
        Assert.Null(arc.Lookup(new(Cs, "R", "7"), "EXP"));
        // Adding IInstrumentKnowledge must preserve the record summaries already used by the GUI.
        foreach (var id in new[] { Cs, Xn, Qw, "abbott.architect.asi-rs232" })
            Assert.DoesNotContain("sin interpretar", ProtocolExplainer.RecordNames("O|1\rR|1\r", Catalog.Resolve(id)), StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("0000.4000.0030", "Sin curva de calibración validada")]
    [InlineData("0000.4000.0020", "Error de cálculo")]
    [InlineData("9999.0000.9110", "Error de lote de reactivo")]
    public void CS2500_masked_errors_are_not_hidden_by_N(string code, string meaning)
    {
        var source = Read(Cs, $"R|1|^^^PT^PT^100^1|******|s||N^[{code} SOURCE-MESSAGE]||F", "CS-2500");
        var item = Assert.Single(source.Observations); var profile = Catalog.Resolve(Cs);
        Assert.Equal(meaning, ProtocolExplainer.Status(item, profile));
        Assert.True(ProtocolExplainer.RequiresAttention(item, (IInstrumentFlagKnowledge)profile.AstmPlugin!));
        Assert.Contains(source.Diagnostics, d => DiagnosticText.Finding(d, profile) == meaning);
        Assert.Equal($"N^[{code} SOURCE-MESSAGE]", item.VendorIdentity["flags.raw"]);
        Assert.Contains(item.Flags, f => f.Code == code && f.Display == "SOURCE-MESSAGE");
        Assert.Null(item.Value.Numeric);
    }
    [Fact]
    public void CS2500_multiple_evaluation_errors_and_instrument_errors_have_separate_contexts()
    {
        var source = Read(Cs, "R|1|^^^PT^PT^100^1|******|s||A^[0008.0002.0000 SHARP],[0032.0002.0000 FLAT]^[34422 LOW-REAGENT]||S", "CS-2500");
        var item = Assert.Single(source.Observations); var profile = Catalog.Resolve(Cs);
        Assert.Equal(4, item.Flags.Count);
        var status = ProtocolExplainer.Status(item, profile);
        Assert.Contains("descenso brusco", status); Assert.Contains("Curva plana", status); Assert.Contains("Reactivo insuficiente", status);
        Assert.DoesNotContain("SOURCE", status); Assert.Equal(InstrumentStatusMeaning.Unknown, item.InstrumentStatusMeaning);
        Assert.Equal("Resultado parcial", new Cs2500Plugin().DescribeState(item)!.Fallback);
    }
    [Fact]
    public void CS2500_unknown_evaluation_code_stays_unknown_and_preserves_the_original()
    {
        var source = Read(Cs, "R|1|^^^PT^PT^100^1|1.2|s||N^[7777.0000.0000 UNKNOWN]||F", "CS-2500");
        var item = Assert.Single(source.Observations);
        Assert.Contains("7777.0000.0000", ProtocolExplainer.Status(item, Catalog.Resolve(Cs)));
        Assert.Contains(item.Flags, f => f.Display == "UNKNOWN");
        Assert.DoesNotContain(source.Diagnostics, d => d.Code.StartsWith("ATSM-CS2500-EVALUATION-", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("Hemolytic Sample", "A", "Interferencia HIL", true)]
    [InlineData("Hemolytic Sample", "W", "Fuera del intervalo del detector HIL", true)]
    [InlineData("Defective Sample Volume", "N", "Volumen de muestra normal", false)]
    [InlineData("Defective Sample Volume", "A", "Volumen de muestra anómalo", true)]
    public void CS2500_sample_indicators_do_not_reuse_the_analysis_error_meaning(string parameter, string flag, string state, bool attention)
    {
        var source = Read(Cs, $"R|1|^^^^{parameter}||||{flag}||F", "CS-2500");
        var item = Assert.Single(source.Observations); var plugin = (IInstrumentFlagKnowledge)Catalog.Resolve(Cs).AstmPlugin!;
        Assert.Equal(state, ProtocolExplainer.Status(item, Catalog.Resolve(Cs)));
        Assert.Equal(attention, ProtocolExplainer.RequiresAttention(item, plugin));
        Assert.False(plugin.DescribeFlag(item, item.Flags[0])!.IsError);
    }
    [Theory]
    [InlineData("PLT_Clumps?", "Sospecha de agregados plaquetarios", false)]
    [InlineData("WBC_Abn_Scattergram", "Dispersograma leucocitario anómalo", false)]
    [InlineData("ACTION_MESSAGE_Delta_HGB", "Revisar cambio en hemoglobina", false)]
    [InlineData("Error_Result", "Error de resultado", true)]
    public void XNL_A_is_interpreted_by_the_IP_message_and_is_not_always_an_analysis_error(string code, string meaning, bool error)
    {
        var source = Read(Xn, $"R|1|^^^^{code}|140|||A||F", "XN-550");
        var item = Assert.Single(source.Observations); var profile = Catalog.Resolve(Xn);
        Assert.Equal(meaning, ProtocolExplainer.Status(item, profile)); Assert.Equal("Indicador IP", ProtocolExplainer.ResultKind(item, profile));
        Assert.Equal(error, ((IInstrumentFlagKnowledge)profile.AstmPlugin!).DescribeFlag(item, item.Flags[0])!.IsError);
        Assert.True(ProtocolExplainer.RequiresAttention(item, (IInstrumentFlagKnowledge)profile.AstmPlugin!));
        var series = Assert.Single(QualitySeriesBuilder.Build([item], (IInstrumentValueKnowledge)profile.AstmPlugin!));
        Assert.Equal(0, series.NumericCount); Assert.Null(series.Range.Minimum); Assert.Null(series.StandardDeviation);
        Assert.Equal("140", item.Value.RawText); // Q-Flag is retained, not converted into a concentration or threshold decision.
    }
    [Fact]
    public void XNL_suspect_QFlag_without_A_does_not_become_positive_from_a_guessed_threshold()
    {
        var item = Assert.Single(Read(Xn, "R|1|^^^^PLT_Clumps?|140|||||F", "XN-550").Observations);
        Assert.Equal("Sin activar", ProtocolExplainer.Status(item, Catalog.Resolve(Xn)));
        Assert.False(ProtocolExplainer.RequiresAttention(item, new XnLPlugin()));
    }
    [Theory]
    [InlineData("----", "A", "Error de análisis o del equipo")]
    [InlineData("++++", "A", "Límite de visualización excedido")]
    [InlineData("195", "W", "Baja fiabilidad")]
    [InlineData("195", "N", "Normal")]
    public void XNL_measurement_flags_use_their_documented_value_context(string value, string flag, string state)
    {
        var item = Assert.Single(Read(Xn, $"R|1|^^^^PLT^1|{value}|10*3/uL||{flag}||F", "XN-550").Observations);
        Assert.Equal(state, ProtocolExplainer.Status(item, Catalog.Resolve(Xn)));
    }
    [Theory]
    [InlineData("A", "CMP", "Compatible")]
    [InlineData("A", "INCMP", "Incompatible")]
    [InlineData("W", "E", "Sin reacción informada")]
    [InlineData("W", "2+", "2+")]
    public void QWALYS_coded_reaction_and_manual_status_are_readable_without_changing_the_value(string type, string code, string result)
    {
        var item = Assert.Single(Read(Qw, $"R|1|^^^{type}^99^SYN^TEST^0|^^^^^^{code}|X||||M", "QWALYS 3", "O|1||SYN").Observations);
        var profile = Catalog.Resolve(Qw);
        Assert.Equal(result, ProtocolExplainer.ResultValue(item, profile));
        Assert.Equal("Modificado por operador", ProtocolExplainer.Status(item, profile));
        Assert.Equal("—", ProtocolExplainer.Unit(item, profile)); Assert.Equal("^^^^^^" + code, item.Value.RawText);
        Assert.Null(item.Value.Numeric);
    }
    [Fact]
    public void ARCHITECT_interprets_expiration_flags_and_instrument_response_separately()
    {
        var profile = Catalog.Resolve("abbott.architect.asi-rs232");
        var item = Assert.Single(Read(profile.Id, "R|1|^^^0021^SYN^UNDILUTED^P^LOT^PACK^^P|9245|RLU||EXP^<||F", "ARCHITECT", "O|1|SYN").Observations);
        Assert.Equal("Respuesta instrumental", ProtocolExplainer.ResultKind(item, profile));
        Assert.Contains("Reactivo vencido", ProtocolExplainer.Status(item, profile));
        Assert.Contains("Por debajo del intervalo de medición", ProtocolExplainer.Status(item, profile));
        Assert.Equal("RLU", ProtocolExplainer.Unit(item, profile)); Assert.Equal("P", item.ResultKind);
    }
    [Theory]
    [InlineData("RBC-Info.", "2", "Posible patrón eritrocitario dismórfico")]
    [InlineData("BACT-Info.", "3", "Posible mezcla grampositiva/gramnegativa")]
    [InlineData("BACT-Info.", "0", "Sin evaluación")]
    [InlineData("UTI-Info.", "1", "Sospecha de infección urinaria")]
    public void UWAM_research_enumerations_are_categorical_and_preserve_the_equipment_qualification(string parameter, string code, string meaning)
    {
        var profile = Catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var item = Assert.Single(Read(profile.Id, $"R|1|^^^{parameter}^A^1^S|{code}^RAW|||N|||||||UF-5000", "U-WAM", "O|1|SYN|||||||||Q").Observations);
        Assert.Equal(meaning, ProtocolExplainer.ResultValue(item, profile)); Assert.Equal("Indicador de investigación", ProtocolExplainer.ResultKind(item, profile));
        Assert.Equal("—", ProtocolExplainer.Unit(item, profile)); Assert.Null(item.Value.Numeric); Assert.Equal(code, item.Value.Coded!.Code);
        Assert.Equal(code, item.Value.RawText); Assert.Equal(code + "^RAW", item.VendorIdentity["result.value.raw"]);
        var series = Assert.Single(QualitySeriesBuilder.Build([item], (IInstrumentValueKnowledge)profile.AstmPlugin!));
        Assert.Null(series.Median); Assert.Null(series.Range.Minimum); Assert.Equal(0, series.NumericCount);
        Assert.NotNull(((IInstrumentKnowledge)profile.AstmPlugin!).Lookup(new(profile.Id, "R", "4.1:" + parameter, "UF-5000"), code));
        Assert.Null(((IInstrumentKnowledge)profile.AstmPlugin!).Lookup(new(profile.Id, "R", "4.1:" + parameter, "UC-3500"), code));
    }
    [Theory]
    [InlineData("RC_RBC_Carryover", "Sospecha de arrastre de eritrocitos")]
    [InlineData("RC_DC", "Conductividad anómala: recuentos posiblemente alterados")]
    [InlineData("RC_USER_1", "Revisión por regla configurada 1")]
    public void UWAM_review_codes_request_attention_without_turning_a_suspicion_into_an_analysis_error(string parameter, string meaning)
    {
        var profile = Catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var item = Assert.Single(Read(profile.Id, $"R|1|^^^{parameter}^A^1^RC|SOURCE^RAW|||N|||||||UF-5000", "U-WAM", "O|1|SYN|||||||||Q").Observations);
        Assert.Equal(meaning, ProtocolExplainer.ResultValue(item, profile)); Assert.Equal(meaning, ProtocolExplainer.Status(item, profile));
        var knowledge = (IInstrumentFlagKnowledge)profile.AstmPlugin!;
        Assert.True(ProtocolExplainer.RequiresAttention(item, knowledge)); Assert.All(item.Flags, f => Assert.False(knowledge.DescribeFlag(item, f)!.IsError));
        Assert.Contains(item.Flags, f => f.Code == "N"); Assert.Equal("SOURCE", item.Value.RawText);
    }
    [Fact]
    public void UWAM_unknown_research_code_and_wrong_result_type_do_not_receive_a_known_meaning()
    {
        var profile = Catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var item = Assert.Single(Read(profile.Id, "R|1|^^^BACT-Info.^A^1^S|9^RAW|||N|||||||UF-5000", "U-WAM").Observations);
        Assert.Equal("9", ProtocolExplainer.ResultValue(item, profile)); Assert.Equal("9", item.Value.Coded!.Code); Assert.Null(item.Value.Numeric);
        var wrong = item with { ResultKind = "RP/RAW" }; Assert.Null(((IInstrumentValueKnowledge)profile.AstmPlugin!).DescribeValue(wrong)!.DisplayText);
        Assert.Equal(11, ((IInstrumentDiagnosticVocabulary)profile.AstmPlugin!).DiagnosticVocabulary.Keys.Count(k => k.StartsWith("research.", StringComparison.Ordinal)));
        Assert.Equal(19, ((IInstrumentDiagnosticVocabulary)profile.AstmPlugin!).DiagnosticVocabulary.Keys.Count(k => k.StartsWith("review.", StringComparison.Ordinal)));
    }
}
