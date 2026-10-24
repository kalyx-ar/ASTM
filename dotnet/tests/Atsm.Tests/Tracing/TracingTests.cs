using System.Text;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.Sysmex.Uwam;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Tracing;

namespace Atsm.Tests.Tracing;

public class TracingTests
{
    private static readonly string[] Query = ["H|\\^&|||SYN-ANALYZER", "Q|1|^SID-1||^^^ALL||||||||O", "L|1"];
    private static readonly string[] Answer = ["H|\\^&", "P|1", "O|1|SID-1||^^^16|R||||||N||||||||||||||Q", "L|1|N"];

    [Fact]
    public void Theoretical_trace_round_trips_through_the_stack()
    {
        var plans = new List<MessagePlan>
        {
            new(TraceDirection.Received, TimeSpan.Zero, Query),
            new(TraceDirection.Transmitted, TimeSpan.FromSeconds(1), Answer),
            new(TraceDirection.Received, TimeSpan.FromSeconds(2), [.. Query[..1], "R|1|^^^16|" + new string('9', 600), "L|1"]),
        };

        var (header, entries) = TraceReconstruction.Theoretical(plans, new TheoreticalOptions { BaudRate = 9600 });
        var stack = new TraceStackBuilder(Encoding.Latin1).Build(entries);

        Assert.Equal("theoretical", header.Source);
        Assert.Equal(3, stack.Messages.Count);
        Assert.All(stack.Messages, m => Assert.True(m.Complete));
        Assert.Equal(plans.Select(p => (p.Direction, string.Join('\r', p.Records))), stack.Messages.Select(m => (m.Direction, string.Join('\r', m.Records))));
        Assert.Contains(stack.Events, e => e.Code == "lis01.established" && e.Direction == TraceDirection.Transmitted);
        // 600 characters do not fit in one frame: the long record is split with ETB.
        Assert.True(stack.Events.Count(e => e.Code == "lis01.frame") > 3 + 4 + 3);
        // At 9600 baud the long message takes longer than 600 characters * 10 bits.
        Assert.True(stack.Messages[2].End - stack.Messages[2].Start >= TimeSpan.FromSeconds(600 * 10 / 9600.0));
    }

    [Fact]
    public void Same_method_rebuilds_a_theoretical_trace_from_a_real_one()
    {
        var (_, real) = TraceReconstruction.Theoretical([new(TraceDirection.Received, TimeSpan.Zero, Query), new(TraceDirection.Transmitted, TimeSpan.Zero, Answer)]);
        var realStack = new TraceStackBuilder(Encoding.Latin1).Build(real);

        var (_, theoretical) = TraceReconstruction.Theoretical(TraceReconstruction.Plan(realStack), new TheoreticalOptions { BaudRate = 0 });
        var comparison = TraceComparison.Of(realStack, new TraceStackBuilder(Encoding.Latin1).Build(theoretical));

        Assert.True(comparison.SameMessages);
        Assert.Equal(1, comparison.First.HostAnswerLatency.Count);
        Assert.Equal(0, comparison.First.UnansweredQueries);
    }

    [Fact]
    public void Observer_reports_rejection_and_retransmission()
    {
        var frame = Lis01Frame.Build(1, "H|\\^&\r"u8, isFinal: true);
        var corrupted = frame.ToArray();
        corrupted[3] ^= 0x01;
        var entries = Entries(
            (TraceDirection.Received, [ControlCharacters.Enq]),
            (TraceDirection.Transmitted, [ControlCharacters.Ack]),
            (TraceDirection.Received, corrupted),
            (TraceDirection.Transmitted, [ControlCharacters.Nak]),
            (TraceDirection.Received, frame),
            (TraceDirection.Transmitted, [ControlCharacters.Ack]),
            (TraceDirection.Received, [ControlCharacters.Eot]));

        var stack = new TraceStackBuilder(Encoding.Latin1).Build(entries);

        Assert.Contains(stack.Events, e => e.Code == "lis01.invalid-frame");
        Assert.Contains(stack.Events, e => e.Code == "lis01.frame-rejected");
        Assert.DoesNotContain(stack.Events, e => e.Code == "lis01.altered-by-recorder");
        Assert.Equal(["H|\\^&"], Assert.Single(stack.Messages).Records);
    }

