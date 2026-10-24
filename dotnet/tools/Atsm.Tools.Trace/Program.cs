using System.Globalization;
using System.Text;
using System.Text.Json;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugin.Glyms.Testing;
using Atsm.Plugin.Sysmex.Uwam;
using Atsm.Plugins;
using Atsm.Runtime.Tracing;
using Atsm.Tracing;

const string Usage = """
    atsm-trace plugins
    atsm-trace extract <adapter> <log> <out.atsmtrace> [--recorded-by host|instrument] [--filter <text>] [--utc-offset <+hh:mm>]
    atsm-trace from-text <lis02.txt> <out.atsmtrace> [--direction received|transmitted]
    atsm-trace from-binary <capture.bin> <out.atsmtrace> [--direction received|transmitted]
    atsm-trace theoretical <in.atsmtrace> <out.atsmtrace> [--profile <id>] [--baud <n> (0 for TCP)] [--reaction <ms>]
    atsm-trace stack <trace.atsmtrace> [--profile <id>] [--out <stack.jsonl>]
    atsm-trace metrics <trace.atsmtrace> [--profile <id>]
    atsm-trace compare <first.atsmtrace> <second.atsmtrace> [--profile <id>]
    atsm-trace anonymize <in.atsmtrace> <out.atsmtrace> [--keep-comments] [--date-shift <days>]
    atsm-trace summary <trace.atsmtrace>
    atsm-trace dump <trace.atsmtrace>

    Real and theoretical traces are built the same way and analyzed with the same layered stack.
    Traces may contain patient data until anonymized: keep them out of version control.
    """;

var plugins = Catalog.Plugins;
var adapters = Catalog.Adapters;

if (args.Length < 1)
{
    Console.Error.WriteLine(Usage);
    return 64;
}

try
{
    return args[0] switch
    {
        "plugins" => ListPlugins(),
        "extract" when args.Length >= 4 => Extract(args[1], args[2], args[3]),
        "from-text" when args.Length >= 3 => FromText(args[1], args[2]),
        "from-binary" when args.Length >= 3 => FromBinary(args[1], args[2]),
        "theoretical" when args.Length >= 3 => Theoretical(args[1], args[2]),
        "stack" when args.Length >= 2 => Stack(args[1]),
        "metrics" when args.Length >= 2 => Metrics(args[1]),
        "compare" when args.Length >= 3 => Compare(args[1], args[2]),
        "anonymize" when args.Length >= 3 => Anonymize(args[1], args[2]),
        "summary" when args.Length >= 2 => Summary(args[1]),
        "dump" when args.Length >= 2 => Dump(args[1]),
        _ => Fail(),
    };
}
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

int ListPlugins()
{
    foreach (var plugin in plugins)
    {
        var descriptor = plugin.Describe();
        Console.WriteLine($"plugin {descriptor.Id} {descriptor.Version} ({descriptor.Manufacturer}: {string.Join(", ", descriptor.Models)})");
        foreach (var profile in plugin.Profiles)
            Console.WriteLine($"  profile {profile.Id}: host answer budget {profile.QueryAnswerBudget.TotalSeconds} s, establishment {profile.Link.EstablishmentTimeout.TotalSeconds} s");
    }
    foreach (var adapter in adapters)
        Console.WriteLine($"adapter {adapter.Id}: {adapter.Description}");
    return 0;
}

int Extract(string adapterId, string log, string output)
{
    var adapter = adapters.FirstOrDefault(a => a.Id == adapterId) ?? throw new ArgumentException($"Unknown adapter {adapterId}; see 'atsm-trace plugins'.");
    var options = new LogAdapterOptions(Option("--recorded-by") == "instrument" ? RecordedBy.Instrument : RecordedBy.Host)
    {
        Filter = Option("--filter"),
        UtcOffset = Option("--utc-offset") is { } offset ? TimeSpan.Parse(offset.TrimStart('+'), CultureInfo.InvariantCulture) * (offset.StartsWith('-') ? -1 : 1) : TimeSpan.Zero,
    };
    var extraction = adapter.Extract(File.ReadAllBytes(log), options);
    var (header, entries) = TraceReconstruction.Real(extraction.Segments, $"{adapter.Id}:{Path.GetFileName(log)}", extraction.InstrumentHint,
        adapter.Limitations.Append($"Directions interpreted as recorded by the {options.RecordedBy.ToString().ToLowerInvariant()}.").ToList());
    Write(output, header, entries);
    Console.WriteLine($"{entries.Count} entries ({extraction.Segments.Count(s => s.Content == SegmentContent.Wire)} wire, {extraction.Segments.Count(s => s.Content == SegmentContent.Message)} message, {extraction.Segments.Count(s => s.Content == SegmentContent.Event)} event), {extraction.Skipped} skipped.");
    foreach (var finding in extraction.Findings)
        Console.WriteLine($"Finding: {finding}");
    Console.WriteLine("The output may contain patient data: run 'anonymize' before sharing it.");
    return 0;
}

