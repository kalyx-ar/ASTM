namespace Atsm.Protocol.Lis02;

/// <summary>
/// One LIS02-A2 record. Field numbers are 1-based as in the standard: field 1 is the record type,
/// field 2 the sequence number (or the delimiter definition in H). Accessors return escaped text exactly as received;
/// use the <c>Text</c> overloads for decoded values.
/// </summary>
public sealed class Lis02Record
{
    private readonly string[] _fields;

    public Lis02Record(string raw, Lis02Delimiters delimiters)
    {
        ArgumentException.ThrowIfNullOrEmpty(raw);
        Raw = raw;
        Delimiters = delimiters;
        Type = char.ToUpperInvariant(raw[0]);
        _fields = raw.Split(delimiters.Field);
    }

    public string Raw { get; }

    public Lis02Delimiters Delimiters { get; }

    /// <summary>Upper-cased record type (H, P, O, R, C, Q, M, S, L...).</summary>
    public char Type { get; }

    public int FieldCount => _fields.Length;

    public string Field(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        return number <= _fields.Length ? _fields[number - 1] : string.Empty;
    }

    public IReadOnlyList<string> Repeats(int field)
    {
        var value = Field(field);
        if (IsDelimiterDefinition(field))
            return [value];
        return value.Split(Delimiters.Repeat);
    }

    public string Component(int field, int component, int repeat = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(component, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeat, 1);
        var repeats = Repeats(field);
        if (repeat > repeats.Count)
            return string.Empty;
        if (IsDelimiterDefinition(field))
            return component == 1 ? repeats[0] : string.Empty;
        var components = repeats[repeat - 1].Split(Delimiters.Component);
        return component <= components.Length ? components[component - 1] : string.Empty;
    }

    public IReadOnlyList<string> Components(int field, int repeat = 1)
    {
        var repeats = Repeats(field);
        if (repeat > repeats.Count)
            return [];
        return IsDelimiterDefinition(field) ? [repeats[0]] : repeats[repeat - 1].Split(Delimiters.Component);
    }

    public string Text(int field) => Delimiters.Decode(Field(field));

    public string Text(int field, int component, int repeat = 1) => Delimiters.Decode(Component(field, component, repeat));

    public override string ToString() => Raw;

    private bool IsDelimiterDefinition(int field) => Type == 'H' && field == 2;
}

/// <summary>Builds a record from decoded values, escaping delimiters found in the data.</summary>
public sealed class Lis02RecordBuilder(char type, Lis02Delimiters delimiters)
{
    private readonly List<string> _fields = [char.ToString(type)];

    public Lis02Delimiters Delimiters { get; } = delimiters;

    /// <summary>Sets a field from already formatted (escaped) text, for values that contain components or repeats.</summary>
    public Lis02RecordBuilder SetRaw(int field, string? raw)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(field, 2);
        while (_fields.Count < field)
            _fields.Add(string.Empty);
        _fields[field - 1] = raw ?? string.Empty;
        return this;
    }

    public Lis02RecordBuilder SetText(int field, string? text) => SetRaw(field, text is null ? null : Delimiters.Encode(text));

    public Lis02RecordBuilder SetComponents(int field, params string?[] components) =>
        SetRaw(field, string.Join(Delimiters.Component, components.Select(c => c is null ? string.Empty : Delimiters.Encode(c))));

    public Lis02RecordBuilder SetRepeats(int field, IEnumerable<string?[]> repeats) =>
        SetRaw(field, string.Join(Delimiters.Repeat,
            repeats.Select(r => string.Join(Delimiters.Component, r.Select(c => c is null ? string.Empty : Delimiters.Encode(c))))));

    public Lis02Record Build()
    {
        var count = _fields.Count;
        while (count > 1 && _fields[count - 1].Length == 0)
            count--;
        var raw = string.Join(Delimiters.Field, _fields.Take(count));
        return new Lis02Record(raw, Delimiters);
    }

    /// <summary>Header with the delimiter definition in field 2.</summary>
    public static Lis02RecordBuilder Header(Lis02Delimiters delimiters) =>
        new Lis02RecordBuilder('H', delimiters).SetRaw(2, delimiters.HeaderDefinition);
}
