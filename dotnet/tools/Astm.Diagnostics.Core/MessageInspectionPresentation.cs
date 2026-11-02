using System.Text;

namespace Astm.Diagnostics;

/// <summary>Renders plugin-owned interpretations next to exact field text. No patient data is logged or exported.</summary>
public static class MessageInspectionPresentation
{
    public static string Describe(string original, DiagnosticProfile profile)
    {
        if (profile.InspectMessage is not { } inspect) return "";
        var inspection = inspect(original);
        var text = new StringBuilder(DiagnosticText.Text("Inspection.Title")).AppendLine();
        foreach (var record in inspection.Records)
        {
            text.AppendLine().AppendLine(DiagnosticText.Format("Inspection.Record", record.Index, record.Context));
            if (record.UnassociatedResult) text.AppendLine(DiagnosticText.Text("Inspection.Unassociated"));
            text.AppendLine(DiagnosticText.Text("Inspection.Columns"));
            foreach (var field in record.Fields)
            {
                var meaning = field.Meaning is { } known ? DiagnosticText.Plugin(typeof(MessageInspectionPresentation), known) : DiagnosticText.Text("Inspection.Unknown");
                text.Append(field.Location).Append(" | ").Append(meaning).Append(" | ")
                    .Append(Quote(field.Raw)).Append(" → ").Append(Quote(field.Decoded));
                if (field.DecodeError) text.Append(" · ").Append(DiagnosticText.Text("Inspection.DecodeError"));
                text.AppendLine();
            }
            foreach (var source in record.Fields.Select(f => f.Meaning?.Evidence).Where(e => e is not null).Distinct(StringComparer.Ordinal))
                text.AppendLine(DiagnosticText.Format("Inspection.Evidence", source));
        }
        foreach (var finding in inspection.Diagnostics)
            text.AppendLine(DiagnosticText.Finding(finding, profile) + " · " + finding.Location);
        return text.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
