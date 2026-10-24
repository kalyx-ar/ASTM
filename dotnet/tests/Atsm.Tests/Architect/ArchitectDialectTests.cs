using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;

namespace Atsm.Tests.Architect;

public class ArchitectDialectTests
{
    // Synthetic upload with the layout of ASI manual figures 2.5-2.11. No real patient data.
    public const string PatientUpload =
        "H|\\^&|||ARCHITECT^9.45^F0000000001^H1P1O1R1C1Q1L1|||||||P|1|20260126104616\r" +
        "P|1|||PID-SYN-1\r" +
        "O|1|SID-SYN-1|SID-SYN-1^A123^5|^^^0021^B-hCG^UNDILUTED^P|R||20260126081223||||||||||||||||||F\r" +
        "R|1|^^^0021^B-hCG^UNDILUTED^P^47331M100^00788^^F|< 1.20|mIU/mL|0.35 TO 4.94|EXP^<||F||OPER1^OPER2||20260126081030|I20100\r" +
        "C|1|I|Example Result Comment|G\r" +
        "R|2|^^^0021^B-hCG^UNDILUTED^P^47331M100^00788^^I|NEGATIVE|||||F||||20260126081030|I20100\r" +
        "R|3|^^^0021^B-hCG^UNDILUTED^P^47331M100^00788^^P|9245|RLU||||F||||20260126081030|I20100\r" +
        "O|2|QC-SYN|QC-SYN^A123^2|^^^0241^TSH^UNDILUTED^P|R||||||Q||||||||||||||F\r" +
        "R|1|^^^0241^TSH^UNDILUTED^P^0607M200^01824^40080^F|4.6011|mIU/mL|4.292500 TO 5.397300|||R||ECB^RY||20260126081030|I20100\r" +
        "L|1\r";

    private static readonly ArchitectPlugin Plugin = new(new ArchitectDialectOptions { InstrumentTimeZone = TimeZoneInfo.Utc });

    private static IAstmDialect Dialect(ArchitectDialectOptions? options = null) =>
        (options is null ? Plugin : new ArchitectPlugin(options))
            .CreateDialect(Plugin.Profiles[0], new DialectContext(new InstrumentRef("architect-01"), TimeProvider.System));

    private static Lis02Message Parse(string text) => Assert.Single(Lis02Codec.Parse(text).Messages);

    [Fact]
    public void Header_identifies_the_instrument()
    {
        var interpretation = Dialect().Interpret(Parse(PatientUpload), TraceRef.None);

        Assert.Equal(new InstrumentRef("architect-01", "Abbott", "ARCHITECT", "F0000000001", "9.45"), interpretation.Instrument);
    }

    [Fact]
    public void Results_keep_vendor_code_raw_value_and_result_type()
    {
        var interpretation = Dialect().Interpret(Parse(PatientUpload), TraceRef.None);

        Assert.Empty(interpretation.Diagnostics);
        Assert.Equal(4, interpretation.Observations.Count);
        var final = interpretation.Observations[0];
        Assert.Equal("0021", final.Test.Vendor.Code);
        Assert.Equal("B-hCG", final.Test.Vendor.Display);
        Assert.Equal(ArchitectDialect.VendorSystem, final.Test.Vendor.System);
        Assert.Equal("F", final.ResultKind);
        Assert.Equal("< 1.20", final.Value.RawText);
        Assert.Equal("<", final.Value.Comparator);
        Assert.Equal(1.20m, final.Value.Numeric);
        Assert.Equal("mIU/mL", final.Value.RawUnit);
        Assert.Equal("0.35 TO 4.94", final.ReferenceRangeText);
        Assert.Equal(["EXP", "<"], final.Flags.Select(f => f.Code));
        Assert.Equal(InstrumentStatusMeaning.Final, final.InstrumentStatusMeaning);
        Assert.Equal(new DateTimeOffset(2026, 1, 26, 8, 10, 30, TimeSpan.Zero), final.ResultTime);
        Assert.Equal("OPER1", final.Operator);
        Assert.Equal("47331M100", final.Reagents.Single().Lot);
        Assert.Equal(["Example Result Comment"], final.Comments);
        Assert.Equal("SID-SYN-1", final.Specimen.SpecimenId);
        Assert.Equal("A123^5", final.Specimen.Position);
        Assert.Equal(SpecimenRole.Patient, final.Specimen.Role);
        Assert.Equal("I20100", final.VendorIdentity["module.serial"]);

        var interpretationResult = interpretation.Observations[1];
        Assert.Equal("I", interpretationResult.ResultKind);
        Assert.Equal("NEGATIVE", interpretationResult.Value.Coded!.Code);
        Assert.Null(interpretationResult.Value.Numeric);

        Assert.Equal(9245m, interpretation.Observations[2].Value.Numeric);
        Assert.Equal("RLU", interpretation.Observations[2].Value.RawUnit);
    }

