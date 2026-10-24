using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;

namespace Atsm.Plugin.BioRad.Cfx;

public sealed record CfxCsvOptions(char Separator = ',', string Culture = "", string WellColumn = "Well",
    string SampleColumn = "Sample", string TargetColumn = "Target", string CqColumn = "Cq");

/// <summary>Imports already analyzed exports. It never recalculates Cq or assigns clinical positivity.</summary>
public sealed class CfxPlugin(CfxCsvOptions? csvOptions = null) : IInstrumentContentPlugin
{
    public const string CsvProfileId = "biorad.cfx.maestro.quantification-csv";
    public const string RdmlProfileId = "biorad.cfx.maestro.rdml";
    private const int MaxContentBytes = 16 * 1024 * 1024; // Host import limit, not an instrument framing limit.
    public IReadOnlyList<InstrumentContentProfile> Profiles { get; } =
    [new(CsvProfileId, "1.0.0", "CSV", ["Bio-Rad CFX Maestro 2.3, 10000126764B, pp. 189-194 and Table 19: exported quantification results; header names are configurable."]),
     new(RdmlProfileId, "1.0.0", "RDML", ["Bio-Rad 10000126764B p.192: RDML v1.0/v1.1 exports; RDML Consortium schemas and compression notes."])];
    public PluginDescriptor Describe() => new("biorad.cfx", "0.1.0", PluginApi.Version, "Bio-Rad",
        ["CFX96/CFX384 Touch", "CFX Connect", "CFX Opus 96/384", "CFX Opus Deepwell", "CFX Duet (via Maestro exports)"],
        ["CFX Maestro 2.3 software compatibility list, 10000126764B p.25. Export format support only; no direct instrument control or hardware qualification."]);

    public ContentInterpretation Interpret(InstrumentContentProfile profile, ReadOnlyMemory<byte> content, InstrumentRef instrument, TraceRef trace)
    {
        var findings = new List<AtsmDiagnostic>();
        var values = new List<InstrumentObservation>();
        try
        {
            if (content.IsEmpty || content.Length > MaxContentBytes) throw new InvalidDataException("ATSM-CFX-CONTENT-LIMIT");
            if (profile.Id == CsvProfileId) ReadCsv(new UTF8Encoding(false, true).GetString(content.Span).TrimStart('\uFEFF'), instrument, trace, values, findings);
            else if (profile.Id == RdmlProfileId) ReadRdml(content, instrument, trace, values, findings);
            else throw new InvalidDataException("ATSM-CFX-UNKNOWN-PROFILE");
        }
        catch (Exception error) when (error is InvalidDataException or XmlException or DecoderFallbackException or FormatException or ArgumentException)
        {
            values.Clear();
            findings.Add(new("ATSM-CFX-INVALID-EXPORT", AtsmSeverity.Error, "Unsupported or invalid CFX export; use the declared CSV columns or RDML 1.0/1.1."));
        }
        return new(values, findings);
    }

