using Astm.Abstractions.Hosting;
using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;
using Astm.Plugin.Sysmex.Urinalysis;
using Astm.Plugins;
using Astm.Protocol.Lis02;

namespace Astm.Tests.Plugins;

public class UwamDialectTests
{
    // Synthetic result message with the layout of R315168 5.3.3 (two records per parameter: RAW and MAINFORMAT).
    private const string Results =
        "H|\\^&|||U-WAM^00-22_Build003^A0000^^^^AU000000||||||||LIS2-A2|20251105005152\r" +
        "P|1||PID-SYN||SYN^PATIENT||19700101|M\r" +
        "O|1|SID-SYN-1^000123^04||^^^RBC\\^^^BACT|R||20251105004002||||N|||20251105004529|*|||ORD-1|||||||F\r" +
        "R|1|^^^RBC^A^1^S^000123^04|2.8^RAW|/ul||N||||^^admin^administrator||20251105004710|UF-5000\r" +
        "R|2|^^^BACT^A^1^S^000123^04|2.8x10&S&4^MAINFORMAT|/ml||A^H||||||20251105004710|UF-5000\r" +
        "L|1|N\r";

    private static readonly IAstmDialect Dialect = new SysmexUrinalysisPlugin().CreateDialect(new SysmexUrinalysisPlugin().Profiles[0], new DialectContext(new InstrumentRef("uwam"), TimeProvider.System));

    [Fact]
    public void Results_follow_the_uwam_field_layout()
    {
        var interpretation = Dialect.Interpret(Assert.Single(Lis02Codec.Parse(Results).Messages), TraceRef.None);

        Assert.Empty(interpretation.Diagnostics);
        Assert.Equal("U-WAM", interpretation.Instrument!.Model);
        var raw = interpretation.Observations[0];
        Assert.Equal("RBC", raw.Test.Vendor.Code);
        Assert.Equal("S/RAW", raw.ResultKind);
        Assert.Equal(2.8m, raw.Value.Numeric);
        Assert.Equal("SID-SYN-1", raw.Specimen.SpecimenId);
        Assert.Equal("000123^04", raw.Specimen.Position);
        Assert.Equal(InstrumentStatusMeaning.Final, raw.InstrumentStatusMeaning);
        Assert.Equal("UF-5000", raw.VendorIdentity["instrument.id"]);

        var main = interpretation.Observations[1];
        Assert.Equal("S/MAINFORMAT", main.ResultKind);
        Assert.Equal("2.8x10^4", main.Value.RawText);
        Assert.Null(main.Value.Numeric);
        Assert.Equal(["A", "H"], main.Flags.Select(f => f.Code));
    }

    [Fact]
    public void Inquiry_uses_the_sample_number_and_device_type()
    {
        var message = Assert.Single(Lis02Codec.Parse("H|\\^&|||U-WAM\rQ|1|SID-SYN-2||||20251105004529||||||C\rL|1|N\r").Messages);

        var query = Assert.Single(Dialect.Interpret(message, TraceRef.None).Queries);

        Assert.Equal("SID-SYN-2", query.Specimens.Single().SpecimenId);
        Assert.Equal("C", query.Scope);
    }

    [Fact]
    public void No_order_answer_is_an_order_record_without_parameters()
    {
        var query = new OrderQuery(Guid.NewGuid(), new InstrumentRef("uwam"), [new SpecimenRef("SID-SYN-2")], "C", TraceRef.None);

        var text = Lis02Codec.Serialize(Dialect.BuildQueryAnswer(query, OrderQueryAnswer.NoInformation));

        Assert.Equal("H|\\^&||||||||||P\rP|1\rO|1|SID-SYN-2|||||||||N||||||||||||||O\rL|1|N\r", text);
    }

    [Fact]
    public void Profiles_carry_the_inquiry_limits_of_the_manual()
    {
        var profiles = new SysmexUrinalysisPlugin().Profiles;

        Assert.True(profiles.Single(p => p.Id == SysmexUrinalysisPlugin.WithChemistryProfile).QueryAnswerBudget < TimeSpan.FromSeconds(8));
        Assert.True(profiles.Single(p => p.Id == SysmexUrinalysisPlugin.WithoutChemistryProfile).QueryAnswerBudget < TimeSpan.FromSeconds(18));
    }
}
