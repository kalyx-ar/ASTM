using System.Globalization;
using System.Text;
using Astm.Abstractions.Tracing;
using Astm.Protocol.Lis01;
using Astm.Protocol.Lis02;

namespace Astm.Tracing;

public sealed record AnonymizationOptions
{
    /// <summary>Constant shift applied to every date in the trace, so intervals are preserved.</summary>
    public int DateShiftDays { get; init; } = -397;

    /// <summary>Free-text comments may contain names; they are replaced unless explicitly kept.</summary>
    public bool KeepComments { get; init; }

    public Encoding Encoding { get; init; } = Encoding.Latin1;
}

public sealed record AnonymizationReport(
    int FramesRewritten,
    int FieldsReplaced,
    int DatesShifted,
    int EventsRedacted,
    int UnparsedFrames,
    IReadOnlyDictionary<string, int> PseudonymsByKind,
    IReadOnlyList<string> Residual)
{
    public bool IsClean => Residual.Count == 0 && UnparsedFrames == 0;
}

/// <summary>
/// Codec-aware anonymization of LIS01/LIS02 traffic. Frames are reassembled per direction, records are parsed with
/// the delimiters declared by H, personal fields are replaced by consistent pseudonyms, and frames are rebuilt with
/// recalculated checksums. Event details (which in legacy logs repeat whole messages) are anonymized the same way
/// or redacted when they cannot be parsed. The pseudonym map stays in memory and is never written.
/// </summary>
public sealed class TraceAnonymizer(AnonymizationOptions? options = null)
{
    private readonly AnonymizationOptions _options = options ?? new AnonymizationOptions();
    private readonly Dictionary<(string Kind, string Value), string> _pseudonyms = [];
    private readonly Dictionary<string, int> _counters = [];
    private int _fields;
    private int _dates;

    public (List<TraceEntry> Entries, AnonymizationReport Report) Anonymize(IEnumerable<TraceEntry> entries)
    {
        var output = new List<TraceEntry>();
        var assemblers = new Dictionary<TraceDirection, FrameAssembler>
        {
            [TraceDirection.Received] = new(),
            [TraceDirection.Transmitted] = new(),
        };
        var delimiters = new Dictionary<TraceDirection, Lis02Delimiters>
        {
            [TraceDirection.Received] = Lis02Delimiters.Default,
            [TraceDirection.Transmitted] = Lis02Delimiters.Default,
        };
        var frames = 0;
        var unparsed = 0;
        var events = 0;

        foreach (var entry in entries)
        {
            if (entry.Direction == TraceDirection.Event)
            {
                var detail = entry.Detail is null ? null : AnonymizeEventDetail(entry.Detail, ref events);
                output.Add(entry with { Detail = detail, Data = ReadOnlyMemory<byte>.Empty });
                continue;
            }

            if (entry.Code == TraceReconstruction.MessageCode)
            {
                var md = delimiters[entry.Direction];
                var record = AnonymizeRecordText(_options.Encoding.GetString(entry.Data.Span), ref md);
                delimiters[entry.Direction] = md;
                output.Add(entry with { Data = _options.Encoding.GetBytes(record) });
                frames++;
                continue;
            }

            var assembler = assemblers[entry.Direction];
            var rebuilt = new List<byte>();
            foreach (var piece in assembler.Feed(entry.Data.Span))
            {
                if (piece.Frame is null)
                {
                    rebuilt.AddRange(piece.Control!);
                    continue;
                }
                var parsed = Lis01Frame.Parse(piece.Frame, acceptLowercaseChecksum: true, strictText: false);
                if (parsed.Error == Lis01FrameError.Checksum && RecordedChecksum(piece.Frame) is { } recorded)
                {
                    // Keep the evidence: the rewritten frame differs from its computed checksum by the same amount as the original.
                    var delta = (byte)(recorded - Lis01Frame.Checksum(piece.Frame.AsSpan(0, piece.Frame.Length - 4)));
                    var dc = delimiters[entry.Direction];
                    var text2 = AnonymizeRecordText(_options.Encoding.GetString(parsed.Text.Span), ref dc);
                    delimiters[entry.Direction] = dc;
                    var frame = Lis01Frame.Build(parsed.FrameNumber, _options.Encoding.GetBytes(text2), parsed.IsFinal);
                    var adjusted = (byte)(Lis01Frame.Checksum(frame.AsSpan(1, frame.Length - 5)) + delta);
                    frame[^4] = (byte)"0123456789ABCDEF"[adjusted >> 4];
                    frame[^3] = (byte)"0123456789ABCDEF"[adjusted & 0x0F];
                    rebuilt.AddRange(frame);
                    frames++;
                    continue;
                }
                if (!parsed.IsValid)
                {
                    // Cannot be rewritten safely: drop the payload and keep only its size as evidence.
                    unparsed++;
                    output.Add(entry with { Data = ReadOnlyMemory<byte>.Empty, Direction = TraceDirection.Event, Code = "ASTM-ANON-UNPARSED-FRAME", Detail = $"{piece.Frame.Length + 1} bytes removed" });
                    continue;
                }
                var text = _options.Encoding.GetString(parsed.Text.Span);
                var d = delimiters[entry.Direction];
                var anonymized = AnonymizeRecordText(text, ref d);
                delimiters[entry.Direction] = d;
                rebuilt.AddRange(Lis01Frame.Build(parsed.FrameNumber, _options.Encoding.GetBytes(anonymized), parsed.IsFinal));
                frames++;
            }
            if (rebuilt.Count > 0)
                output.Add(entry with { Data = rebuilt.ToArray() });
        }

        for (var i = 0; i < output.Count; i++)
            output[i] = output[i] with { Sequence = i };

        var report = new AnonymizationReport(frames, _fields, _dates, events, unparsed,
            _counters.ToDictionary(), FindResidual(output));
        return (output, report);
    }

