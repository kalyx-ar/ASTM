using System.Text;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.Glyms.Testing;
using Atsm.Protocol.Lis01;
using Atsm.Tracing;

namespace Atsm.Tests.Plugins;

public class GlymsTestingPluginTests
{
    private const string Dash = "\u0096";

    private static readonly byte[] PatientFrame = Lis01Frame.Build(2, Encoding.Latin1.GetBytes("P|1||12345678|12345678|Doe^John|||U\r"), isFinal: true);
    private static readonly byte[] ResultFrame = Lis01Frame.Build(3, Encoding.Latin1.GetBytes("R|1|^^^651^B-hCG^UNDILUTED^P^78462UD00^07081^^F|< 1.20|mIU/mL||||F\r"), isFinal: true);

    // Synthetic log with the layout of a Glyms interface log (Windows-1252, en dash, 12-hour clock).
    private static byte[] SyntheticLog()
    {
        var log = new StringBuilder();
        void Line(string time, string label, string content, string count) =>
            log.Append($"INFO 2026-01-13 {time} PM {Dash} Architect {label}>{content}<{count}\r\n");
        Line("12:31:30.000", "<<< ", "\u0005", "1");
        Line("12:31:30.100", "Host.EC", "\u0006", "");
        foreach (var frame in new[] { PatientFrame, ResultFrame })
        {
            // The frame ends with CR LF, which is also where the log breaks the line before "<count".
            log.Append($"INFO 2026-01-13 12:31:31.722 PM {Dash} Architect <<< >").Append(Encoding.Latin1.GetString(frame)).Append($"<{frame.Length}\r\n");
            Line("12:31:31.800", "Host.EC", "\u0006", "");
        }
        Line("12:31:36.110", "<<< ", "\u0004", "1");
        log.Append($"INFO 2026-01-13 12:31:36.479 PM {Dash} Architect Procesada Correctamente >H|\\^&\rP|1||12345678|12345678|Doe^John|||U\rL|1\r<\r\n");
        return Encoding.Latin1.GetBytes(log.ToString());
    }

    [Fact]
    public void Glyms_log_is_extracted_byte_exact_through_the_generic_adapter()
    {
        var adapter = new GlymsTestingPlugin().LogAdapters.Single();

        var extraction = adapter.Extract(SyntheticLog(), new LogAdapterOptions());

        Assert.Equal(0, extraction.Skipped);
        Assert.Equal("Architect", extraction.InstrumentHint);
        Assert.Equal(8, extraction.Segments.Count);
        Assert.Equal([ControlCharacters.Enq], extraction.Segments[0].Data.ToArray());
        Assert.Equal(TraceDirection.Transmitted, extraction.Segments[1].Direction);
        Assert.Equal(PatientFrame, extraction.Segments[2].Data.ToArray());
        Assert.Equal(ResultFrame, extraction.Segments[4].Data.ToArray());
        Assert.Equal(SegmentContent.Event, extraction.Segments[^1].Content);
        Assert.StartsWith("Architect Procesada Correctamente: H|", extraction.Segments[^1].Detail);
    }

    [Fact]
    public void Extracted_log_feeds_the_neutral_stack_and_anonymizer()
    {
        var extraction = new GlymsTestingPlugin().LogAdapters.Single().Extract(SyntheticLog(), new LogAdapterOptions());
        var (_, entries) = TraceReconstruction.Real(extraction.Segments, "synthetic", extraction.InstrumentHint);

        var stack = new TraceStackBuilder(Encoding.Latin1).Build(entries);
        var (anonymized, report) = new TraceAnonymizer().Anonymize(entries);

        Assert.Equal(2, Assert.Single(stack.Messages).Records.Count);
        Assert.True(report.IsClean);
        var text = string.Concat(anonymized.Select(e => Encoding.Latin1.GetString(e.Data.Span) + e.Detail));
        Assert.DoesNotContain("Doe", text);
        Assert.DoesNotContain("12345678", text);
        Assert.Contains("< 1.20", text);
    }
}
