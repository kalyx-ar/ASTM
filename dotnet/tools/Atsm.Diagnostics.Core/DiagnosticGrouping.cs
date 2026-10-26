using System.Globalization;

namespace Atsm.Diagnostics;

public sealed record DiagnosticRow(string[] Cells, string Details, string GroupKey)
{
    public bool Attention { get; init; }
}
public sealed record DiagnosticRowGroup(string Key, IReadOnlyList<DiagnosticRow> Children)
{
    public int Count => Children.Count;
    public int? Repetitions { get; init; }
    public DiagnosticRow? SummaryRow { get; init; }
    public DiagnosticRow Summary(Func<int, string> multiple)
    {
        if (SummaryRow is { } summary) return summary;
        var cells = Enumerable.Range(0, Children[0].Cells.Length).Select(column =>
        {
            var values = Children.Select(row => row.Cells[column]).Distinct(StringComparer.Ordinal).ToArray();
            return values.Length == 1 ? values[0] : multiple(values.Length);
        }).ToArray();
        return new(cells, DiagnosticText.Format("Group.Details", Count), Key);
    }
}

/// <summary>Grouping is only a view. Every row and its exact detail survive expansion and paging.</summary>
public static class DiagnosticGrouping
{
    public static string Key(params string?[] parts) => string.Concat(parts.Select(p =>
        (p?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + ":" + p));

    public static IReadOnlyList<DiagnosticRowGroup> Build(IEnumerable<DiagnosticRow> rows, bool group, bool consecutive = false)
    {
        if (group && !consecutive) return rows.GroupBy(row => row.GroupKey, StringComparer.Ordinal).Select(g => new DiagnosticRowGroup(g.Key, g.ToArray())).ToArray();
        var result = new List<DiagnosticRowGroup>(); var run = new List<DiagnosticRow>();
        foreach (var row in rows)
        {
            if (run.Count > 0 && (!group || row.GroupKey != run[0].GroupKey))
            { result.Add(new(Key(result.Count.ToString(CultureInfo.InvariantCulture), run[0].GroupKey), run.ToArray())); run.Clear(); }
            run.Add(row);
        }
        if (run.Count > 0) result.Add(new(Key(result.Count.ToString(CultureInfo.InvariantCulture), run[0].GroupKey), run.ToArray()));
        return result;
    }
}
