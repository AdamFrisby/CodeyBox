namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Parses <c>dotnet test --list-tests</c> stdout into fully-qualified test
/// names. VSTest prints a header followed by one indented name per test.
/// Untrusted process output: names are bounded and control characters rejected.
/// </summary>
public static class DotnetTestListParser
{
    public const string AvailableHeader = "The following Tests are available:";

    public static IReadOnlyList<string> Parse(string stdout, int maxTests, int maxNameChars)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        if (maxTests <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTests));
        if (maxNameChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxNameChars));

        var names = new List<string>();
        var seenHeader = false;
        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.TrimEnd();
            if (!seenHeader)
            {
                if (line.Trim().Equals(AvailableHeader, StringComparison.Ordinal))
                    seenHeader = true;
                continue;
            }

            if (line.Length == 0)
                continue;
            if (!char.IsWhiteSpace(raw[0]))
            {
                // A non-indented line after the list is typically a summary or
                // the next VSTest banner — stop rather than treating it as a name.
                if (line.StartsWith("The following Tests", StringComparison.Ordinal))
                    continue;
                break;
            }

            var name = line.Trim();
            if (name.Length == 0)
                continue;
            if (name.Length > maxNameChars)
            {
                throw new TestSelectionBaselineProduceException(
                    $"A listed test name exceeds the {maxNameChars}-character cap.");
            }

            if (ContainsControlCharacter(name))
            {
                throw new TestSelectionBaselineProduceException(
                    "A listed test name contains a control character.");
            }

            if (names.Count >= maxTests)
            {
                throw new TestSelectionBaselineProduceException(
                    $"Baseline exceeds the test cap ({maxTests} tests).");
            }

            names.Add(name);
        }

        return names;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
                return true;
        }

        return false;
    }
}
