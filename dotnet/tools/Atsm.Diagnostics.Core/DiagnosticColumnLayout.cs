namespace Atsm.Diagnostics;

/// <summary>Content widths bounded by the viewport. If headers alone cannot fit, horizontal scrolling remains available.</summary>
public static class DiagnosticColumnLayout
{
    public const double Gutter = 72;
    public static double[] Fit(string[] headers, IReadOnlyList<DiagnosticRow> rows, double viewportWidth, int flexibleColumn)
    {
        if (headers.Length == 0) return [];
        var minimum = headers.Select(header => Math.Clamp(header.Length * 6.8 + 24, 52, 168)).ToArray();
        var desired = headers.Select((header, index) => Math.Clamp(rows.Take(1500).Select(row =>
            ProtocolExplainer.SingleLine(row.Cells[index]).Length * 6.8 + 24).Append(minimum[index]).Max(), minimum[index], 900)).ToArray();
        var available = Math.Max(minimum.Sum(), viewportWidth - Gutter);
        var total = desired.Sum();
        if (total > available)
        {
            var room = total - minimum.Sum(); var scale = room == 0 ? 0 : (available - minimum.Sum()) / room;
            for (var i = 0; i < desired.Length; i++) desired[i] = minimum[i] + (desired[i] - minimum[i]) * scale;
        }
        else desired[Math.Clamp(flexibleColumn, 0, headers.Length - 1)] += available - total;
        return desired;
    }
}