    /// <summary>Anonymizes CR-separated LIS02 records. A record split across frames is anonymized per fragment
    /// when the fragment starts with a record type; other fragments are kept only if they hold no letters or digits.</summary>
    public string AnonymizeRecordText(string text, ref Lis02Delimiters delimiters)
    {
        var lines = text.Split('\r');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length < 2 || line[1] != delimiters.Field && !(char.ToUpperInvariant(line[0]) == 'H' && line.Length >= 5))
            {
                if (line.Any(char.IsLetterOrDigit) && i == 0 && line.Length > 0)
                    lines[i] = new string('*', line.Length);
                continue;
            }
            if (char.ToUpperInvariant(line[0]) == 'H' && Lis02Delimiters.TryParseHeader(line, out var declared))
                delimiters = declared;
            lines[i] = AnonymizeRecord(new Lis02Record(line, delimiters));
        }
        return string.Join('\r', lines);
    }

    private string AnonymizeRecord(Lis02Record record)
    {
        var fields = Enumerable.Range(1, record.FieldCount).Select(record.Field).ToArray();
        var d = record.Delimiters;

        void Replace(int field, string kind)
        {
            if (field > fields.Length || fields[field - 1].Length == 0)
                return;
            fields[field - 1] = string.Join(d.Repeat, fields[field - 1].Split(d.Repeat).Select(r =>
                string.Join(d.Component, r.Split(d.Component).Select(c => c.Length == 0 ? c : Pseudonym(kind, c)))));
            _fields++;
        }

        void ReplaceComponent(int field, int component, string kind)
        {
            if (field > fields.Length)
                return;
            var parts = fields[field - 1].Split(d.Component);
            if (component > parts.Length || parts[component - 1].Length == 0)
                return;
            parts[component - 1] = Pseudonym(kind, parts[component - 1]);
            fields[field - 1] = string.Join(d.Component, parts);
            _fields++;
        }

        switch (record.Type)
        {
            case 'P':
                Replace(3, "patient-id");
                Replace(4, "patient-id");
                Replace(5, "patient-id");
                if (fields.Length >= 6 && fields[5].Length > 0)
                {
                    fields[5] = $"SYN{d.Component}{Pseudonym("patient-name", fields[5])}";
                    _fields++;
                }
                Replace(7, "patient-name");
                Replace(11, "address");
                Replace(13, "phone");
                Replace(14, "physician");
                Replace(15, "free-text");
                Replace(16, "free-text");
                Replace(26, "location");
                for (var f = 17; f <= fields.Length; f++)
                {
                    // Insurance, diagnosis and other identifying free text beyond the standard demographic fields.
                    if (f != 26 && fields[f - 1].Any(char.IsLetter))
                        Replace(f, "free-text");
                }
                break;
            case 'O':
                // Quality-control material names (action code Q) are not personal data and keep their meaning.
                if (fields.Length < 12 || fields[11] != "Q")
                {
                    Replace(3, "specimen");
                    ReplaceComponent(4, 1, "specimen");
                }
                Replace(16, "free-text");
                Replace(17, "physician");
                break;
            case 'Q':
                Replace(3, "specimen");
                Replace(4, "specimen");
                break;
            case 'R':
                Replace(11, "operator");
                break;
            case 'C':
                if (!_options.KeepComments)
                    Replace(4, "comment");
                break;
        }

        for (var f = 3; f <= fields.Length; f++)
        {
            if (record.Type == 'R' && f is 4 or 5 or 6)
                continue;
            fields[f - 1] = ShiftDates(fields[f - 1], d);
        }
        return string.Join(d.Field, fields);
    }

    private string ShiftDates(string field, Lis02Delimiters d)
    {
        if (field.Length < 8)
            return field;
        var parts = field.Split(d.Component);
        for (var i = 0; i < parts.Length; i++)
        {
            var value = parts[i];
            if (value.Length is not (8 or 12 or 14) || !value.All(char.IsAsciiDigit))
                continue;
            var format = value.Length switch { 8 => "yyyyMMdd", 12 => "yyyyMMddHHmm", _ => "yyyyMMddHHmmss" };
            if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year < 1880)
                continue;
            parts[i] = date.AddDays(_options.DateShiftDays).ToString(format, CultureInfo.InvariantCulture);
            _dates++;
        }
        return string.Join(d.Component, parts);
    }

    private string? AnonymizeEventDetail(string detail, ref int events)
    {
        var start = detail.IndexOf(": ", StringComparison.Ordinal);
        var label = start < 0 ? detail : detail[..start];
        var body = start < 0 ? string.Empty : detail[(start + 2)..];
        if (body.Length == 0)
            return detail;
        events++;
        if (body.Length > 1 && char.IsAsciiLetterUpper(body[0]) && body[1] == '|')
        {
            var d = Lis02Delimiters.Default;
            return $"{label}: {AnonymizeRecordText(body, ref d)}";
        }
        return $"{label}: [redacted {body.Length} chars]";
    }

    private string Pseudonym(string kind, string value)
    {
        if (_pseudonyms.TryGetValue((kind, value), out var existing))
            return existing;
        _counters[kind] = _counters.GetValueOrDefault(kind) + 1;
        var prefix = kind switch
        {
            "patient-id" => "PID",
            "patient-name" => "NAME",
            "specimen" => "SID",
            "physician" => "DR",
            "operator" => "OP",
            "comment" => "COMMENT",
            "location" => "LOC",
            _ => "X",
        };
        var pseudonym = $"{prefix}{_counters[kind]:D5}";
        _pseudonyms[(kind, value)] = pseudonym;
        return pseudonym;
    }

    private static byte? RecordedChecksum(byte[] frameAfterStx)
    {
        if (frameAfterStx.Length < 6)
            return null;
        static int Nibble(byte b) => b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            _ => -1,
        };
        var high = Nibble(frameAfterStx[^4]);
        var low = Nibble(frameAfterStx[^3]);
        return high < 0 || low < 0 ? null : (byte)(high << 4 | low);
    }

    /// <summary>Re-scans the output for P records whose identifying fields were not replaced.</summary>
    private List<string> FindResidual(List<TraceEntry> entries)
    {
        var residual = new List<string>();
        var known = _pseudonyms.Values.ToHashSet();
        foreach (var direction in (ReadOnlySpan<TraceDirection>)[TraceDirection.Received, TraceDirection.Transmitted])
        {
            var assembler = new FrameAssembler();
            foreach (var entry in entries.Where(e => e.Direction == direction))
            {
                if (entry.Code == TraceReconstruction.MessageCode)
                {
                    foreach (var line in _options.Encoding.GetString(entry.Data.Span).Split('\r'))
                    {
                        if (line.Length < 2 || char.ToUpperInvariant(line[0]) != 'P')
                            continue;
                        var patient = new Lis02Record(line, Lis02Delimiters.Default);
                        foreach (var field in (ReadOnlySpan<int>)[3, 4, 5, 6, 7])
                        {
                            foreach (var component in patient.Components(field))
                            {
                                if (component.Trim().Length > 0 && component != "SYN" && !known.Contains(component))
                                    residual.Add($"entry {entry.Sequence}: P.{field}");
                            }
                        }
                    }
                    continue;
                }
                foreach (var piece in assembler.Feed(entry.Data.Span))
                {
                    if (piece.Frame is null)
                        continue;
                    var parsed = Lis01Frame.Parse(piece.Frame, true, false);
                    if (!parsed.IsValid)
                        continue;
                    var text = _options.Encoding.GetString(parsed.Text.Span);
                    if (text.Length < 2 || char.ToUpperInvariant(text[0]) != 'P')
                        continue;
                    var record = new Lis02Record(text.TrimEnd('\r'), Lis02Delimiters.Default);
                    foreach (var field in (ReadOnlySpan<int>)[3, 4, 5, 6, 7])
                    {
                        foreach (var component in record.Components(field))
                        {
                            if (component.Length > 0 && component != "SYN" && !known.Contains(component))
                                residual.Add($"entry {entry.Sequence}: P.{field}");
                        }
                    }
                }
            }
        }
        return residual;
    }

    /// <summary>Splits a byte stream into LIS01 frames (STX..LF) and standalone control bytes.</summary>
    private sealed class FrameAssembler
    {
        private readonly List<byte> _frame = [];
        private bool _inFrame;

        public IEnumerable<(byte[]? Frame, byte[]? Control)> Feed(ReadOnlySpan<byte> data)
        {
            var pieces = new List<(byte[]?, byte[]?)>();
            var control = new List<byte>();
            foreach (var value in data)
            {
                if (!_inFrame)
                {
                    if (value == ControlCharacters.Stx)
                    {
                        if (control.Count > 0)
                        {
                            pieces.Add((null, control.ToArray()));
                            control.Clear();
                        }
                        _inFrame = true;
                        _frame.Clear();
                    }
                    else
                    {
                        control.Add(value);
                    }
                    continue;
                }
                _frame.Add(value);
                if (value == ControlCharacters.Lf)
                {
                    _inFrame = false;
                    pieces.Add((_frame.ToArray(), null));
                }
            }
            if (control.Count > 0)
                pieces.Add((null, control.ToArray()));
            return pieces;
        }
    }
}
