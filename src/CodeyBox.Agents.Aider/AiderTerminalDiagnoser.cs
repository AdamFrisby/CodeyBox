using System.Text.Json;

namespace CodeyBox.Agents.Aider;

/// <summary>
/// Pure extraction of aider's terminal run error from one-shot stdout.
///
/// <para>Aider exits 0 even when the run dies before producing output (verified
/// against aider 0.86.2: a bad OpenRouter key exits 0 with only the litellm
/// exception and the "not able to authenticate you" sentence on stdout). The
/// failure surfaces as litellm exception lines relayed verbatim —
/// <c>litellm.AuthenticationError: AuthenticationError: OpenrouterException -
/// {"error":{"message":"Missing Authentication header","code":401}}</c> —
/// followed by the human sentence <c>The API provider is not able to
/// authenticate you. Check your API key.</c> The first error line wins.</para>
///
/// <para>Returns null when no terminal error is present (a healthy run ends with
/// <c>Tokens: …</c> and <c>Applied edit to …</c> lines). Never throws: output
/// is plain text, so extraction is line matching only. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks never
/// balloon on a verbose provider error body.</para>
/// </summary>
internal static class AiderTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (IsTerminalErrorLine(line))
                return Truncate(line);
        }

        return null;
    }

    private static bool IsTerminalErrorLine(string line)
    {
        // litellm exception relay, e.g. "litellm.AuthenticationError:
        // AuthenticationError: OpenrouterException - {...}". The "litellm."
        // prefix plus "Error" anchors to the relay rather than to model output
        // discussing errors in code under review.
        if (line.Contains("litellm.", StringComparison.Ordinal)
            && line.Contains("Error", StringComparison.Ordinal))
            return true;

        // Human sentence aider prints after the relay.
        if (line.Contains("not able to authenticate you", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
