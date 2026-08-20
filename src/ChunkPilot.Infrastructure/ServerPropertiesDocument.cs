using System.Text;

namespace ChunkPilot.Infrastructure;

public sealed class ServerPropertiesDocument
{
    private readonly List<PropertyLine> lines;

    private ServerPropertiesDocument(List<PropertyLine> lines, string lineEnding, bool endsWithNewLine)
    {
        this.lines = lines;
        LineEnding = lineEnding;
        EndsWithNewLine = endsWithNewLine;
    }

    public string LineEnding { get; }
    public bool EndsWithNewLine { get; }

    public IReadOnlyDictionary<string, string> Values =>
        lines.Where(line => line.Kind == PropertyLineKind.Property)
            .GroupBy(line => line.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

    public static ServerPropertiesDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
            text.Contains('\n', StringComparison.Ordinal) ? "\n" : Environment.NewLine;
        var endsWithNewLine = text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n');
        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (endsWithNewLine && rawLines.Length > 0)
            rawLines = rawLines[..^1];
        var parsed = new List<PropertyLine>(rawLines.Length);
        foreach (var raw in rawLines)
        {
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0)
            {
                parsed.Add(new(PropertyLineKind.Blank, raw, "", ""));
                continue;
            }
            if (trimmed.StartsWith('#') || trimmed.StartsWith('!'))
            {
                parsed.Add(new(PropertyLineKind.Comment, raw, "", ""));
                continue;
            }
            var separator = FindSeparator(raw);
            if (separator < 0)
            {
                parsed.Add(new(PropertyLineKind.Raw, raw, "", ""));
                continue;
            }
            var key = raw[..separator].Trim();
            var valueStart = separator + 1;
            while (valueStart < raw.Length && char.IsWhiteSpace(raw[valueStart]))
                valueStart++;
            parsed.Add(new(PropertyLineKind.Property, raw, Unescape(key), Unescape(raw[valueStart..])));
        }
        return new ServerPropertiesDocument(parsed, lineEnding, endsWithNewLine);
    }

    public string? Get(string key) =>
        lines.LastOrDefault(line => line.Kind == PropertyLineKind.Property &&
            line.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

    public void Set(string key, string value)
    {
        Validate(key, value);
        var index = lines.FindLastIndex(line => line.Kind == PropertyLineKind.Property &&
            line.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var escaped = $"{Escape(key, true)}={Escape(value, false)}";
        if (index >= 0)
            lines[index] = new(PropertyLineKind.Property, escaped, key, value);
        else
            lines.Add(new(PropertyLineKind.Property, escaped, key, value));
    }

    public bool Remove(string key)
    {
        var removed = lines.RemoveAll(line => line.Kind == PropertyLineKind.Property &&
            line.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return removed > 0;
    }

    public override string ToString()
    {
        var text = string.Join(LineEnding, lines.Select(line => line.Raw));
        return EndsWithNewLine ? text + LineEnding : text;
    }

    private static int FindSeparator(string line)
    {
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (line[index] == '\\')
            {
                escaped = true;
                continue;
            }
            if (line[index] is '=' or ':' || char.IsWhiteSpace(line[index]))
                return index;
        }
        return -1;
    }

    private static void Validate(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Property name cannot be empty.", nameof(key));
        if (key.Contains('\r') || key.Contains('\n'))
            throw new ArgumentException("Minecraft property names cannot contain literal newlines.");
    }

    private static string Escape(string value, bool key)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '\t' => "\\t",
                '\n' => "\\n",
                '\r' => "\\r",
                '=' when key => "\\=",
                ':' when key => "\\:",
                ' ' when key => "\\ ",
                _ => character.ToString()
            });
        }
        return builder.ToString();
    }

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }
            var next = value[++index];
            if (next == 'u' && index + 4 < value.Length &&
                ushort.TryParse(value.AsSpan(index + 1, 4), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var unicode))
            {
                builder.Append((char)unicode);
                index += 4;
                continue;
            }
            builder.Append(next switch
            {
                't' => '\t',
                'n' => '\n',
                'r' => '\r',
                _ => next
            });
        }
        return builder.ToString();
    }

    private enum PropertyLineKind { Blank, Comment, Property, Raw }
    private sealed record PropertyLine(PropertyLineKind Kind, string Raw, string Key, string Value);
}

