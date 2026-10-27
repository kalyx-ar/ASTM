using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Astm.Abstractions.Hosting;
using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;
using Astm.Plugin.Abbott.Architect;
using Astm.Plugins;
using Astm.Protocol.Hl7v2;
using Astm.Protocol.Lis02;
using Astm.Protocol.Mllp;
using Astm.Runtime.Sessions;
using Astm.Runtime.Tracing;
using Astm.Testing;
using Astm.Tests.Architect;

namespace Astm.Tests.Hl7;

public class Hl7Tests
{
    [Fact]
    public void MSH_numbering_repetitions_subcomponents_and_custom_separators_round_trip()
    {
        var message = Assert.IsType<Hl7Message>(Hl7Codec.Parse(Hl7Data.Result()).Message);
        Assert.Equal("|", message.Header.Field(1)); Assert.Equal("^~\\&", message.Header.Field(2));
        Assert.Equal("ARCHITECT", message.Header.Text(3)); Assert.Equal("OUL", message.Type);
        Assert.Equal("R22", message.Trigger); Assert.Equal("MSG-SYN", message.ControlId);
        Assert.Equal("UNICODE UTF-8", message.Header.Field(18));
        var obx = message.OfType("OBX").First();
        Assert.Equal("0021", obx.Text(3, subcomponent: 1));
        Assert.Equal("F", obx.Text(3, subcomponent: 3));
        Assert.Equal("<", obx.Repetitions(8)[1]);
        var custom = Hl7Data.Result().Select(c => c switch { '|' => '*', '^' => '%', '~' => '!', '\\' => '?', '&' => '$', _ => c }).ToArray();
        var parsed = Assert.IsType<Hl7Message>(Hl7Codec.Parse(new string(custom)).Message);
        Assert.Equal("F", parsed.OfType("OBX").First().Text(3, subcomponent: 3));
        Assert.Equal(new string(custom), parsed.Serialize());
    }

