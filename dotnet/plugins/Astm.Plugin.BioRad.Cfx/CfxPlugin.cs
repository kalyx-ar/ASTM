using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;
using Astm.Plugins;

namespace Astm.Plugin.BioRad.Cfx;

public sealed record CfxCsvOptions(char Separator = ',', string Culture = "", string WellColumn = "Well",
    string SampleColumn = "Sample", string TargetColumn = "Target", string CqColumn = "Cq");

/// <summary>Imports already analyzed exports. It never recalculates Cq or assigns clinical positivity.</summary>
public sealed class CfxPlugin(CfxCsvOptions? csvOptions = null) : IInstrumentContentPlugin
{
    public const string CsvProfileId = "biorad.cfx.maestro.quantification-csv";
    public const string RdmlProfileId = "biorad.cfx.maestro.rdml";
    public const string CurveProfileId = "biorad.cfx.maestro.standard-curve-csv";
    private const int MaxContentBytes = 16 * 1024 * 1024; // Host import limit, not an instrument framing limit.
    public IReadOnlyList<InstrumentContentProfile> Profiles { get; } =
    [new(CsvProfileId, "1.1.0", "CSV", ["Bio-Rad CFX Maestro 2.3, 10000126764B, pp. 189-194 and Table 19: quantification, Content, replicate Cq/SQ statistics; header names are configurable."]),
     new(RdmlProfileId, "1.1.0", "RDML", ["Bio-Rad 10000126764B p.192: RDML v1.0/v1.1 exports; sample/type preserves standards/controls, no proprietary recalculation."]),
     new(CurveProfileId, "1.0.0", "CSV curva estándar", ["Bio-Rad CFX Maestro 2.3 10000126764B p.204 Table 20: exported Standard Curve Results; instrument-software statistics, no recomputation."])];
    public PluginDescriptor Describe() => new("biorad.cfx", "0.1.0", PluginApi.Version, "Bio-Rad",
        ["CFX96/CFX384 Touch", "CFX Connect", "CFX Opus 96/384", "CFX Opus Deepwell", "CFX Duet (via Maestro exports)"],
        ["CFX Maestro 2.3 software compatibility list, 10000126764B p.25. Export format support only; no direct instrument control or hardware qualification."]);

    public ContentInterpretation Interpret(InstrumentContentProfile profile, ReadOnlyMemory<byte> content, InstrumentRef instrument, TraceRef trace)
    {
        var findings = new List<AstmDiagnostic>();
        var values = new List<InstrumentObservation>();
        try
        {
            if (content.IsEmpty || content.Length > MaxContentBytes) throw new InvalidDataException("ASTM-CFX-CONTENT-LIMIT");
            if (profile.Id == CsvProfileId) ReadCsv(new UTF8Encoding(false, true).GetString(content.Span).TrimStart('\uFEFF'), instrument, trace, values, findings);
            else if (profile.Id == CurveProfileId) ReadCurve(new UTF8Encoding(false, true).GetString(content.Span).TrimStart('\uFEFF'), instrument, trace, values, findings);
            else if (profile.Id == RdmlProfileId) ReadRdml(content, instrument, trace, values, findings);
            else throw new InvalidDataException("ASTM-CFX-UNKNOWN-PROFILE");
        }
        catch (Exception error) when (error is InvalidDataException or XmlException or DecoderFallbackException or FormatException or ArgumentException)
        {
            values.Clear();
            findings.Add(new("ASTM-CFX-INVALID-EXPORT", AstmSeverity.Error, "Unsupported or invalid CFX export; use the declared CSV columns or RDML 1.0/1.1."));
        }
        return new(values, findings);
    }

