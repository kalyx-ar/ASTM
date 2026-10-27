using System.Text;
using Astm.Abstractions.Diagnostics;

namespace Astm.Protocol.Hl7v2;

public sealed record Hl7Delimiters(char Field = '|', char Component = '^', char Repeat = '~', char Escape = '\\', char Subcomponent = '&')
{
    public static readonly Hl7Delimiters Default = new();
    public string EncodingCharacters => $"{Component}{Repeat}{Escape}{Subcomponent}";
    public string Encode(string? text)
    {
        var output = new StringBuilder();
        foreach (var c in text ?? string.Empty)
        {
            var code = c == Field ? "F" : c == Component ? "S" : c == Repeat ? "R" : c == Escape ? "E" : c == Subcomponent ? "T" : null;
            if (code is not null) output.Append(Escape).Append(code).Append(Escape);
            else if (char.IsControl(c)) output.Append(Escape).Append('X').Append(Convert.ToHexString(Encoding.UTF8.GetBytes(c.ToString()))).Append(Escape);
            else output.Append(c);
        }
        return output.ToString();
    }

    public string Decode(string raw)
    {
        var output = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != Escape) { output.Append(raw[i]); continue; }
            var end = raw.IndexOf(Escape, i + 1);
            if (end < 0) throw new FormatException("ASTM-HL7-UNTERMINATED-ESCAPE");
            var token = raw[(i + 1)..end];
            output.Append(token switch
            {
                "F" => Field.ToString(), "S" => Component.ToString(), "R" => Repeat.ToString(),
                "E" => Escape.ToString(), "T" => Subcomponent.ToString(),
                _ when token.StartsWith('X') => new UTF8Encoding(false, true).GetString(Convert.FromHexString(token[1..])),
                _ => raw[i..(end + 1)], // Preserve formatting/local escapes; do not invent their meaning.
            });
            i = end;
        }
        return output.ToString();
    }
}

public sealed class Hl7Segment
{
    private readonly string[] _fields;
    public Hl7Segment(string raw, Hl7Delimiters delimiters)
    {
        Raw = raw; Delimiters = delimiters; _fields = raw.Split(delimiters.Field);
        Name = _fields[0];
    }
    public string Name { get; }
    public string Raw { get; }
    public Hl7Delimiters Delimiters { get; }
    public string Field(int number)
    {
        if (number < 1) throw new ArgumentOutOfRangeException(nameof(number));
        if (Name == "MSH" && number == 1) return Delimiters.Field.ToString();
        var index = Name == "MSH" ? number - 1 : number;
        return index < _fields.Length ? _fields[index] : string.Empty;
    }
    public bool IsNull(int number) => Field(number) == "\"\"";
    public IReadOnlyList<string> Repetitions(int number) => Field(number).Split(Delimiters.Repeat);
    public string Text(int field, int component = 1, int repetition = 1, int subcomponent = 0)
    {
        if (component < 1 || repetition < 1 || subcomponent < 0) throw new ArgumentOutOfRangeException(nameof(component));
        var repeats = Field(field).Split(Delimiters.Repeat);
        var components = (repetition <= repeats.Length ? repeats[repetition - 1] : string.Empty).Split(Delimiters.Component);
        var value = component <= components.Length ? components[component - 1] : string.Empty;
        if (subcomponent > 0)
        {
            var parts = value.Split(Delimiters.Subcomponent);
            value = subcomponent <= parts.Length ? parts[subcomponent - 1] : string.Empty;
        }
        return Delimiters.Decode(value);
    }

    /// <summary>Builds from already encoded fields. Use delimiters.Encode for literal text.</summary>
    public static Hl7Segment Build(string name, Hl7Delimiters delimiters, params (int Field, string Value)[] values)
    {
        if (name.Length != 3 || !name.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Invalid segment name.", nameof(name));
        if (values.Any(v => v.Field < (name == "MSH" ? 3 : 1) || v.Value.Any(char.IsControl))) throw new ArgumentException("Invalid raw field.", nameof(values));
        var fields = values.ToDictionary(v => v.Field, v => v.Value);
        var last = Math.Max(name == "MSH" ? 2 : 0, fields.Keys.DefaultIfEmpty(0).Max());
        var output = name == "MSH" ? name + delimiters.Field + delimiters.EncodingCharacters : name;
        for (var i = name == "MSH" ? 3 : 1; i <= last; i++) output += delimiters.Field + fields.GetValueOrDefault(i, string.Empty);
        return new(output, delimiters);
    }
}

public sealed record Hl7Message(Hl7Delimiters Delimiters, IReadOnlyList<Hl7Segment> Segments)
{
    public Hl7Segment Header => Segments[0];
    public string Type => Header.Text(9);
    public string Trigger => Header.Text(9, 2);
    public string ControlId => Header.Text(10);
    public IEnumerable<Hl7Segment> OfType(string name) => Segments.Where(s => s.Name == name);
    public string Serialize() => string.Concat(Segments.Select(s => s.Raw + "\r"));
}

public sealed record Hl7ParseResult(Hl7Message? Message, IReadOnlyList<AstmDiagnostic> Diagnostics);

public static class Hl7Codec
{
    public static Hl7ParseResult Parse(string text)
    {
        Hl7ParseResult Invalid(string code) => new(null, [new(code, AstmSeverity.Error, "Invalid HL7 message.")]);
        if (text.Length < 8 || !text.StartsWith("MSH", StringComparison.Ordinal)) return Invalid("ASTM-HL7-MISSING-MSH");
        var all = text.Substring(3, 5);
        if (all.Distinct().Count() != 5 || all.Any(c => c < '!' || c > '~' || char.IsAsciiLetterOrDigit(c)) || text.Length < 9 || text[8] != text[3])
            return Invalid("ASTM-HL7-INVALID-DELIMITERS");
        var d = new Hl7Delimiters(text[3], text[4], text[5], text[6], text[7]);
        var lines = text.Split('\r', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim('\n')).ToArray();
        if (lines.Any(s => s.Length < 4 || s[3] != d.Field || !s[..3].All(char.IsAsciiLetterOrDigit) || s.Any(char.IsControl))
            || lines.Skip(1).Any(s => s.StartsWith("MSH", StringComparison.Ordinal))) return Invalid("ASTM-HL7-INVALID-SEGMENTS");
        var message = new Hl7Message(d, lines.Select(s => new Hl7Segment(s, d)).ToArray());
        if (message.Header.Field(9).Length == 0 || message.Header.Field(10).Length == 0 || message.Header.Field(12).Length == 0)
            return Invalid("ASTM-HL7-MISSING-HEADER-FIELD");
        return new(message, []);
    }
}