    private void ReadCsv(string text, InstrumentRef instrument, TraceRef trace, List<InstrumentObservation> values, List<AtsmDiagnostic> findings)
    {
        var options = csvOptions ?? new();
        var rows = CsvRows(text, options.Separator);
        if (rows.Count < 2) throw new InvalidDataException("ATSM-CFX-MISSING-ROWS");
        var names = rows[0].Select((name, index) => (Name: name.Trim(), Index: index)).ToArray();
        if (names.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new InvalidDataException("ATSM-CFX-DUPLICATE-COLUMN");
        int Column(string name) => names.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase), (Name: "", Index: -1)).Index;
        var well = Column(options.WellColumn); var sample = Column(options.SampleColumn); var target = Column(options.TargetColumn); var cq = Column(options.CqColumn);
        if (well < 0 || sample < 0 || target < 0 || cq < 0) throw new InvalidDataException("ATSM-CFX-MISSING-COLUMN");
        var culture = CultureInfo.GetCultureInfo(options.Culture);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i]; if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (row.Length != names.Length) throw new InvalidDataException("ATSM-CFX-COLUMN-COUNT");
            Add(row[sample], row[well], row[target], row[cq], culture, instrument, trace, values, findings,
                new() { ["row"] = i.ToString(CultureInfo.InvariantCulture), ["format"] = "CFX quantification CSV" });
        }
    }

    private static void ReadRdml(ReadOnlyMemory<byte> content, InstrumentRef instrument, TraceRef trace, List<InstrumentObservation> values, List<AtsmDiagnostic> findings)
    {
        XDocument document;
        using var source = new MemoryStream(content.ToArray(), writable: false);
        if (content.Span.StartsWith("PK"u8))
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            if (archive.Entries.Count > 64) throw new InvalidDataException("ATSM-CFX-ARCHIVE-LIMIT");
            var entries = archive.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
            var canonical = entries.Where(e => e.FullName == "rdml_data.xml").ToArray();
            var candidates = canonical.Length > 0 ? canonical : entries;
            if (candidates.Length != 1 || candidates[0].Length > MaxContentBytes) throw new InvalidDataException("ATSM-CFX-AMBIGUOUS-ARCHIVE");
            using var data = candidates[0].Open(); document = LoadXml(data);
        }
        else document = LoadXml(source);
        var root = document.Root ?? throw new InvalidDataException("ATSM-CFX-MISSING-ROOT");
        if (root.Name != XName.Get("rdml", "http://www.rdml.org") || (string?)root.Attribute("version") is not ("1.0" or "1.1"))
            throw new InvalidDataException("ATSM-CFX-UNSUPPORTED-RDML");
        var ns = root.Name.Namespace;
        var samples = root.Elements(ns + "sample").Select(e => (string?)e.Attribute("id") ?? "").ToArray();
        var targets = root.Elements(ns + "target").Select(e => (string?)e.Attribute("id") ?? "").ToArray();
        if (samples.Distinct().Count() != samples.Length || targets.Distinct().Count() != targets.Length) throw new InvalidDataException("ATSM-CFX-DUPLICATE-ID");
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
                throw new InvalidDataException("ATSM-CFX-INVALID-REFERENCE");
            Add(sample, (string?)reaction.Attribute("id") ?? "", target, cq, CultureInfo.InvariantCulture, instrument, trace, values, findings,
                new() { ["experiment"] = (string?)experiment.Attribute("id") ?? "", ["run"] = (string?)run.Attribute("id") ?? "",
                    ["format"] = "RDML " + (string?)root.Attribute("version"), ["exclusion"] = (string?)data.Element(ns + "excl") ?? "",
                    ["excluded"] = data.Element(ns + "excl") is null ? "false" : "true" });
        }
        if (values.Count == 0) throw new InvalidDataException("ATSM-CFX-NO-REACTIONS");
    }

    private static XDocument LoadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxContentBytes });
        return XDocument.Load(reader);
    }

    private static void Add(string sample, string well, string target, string raw, CultureInfo culture, InstrumentRef instrument, TraceRef trace,
        List<InstrumentObservation> values, List<AtsmDiagnostic> findings, Dictionary<string, string> identity)
    {
        if (well.Length == 0 || target.Length == 0) throw new InvalidDataException("ATSM-CFX-MISSING-IDENTIFIER");
        if (sample.Length == 0) findings.Add(new("ATSM-CFX-MISSING-SAMPLE", AtsmSeverity.Warning, "A well has no sample identifier; do not infer a clinical specimen from its position."));
        var numeric = decimal.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, culture, out var number) ? (decimal?)number : null;
        var flags = new List<CodedValue>();
        if (identity["format"].StartsWith("RDML", StringComparison.Ordinal) && numeric < 0) { numeric = null; flags.Add(new("cq.unavailable", "RDML negative sentinel", "urn:rdml:quality")); }
        if (identity.GetValueOrDefault("excluded") == "true") flags.Add(new("excluded", identity.GetValueOrDefault("exclusion"), "urn:rdml:quality"));
        if (numeric is null) findings.Add(new("ATSM-CFX-NONNUMERIC-CQ", AtsmSeverity.Information, "A Cq value is absent or nonnumeric; its original text is preserved."));
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
                if (row.Count >= 256 || rows.Count >= 100000) throw new InvalidDataException("ATSM-CFX-CSV-LIMIT");
                row.Add(cell.ToString()); cell.Clear(); closed = false;
                if (c != separator) { rows.Add(row.ToArray()); row.Clear(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            }
            else { if (closed || c == '"') throw new InvalidDataException("ATSM-CFX-INVALID-QUOTE"); cell.Append(c); }
        }
        if (quoted) throw new InvalidDataException("ATSM-CFX-UNCLOSED-QUOTE");
        if (cell.Length > 0 || closed || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