int FromText(string input, string output)
{
    var direction = Direction();
    var segments = TraceReconstruction.Plan(File.ReadAllText(input, Encoding.Latin1), direction)
        .SelectMany(p => p.Records.Select(r => new TraceSegment(null, direction, SegmentContent.Message, Encoding.Latin1.GetBytes(r + "\r"))));
    var (header, entries) = TraceReconstruction.Real(segments, $"text:{Path.GetFileName(input)}", null, ["Plain LIS02 text without timing."]);
    Write(output, header, entries);
    Console.WriteLine($"{entries.Count} records written to {output}.");
    return 0;
}

int FromBinary(string input, string output)
{
    var segment = new TraceSegment(null, Direction(), SegmentContent.Wire, File.ReadAllBytes(input));
    var (header, entries) = TraceReconstruction.Real([segment], $"binary:{Path.GetFileName(input)}", null, ["Single-direction capture without timing."]);
    Write(output, header, entries);
    Console.WriteLine($"{segment.Data.Length} bytes written to {output}.");
    return 0;
}

int Theoretical(string input, string output)
{
    var (header, entries) = Read(input);
    var profile = Profile();
    var stack = new TraceStackBuilder(profile?.Encoding ?? Encoding.Latin1).Build(entries);
    var options = new TheoreticalOptions
    {
        Link = profile?.Link ?? Atsm.Protocol.Lis01.Lis01Options.Default,
        Encoding = profile?.Encoding ?? Encoding.Latin1,
        BaudRate = int.TryParse(Option("--baud"), out var baud) ? baud : 9600,
        ResponseDelay = double.TryParse(Option("--reaction"), CultureInfo.InvariantCulture, out var reaction) ? TimeSpan.FromMilliseconds(reaction) : TimeSpan.FromMilliseconds(5),
    };
    var plans = TraceReconstruction.Plan(stack);
    var (theoreticalHeader, theoretical) = TraceReconstruction.Theoretical(plans, options, header.InstrumentId, $"theoretical:{Path.GetFileName(input)}");
    Write(output, theoreticalHeader with { Profile = profile?.Id, StartedAt = header.StartedAt }, theoretical);
    Console.WriteLine($"{plans.Count} messages replayed into {theoretical.Count} theoretical entries.");
    return 0;
}

int Stack(string input)
{
    var (_, entries) = Read(input);
    var stack = Builder().Build(entries);
    var path = Option("--out");
    using var writer = path is null ? Console.Out : new StreamWriter(path);
    foreach (var stackEvent in stack.Events.Where(e => e.Level != StackLevel.Technical || e.Direction == TraceDirection.Event))
        writer.WriteLine(JsonSerializer.Serialize(stackEvent, TraceJson.Options));
    return 0;
}

int Metrics(string input)
{
    var (_, entries) = Read(input);
    Console.WriteLine(JsonSerializer.Serialize(TraceMetrics.Of(Builder().Build(entries)), new JsonSerializerOptions(TraceJson.Options) { WriteIndented = true }));
    return 0;
}

int Compare(string first, string second)
{
    var builder = Builder();
    var comparison = TraceComparison.Of(builder.Build(Read(first).Entries), builder.Build(Read(second).Entries));
    Console.WriteLine($"Same messages: {comparison.SameMessages} (only in first: {comparison.OnlyInFirst}, only in second: {comparison.OnlyInSecond})");
    Console.WriteLine($"{"event",-36} {"first",8} {"second",8}");
    foreach (var (code, a, b) in comparison.CountDifferences)
        Console.WriteLine($"{code,-36} {a,8} {b,8}");
    Console.WriteLine($"Received message duration p50/p95/max: {Format(comparison.First.ReceivedMessageDuration)} | {Format(comparison.Second.ReceivedMessageDuration)}");
    Console.WriteLine($"Host answer latency p50/p95/max:       {Format(comparison.First.HostAnswerLatency)} | {Format(comparison.Second.HostAnswerLatency)}");
    Console.WriteLine($"Unanswered queries:                    {comparison.First.UnansweredQueries} | {comparison.Second.UnansweredQueries}");
    return 0;
}

