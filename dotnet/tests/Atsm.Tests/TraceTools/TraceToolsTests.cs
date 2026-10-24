using System.Text;
using Atsm.Abstractions.Tracing;
using Atsm.Protocol.Lis01;
using Atsm.TraceTools;

namespace Atsm.Tests.TraceTools;

public class TraceToolsTests
{
    private const string Dash = "\u0096";

    private static readonly byte[] PatientFrame = Lis01Frame.Build(2, Encoding.Latin1.GetBytes("P|1||12345678|12345678|Doe^John|||U\r"), isFinal: true);
    private static readonly byte[] OrderFrame = Lis01Frame.Build(3, Encoding.Latin1.GetBytes("O|1|D100200PH|D100200PH^W536^1|^^^651^B-hCG^UNDILUTED^P|R||||||||||||||||||||F\r"), isFinal: true);
    private static readonly byte[] ControlFrame = Lis01Frame.Build(4, Encoding.Latin1.GetBytes("O|2|MULTI Level 1|MULTI Level 1^J429^1|^^^530^B12^SAMP/CNTRL^P|R||||||Q||||||||||||||F\r"), isFinal: true);
    private static readonly byte[] ResultFrame = Lis01Frame.Build(5, Encoding.Latin1.GetBytes("R|1|^^^651^B-hCG^UNDILUTED^P^78462UD00^07081^^F|2496.16|mIU/mL||||F||ADMIN^ADMIN||20260113122952|i1SR02002\r"), isFinal: true);

    // Synthetic log with the exact layout of a Glyms interface log (Windows-1252, en dash, 12-hour clock).
    private static byte[] SyntheticLog()
    {
        var log = new StringBuilder();
        void Line(string time, string label, string content, string count) =>
            log.Append($"INFO 2026-01-13 {time} PM {Dash} Architect {label}>{content}<{count}\r\n");
        Line("12:31:30.000", "<<< ", "\u0005", "1");
        Line("12:31:30.100", "Host.EC", "\u0006", "");
        foreach (var frame in new[] { PatientFrame, OrderFrame, ControlFrame, ResultFrame })
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
    public void Glyms_log_is_imported_byte_exact_with_directions()
    {
        var result = GlymsLogImporter.Import(SyntheticLog(), "synthetic.log");

        Assert.Equal(0, result.Skipped);
        Assert.Equal("Architect", result.Header.InstrumentId);
        Assert.Equal(12, result.Entries.Count);
        Assert.Equal([ControlCharacters.Enq], result.Entries[0].Data.ToArray());
        Assert.Equal(TraceDirection.Transmitted, result.Entries[1].Direction);
        Assert.Equal(PatientFrame, result.Entries[2].Data.ToArray());
        Assert.Equal(ResultFrame, result.Entries[8].Data.ToArray());
        var processed = result.Entries[^1];
        Assert.Equal(TraceDirection.Event, processed.Direction);
        Assert.StartsWith("Architect Procesada Correctamente: H|", processed.Detail);
        Assert.Equal(TimeSpan.FromMilliseconds(6479), processed.Elapsed);
    }

    [Fact]
    public void Anonymization_replaces_personal_fields_and_keeps_frames_valid()
    {
        var imported = GlymsLogImporter.Import(SyntheticLog(), "synthetic.log");

        var (entries, report) = new TraceAnonymizer().Anonymize(imported.Entries);

        Assert.True(report.IsClean);
        var text = string.Concat(entries.Select(e => Encoding.Latin1.GetString(e.Data.Span) + e.Detail));
        Assert.DoesNotContain("Doe", text);
        Assert.DoesNotContain("John", text);
        Assert.DoesNotContain("12345678", text);
        Assert.DoesNotContain("D100200PH", text);
        Assert.Contains("MULTI Level 1", text);
        Assert.Contains("2496.16", text);
        Assert.Contains("|20241212122952|", text);

        foreach (var entry in entries.Where(e => !e.Data.IsEmpty && e.Data.Span[0] == ControlCharacters.Stx))
            Assert.True(Lis01Frame.Parse(entry.Data[1..]).IsValid);

        var patient = Encoding.Latin1.GetString(entries[2].Data.Span);
        Assert.Contains("|PID00001|PID00001|SYN^NAME00001|", patient);
        Assert.Contains("|PID00001|PID00001|SYN^NAME00001|", entries[^1].Detail);
        Assert.Equal(1, report.PseudonymsByKind["patient-id"]);
    }

    [Fact]
    public void Unparseable_event_text_is_redacted()
    {
        var entry = new TraceEntry(0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Event, ReadOnlyMemory<byte>.Empty, "GLYMS-INFO", "Error de conexion: paciente Juan Perez");

        var (entries, _) = new TraceAnonymizer().Anonymize([entry]);

        Assert.Equal("Error de conexion: [redacted 19 chars]", entries[0].Detail);
    }
}
