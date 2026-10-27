using Astm.Abstractions.Model;
using Astm.Plugins;

namespace Astm.Diagnostics;

public static class QualityStatus
{
    /// <summary>Summarize communicated measurement states, preserving mixed alerts instead of the last state only.</summary>
    public static string Summarize(QualitySeries series, DiagnosticProfile profile, bool interpreted = true)
    {
        var knowledge = ((object?)profile.TracePlugin ?? profile.AstmPlugin) as IInstrumentFlagKnowledge;
        var states = series.Records.Select(o => new
        {
            Text = interpreted ? ProtocolExplainer.Status(o, profile) : string.Join(" ", new[] { o.InstrumentStatus }.Concat(o.Flags.Select(f => f.Code))).Trim(),
            Error = o.InstrumentStatusMeaning == InstrumentStatusMeaning.Warning || o.Flags.Any(f => knowledge?.DescribeFlag(o, f)?.IsError == true),
            Attention = ProtocolExplainer.RequiresAttention(o, knowledge),
        }).GroupBy(s => s.Text, StringComparer.Ordinal).Select(g => new { Text = g.Key, Count = g.Count(), Error = g.Any(s => s.Error), Attention = g.Any(s => s.Attention) })
            .OrderByDescending(g => g.Error).ThenByDescending(g => g.Attention).ThenBy(g => g.Text, StringComparer.CurrentCulture).ToArray();
        if (states.Length == 1) return states[0].Text;
        return string.Join(" · ", states.Select(s => DiagnosticText.Format("Quality.StateCount", s.Text, s.Count)));
    }
}
