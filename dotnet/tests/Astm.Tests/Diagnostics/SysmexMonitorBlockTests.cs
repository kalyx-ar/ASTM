using System.Text;
using Astm.Abstractions.Tracing;
using Astm.Plugin.Sysmex.Urinalysis;

namespace Astm.Tests.Diagnostics;

public sealed class SysmexMonitorBlockTests
{
    [Fact]
    public void Long_block_reads_are_all_preserved_when_decoration_braces_span_multiple_lines()
    {
        var result = new SysmexPortMonitorAdapter().Extract(Encoding.Latin1.GetBytes(
            "2026/10/25 10:00:00.000 OPEN\r\n" +
            "2026/10/25 10:00:00.001 R 003{[STX][NUL][NUL]\r\n" +
            "2026/10/25 10:00:00.002 R 00312[NUL]\r\n" +
            "2026/10/25 10:00:00.003 R 002[NUL][ETX]}\r\n"), new(RecordedBy.Host));
        var data = result.Segments.Where(s => s.Direction != TraceDirection.Event).ToArray();
        Assert.Equal(3, data.Length); Assert.Empty(result.Findings); Assert.Equal(0, result.Skipped);
        Assert.Equal(new byte[] { 2, 0, 0, 49, 50, 0, 0, 3 }, data.SelectMany(s => s.Data.ToArray()).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, data.Select(s => s.Timestamp!.Value.Millisecond));
    }
    [Fact]
    public void Literal_binary_braces_are_preserved_and_decoration_state_is_separate_for_RX_and_TX()
    {
        var result = new SysmexPortMonitorAdapter().Extract(Encoding.Latin1.GetBytes(
            "2026/10/25 10:00:00.000 OPEN\r\n" +
            "2026/10/25 10:00:00.001 R 003{[STX][NUL]}\r\n" +
            "2026/10/25 10:00:00.002 S 003{[STX][NUL]}\r\n" +
            "2026/10/25 10:00:00.003 R 003{[NUL][ETX]}\r\n" +
            "2026/10/25 10:00:00.004 S 003{[NUL][ETX]}\r\n"), new(RecordedBy.Host));
        Assert.Empty(result.Findings);
        foreach (var direction in new[] { TraceDirection.Received, TraceDirection.Transmitted })
            Assert.Equal(new byte[] { 2, 0, 125, 123, 0, 3 }, result.Segments.Where(s => s.Direction == direction).SelectMany(s => s.Data.ToArray()).ToArray());
    }
    [Fact]
    public void Existing_complete_records_keep_spaces_and_report_incorrect_counters_without_changing_data()
    {
        var result = new SysmexPortMonitorAdapter().Extract(Encoding.Latin1.GetBytes(
            "2026/10/25 10:00:00.001 R 004{ A }\r\n" +
            "2026/10/25 10:00:00.002 R 1{B}\r\n"), new(RecordedBy.Host));
        Assert.Equal(" A ", Encoding.Latin1.GetString(result.Segments[0].Data.Span));
        Assert.Equal("B", Encoding.Latin1.GetString(result.Segments[1].Data.Span));
        Assert.Single(result.Findings); Assert.Contains("declared length", result.Findings[0]);
    }
    [Fact]
    public void An_ambiguous_initial_block_keeps_the_existing_braced_parse_and_reports_the_mismatch()
    {
        var result = new SysmexPortMonitorAdapter().Extract("2026/10/25 10:00:00.001 R 002{A}\r\n"u8, new(RecordedBy.Host));
        Assert.Equal("A", Encoding.Latin1.GetString(Assert.Single(result.Segments).Data.Span));
        Assert.Single(result.Findings);
    }
}
