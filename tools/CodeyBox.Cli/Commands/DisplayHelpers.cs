using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Cli.Commands;

internal readonly record struct CliTableColumn(string Header, int Width);

internal static class DisplayHelpers
{
    internal static void PrintTable(IReadOnlyList<CliTableColumn> columns, IEnumerable<IReadOnlyList<string?>> rows)
    {
        Console.WriteLine(string.Join("  ", columns.Select(c => Sanitize(c.Header).PadRight(c.Width))));
        Console.WriteLine(new string('-', columns.Sum(c => c.Width) + (columns.Count - 1) * 2));

        foreach (var row in rows)
        {
            var cells = columns.Select((column, index) =>
            {
                var value = index < row.Count ? row[index] : "";
                return Truncate(Sanitize(value), column.Width).PadRight(column.Width);
            });
            Console.WriteLine(string.Join("  ", cells));
        }
    }

    internal static string Field(JsonElement element, string propertyName, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
            return fallback;

        return Value(property, fallback);
    }

    internal static string Value(JsonElement element, string fallback = "")
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? fallback,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => fallback,
            JsonValueKind.Undefined => fallback,
            _ => element.ToString(),
        };
    }

    internal static string Percent(JsonElement element, string propertyName)
    {
        return TryGetDouble(element, propertyName, out var value)
            ? Percent(value)
            : "";
    }

    internal static string Percent(double value)
    {
        // Backend uses -1 to signal unknown availability.
        if (value < 0)
            return "unknown";

        return value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    internal static string Decimal(JsonElement element, string propertyName)
    {
        return TryGetDouble(element, propertyName, out var value)
            ? value.ToString("0.##", CultureInfo.InvariantCulture)
            : "";
    }

    internal static int CountArray(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Array
                ? property.GetArrayLength()
                : 0;
    }

    internal static int CountObjectProperties(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object
                ? property.EnumerateObject().Count()
                : 0;
    }

    internal static string JoinArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
            return "";

        return string.Join(",", property.EnumerateArray().Select(item => Value(item)));
    }

    internal static string ShortId(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return Truncate(Sanitize(value), maxLen);
    }

    internal static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen)
            return s;

        if (maxLen <= 3)
            return s[..maxLen];

        return s[..(maxLen - 3)] + "...";
    }

    internal static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetDouble(out value);

        if (property.ValueKind == JsonValueKind.String)
            return double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        return false;
    }

    /// <summary>
    /// Strips terminal control characters from multi-line untrusted text while preserving
    /// layout whitespace (newline, carriage return, tab). Use for rendering server-supplied
    /// diffs and captured agent stdout to the terminal, where a bare <see cref="Sanitize"/>
    /// would also swallow the newlines/tabs that make the output readable. Prevents ANSI /
    /// escape-sequence injection from untrusted repo file content or agent output.
    /// </summary>
    internal static string SanitizeMultiline(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return s ?? "";

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\n' or '\r' or '\t' || !char.IsControl(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    internal static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return s ?? "";

        const int MaxStackallocChars = 1024;
        Span<char> buf = s.Length <= MaxStackallocChars
            ? stackalloc char[s.Length]
            : new char[s.Length];
        int pos = 0;
        foreach (var c in s)
        {
            if (!char.IsControl(c))
                buf[pos++] = c;
        }

        return new string(buf[..pos]);
    }
}
