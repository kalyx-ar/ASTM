using System.Text.Json;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Channels;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;
using Atsm.Runtime.Sessions;
using Atsm.Runtime.Tracing;
using Atsm.Testing;
using Atsm.TraceTools;

const string Usage = """
    atsm-trace import-glyms <log> <out.atsmtrace> [--instrument <name>]
    atsm-trace anonymize <in.atsmtrace> <out.atsmtrace> [--keep-comments] [--date-shift <days>]
    atsm-trace summary <trace.atsmtrace>
    atsm-trace dump <trace.atsmtrace>
    atsm-trace interpret architect <file.atsmtrace | message.txt>...

    Traces may contain patient data until anonymized: keep them out of version control.
    """;

if (args.Length < 2)
{
    Console.Error.WriteLine(Usage);
    return 64;
}

try
{
    return args[0] switch
    {
        "import-glyms" when args.Length >= 3 => ImportGlyms(args[1], args[2], Option(args, "--instrument")),
        "anonymize" when args.Length >= 3 => Anonymize(args[1], args[2], args.Contains("--keep-comments"), int.TryParse(Option(args, "--date-shift"), out var shift) ? shift : null),
        "summary" => Summary(args[1]),
        "dump" => Dump(args[1]),
        "interpret" when args.Length >= 3 && args[1] == "architect" => await InterpretAsync(args[2..].Where(a => !a.StartsWith("--", StringComparison.Ordinal))),
        _ => Fail(Usage),
    };
}
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static int ImportGlyms(string log, string output, string? instrument)
{
    var result = GlymsLogImporter.Import(File.ReadAllBytes(log), Path.GetFileName(log), instrument);
    using (var writer = new StreamWriter(output))
        TraceFile.Write(writer, result.Header, result.Entries);
    Console.WriteLine($"{result.Entries.Count} entries written to {output} ({result.Skipped} skipped). Instrument: {result.Header.InstrumentId}");
    Console.WriteLine("The output still contains patient data: run 'anonymize' before sharing it.");
    return 0;
}

static int Anonymize(string input, string output, bool keepComments, int? dateShift)
{
    var (header, entries) = Read(input);
    var options = new AnonymizationOptions { KeepComments = keepComments };
    if (dateShift is { } days)
        options = options with { DateShiftDays = days };
    var (anonymized, report) = new TraceAnonymizer(options).Anonymize(entries);
    var limitations = (header.Limitations ?? []).Append($"Anonymized: personal fields pseudonymized, dates shifted by a constant, checksums recalculated{(keepComments ? ", comments kept" : ", comments replaced")}.").ToList();
    using (var writer = new StreamWriter(output))
        TraceFile.Write(writer, header with { TraceId = Guid.CreateVersion7(), Limitations = limitations, Properties = new Dictionary<string, string> { ["anonymized"] = "true" } }, anonymized);
    Console.WriteLine(JsonSerializer.Serialize(report with { Residual = report.Residual.Take(20).ToList() }, new JsonSerializerOptions { WriteIndented = true }));
    if (!report.IsClean)
    {
        Console.Error.WriteLine("Anonymization is not clean: review the residual findings before sharing.");
        return 2;
    }
    return 0;
}

static int Summary(string input)
{
    var (header, entries) = Read(input);
    Console.WriteLine($"Trace {header.TraceId} from {header.Source} ({header.ImportedFrom}), instrument {header.InstrumentId}");
    Console.WriteLine($"Received bytes: {entries.Where(e => e.Direction == TraceDirection.Received).Sum(e => e.Data.Length)}");
    Console.WriteLine($"Transmitted bytes: {entries.Where(e => e.Direction == TraceDirection.Transmitted).Sum(e => e.Data.Length)}");
    foreach (var group in entries.Where(e => e.Direction == TraceDirection.Event).GroupBy(e => e.Code).OrderByDescending(g => g.Count()))
        Console.WriteLine($"Event {group.Key}: {group.Count()}");
    foreach (var limitation in header.Limitations ?? [])
        Console.WriteLine($"Limitation: {limitation}");
    return 0;
}