    [Fact]
    public void Control_orders_and_previously_transmitted_results_are_identified()
    {
        var control = Dialect().Interpret(Parse(PatientUpload), TraceRef.None).Observations[3];

        Assert.Equal(SpecimenRole.Control, control.Specimen.Role);
        Assert.Equal("R", control.InstrumentStatus);
        Assert.Equal(InstrumentStatusMeaning.PreviouslyTransmitted, control.InstrumentStatusMeaning);
        Assert.Equal("40080", control.VendorIdentity["control.lot"]);
    }

    [Fact]
    public void Test_not_performed_is_reported_with_its_reason()
    {
        const string text =
            "H|\\^&|||ARCHITECT^9.45^F0000000001^H1P1O1R1C1Q1L1|||||||P|1\r" +
            "P|1\r" +
            "O|1|SID-SYN-2||^^^0241^TSH^UNDILUTED^P|R||||||||||||||||||||X\r" +
            "C|1|I|Test exception: reagent not available|I\r" +
            "L|1\r";

        var observation = Assert.Single(Dialect().Interpret(Parse(text), TraceRef.None).Observations);

        Assert.Equal(InstrumentStatusMeaning.NotPerformed, observation.InstrumentStatusMeaning);
        Assert.Equal("0241", observation.Test.Vendor.Code);
        Assert.Equal(["Test exception: reagent not available"], observation.Comments);
    }

    [Fact]
    public void Query_record_produces_an_order_query()
    {
        const string text = "H|\\^&|||ARCHITECT^9.45^F0000000001\rQ|1|^SID-SYN-3||^^^ALL||||||||O\rL|1\r";

        var query = Assert.Single(Dialect().Interpret(Parse(text), TraceRef.None).Queries);

        Assert.Equal("SID-SYN-3", Assert.Single(query.Specimens).SpecimenId);
        Assert.Equal("ALL", query.Scope);
    }

    [Fact]
    public void Negative_query_response_follows_the_manual()
    {
        var query = new OrderQuery(Guid.NewGuid(), new InstrumentRef("architect-01"), [new SpecimenRef("SID1234")], "ALL", TraceRef.None);

        var records = Dialect().BuildQueryAnswer(query, OrderQueryAnswer.NoInformation);

        Assert.Equal("H|\\^&||||||||||P|1\rQ|1|^SID1234||^^^ALL||||||||X\rL|1\r", Lis02Codec.Serialize(records));
    }

    [Fact]
    public void Orders_answer_a_query_with_patient_and_order_records()
    {
        var order = new WorkOrder(
            "ORD-1",
            new SpecimenRef("SID1000"),
            new PatientContext("PID-SYN", "Doe", "Jane", new DateOnly(1950, 5, 22), "F", "Dr Who", "Ward 3"),
            [CodeSet.Of(new CodedValue("16", null, ArchitectDialect.VendorSystem)), CodeSet.Of(new CodedValue("606", null, ArchitectDialect.VendorSystem))],
            OrderPriority.Stat,
            CollectedAt: new DateTimeOffset(2026, 2, 23, 8, 12, 23, TimeSpan.Zero),
            SpecimenType: "serum");
        var query = new OrderQuery(Guid.NewGuid(), new InstrumentRef("architect-01"), [new SpecimenRef("SID1000")], "ALL", TraceRef.None);

        var text = Lis02Codec.Serialize(Dialect().BuildQueryAnswer(query, new OrderQueryAnswer(QueryAnswerState.Orders, [order])));

        Assert.Equal(
            "H|\\^&||||||||||P|1\r" +
            "P|1|PID-SYN|||Doe^Jane||19500522|F|||||Dr Who||||||||||||Ward 3\r" +
            "O|1|SID1000||^^^16\\^^^606|S||20260223081223||||N||||serum||||||||||Q\r" +
            "L|1\r",
            text);
    }

    [Fact]
    public void Legacy_sex_policy_always_sends_unknown()
    {
        var order = new WorkOrder("ORD-2", new SpecimenRef("S2"), new PatientContext(AdministrativeSex: "M"), [CodeSet.Of(new CodedValue("16", null, ArchitectDialect.VendorSystem))]);

        var records = Dialect(new ArchitectDialectOptions { Sex = SexPolicy.AlwaysUnknown }).BuildOrders([order]);

        Assert.Equal("U", records.Single(r => r.Type == 'P').Field(9));
        Assert.Equal("O", records.Single(r => r.Type == 'O').Field(26));
    }
}
