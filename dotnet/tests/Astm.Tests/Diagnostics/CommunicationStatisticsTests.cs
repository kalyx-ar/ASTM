using Astm.Abstractions.Tracing;
using Astm.Diagnostics;
using Astm.Plugins;
using Astm.Protocol.Lis01;

namespace Astm.Tests.Diagnostics;

public sealed class CommunicationStatisticsTests
{
    private static TraceEntry Entry(long sequence, double milliseconds, TraceDirection direction = TraceDirection.Received, byte[]? bytes = null) =>
        new(sequence, TimeSpan.FromMilliseconds(milliseconds), DateTimeOffset.UnixEpoch, direction, bytes ?? [1]);
    [Fact]
    public void Read_intervals_and_direction_changes_are_measured_without_an_operation_or_response_assumption()
    {
        var analysis = new DiagnosticAnalysis([Entry(0, 0), Entry(1, 10), Entry(2, 25, TraceDirection.Transmitted), Entry(3, 100)], [], [], [], []);
        var metrics = CommunicationStatistics.Build(analysis);
        var all = Assert.Single(metrics, m => m.Code == "ReadInterval" && m.Direction == "All");
        Assert.Equal(3, all.Distribution.Count); Assert.Equal(15, all.Distribution.Median!.Value.TotalMilliseconds);
        Assert.Equal(10, all.Distribution.Minimum!.Value.TotalMilliseconds); Assert.Equal(75, all.Distribution.P95!.Value.TotalMilliseconds);
        Assert.Equal(4, all.Bytes); Assert.Equal(4, all.Reads);
        var rx = Assert.Single(metrics, m => m.Code == "ReadInterval" && m.Direction == "Received");
        Assert.Equal(50, rx.Distribution.Median!.Value.TotalMilliseconds);
        Assert.Equal(new[] { 15d, 75d }, metrics.Where(m => m.Code == "DirectionTurn").SelectMany(m => m.Intervals).Select(i => i.Duration.TotalMilliseconds));
        Assert.DoesNotContain(metrics, m => m.Code is "Response" or "OperationTotal");
    }
    [Fact]
    public void Clock_regression_is_counted_and_not_clamped_into_an_invented_zero_latency()
    {
        var analysis = new DiagnosticAnalysis([Entry(0, 10), Entry(1, 0), Entry(2, 30)], [], [], [], []);
        var metric = Assert.Single(CommunicationStatistics.Build(analysis));
        Assert.Equal(1, metric.InvalidIntervals); Assert.Equal(1, metric.Distribution.Count); Assert.Equal(30, metric.Distribution.Median!.Value.TotalMilliseconds);
    }
    [Fact]
    public void Response_total_operation_and_processing_have_separate_boundaries_and_pending_counts()
    {
        var analysis = new DiagnosticAnalysis([Entry(0, 0), Entry(1, 100), Entry(2, 10000)], [], [], [], [])
        { Timings = [new(InstrumentTimingKind.Response, "SYN", 0, 1), new(InstrumentTimingKind.OperationTotal, "SYN", 0, 2),
            new(InstrumentTimingKind.OperationProcessing, "SYN", 1, 2), new(InstrumentTimingKind.OperationTotal, "SYN", 2, null)] };
        var metrics = CommunicationStatistics.Build(analysis);
        Assert.Equal(100, Assert.Single(metrics, m => m.Code == "Response").Distribution.Median!.Value.TotalMilliseconds);
        var total = Assert.Single(metrics, m => m.Code == "OperationTotal"); Assert.Equal(10000, total.Distribution.Median!.Value.TotalMilliseconds); Assert.Equal(1, total.Pending);
        Assert.Equal(9900, Assert.Single(metrics, m => m.Code == "OperationProcessing").Distribution.Median!.Value.TotalMilliseconds);
    }
    [Fact]
    public void A_missing_boundary_is_invalid_and_a_pending_end_does_not_become_a_completed_duration()
    {
        var analysis = new DiagnosticAnalysis([Entry(0, 0)], [], [], [], [])
        { Timings = [new(InstrumentTimingKind.Response, "SYN", 0, 999), new(InstrumentTimingKind.Response, "SYN", 0, null)] };
        var response = Assert.Single(CommunicationStatistics.Build(analysis), m => m.Code == "Response");
        Assert.Equal(1, response.InvalidIntervals); Assert.Equal(1, response.Pending); Assert.Equal(0, response.Distribution.Count); Assert.Null(response.Distribution.Median);
    }
    [Fact]
    public void ASTM_link_confirmations_use_the_passive_decoders_boundaries()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var input = DiagnosticReader.Read("H|\\^&\rL|1|N\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero);
        input = input with { Entries = [Entry(0, 0, bytes: [5]), Entry(1, 10, TraceDirection.Transmitted, [6]),
            Entry(2, 20, bytes: Lis01Frame.Build(1, "H|\\^&\rL|1|N\r"u8, true)), Entry(3, 45, TraceDirection.Transmitted, [6]), Entry(4, 50, bytes: [4])] };
        var analysis = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        var metrics = CommunicationStatistics.Build(analysis);
        Assert.Equal(10, Assert.Single(metrics, m => m.Code == "EnquiryAck").Distribution.Median!.Value.TotalMilliseconds);
        Assert.Equal(25, Assert.Single(metrics, m => m.Code == "FrameAck").Distribution.Median!.Value.TotalMilliseconds);
    }
    [Fact]
    public void Binary_control_byte_values_do_not_create_ASTM_acknowledgement_statistics()
    {
        var analysis = new DiagnosticAnalysis([Entry(0, 0, bytes: [5]), Entry(1, 20, bytes: [6])], [], [], [], []);
        Assert.DoesNotContain(CommunicationStatistics.Build(analysis), m => m.Code is "EnquiryAck" or "FrameAck");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASTM_negative_confirmations_are_responses_and_not_counted_as_unanswered(bool rejectFrame)
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var input = DiagnosticReader.Read("H|\\^&\rL|1|N\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero);
        input = input with { Entries = rejectFrame
            ? [Entry(0, 0, bytes: [5]), Entry(1, 10, TraceDirection.Transmitted, [6]), Entry(2, 20, bytes: Lis01Frame.Build(1, "H|\\^&\rL|1|N\r"u8, true)), Entry(3, 45, TraceDirection.Transmitted, [21])]
            : [Entry(0, 0, bytes: [5]), Entry(1, 10, TraceDirection.Transmitted, [21])] };
        var analysis = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        var metrics = CommunicationStatistics.Build(analysis);
        var rejected = Assert.Single(metrics, m => m.Code == (rejectFrame ? "FrameNak" : "EnquiryNak"));
        Assert.Equal(rejectFrame ? 25 : 10, rejected.Distribution.Median!.Value.TotalMilliseconds);
        Assert.All(metrics, m => Assert.Equal(0, m.Pending));
    }
    [Fact]
    public void A_recorded_checksum_error_with_an_explicit_ACK_still_has_measurable_acknowledgement_time()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var input = DiagnosticReader.Read("H|\\^&\rL|1|N\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero);
        var frame = Lis01Frame.Build(1, "H|\\^&\rL|1|N\r"u8, true); frame[^4] = frame[^4] == 48 ? (byte)49 : (byte)48;
        input = input with { Entries = [Entry(0, 0, bytes: [5]), Entry(1, 10, TraceDirection.Transmitted, [6]), Entry(2, 20, bytes: frame), Entry(3, 45, TraceDirection.Transmitted, [6]), Entry(4, 50, bytes: [4])] };
        var analysis = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        Assert.Contains(analysis.Events, e => e.Code == "lis01.altered-by-recorder");
        Assert.Equal(25, Assert.Single(CommunicationStatistics.Build(analysis), m => m.Code == "FrameAck").Distribution.Median!.Value.TotalMilliseconds);
    }
    [Fact]
    public void UC_order_response_timing_is_a_communication_interval_and_not_an_analysis_operation_duration()
    {
        var profile = new DiagnosticCatalog().Resolve(Astm.Plugin.Sysmex.Urinalysis.SysmexUrinalysisPlugin.Uc3500Profile);
        var query = "RQ02," + "SYN-1".PadRight(14) + "01\r\n";
        var order = "CM00,01," + "SYN-1".PadRight(14) + "01" + "TEST".PadLeft(10) + "\r\n";
        var input = DiagnosticReader.Read(System.Text.Encoding.ASCII.GetBytes(query), DiagnosticInputFormat.WireBytes, profile, "synthetic", TimeSpan.Zero);
        input = input with { Entries = [Entry(0, 0, bytes: System.Text.Encoding.ASCII.GetBytes(query)), Entry(1, 100, TraceDirection.Transmitted, System.Text.Encoding.ASCII.GetBytes(order)), Entry(2, 350, bytes: "ER00,000000\r\n"u8.ToArray())] };
        var analysis = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        var response = Assert.Single(CommunicationStatistics.Build(analysis), m => m.Code == "Response");
        Assert.Equal(250, response.Distribution.Median!.Value.TotalMilliseconds);
        Assert.DoesNotContain(analysis.Timings, t => t.Kind != InstrumentTimingKind.Response);
    }
    [Fact]
    public void Raw_file_without_capture_clock_does_not_report_invented_zero_durations()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var input = DiagnosticReader.Read("H|\\^&\rL|1|N\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero);
        Assert.False(CommunicationStatistics.HasCaptureClock(input));
        var analysis = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        foreach (var metric in CommunicationStatistics.Build(analysis, CommunicationStatistics.HasCaptureClock(input)))
        { Assert.False(metric.ClockAvailable); Assert.Null(metric.Distribution.Median); Assert.Empty(metric.Intervals); }
    }
    [Fact]
    public void Equal_recorded_timestamps_are_real_zero_resolution_intervals()
    {
        var profile = new DiagnosticCatalog().Resolve(StandardAstmPlugin.ProfileId);
        var input = DiagnosticReader.Read("2026/10/25 10:00:00.000 R 001{[ENQ]}\r\n2026/10/25 10:00:00.000 R 001{[ACK]}\r\n"u8.ToArray(), DiagnosticInputFormat.SysmexLog, profile, "synthetic", TimeSpan.Zero);
        Assert.True(CommunicationStatistics.HasCaptureClock(input));
        var analysis = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        var metric = Assert.Single(CommunicationStatistics.Build(analysis, true), m => m.Code == "ReadInterval");
        Assert.Equal(TimeSpan.Zero, metric.Distribution.Median); Assert.Equal(1, metric.Distribution.Count);
    }
}