    private void ReadCsv(string text, InstrumentRef instrument, TraceRef trace, List<InstrumentObservation> values, List<AstmDiagnostic> findings)
    {
        var options = csvOptions ?? new();
        var rows = CsvRows(text, options.Separator);
        if (rows.Count < 2) throw new InvalidDataException("ASTM-CFX-MISSING-ROWS");
        var names = rows[0].Select((name, index) => (Name: name.Trim(), Index: index)).ToArray();
        if (names.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new InvalidDataException("ASTM-CFX-DUPLICATE-COLUMN");
        int Column(string name) => names.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase), (Name: "", Index: -1)).Index;
        var well = Column(options.WellColumn); var sample = Column(options.SampleColumn); var target = Column(options.TargetColumn); var cq = Column(options.CqColumn);
        if (well < 0 || sample < 0 || target < 0 || cq < 0) throw new InvalidDataException("ASTM-CFX-MISSING-COLUMN");
        var culture = CultureInfo.GetCultureInfo(options.Culture);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i]; if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (row.Length != names.Length) throw new InvalidDataException("ASTM-CFX-COLUMN-COUNT");
            Add(row[sample], row[well], row[target], row[cq], culture, instrument, trace, values, findings,
                new() { ["row"] = i.ToString(CultureInfo.InvariantCulture), ["format"] = "CFX quantification CSV" });
            string Field(string name) => Column(name) is var column && column >= 0 ? row[column] : "";
            var content = Field("Content");
            var sampleType = Regex.Match(content.Trim(), @"^(?<type>Std|Standard|NTC|NRT|Pos|Neg|Positive Control|Negative Control)(?:-\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var material = Material(sampleType.Groups["type"].Value, row[sample]);
            var sourceFields = new Dictionary<string, string> { ["Content"] = content, ["Fluor"] = Field("Fluor"), ["Biological Set Name"] = Field("Biological Set Name") };
            var statistics = new List<ReportedStatistic>();
            foreach (var (name, kind, scope) in new[] {
                ("Cq Mean", ReportedStatisticKind.CqMean, StatisticScope.ReplicateGroup),
                ("Cq Std. Dev", ReportedStatisticKind.CqStandardDeviation, StatisticScope.ReplicateGroup),
                ("Starting Quantity (SQ)", ReportedStatisticKind.StartingQuantity, StatisticScope.Well),
                ("Log Starting Quantity", ReportedStatisticKind.LogStartingQuantity, StatisticScope.Well),
                ("SQ Mean", ReportedStatisticKind.StartingQuantityMean, StatisticScope.ReplicateGroup),
                ("SQ Std. Dev", ReportedStatisticKind.StartingQuantityStandardDeviation, StatisticScope.ReplicateGroup) })
            {
                if (Column(name) < 0) continue;
                var raw = Field(name); sourceFields[name] = raw;
                // The export does not identify replicate membership completely: do not fabricate a group ID.
                var metric = MetricValue(raw, culture);
                if (metric.Numeric is null) findings.Add(new("ASTM-CFX-NONNUMERIC-STATISTIC", AstmSeverity.Warning, "Reported statistic is absent or nonnumeric; original text retained.", name));
                statistics.Add(new(kind, metric, scope, "CSV:" + name));
            }
            values[^1] = values[^1] with { Specimen = values[^1].Specimen with { Role = ControlRole(material) },
                Quality = material is null && statistics.Count == 0 ? null : new(material)
                { Statistics = statistics, SourceFields = sourceFields } };
        }
    }

    private void ReadCurve(string text, InstrumentRef instrument, TraceRef trace, List<InstrumentObservation> values, List<AstmDiagnostic> findings)
    {
        var options = csvOptions ?? new(); var rows = CsvRows(text, options.Separator);
        if (rows.Count < 2) throw new InvalidDataException("ASTM-CFX-MISSING-ROWS");
        var headers = rows[0].Select(h => h.Trim()).ToArray();
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length) throw new InvalidDataException("ASTM-CFX-DUPLICATE-COLUMN");
        int Column(string name) => Array.FindIndex(headers, h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        var target = Column(options.TargetColumn);
        if (target < 0) target = Column("Fluor");
        if (target < 0) target = Column("Fluor (or Target)");
        if (target < 0) throw new InvalidDataException("ASTM-CFX-MISSING-TARGET");
        var metrics = new[] { ("Efficiency %", ReportedStatisticKind.EfficiencyPercent), ("Slope", ReportedStatisticKind.Slope),
            ("Y-intercept", ReportedStatisticKind.YIntercept), ("R^2", ReportedStatisticKind.RSquared) };
        if (metrics.Any(m => Column(m.Item1) < 0)) throw new InvalidDataException("ASTM-CFX-MISSING-CURVE-METRIC");
        var culture = CultureInfo.GetCultureInfo(options.Culture);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i]; if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (row.Length != headers.Length || string.IsNullOrWhiteSpace(row[target])) throw new InvalidDataException("ASTM-CFX-INVALID-CURVE");
            var statistics = metrics.Select(m => new ReportedStatistic(m.Item2, MetricValue(row[Column(m.Item1)], culture),
                StatisticScope.StandardCurve, "CSV:" + m.Item1, $"row:{i}")).ToArray();
            findings.AddRange(statistics.Where(s => s.Value.Numeric is null).Select(s => new AstmDiagnostic("ASTM-CFX-NONNUMERIC-STATISTIC", AstmSeverity.Warning,
                "Reported curve statistic is absent or nonnumeric; original text retained.", s.SourceField)));
            values.Add(new(Guid.CreateVersion7(), instrument, new(""), CodeSet.Of(new(row[target], row[target], CodeSystems.Vendor("biorad.cfx"))),
                new(""), null, [], "exported", InstrumentStatusMeaning.Unknown, "standard-curve", null, null, null, [], [],
                new(new Dictionary<string, string> { ["row"] = i.ToString(CultureInfo.InvariantCulture), ["format"] = "CFX Standard Curve Results" }), trace)
            { Quality = new() { Statistics = statistics, SourceFields = headers.Select((h, column) => (h, row[column])).ToDictionary(x => x.h, x => x.Item2) } });
        }
        if (values.Count == 0) throw new InvalidDataException("ASTM-CFX-NO-CURVES");
    }

    private static ObservationValue MetricValue(string raw, CultureInfo culture) => new(raw,
        decimal.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, culture, out var number) ? number : null);

    private static QualityMaterial? Material(string type, string id) => type.ToLowerInvariant() switch
    {
        "std" or "standard" => new(QualityMaterialKind.Standard, id, TypeRaw: type),
        "ntc" => new(QualityMaterialKind.NoTemplateControl, id, TypeRaw: type),
        "nrt" => new(QualityMaterialKind.NoReverseTranscriptaseControl, id, TypeRaw: type),
        "pos" or "positive control" => new(QualityMaterialKind.PositiveControl, id, TypeRaw: type),
        "neg" or "negative control" => new(QualityMaterialKind.NegativeControl, id, TypeRaw: type),
        _ => null,
    };

    private static SpecimenRole ControlRole(QualityMaterial? material) => material?.Kind is QualityMaterialKind.NoTemplateControl
        or QualityMaterialKind.NoReverseTranscriptaseControl or QualityMaterialKind.PositiveControl or QualityMaterialKind.NegativeControl ? SpecimenRole.Control : SpecimenRole.Unknown;

    private static void ReadRdml(ReadOnlyMemory<byte> content, InstrumentRef instrument, TraceRef trace, List<InstrumentObservation> values, List<AstmDiagnostic> findings)
    {
        XDocument document;
        using var source = new MemoryStream(content.ToArray(), writable: false);
        if (content.Span.StartsWith("PK"u8))
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            if (archive.Entries.Count > 64) throw new InvalidDataException("ASTM-CFX-ARCHIVE-LIMIT");
            var entries = archive.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
            var canonical = entries.Where(e => e.FullName == "rdml_data.xml").ToArray();
            var candidates = canonical.Length > 0 ? canonical : entries;
            if (candidates.Length != 1 || candidates[0].Length > MaxContentBytes) throw new InvalidDataException("ASTM-CFX-AMBIGUOUS-ARCHIVE");
            using var data = candidates[0].Open(); document = LoadXml(data);
        }
        else document = LoadXml(source);
        var root = document.Root ?? throw new InvalidDataException("ASTM-CFX-MISSING-ROOT");
        if (root.Name != XName.Get("rdml", "http://www.rdml.org") || (string?)root.Attribute("version") is not ("1.0" or "1.1"))
            throw new InvalidDataException("ASTM-CFX-UNSUPPORTED-RDML");
        var ns = root.Name.Namespace;
        var samples = root.Elements(ns + "sample").Select(e => (string?)e.Attribute("id") ?? "").ToArray();
        var targets = root.Elements(ns + "target").Select(e => (string?)e.Attribute("id") ?? "").ToArray();
        if (samples.Distinct().Count() != samples.Length || targets.Distinct().Count() != targets.Length) throw new InvalidDataException("ASTM-CFX-DUPLICATE-ID");
        var identities = new HashSet<(string, string, string, string)>();
        foreach (var experiment in root.Elements(ns + "experiment"))
        foreach (var run in experiment.Elements(ns + "run"))
        foreach (var reaction in run.Elements(ns + "react"))
        foreach (var data in reaction.Elements(ns + "data"))
        {
            var sample = (string?)reaction.Element(ns + "sample")?.Attribute("id") ?? "";
            var target = (string?)data.Element(ns + "tar")?.Attribute("id") ?? "";
            var cq = (string?)data.Element(ns + "cq") ?? "";
            if (!samples.Contains(sample) || !targets.Contains(target) || !identities.Add(((string?)experiment.Attribute("id") ?? "", (string?)run.Attribute("id") ?? "", (string?)reaction.Attribute("id") ?? "", target)))
                throw new InvalidDataException("ASTM-CFX-INVALID-REFERENCE");
            Add(sample, (string?)reaction.Attribute("id") ?? "", target, cq, CultureInfo.InvariantCulture, instrument, trace, values, findings,
                new() { ["experiment"] = (string?)experiment.Attribute("id") ?? "", ["run"] = (string?)run.Attribute("id") ?? "",
                    ["format"] = "RDML " + (string?)root.Attribute("version"), ["exclusion"] = (string?)data.Element(ns + "excl") ?? "",
                    ["excluded"] = data.Element(ns + "excl") is null ? "false" : "true" });
            var sampleNode = root.Elements(ns + "sample").Single(s => (string?)s.Attribute("id") == sample);
            var rawType = (string?)sampleNode.Element(ns + "type") ?? "";
            var material = Material(rawType, sample);
            values[^1] = values[^1] with { Specimen = values[^1].Specimen with { Role = ControlRole(material) },
                Quality = material is null ? null : new(material, RunId: (string?)experiment.Attribute("id") + "/" + (string?)run.Attribute("id"))
                { SourceFields = new Dictionary<string, string> { ["sample/type"] = rawType } } };
        }
        if (values.Count == 0) throw new InvalidDataException("ASTM-CFX-NO-REACTIONS");
    }

    private static XDocument LoadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxContentBytes });
        return XDocument.Load(reader);
    }

    private static void Add(string sample, string well, string target, string raw, CultureInfo culture, InstrumentRef instrument, TraceRef trace,
        List<InstrumentObservation> values, List<AstmDiagnostic> findings, Dictionary<string, string> identity)
    {
        if (well.Length == 0 || target.Length == 0) throw new InvalidDataException("ASTM-CFX-MISSING-IDENTIFIER");
        if (sample.Length == 0) findings.Add(new("ASTM-CFX-MISSING-SAMPLE", AstmSeverity.Warning, "A well has no sample identifier; do not infer a clinical specimen from its position."));
        var numeric = decimal.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, culture, out var number) ? (decimal?)number : null;
        var flags = new List<CodedValue>();
        if (identity["format"].StartsWith("RDML", StringComparison.Ordinal) && numeric < 0) { numeric = null; flags.Add(new("cq.unavailable", "RDML negative sentinel", "urn:rdml:quality")); }
        if (identity.GetValueOrDefault("excluded") == "true") flags.Add(new("excluded", identity.GetValueOrDefault("exclusion"), "urn:rdml:quality"));
        if (numeric is null) findings.Add(new("ASTM-CFX-NONNUMERIC-CQ", AstmSeverity.Information, "A Cq value is absent or nonnumeric; its original text is preserved."));
        identity["well"] = well; identity["target"] = target;
        values.Add(new(Guid.CreateVersion7(), instrument, new(sample, Position: well), CodeSet.Of(new(target, target, CodeSystems.Vendor("biorad.cfx"))),
            new(raw, numeric), null, flags, "exported", InstrumentStatusMeaning.Unknown, "Cq", null, null, null, [], [], new(identity), trace));
    }

    private static List<string[]> CsvRows(string text, char separator)
    {
        if (separator is '\r' or '\n' or '"') throw new ArgumentException("Invalid CSV separator.");
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false; var closed = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { quoted = false; closed = true; }
                else cell.Append(c);
                continue;
            }
            if (c == '"' && cell.Length == 0 && !closed) { quoted = true; continue; }
            if (c == separator || c is '\r' or '\n')
            {
                if (row.Count >= 256 || rows.Count >= 100000) throw new InvalidDataException("ASTM-CFX-CSV-LIMIT");
                row.Add(cell.ToString()); cell.Clear(); closed = false;
                if (c != separator) { rows.Add(row.ToArray()); row.Clear(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            }
            else { if (closed || c == '"') throw new InvalidDataException("ASTM-CFX-INVALID-QUOTE"); cell.Append(c); }
        }
        if (quoted) throw new InvalidDataException("ASTM-CFX-UNCLOSED-QUOTE");
        if (cell.Length > 0 || closed || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
