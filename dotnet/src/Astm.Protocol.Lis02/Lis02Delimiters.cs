using System.Text;

namespace Astm.Protocol.Lis02;

/// <summary>Delimiters declared by the header record: <c>H|\^&amp;</c> = field, repeat, component, escape.</summary>
public sealed record Lis02Delimiters(char Field, char Repeat, char Component, char Escape)
{
    public static readonly Lis02Delimiters Default = new('|', '\\', '^', '&');

    /// <summary>Content of field H.2 for these delimiters.</summary>
    public string HeaderDefinition => $"{Repeat}{Component}{Escape}";

    /// <summary>Reads the delimiters from a header record. All four must be distinct printable characters.</summary>
    public static bool TryParseHeader(ReadOnlySpan<char> header, out Lis02Delimiters delimiters)
    {
        delimiters = Default;
        if (header.Length < 5 || char.ToUpperInvariant(header[0]) != 'H')
            return false;
        var candidate = new Lis02Delimiters(header[1], header[2], header[3], header[4]);
        ReadOnlySpan<char> all = [candidate.Field, candidate.Repeat, candidate.Component, candidate.Escape];
        for (var i = 0; i < all.Length; i++)
        {
            if (char.IsControl(all[i]) || char.IsLetterOrDigit(all[i]) || all[i] == ' ')
                return false;
            for (var j = i + 1; j < all.Length; j++)
            {
                if (all[i] == all[j])
                    return false;
            }
        }
        delimiters = candidate;
        return true;
    }

    /// <summary>Replaces escape sequences <c>&amp;F&amp; &amp;S&amp; &amp;R&amp; &amp;E&amp;</c>; unknown sequences are kept verbatim.</summary>
    public string Decode(string value)
    {
        if (value.IndexOf(Escape) < 0)
            return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == Escape && i + 2 < value.Length && value[i + 2] == Escape)
            {
                char? replacement = value[i + 1] switch
                {
                    'F' => Field,
                    'S' => Component,
                    'R' => Repeat,
                    'E' => Escape,
                    _ => null,
                };
                if (replacement is { } c)
                {
                    builder.Append(c);
                    i += 2;
                    continue;
                }
            }
            builder.Append(value[i]);
        }
        return builder.ToString();
    }

    public string Encode(string value)
    {
        if (value.AsSpan().IndexOfAny([Field, Repeat, Component, Escape]) < 0)
            return value;
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            var code = c == Field ? 'F' : c == Component ? 'S' : c == Repeat ? 'R' : c == Escape ? 'E' : '\0';
            if (code == '\0')
                builder.Append(c);
            else
                builder.Append(Escape).Append(code).Append(Escape);
        }
        return builder.ToString();
    }
}
