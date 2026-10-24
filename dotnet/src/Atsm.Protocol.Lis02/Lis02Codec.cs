using System.Text;
using Atsm.Abstractions.Diagnostics;

namespace Atsm.Protocol.Lis02;

/// <summary>Records from H to L. <see cref="Header"/> is null for records received outside a header.</summary>
public sealed class Lis02Message(Lis02Delimiters delimiters, IReadOnlyList<Lis02Record> records)
{
    public Lis02Delimiters Delimiters { get; } = delimiters;

    public IReadOnlyList<Lis02Record> Records { get; } = records;

    public Lis02Record? Header => Records.Count > 0 && Records[0].Type == 'H' ? Records[0] : null;

    public bool IsTerminated => Records.Count > 0 && Records[^1].Type == 'L';

    public IEnumerable<Lis02Record> OfType(char type) => Records.Where(r => r.Type == type);
}

public sealed record Lis02ParseResult(IReadOnlyList<Lis02Message> Messages, IReadOnlyList<AtsmDiagnostic> Diagnostics);

public static class Lis02Codes
{
    public const string InvalidDelimiters = "ATSM-L02-INVALID-DELIMITERS";
    public const string RecordOutsideMessage = "ATSM-L02-RECORD-OUTSIDE-MESSAGE";
    public const string UnexpectedRecordOrder = "ATSM-L02-UNEXPECTED-RECORD-ORDER";
    public const string MissingTerminator = "ATSM-L02-MISSING-TERMINATOR";
}

public static class Lis02Codec
{
    /// <summary>
    /// Splits text into messages. Records end with CR; an LF next to a CR is tolerated.
    /// Order problems are reported as diagnostics and never drop records: the dialect decides what to do.
    /// </summary>
    public static Lis02ParseResult Parse(string text, Lis02Delimiters? fallback = null)
    {
        var messages = new List<Lis02Message>();
        var diagnostics = new List<AtsmDiagnostic>();
        var delimiters = fallback ?? Lis02Delimiters.Default;
        List<Lis02Record>? current = null;
        char previous = '\0';
        var index = 0;

        foreach (var rawLine in text.Split('\r'))
        {
            var line = rawLine.Trim('\n');
            if (line.Length == 0)
                continue;
            index++;
            var type = char.ToUpperInvariant(line[0]);

            if (type == 'H')
            {
                if (current is not null)
                {
                    diagnostics.Add(new(Lis02Codes.MissingTerminator, AtsmSeverity.Warning, "Header received before the terminator of the previous message.", $"record {index}"));
                    messages.Add(new Lis02Message(delimiters, current));
                }
                if (!Lis02Delimiters.TryParseHeader(line, out var declared))
                    diagnostics.Add(new(Lis02Codes.InvalidDelimiters, AtsmSeverity.Error, "Header does not declare four distinct delimiters; using the previous ones.", $"record {index}"));
                else
                    delimiters = declared;
                current = [new Lis02Record(line, delimiters)];
                previous = 'H';
                continue;
            }

            if (current is null)
            {
                diagnostics.Add(new(Lis02Codes.RecordOutsideMessage, AtsmSeverity.Warning, $"Record {type} outside a message.", $"record {index}"));
                current = [];
                previous = '\0';
            }

            if (!IsExpected(previous, type))
                diagnostics.Add(new(Lis02Codes.UnexpectedRecordOrder, AtsmSeverity.Warning, $"Record {type} after {(previous == '\0' ? "start" : previous)}.", $"record {index}"));
            current.Add(new Lis02Record(line, delimiters));
            if (type != 'C' && type != 'M')
                previous = type;

            if (type == 'L')
            {
                messages.Add(new Lis02Message(delimiters, current));
                current = null;
                previous = '\0';
            }
        }

        if (current is not null)
        {
            diagnostics.Add(new(Lis02Codes.MissingTerminator, AtsmSeverity.Warning, "Message without terminator record."));
            messages.Add(new Lis02Message(delimiters, current));
        }
        return new Lis02ParseResult(messages, diagnostics);
    }

    public static Lis02ParseResult Parse(ReadOnlySpan<byte> bytes, Encoding encoding, Lis02Delimiters? fallback = null) =>
        Parse(encoding.GetString(bytes), fallback);

    /// <summary>One CR-terminated byte array per record, ready to be framed by the low-level protocol.</summary>
    public static List<ReadOnlyMemory<byte>> ToRecordBytes(IEnumerable<Lis02Record> records, Encoding encoding) =>
        records.Select(r => (ReadOnlyMemory<byte>)encoding.GetBytes(r.Raw + "\r")).ToList();

    public static string Serialize(IEnumerable<Lis02Record> records) => string.Concat(records.Select(r => r.Raw + "\r"));

    // Hierarchy of LIS02-A2: H > P > O > R, with Q under H; C and M may follow any record.
    private static bool IsExpected(char previous, char type) => type switch
    {
        'P' => previous is 'H' or 'P' or 'O' or 'R',
        'O' => previous is 'P' or 'O' or 'R',
        'R' => previous is 'O' or 'R',
        'Q' => previous is 'H' or 'Q',
        'S' => previous is 'H' or 'S',
        'C' or 'M' or 'L' => previous != '\0',
        _ => true,
    };
}
