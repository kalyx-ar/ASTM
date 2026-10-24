using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.Abbott.CellDynRuby;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;
using Atsm.Tracing;

namespace Atsm.Tests.Plugins;

public class CellDynRubyTests
{
    // Synthetic message with the layout seen in CELL-DYN Ruby traces (whole message, padded serial in H.5).
    private const string Results =
        "H|\\^&|||  00000XX^CDRuby^2.2 ML^1.0|||||||P|LIS2-A\r" +
        "P|1||PID-SYN\r" +
        "O|1|SID-SYN-1\r" +
        "R|1|^^^CBC^^^WBC|7.103|||||F||Admin|20260119130647||  00000XX\r" +
        "R|2|^^^CBC^^^MONO|.5198|||||W||Admin|20260119130647||  00000XX\r" +
        "R|3|^^^CBC^^^VAR LYM|FLAG|||||||Admin|20260119130647||  00000XX\r" +
        "L|1|N\r";

    private static readonly CellDynRubyPlugin Plugin = new();

    private static IAstmDialect Dialect() => Plugin.CreateDialect(Plugin.Profiles[0], new DialectContext(new InstrumentRef("ruby"), TimeProvider.System));

    [Fact]
    public void Results_use_the_parameter_component_and_alarms_become_flags()
    {
        var interpretation = Dialect().Interpret(Assert.Single(Lis02Codec.Parse(Results).Messages), TraceRef.None);

        Assert.Equal(new InstrumentRef("ruby", "Abbott", "CDRuby", "00000XX", "2.2 ML"), interpretation.Instrument);
        Assert.Equal(2, interpretation.Observations.Count);
        var wbc = interpretation.Observations[0];
        Assert.Equal("WBC", wbc.Test.Vendor.Code);
        Assert.Equal("CBC", wbc.ResultKind);
        Assert.Equal(7.103m, wbc.Value.Numeric);
        Assert.Equal(InstrumentStatusMeaning.Final, wbc.InstrumentStatusMeaning);
        Assert.NotNull(wbc.ResultTime);
        Assert.Equal("00000XX", wbc.VendorIdentity["instrument.id"]);
        Assert.Equal(0.5198m, interpretation.Observations[1].Value.Numeric);
        Assert.Equal(InstrumentStatusMeaning.Warning, interpretation.Observations[1].InstrumentStatusMeaning);
        Assert.All(interpretation.Observations, o => Assert.Contains(o.Flags, f => f.Code == "VAR LYM"));
        Assert.Contains(interpretation.Diagnostics, d => d.Code == CellDynRubyDialect.InstrumentAlarm);
    }

    [Fact]
    public void Query_all_asks_for_every_pending_order()
    {
        var all = Dialect().Interpret(Assert.Single(Lis02Codec.Parse("H|\\^&|||  00000XX^CDRuby\rQ|1|^ALL\rL|1|N\r").Messages), TraceRef.None);
        var one = Dialect().Interpret(Assert.Single(Lis02Codec.Parse("H|\\^&|||  00000XX^CDRuby\rQ|1|^SID-SYN-2\rL|1|N\r").Messages), TraceRef.None);

        Assert.Equal(("ALL", 0), (all.Queries.Single().Scope, all.Queries.Single().Specimens.Count));
        Assert.Equal("SID-SYN-2", one.Queries.Single().Specimens.Single().SpecimenId);
    }

    [Fact]
    public void Parsing_error_answer_is_reported_as_a_rejection()
    {
        var message = Assert.Single(Lis02Codec.Parse("H|\\^&|||  00000XX^CDRuby\rC|1|I|Parsing Error^RecordID|G\rL|1|E\r").Messages);

        var diagnostic = Assert.Single(Dialect().Interpret(message, TraceRef.None).Diagnostics);

        Assert.Equal(CellDynRubyDialect.RejectedByAnalyzer, diagnostic.Code);
        Assert.Contains("Parsing Error", diagnostic.Message);
    }

    [Fact]
    public void Host_messages_are_framed_as_whole_messages_like_the_analyzer_sends()
    {
        var profile = Plugin.Profiles[0];
        var records = Dialect().BuildOrders([new WorkOrder("ORD-1", new SpecimenRef("SID-SYN-3"), null, [CodeSet.Of(new CodedValue("CBC", null, "x"))])]);

        var messages = profile.Frame(records, profile.HostFraming);
        var frames = Lis01Frame.BuildTransmission(messages);

        Assert.Single(messages);
        Assert.Single(frames);
        Assert.Equal(Lis02Codec.Serialize(records), Encoding.Latin1.GetString(Lis01Frame.Parse(frames[0].AsMemory(1)).Text.Span));
    }

    [Fact]
    public void Theoretical_trace_follows_the_instrument_framing()
    {
        var plans = new[] { new MessagePlan(TraceDirection.Received, TimeSpan.Zero, Results.Split('\r', StringSplitOptions.RemoveEmptyEntries)) };

        var perMessage = new TraceStackBuilder(Encoding.Latin1).Build(TraceReconstruction.Theoretical(plans, new TheoreticalOptions { InstrumentFraming = AstmFraming.FramePerMessage }).Entries);
        var perRecord = new TraceStackBuilder(Encoding.Latin1).Build(TraceReconstruction.Theoretical(plans).Entries);

        Assert.Equal(7, Assert.Single(perMessage.Messages).Records.Count);
        Assert.Equal(Assert.Single(perRecord.Messages).Records, perMessage.Messages[0].Records);
        Assert.True(perMessage.Events.Count(e => e.Code == "lis01.frame") < perRecord.Events.Count(e => e.Code == "lis01.frame"));
    }
}
