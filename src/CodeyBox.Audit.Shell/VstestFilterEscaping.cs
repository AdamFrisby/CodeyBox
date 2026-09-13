namespace CodeyBox.Audit.Shell;

/// <summary>
/// Single source of truth for escaping VSTest <c>--filter</c> property values.
/// Every test name that reaches an executed <c>dotnet test --filter</c>
/// expression passes through here — including names sourced from the
/// test-selection baseline, which is untrusted sandbox-produced input. VSTest
/// treats <c>\ , ( ) ! ~ &amp; | =</c> as filter metacharacters, so each is
/// backslash-escaped; anything else (including whitespace) is literal.
/// </summary>
internal static class VstestFilterEscaping
{
    public static string EscapeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var chars = new List<char>(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or ',' or '(' or ')' or '!' or '~' or '&' or '|' or '=')
                chars.Add('\\');
            chars.Add(ch);
        }

        return new string(chars.ToArray());
    }
}
