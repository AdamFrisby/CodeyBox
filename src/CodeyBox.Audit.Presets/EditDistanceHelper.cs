namespace CodeyBox.Audit.Presets;

internal static class EditDistanceHelper
{
    public static int Compute(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (left.Length > 256 || right.Length > 256)
            return Math.Max(left.Length, right.Length);

        var dp = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++)
            dp[i, 0] = i;
        for (var j = 0; j <= right.Length; j++)
            dp[0, j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }
        return dp[left.Length, right.Length];
    }
}
