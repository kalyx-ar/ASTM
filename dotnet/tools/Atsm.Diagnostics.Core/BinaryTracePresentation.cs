using Atsm.Abstractions.Tracing;

namespace Atsm.Diagnostics;

public sealed record BinaryTraceEntry(string Meaning, string Details);

/// <summary>Maps reconstructed fields back to recorder reads. A byte inside a field is data, not an ASCII command.</summary>
public static class BinaryTracePresentation
{
    public static string DescribeMessage(DecodedMessage message, DiagnosticProfile profile) => string.Join("\n",
        message.BinaryFields.Select(f => DiagnosticText.Format("Binary.FieldRange", DiagnosticText.Explanation(profile.TracePlugin!, f.Explanation), f.Offset, f.Offset + f.Length - 1)));

    public static IReadOnlyDictionary<long, BinaryTraceEntry> DescribeEntries(IReadOnlyList<TraceEntry> entries,
        IReadOnlyList<DecodedMessage> messages, DiagnosticProfile profile)
    {
        var result = new Dictionary<long, BinaryTraceEntry>();
        if (profile.TracePlugin is not { } plugin) return result;
        var positions = entries.Select((e, i) => (e.Sequence, i)).ToDictionary(p => p.Sequence, p => p.i);
        var meanings = new Dictionary<long, List<string>>(); var details = new Dictionary<long, List<string>>();
        foreach (var message in messages.Where(m => m.BinaryFields.Count > 0))
        {
            if (!positions.TryGetValue(message.FirstSequence, out var first) || !positions.TryGetValue(message.Sequence, out var last)) continue;
            var offset = -message.FirstByteOffset;
            for (var i = first; i <= last; i++)
            {
                var entry = entries[i]; if (entry.Direction.ToString() != message.Direction) continue;
                foreach (var field in message.BinaryFields.Where(f => f.Offset < (long)offset + entry.Data.Length && (long)f.Offset + f.Length > offset))
                {
                    var meaning = DiagnosticText.Explanation(plugin, field.Explanation);
                    if (!meanings.TryGetValue(entry.Sequence, out var names)) meanings[entry.Sequence] = names = [];
                    if (!details.TryGetValue(entry.Sequence, out var lines)) details[entry.Sequence] = lines = [];
                    names.Add(meaning);
                    lines.Add(DiagnosticText.Format("Binary.FieldRange", meaning, field.Offset, field.Offset + field.Length - 1));
                }
                offset += entry.Data.Length;
            }
        }
        foreach (var (sequence, names) in meanings) result[sequence] = new(string.Join(" · ", names.Distinct(StringComparer.Ordinal)),
            string.Join("\n", details[sequence].Distinct(StringComparer.Ordinal)));
        return result;
    }
}