    [Fact]
    public void Escapes_preserve_unknown_extensions_and_do_not_inject_segments()
    {
        var d = Hl7Delimiters.Default;
        const string text = "a|b^c~d&e\\f\rα";
        Assert.Equal(text, d.Decode(d.Encode(text)));
        Assert.Equal("α", d.Decode("\\XCEB1\\"));
        Assert.Equal("\\Zlocal\\", d.Decode("\\Zlocal\\"));
        Assert.Throws<FormatException>(() => d.Decode("bad\\F"));
        Assert.Throws<ArgumentException>(() => Hl7Segment.Build("NTE", d, (3, "text\rOBX|injection")));
        Assert.True(new Hl7Segment("PID|1||\"\"", d).IsNull(3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PID|1\r")]
    [InlineData("MSH|^^^^|A|B|||||OUL^R22|M1|P|2.5.1\r")]
    [InlineData("MSH|^~\\&|A|B||||OUL^R22||P|2.5.1\r")]
    public void Invalid_headers_are_rejected_without_partial_messages(string text)
    { Assert.Null(Hl7Codec.Parse(text).Message); Assert.NotEmpty(Hl7Codec.Parse(text).Diagnostics); }

    [Fact]
    public void Multiple_messages_inside_one_MLLP_payload_are_rejected()
    { Assert.Null(Hl7Codec.Parse(Hl7Data.Result() + Hl7Data.Result()).Message); }

    [Fact]
    public void Original_and_enhanced_ACK_policies_have_distinct_phases_and_correlation()
    {
        var request = Hl7Codec.Parse(Hl7Data.Result()).Message!;
        Assert.True(Hl7Acknowledgements.Required(request, Hl7AcknowledgementPhase.Application, true));
        Assert.False(Hl7Acknowledgements.Required(request, Hl7AcknowledgementPhase.Commit, true));
        var enhancedHeader = Hl7Segment.Build("MSH", request.Delimiters, (9, "OUL^R22^OUL_R22"), (10, "M2"), (11, "P"), (12, "2.5.1"), (15, "AL"), (16, "ER"));
        var enhanced = new Hl7Message(request.Delimiters, [enhancedHeader]);
        Assert.True(Hl7Acknowledgements.Required(enhanced, Hl7AcknowledgementPhase.Commit, true));
        Assert.False(Hl7Acknowledgements.Required(enhanced, Hl7AcknowledgementPhase.Application, true));
        Assert.True(Hl7Acknowledgements.Required(enhanced, Hl7AcknowledgementPhase.Application, false));
        var response = Hl7Acknowledgements.Build(request, "CA", "NEW-ID", DateTimeOffset.UtcNow);
        var ack = Assert.IsType<Hl7Acknowledgement>(Hl7Acknowledgements.Read(response));
        Assert.Equal("MSG-SYN", ack.ControlId); Assert.Equal("NEW-ID", response.ControlId);
        Assert.Equal(Hl7AcknowledgementPhase.Commit, ack.Phase);
        Assert.False(Hl7Acknowledgements.Required(response, Hl7AcknowledgementPhase.Application, true));
    }

    [Fact]
    public void ASTM_and_HL7_Architect_results_have_equivalent_core_semantics()
    {
        var astm = new ArchitectPlugin(new() { InstrumentTimeZone = TimeZoneInfo.Utc });
        var instrument = new InstrumentRef("a1");
        var expected = astm.CreateDialect(astm.Profiles[0], new(instrument, TimeProvider.System))
            .Interpret(Assert.Single(Lis02Codec.Parse(ArchitectDialectTests.PatientUpload).Messages), TraceRef.None).Observations.Take(3);
        var actual = Hl7Data.Dialect().Interpret(Hl7Codec.Parse(Hl7Data.Result()).Message!, TraceRef.None);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Select(Project), actual.Observations.Select(Project));
        Assert.Equal("Example Result Comment", actual.Observations[0].Comments.Single());
        Assert.Equal("47331M100", actual.Observations[0].Reagents.Single().Lot);
    }

    private static object Project(InstrumentObservation o) => (o.Specimen.SpecimenId, o.Test.Vendor.Code,
        o.ResultKind, o.Value.Numeric, o.Value.Comparator, o.Value.Coded?.Code, o.InstrumentStatusMeaning);

    [Theory]
    [InlineData(IngestState.Accepted, "AA")]
    [InlineData(IngestState.Duplicate, "AA")]
    [InlineData(IngestState.Deferred, "AE")]
    [InlineData(IngestState.Rejected, "AR")]
    public async Task Application_ACK_reflects_host_custody_of_the_whole_batch(IngestState state, string code)
    {
        var host = new ControlledHost { Outcome = new(state) };
        var processor = Hl7Data.Processor(host);
        var receipt = Guid.NewGuid();
        var result = await processor.ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result()), receipt);
        Assert.Equal(code, Hl7Acknowledgements.Read(Assert.Single(result.Responses))!.Code);
        Assert.Equal("MSG-SYN", Hl7Acknowledgements.Read(result.Responses[0])!.ControlId);
        await processor.ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result()), receipt);
        Assert.Equal(host.Batches[0].Observations.Select(o => o.ObservationId), host.Batches[1].Observations.Select(o => o.ObservationId));
    }

    [Fact]
    public async Task No_positive_ACK_is_available_until_durable_ingestion_completes()
    {
        var host = new ControlledHost { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var process = Hl7Data.Processor(host).ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result()), Guid.NewGuid()).AsTask();
        Assert.False(process.IsCompleted);
        host.Gate.SetResult(IngestOutcome.Accepted);
        Assert.Equal("AA", Hl7Acknowledgements.Read(Assert.Single((await process).Responses))!.Code);
    }

    [Fact]
    public async Task Host_failure_produces_AE_and_cancellation_never_produces_AA()
    {
        var result = await Hl7Data.Processor(new ControlledHost { Throw = true }).ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result()), Guid.NewGuid());
        Assert.Equal("AE", Hl7Acknowledgements.Read(Assert.Single(result.Responses))!.Code);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Hl7Data.Processor(new ControlledHost())
            .ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result()), Guid.NewGuid(), stop.Token));
    }

    [Fact]
    public async Task Unsupported_profile_or_value_type_rejects_the_entire_message()
    {
        var host = new ControlledHost();
        var result = await Hl7Data.Processor(host).ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result().Replace("OBX|2|ST", "OBX|2|ED")), Guid.NewGuid());
        Assert.Equal("AR", Hl7Acknowledgements.Read(Assert.Single(result.Responses))!.Code); Assert.Empty(host.Batches);
        result = await Hl7Data.Processor(host).ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result().Replace("2.5.1", "2.3.1")), Guid.NewGuid());
        Assert.Equal("AR", Hl7Acknowledgements.Read(Assert.Single(result.Responses))!.Code); Assert.Empty(host.Batches);
    }

    [Theory]
    [InlineData("1,20", false)]
    [InlineData("1,200.50", true)]
    [InlineData("1200.50", true)]
    public async Task Numeric_grouping_is_not_guessed(string value, bool accepted)
    {
        var host = new ControlledHost();
        var result = await Hl7Data.Processor(host).ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result().Replace("9245", value)), Guid.NewGuid());
        Assert.Equal(accepted ? "AA" : "AR", Hl7Acknowledgements.Read(Assert.Single(result.Responses))!.Code);
        if (accepted) Assert.Equal(1200.50m, host.Batches.Single().Observations.Last().Value.Numeric);
        else Assert.Empty(host.Batches);
    }

    [Fact]
    public async Task ZOS_queries_echo_QPD_and_distinguish_missing_orders_from_unavailable_host()
    {
        var host = new ControlledHost();
        var processor = Hl7Data.Processor(host);
        var query = Encoding.UTF8.GetBytes(Hl7Data.Query());
        var negative = Assert.Single((await processor.ProcessAsync(query, Guid.NewGuid())).Responses);
        Assert.Equal("NF", negative.OfType("QAK").Single().Text(2));
        Assert.Empty(negative.OfType("SPM")); Assert.Equal("ZOS", negative.Trigger);
        host.Answer = new(QueryAnswerState.Orders, [Hl7Data.Order()]);
        var positive = Assert.Single((await processor.ProcessAsync(query, Guid.NewGuid())).Responses);
        Assert.Equal("OK", positive.OfType("QAK").Single().Text(2)); Assert.Single(positive.OfType("OBR"));
        Assert.Equal(Hl7Codec.Parse(Hl7Data.Query()).Message!.OfType("QPD").Single().Raw, positive.OfType("QPD").Single().Raw);
        host.Answer = OrderQueryAnswer.Unavailable;
        var unavailable = Assert.Single((await processor.ProcessAsync(query, Guid.NewGuid())).Responses);
        Assert.Equal("AE", unavailable.OfType("QAK").Single().Text(2));
        Assert.Equal("AE", Hl7Acknowledgements.Read(unavailable)!.Code);
    }

    [Fact]
    public async Task Query_budget_expiration_is_unavailable_instead_of_no_orders()
    {
        var host = new ControlledHost { QueryDelay = TimeSpan.FromSeconds(10) };
        var result = await Hl7Data.Processor(host).ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Query()), Guid.NewGuid()).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var response = Assert.Single(result.Responses);
        Assert.Equal("AE", response.OfType("QAK").Single().Text(2));
        Assert.Contains(result.Diagnostics, d => d.Code == SessionCodes.HostQueryTimeout);
    }

    [Fact]
    public async Task Cancellation_during_custody_produces_no_acknowledgement()
    {
        using var stop = new CancellationTokenSource();
        var host = new ControlledHost { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var pending = Hl7Data.Processor(host).ProcessAsync(Encoding.UTF8.GetBytes(Hl7Data.Result()), Guid.NewGuid(), stop.Token).AsTask();
        Assert.False(pending.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Order_messages_have_one_specimen_one_action_and_escaped_patient_fields()
    {
        var order = Hl7Data.Order() with { Patient = new("SYN-P", "Family|Synthetic", "Given^Synthetic") };
        var message = Hl7Data.Dialect().BuildOrders([order], "ORDER-1");
        Assert.Equal("OML", message.Type); Assert.Equal("O33", message.Trigger);
        Assert.Equal("Family|Synthetic", message.OfType("PID").Single().Text(5));
        Assert.Equal("0021", message.OfType("OBR").Single().Text(4));
        Assert.Throws<ArgumentException>(() => Hl7Data.Dialect().BuildOrders([order, order with { Action = OrderAction.Cancel }], "ORDER-2"));
        Assert.Throws<ArgumentException>(() => Hl7Data.Dialect().BuildOrders([order, order with { Specimen = new("OTHER") }], "ORDER-3"));
    }

    [Fact]
    public void LAW_query_acknowledgement_is_not_an_embedded_order_download()
    {
        var d = Hl7Delimiters.Default;
        var query = new Hl7Message(d, [Hl7Segment.Build("MSH", d, (9, "QBP^Q11^QBP_Q11"), (10, "LAW-Q1"), (11, "P"), (12, "2.5.1"), (21, "LAB-27^IHE")),
            new("QPD|WOS||CONTAINER-SYN", d), new("RCP|I", d)]);
        Assert.Equal("CONTAINER-SYN", LawQueryMessages.ContainerId(query));
        var response = LawQueryMessages.Acknowledge(query, "LAW-R1", DateTimeOffset.UtcNow);
        Assert.Equal("K11", response.Trigger); Assert.Equal("LAB-27^IHE", response.Header.Field(21));
        Assert.Equal(query.OfType("QPD").Single().Raw, response.OfType("QPD").Single().Raw);
        Assert.Empty(response.OfType("OBR")); Assert.Empty(response.OfType("SPM"));
    }
}

internal static class Hl7Data
{
    public static ArchitectHl7Plugin Plugin() => new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300), TimeZoneInfo.Utc);
    public static IHl7Dialect Dialect() => Plugin().CreateDialect(Plugin().Profiles[0], new(new("a1"), TimeProvider.System));
    public static Hl7MessageProcessor Processor(ILabWorkflowHost host, ITraceSink? trace = null) => new(Dialect(), Plugin().Profiles[0], host, new("a1"), trace ?? new MemoryTraceSink());
    public static WorkOrder Order() => new("ORD-SYN", new("SID-SYN-1"), null, [CodeSet.Of(new("0021", "B-hCG", ArchitectDialect.VendorSystem))]);
    public static string Header(string type, string id) => $"MSH|^~\\&|ARCHITECT|SYN|HOST|LAB|20260126081030+0000||{type}|{id}|P|2.5.1||||||UNICODE UTF-8\r";
    public static string Query() => Header("QBP^ZOS^QBP_Q11", "QUERY-SYN") + "SFT|SYN|1.0|ARCHITECT|\"\"\rQPD|ZOS^Order for Specimen^ARCHITECT|TAG-SYN|SID-SYN-1\rRCP|I||R\r";
    public static string Result() => Header("OUL^R22^OUL_R22", "MSG-SYN") + "SFT|SYN|1.0|ARCHITECT|\"\"\rSPM|1|SID-SYN-1|||||||||P\rSAC|1\rOBR|1|||0021^B-hCG^ARCHITECT\rTQ1|1\r"
        + Segment("OBX", (1, "1"), (2, "ST"), (3, "0021&UNDILUTED&F^B-hCG^ARCHITECT"), (5, "< 1.20"), (6, "mIU/mL^^ARCHITECT"), (7, "0.35 TO 4.94"), (8, "EXP~<"), (11, "F"), (16, "OPER1"), (18, "SYN-MODULE&1~SYN-SYSTEM"), (19, "20260126081030"))
        + "SID|0021&1&SR|47331M100|00788\rNTE|1||Example Result Comment\r"
        + Segment("OBX", (1, "2"), (2, "ST"), (3, "0021&UNDILUTED&I^B-hCG^ARCHITECT"), (5, "NEGATIVE"), (11, "F"), (18, "SYN-MODULE&1~SYN-SYSTEM"), (19, "20260126081030"))
        + Segment("OBX", (1, "3"), (2, "NM"), (3, "0021&UNDILUTED&P^B-hCG^ARCHITECT"), (5, "9245"), (6, "RLU^^ARCHITECT"), (11, "F"), (18, "SYN-MODULE&1~SYN-SYSTEM"), (19, "20260126081030"));
    private static string Segment(string name, params (int Number, string Text)[] fields)
    {
        var values = new string[fields.Max(f => f.Number) + 1]; values[0] = name;
        foreach (var (number, text) in fields) values[number] = text;
        return string.Join('|', values) + "\r";
    }
}