static int Dump(string input)
{
    var (_, entries) = Read(input);
    var output = new System.Text.StringBuilder();
    foreach (var entry in entries)
    {
        output.Append(entry.Sequence).Append(' ').Append(entry.Utc.ToString("O")).Append(' ');
        output.Append(entry.Direction switch { TraceDirection.Received => "rx ", TraceDirection.Transmitted => "tx ", _ => "ev " });
        if (entry.Direction == TraceDirection.Event)
            output.Append(entry.Code).Append(' ').Append(entry.Detail?.Replace("\r", "<CR>"));
        foreach (var value in entry.Data.Span)
            output.Append(value switch
            {
                0x02 => "<STX>", 0x03 => "<ETX>", 0x04 => "<EOT>", 0x05 => "<ENQ>", 0x06 => "<ACK>",
                0x0A => "<LF>", 0x0D => "<CR>", 0x15 => "<NAK>", 0x17 => "<ETB>",
                < 0x20 or 0x7F => $"<{value:X2}>",
                _ => ((char)value).ToString(),
            });
        output.Append('\n');
    }
    Console.Out.Write(output);
    return 0;
}

static async Task<int> InterpretAsync(IEnumerable<string> files)
{
    var plugin = new ArchitectPlugin();
    var profile = plugin.Profiles[0];
    var instrument = new InstrumentRef("architect");
    var dialect = plugin.CreateDialect(profile, new DialectContext(instrument, TimeProvider.System));
    var stats = new Dictionary<string, int>();
    foreach (var file in files)
    {
        IEnumerable<(InstrumentObservation? Observation, OrderQuery? Query, string? Diagnostic)> facts;
        if (file.EndsWith(".atsmtrace", StringComparison.OrdinalIgnoreCase))
        {
            var (_, entries) = Read(file);
            var host = new InMemoryLabHost();
            var diagnostics = new List<string>();
            var session = new AstmInstrumentSession(new ReplayChannel("replay", entries), dialect, profile, host, instrument, new MemoryTraceSink(),
                new AstmSessionOptions("atsm-trace") { TransmitHostMessages = false, Diagnostics = d => diagnostics.Add(d.ToString()) });
            await session.RunAsync(CancellationToken.None);
            facts = host.Batches.SelectMany(b => b.Observations).Select(o => ((InstrumentObservation?)o, (OrderQuery?)null, (string?)null))
                .Concat(host.Queries.Select(q => ((InstrumentObservation?)null, (OrderQuery?)q, (string?)null)))
                .Concat(diagnostics.Select(d => ((InstrumentObservation?)null, (OrderQuery?)null, (string?)d)));
        }
        else
        {
            var parsed = Lis02Codec.Parse(await File.ReadAllTextAsync(file, profile.Encoding));
            var list = parsed.Diagnostics.Select(d => ((InstrumentObservation?)null, (OrderQuery?)null, (string?)d.ToString())).ToList();
            foreach (var message in parsed.Messages)
            {
                var interpretation = dialect.Interpret(message, TraceRef.None);
                list.AddRange(interpretation.Observations.Select(o => ((InstrumentObservation?)o, (OrderQuery?)null, (string?)null)));
                list.AddRange(interpretation.Queries.Select(q => ((InstrumentObservation?)null, (OrderQuery?)q, (string?)null)));
                list.AddRange(interpretation.Diagnostics.Select(d => ((InstrumentObservation?)null, (OrderQuery?)null, (string?)d.ToString())));
            }
            facts = list;
        }

        foreach (var (observation, query, diagnostic) in facts)
        {
            // Only non-identifying fields are printed: specimen ids may be personal data.
            if (observation is not null)
            {
                Count(stats, $"observation {observation.Specimen.Role} {observation.ResultKind ?? "-"} {observation.InstrumentStatusMeaning}");
                Count(stats, $"numeric parsed: {observation.Value.Numeric is not null || observation.ResultKind?.StartsWith('I') == true}");
                Count(stats, $"timestamp parsed: {observation.ResultTime is not null}");
            }
            if (query is not null)
                Count(stats, $"query specimens={query.Specimens.Count} scope={query.Scope}");
            if (diagnostic is not null)
                Count(stats, "diagnostic " + diagnostic.Split(':')[0]);
        }
    }
    foreach (var (key, value) in stats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        Console.WriteLine($"{value,8}  {key}");
    return 0;
}

static void Count(Dictionary<string, int> stats, string key) => stats[key] = stats.GetValueOrDefault(key) + 1;

static (TraceHeader Header, List<TraceEntry> Entries) Read(string path)
{
    using var reader = new StreamReader(path);
    return TraceFile.Read(reader);
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int Fail(string usage)
{
    Console.Error.WriteLine(usage);
    return 64;
}
