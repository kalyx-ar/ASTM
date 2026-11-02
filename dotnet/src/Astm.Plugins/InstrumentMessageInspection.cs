using System.Collections.ObjectModel;
using Astm.Abstractions.Diagnostics;
using Astm.Protocol.Hl7v2;
using Astm.Protocol.Lis02;

namespace Astm.Plugins;

/// <summary>Optional, read-only diagnostic interpretation. A plugin selects its own field schema;
/// this does not import patients, validate clinical results or send anything to the instrument.</summary>
public interface IInstrumentMessageInspection
{
    InstrumentMessageSchema MessageSchema(string profileId);
    InstrumentMessageInspection InspectMessage(string profileId, string original) =>
        DelimitedMessageInspector.Inspect(original, MessageSchema(profileId));
}

public enum InstrumentMessageSyntax { Lis02, Hl7 }

public sealed record InstrumentMessageSchema(InstrumentMessageSyntax Syntax, IReadOnlyDictionary<string, ProtocolText> Fields)
{
    public static InstrumentMessageSchema Create<T>(InstrumentMessageSyntax syntax, string evidence, params (string Location, string Meaning)[] fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        var source = typeof(T).Assembly.GetName().Name!;
        var definitions = fields.ToDictionary(f => f.Location,
            f => new ProtocolText("inspection." + f.Location, f.Meaning, evidence) { SourceAssembly = source }, StringComparer.Ordinal);
        if (syntax == InstrumentMessageSyntax.Lis02)
        {
            var commonSource = typeof(InstrumentMessageSchema).Assembly.GetName().Name!;
            void SyntaxField(string path, string text) => definitions.TryAdd(path,
                new("inspection." + path, text, "CLSI LIS02-A2: record type, sequence and header delimiter definition.") { SourceAssembly = commonSource });
            foreach (var type in new[] { "P", "O", "R", "C", "Q", "L" })
            { SyntaxField(type + ".1", "Tipo de registro"); SyntaxField(type + ".2", "Secuencia del registro"); }
            SyntaxField("H.1", "Tipo de registro"); SyntaxField("H.2", "Separadores del mensaje");
        }
        return new(syntax, new ReadOnlyDictionary<string, ProtocolText>(definitions));
    }
}

/// <summary>Location is 1-based, with repetitions in brackets. Raw and Decoded are data, never translations.</summary>
public sealed record InstrumentFieldInspection(string Location, string Raw, string Decoded, ProtocolText? Meaning, bool DecodeError = false);
public sealed record InstrumentRecordInspection(int Index, string Type, string Context, string Raw,
    IReadOnlyList<InstrumentFieldInspection> Fields, bool UnassociatedResult = false);
public sealed record InstrumentMessageInspection(string Original, IReadOnlyList<InstrumentRecordInspection> Records,
    IReadOnlyList<AstmDiagnostic> Diagnostics);

/// <summary>Syntax only: numbering, delimiters, escapes and structural ancestry. Vendor semantics come from the schema.</summary>
public static class DelimitedMessageInspector
{
    public static InstrumentMessageInspection Inspect(string original, InstrumentMessageSchema schema)
    {
        ArgumentNullException.ThrowIfNull(original);
        var records = new List<InstrumentRecordInspection>();
        var findings = new List<AstmDiagnostic>();
        int? patient = null, specimen = null, order = null;
        string patientType = schema.Syntax == InstrumentMessageSyntax.Lis02 ? "P" : "PID";
        string orderType = schema.Syntax == InstrumentMessageSyntax.Lis02 ? "O" : "OBR";
        void Add(string type, string raw, int first, int count, Func<int, string> field,
            char repeat, char component, char? subcomponent, Func<string, string> decode, Func<int, bool> literal)
        {
            var index = records.Count + 1;
            if (type is "H" or "MSH") { patient = specimen = order = null; }
            if (type == patientType) { patient = index; specimen = order = null; }
            if (type == "SPM") { specimen = index; order = null; }
            if (type == "ORC") order = null;
            if (type == orderType) order = index;
            var result = type is "R" or "OBX";
            var ancestry = new List<string>();
            if (patient is { } p && p != index) ancestry.Add(patientType + "@" + p);
            if (specimen is { } s && s != index) ancestry.Add("SPM@" + s);
            if (order is { } o && o != index) ancestry.Add(orderType + "@" + o);
            ancestry.Add(type + "@" + index);
            var fields = new List<InstrumentFieldInspection>();
            void Value(string path, string schemaPath, string value, bool isLiteral = false)
            {
                var error = false; var decoded = value;
                if (!isLiteral)
                try { decoded = decode(value); }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    error = true;
                    findings.Add(new("ASTM-INSPECTION-ESCAPE", AstmSeverity.Warning, "Field escape could not be decoded.", $"record {index}:{path}"));
                }
                fields.Add(new(path, value, decoded, schema.Fields.GetValueOrDefault(schemaPath), error));
            }
            for (var f = first; f <= count; f++)
            {
                var path = type + "." + f; var rawField = field(f);
                Value(path, path, rawField, literal(f));
                if (literal(f)) continue;
                var repeats = rawField.Split(repeat);
                for (var r = 0; r < repeats.Length; r++)
                {
                    var repeatedPath = repeats.Length > 1 ? path + "[" + (r + 1) + "]" : path;
                    if (repeats.Length > 1) Value(repeatedPath, path, repeats[r]);
                    var components = repeats[r].Split(component);
                    if (components.Length == 1 && !(subcomponent is { } sc && repeats[r].Contains(sc))) continue;
                    for (var c = 0; c < components.Length; c++)
                    {
                        var cp = path + "." + (c + 1); var location = repeatedPath + "." + (c + 1);
                        Value(location, cp, components[c]);
                        if (subcomponent is not { } separator || !components[c].Contains(separator)) continue;
                        var parts = components[c].Split(separator);
                        for (var n = 0; n < parts.Length; n++) Value(location + "." + (n + 1), cp + "." + (n + 1), parts[n]);
                    }
                }
            }
            records.Add(new(index, type, string.Join("/", ancestry), raw, fields.ToArray(), result && order is null));
            if (type is "L") { patient = specimen = order = null; }
        }
        if (schema.Syntax == InstrumentMessageSyntax.Lis02)
        {
            var parsed = Lis02Codec.Parse(original); findings.AddRange(parsed.Diagnostics);
            foreach (var message in parsed.Messages)
            {
                patient = specimen = order = null;
                foreach (var record in message.Records)
                    Add(record.Type.ToString(), record.Raw, 1, record.FieldCount, record.Field,
                        record.Delimiters.Repeat, record.Delimiters.Component, null, record.Delimiters.Decode, f => record.Type == 'H' && f == 2);
            }
        }
        else
        {
            var parsed = Hl7Codec.Parse(original); findings.AddRange(parsed.Diagnostics);
            if (parsed.Message is { } message)
                foreach (var segment in message.Segments)
                {
                    var count = segment.Raw.Split(message.Delimiters.Field).Length - (segment.Name == "MSH" ? 0 : 1);
                    Add(segment.Name, segment.Raw, 1, count, segment.Field, message.Delimiters.Repeat,
                        message.Delimiters.Component, message.Delimiters.Subcomponent, message.Delimiters.Decode, f => segment.Name == "MSH" && f <= 2);
                }
        }
        return new(original, records.ToArray(), findings.ToArray());
    }
}