internal sealed class ControlledHost : ILabWorkflowHost
{
    public IngestOutcome Outcome { get; set; } = IngestOutcome.Accepted;
    public OrderQueryAnswer Answer { get; set; } = OrderQueryAnswer.NoInformation;
    public TaskCompletionSource<IngestOutcome>? Gate { get; set; }
    public bool Throw { get; set; }
    public TimeSpan QueryDelay { get; set; }
    public List<InstrumentObservationBatch> Batches { get; } = [];
    public async ValueTask<OrderQueryAnswer> AnswerQueryAsync(OrderQuery query, CancellationToken token)
    { if (QueryDelay > TimeSpan.Zero) await Task.Delay(QueryDelay, token); return Answer; }
    public async ValueTask<IngestOutcome> IngestAsync(InstrumentObservationBatch batch, CancellationToken token)
    { if (Throw) throw new IOException("synthetic host failure"); Batches.Add(batch); return Gate is null ? Outcome : await Gate.Task.WaitAsync(token); }
    public async IAsyncEnumerable<WorkOrder> PendingOrdersAsync(InstrumentRef instrument, [EnumeratorCancellation] CancellationToken token) { await Task.CompletedTask; yield break; }
    public ValueTask AcknowledgeOrderAsync(string orderId, OrderDeliveryOutcome outcome, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask<CodeMappingSet?> MappingsAsync(InstrumentRef instrument, CancellationToken token) => ValueTask.FromResult<CodeMappingSet?>(null);
}