int Anonymize(string input, string output)
{
    var (header, entries) = Read(input);
    var keepComments = args.Contains("--keep-comments");
    var options = new AnonymizationOptions { KeepComments = keepComments };
    if (int.TryParse(Option("--date-shift"), out var days))
        options = options with { DateShiftDays = days };
    var (anonymized, report) = new TraceAnonymizer(options).Anonymize(entries);
    var limitations = (header.Limitations ?? []).Append($"Anonymized: personal fields pseudonymized, dates shifted by a constant, checksums recalculated{(keepComments ? ", comments kept" : ", comments replaced")}.").ToList();
    Write(output, header with { TraceId = Guid.CreateVersion7(), Limitations = limitations, Properties = new Dictionary<string, string> { ["anonymized"] = "true" } }, anonymized);
    Console.WriteLine(JsonSerializer.Serialize(report with { Residual = report.Residual.Take(20).ToList() }, new JsonSerializerOptions { WriteIndented = true }));
    if (report.IsClean)
        return 0;
    Console.Error.WriteLine("Anonymization is not clean: review the residual findings before sharing.");
    return 2;
}

int Summary(string input)
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

int Dump(string input)
{
    var (_, entries) = Read(input);
    var output = new StringBuilder();
    foreach (var entry in entries)
    {
        output.Append(entry.Sequence).Append(' ').Append(entry.Utc.ToString("O", CultureInfo.InvariantCulture)).Append(' ');
        output.Append(entry.Direction switch { TraceDirection.Received => "rx ", TraceDirection.Transmitted => "tx ", _ => "ev " });
        if (entry.Code is not null)
            output.Append(entry.Code).Append(' ');
        output.Append(entry.Detail?.Replace("\r", "<CR>", StringComparison.Ordinal));
        foreach (var value in entry.Data.Span)
        {
            output.Append(value switch
            {
                0x02 => "<STX>", 0x03 => "<ETX>", 0x04 => "<EOT>", 0x05 => "<ENQ>", 0x06 => "<ACK>",
                0x0A => "<LF>", 0x0D => "<CR>", 0x15 => "<NAK>", 0x17 => "<ETB>",
                < 0x20 or >= 0x7F => $"<{value:X2}>",
                _ => ((char)value).ToString(),
            });
        }
        output.Append('\n');
    }
    Console.Out.Write(output);
    return 0;
}

TraceStackBuilder Builder()
{
    var profile = Profile();
    var plugin = profile is null ? null : plugins.First(p => p.Profiles.Contains(profile));
    var dialect = plugin?.CreateDialect(profile!, new DialectContext(new InstrumentRef("trace"), TimeProvider.System));
    return new TraceStackBuilder(profile?.Encoding ?? Encoding.Latin1, dialect);
}

AstmProfile? Profile()
{
    var id = Option("--profile");
    if (id is null)
        return null;
    return plugins.SelectMany(p => p.Profiles).FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"Unknown profile {id}; see 'atsm-trace plugins'.");
}

TraceDirection Direction() => Option("--direction") == "transmitted" ? TraceDirection.Transmitted : TraceDirection.Received;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string Format(DurationStats stats) =>
    stats.Count == 0 ? "-" : $"{stats.P50.TotalMilliseconds:F0}/{stats.P95.TotalMilliseconds:F0}/{stats.Max.TotalMilliseconds:F0} ms (n={stats.Count})";

static (TraceHeader Header, List<TraceEntry> Entries) Read(string path)
{
    using var reader = new StreamReader(path);
    return TraceFile.Read(reader);
}

static void Write(string path, TraceHeader header, IEnumerable<TraceEntry> entries)
{
    using var writer = new StreamWriter(path);
    TraceFile.Write(writer, header, entries);
}

static int Fail()
{
    Console.Error.WriteLine(Usage);
    return 64;
}

/// <summary>Plugins known to this build. Dynamic discovery from a plugin folder is a later step.</summary>
internal static class Catalog
{
    public static IReadOnlyList<IInstrumentPlugin> Plugins { get; } = [new StandardAstmPlugin(), new ArchitectPlugin(), new UwamPlugin()];

    public static IReadOnlyList<ITraceLogAdapter> Adapters { get; } =
        Plugins.OfType<ITraceLogAdapterProvider>().Append(new GlymsTestingPlugin()).SelectMany(p => p.LogAdapters).ToList();
}