    [Fact]
    public void Acknowledged_frame_with_a_wrong_recorded_checksum_is_attributed_to_the_recorder()
    {
        // The recorder wrote a space where the instrument sent 0xB5 (as observed in a vendor port monitor).
        var original = Lis01Frame.Build(1, Encoding.Latin1.GetBytes("R|1|^^^RBC|2.8|/µl\r"), isFinal: true);
        var recorded = original.ToArray();
        recorded[Array.IndexOf(recorded, (byte)0xB5)] = (byte)' ';
        var entries = Entries(
            (TraceDirection.Received, [ControlCharacters.Enq]),
            (TraceDirection.Transmitted, [ControlCharacters.Ack]),
            (TraceDirection.Received, recorded),
            (TraceDirection.Transmitted, [ControlCharacters.Ack]),
            (TraceDirection.Received, [ControlCharacters.Eot]));

        var stack = new TraceStackBuilder(Encoding.Latin1).Build(entries);

        Assert.Contains(stack.Events, e => e.Code == "lis01.altered-by-recorder");
        Assert.Equal(["R|1|^^^RBC|2.8|/ l"], Assert.Single(stack.Messages).Records);

        var (anonymized, report) = new TraceAnonymizer().Anonymize(entries);
        var rewritten = anonymized.Single(e => !e.Data.IsEmpty && e.Data.Span[0] == ControlCharacters.Stx).Data.ToArray();
        Assert.Equal(Lis01FrameError.Checksum, Lis01Frame.Parse(rewritten.AsMemory(1)).Error);
        Assert.Equal(0, report.UnparsedFrames);
    }

    [Fact]
    public void Message_level_entries_build_messages_without_link_events()
    {
        var segments = Query.Select(r => new TraceSegment(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TraceDirection.Received, SegmentContent.Message, Encoding.Latin1.GetBytes(r + "\r")));
        var (_, entries) = TraceReconstruction.Real(segments, "test", null);

        var stack = new TraceStackBuilder(Encoding.Latin1).Build(entries);

        Assert.Equal(Query, Assert.Single(stack.Messages).Records);
        Assert.DoesNotContain(stack.Events, e => e.Level == StackLevel.Link);
        var theoretical = new TraceStackBuilder(Encoding.Latin1).Build(TraceReconstruction.Theoretical(TraceReconstruction.Plan(stack)).Entries);
        Assert.Contains(theoretical.Events, e => e.Code == "lis01.transmission-end");
    }

    [Fact]
    public void Sysmex_port_monitor_lines_become_segments_from_the_host_point_of_view()
    {
        // Synthetic lines in the layout of the Sysmex port monitor, written by the instrument side of the LIS port.
        var log = Encoding.Latin1.GetBytes(
            "2025/11/05 00:45:29.301 OPEN\r\n" +
            "2025/11/05 00:45:29.300 S  001{[ENQ]}\r\n" +
            "2025/11/05 00:45:29.302 R  001{[ACK]}\r\n" +
            "2025/11/05 00:45:29.312 S  020{[STX]1H|\\^&|||U-WAM[CR][ETX]00[CR][LF]}\r\n" +
            "2025/11/05 00:45:29.320 R  009{L|1|N[CR]}\r\n");
        var adapter = new UwamPlugin().LogAdapters.Single();

        var extraction = adapter.Extract(log, new LogAdapterOptions(RecordedBy.Instrument));

        Assert.Equal(5, extraction.Segments.Count);
        Assert.Equal(SegmentContent.Event, extraction.Segments[0].Content);
        Assert.Equal((TraceDirection.Received, SegmentContent.Wire), (extraction.Segments[1].Direction, extraction.Segments[1].Content));
        Assert.Equal([ControlCharacters.Enq], extraction.Segments[1].Data.ToArray());
        Assert.Equal(TraceDirection.Transmitted, extraction.Segments[2].Direction);
        Assert.Equal(ControlCharacters.Stx, extraction.Segments[3].Data.Span[0]);
        Assert.Equal(SegmentContent.Message, extraction.Segments[4].Content);
        // Declared lengths are checked: 020 and 009 do not match the decoded payloads.
        Assert.Contains(extraction.Findings, f => f.StartsWith("declared length differs", StringComparison.Ordinal));
        Assert.Equal(TraceDirection.Received, adapter.Extract(log, new LogAdapterOptions(RecordedBy.Host)).Segments[2].Direction);
    }

    private static List<TraceEntry> Entries(params (TraceDirection Direction, byte[] Data)[] items) =>
        items.Select((item, i) => new TraceEntry(i, TimeSpan.FromMilliseconds(i * 10), DateTimeOffset.UnixEpoch, item.Direction, item.Data)).ToList();
}
