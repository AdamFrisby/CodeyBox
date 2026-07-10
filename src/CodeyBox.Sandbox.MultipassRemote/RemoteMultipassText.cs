using System.Globalization;
using System.Text;

namespace CodeyBox.Sandbox.MultipassRemote;

internal static class RemoteMultipassText
{
    public static bool IsInstanceNotFound(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;

        return stderr.Contains("argument not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("instance not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    public static string TruncateForLog(string s, int max = 200)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var trimmed = s.Trim();
        var sb = new StringBuilder(Math.Min(trimmed.Length, max));
        foreach (var ch in trimmed)
        {
            var escaped = ch switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(ch) => "\\u" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture),
                _ => ch.ToString(),
            };

            if (sb.Length + escaped.Length > max)
            {
                sb.Append("...");
                break;
            }
            sb.Append(escaped);
        }
        return sb.ToString();
    }
}
