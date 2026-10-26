using Atsm.Diagnostics;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugins;

namespace Atsm.Tools.Trace;

internal static class InspectInput
{
    public static int Run(string path, string? selectedProfile)
    {
        try
        {
            if (new FileInfo(path).Length > DiagnosticReader.MaxInputBytes) throw new InvalidDataException("ATSM-DIAG-INPUT-LIMIT");
            var catalog = new DiagnosticCatalog(); var selected = catalog.Resolve(selectedProfile ?? CfxPlugin.CsvProfileId);
            var bytes = File.ReadAllBytes(path); var detected = DiagnosticInputDetector.Detect(bytes, selected, catalog);
            if (!detected.Recognized || detected.Format == DiagnosticInputFormat.ProtectedCase) { Console.WriteLine("ATSM_INPUT_REQUIRES_EXPLICIT_FORMAT_OR_PROTECTED_READER"); return 2; }
            var input = DiagnosticReader.Read(bytes, DiagnosticInputFormat.Auto, selected, "local diagnostic input", TimeSpan.Zero);
            var profile = catalog.Resolve(input.Header.Profile ?? selected.Id);
            var analysis = DiagnosticReader.Analyze(input, profile, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeZoneInfo.Utc));
            // Aggregates only: safe for checking private local corpora without printing patient fields or file names.
            var recorder = input.Header.Properties?.GetValueOrDefault("directionUncertain") == "true" ? "Unknown" : input.Header.Properties?.GetValueOrDefault("recordedBy") ?? "none";
            Console.WriteLine($"ATSM_INPUT format={detected.Format} profile={profile.Id} recorder={recorder} entries={analysis.Entries.Count} messages={analysis.Messages.Count} observations={analysis.Observations.Count} findings={analysis.Diagnostics.Count} findingGroups={analysis.Diagnostics.Select(d => d.Code).Distinct().Count()} unsupported={detected.UnsupportedProtocol}");
            var knowledge = (object?)profile.TracePlugin ?? profile.AstmPlugin ?? (object?)profile.ContentPlugin;
            var quality = QualitySeriesBuilder.Build(analysis.Observations, knowledge as IInstrumentValueKnowledge);
            Console.WriteLine($"ATSM_QUALITY groups={quality.Count} records={quality.Sum(s => s.MeasurementCount)} flagged={quality.Sum(s => s.Flagged)} missingMaterial={quality.Count(s => string.IsNullOrWhiteSpace(s.Material))} observationAlerts={analysis.Observations.Count(o => ProtocolExplainer.RequiresAttention(o, knowledge as IInstrumentFlagKnowledge))}");
            foreach (var group in analysis.Diagnostics.GroupBy(d => d.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
                Console.WriteLine($"ATSM_FINDING code={group.Key} count={group.Count()}");
            static string Ms(TimeSpan? duration) => duration?.TotalMilliseconds.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) ?? "none";
            foreach (var statistic in CommunicationStatistics.Build(analysis, CommunicationStatistics.HasCaptureClock(input)))
            {
                var distribution = statistic.Distribution;
                Console.WriteLine($"ATSM_TIMING code={statistic.Code} direction={statistic.Direction} count={distribution.Count} minMs={Ms(distribution.Minimum)} medianMs={Ms(distribution.Median)} p95Ms={Ms(distribution.P95)} maxMs={Ms(distribution.Maximum)} pending={statistic.Pending} invalidIntervals={statistic.InvalidIntervals} clockAvailable={statistic.ClockAvailable} reads={statistic.Reads} bytes={statistic.Bytes}");
            }
            if (profile.TracePlugin is not null)
            {
                foreach (var group in analysis.Messages.GroupBy(m => m.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
                    Console.WriteLine($"ATSM_MESSAGE code={group.Key} count={group.Count()}");
                foreach (var group in analysis.Messages.GroupBy(m => m.Fields.GetValueOrDefault("checksum", "unknown")).OrderBy(g => g.Key, StringComparer.Ordinal))
                    Console.WriteLine($"ATSM_CHECK kind={group.Key} count={group.Count()}");
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        { Console.Error.WriteLine("ATSM_INPUT_FAILED " + error.GetType().Name); return 1; }
    }
}
