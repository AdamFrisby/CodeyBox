namespace CodeyBox.Sandbox.MultipassRemote;

internal static class RemoteMultipassVmNames
{
    private const int ProviderVmNameMaxLength = 24;
    private const int MultipassVmNameMaxLength = 63;

    public static string NewVmName(MultipassRemoteSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ValidateVmNamePrefix(opts.VmNamePrefix);

        var hex = Guid.NewGuid().ToString("N");
        var budget = ProviderVmNameMaxLength - opts.VmNamePrefix.Length;
        return opts.VmNamePrefix + hex[..Math.Min(budget, hex.Length)];
    }

    public static bool IsManagedVmNameForPrefix(string? name, string prefix) =>
        !string.IsNullOrEmpty(name)
        && IsValidVmNamePrefix(prefix)
        && name.StartsWith(prefix, StringComparison.Ordinal)
        && name.Length > prefix.Length
        && IsValidVmName(name);

    public static void ValidateManagedVmNameForPrefix(string name, string prefix)
    {
        if (!IsManagedVmNameForPrefix(name, prefix))
            throw new InvalidOperationException(
                $"Remote VM name '{name}' is not a valid managed Multipass name for prefix '{prefix}'.");
    }

    public static void ValidateVmNamePrefix(string prefix)
    {
        if (!IsValidVmNamePrefix(prefix))
            throw new InvalidOperationException(
                $"VmNamePrefix '{prefix}' must start with a lowercase letter, contain only lowercase letters, digits, or hyphens, and leave room for a generated suffix.");
    }

    public static string BuildRemoteSandboxRoot(string remoteStagingRoot, string vmName)
    {
        if (!IsValidVmName(vmName))
            throw new InvalidOperationException($"Remote VM name '{vmName}' is not a valid Multipass instance name.");
        ValidateRemoteStagingRoot(remoteStagingRoot);
        var root = NormalizeRemoteStagingRoot(remoteStagingRoot);
        return root + "/" + vmName;
    }

    public static void ValidateRemoteSandboxRoot(string remoteStagingRoot, string vmName, string remoteSandboxRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteSandboxRoot);
        if (ContainsInvalidPathCharacter(remoteSandboxRoot))
            throw new InvalidOperationException("Remote sandbox staging path contains an invalid control character.");

        var expected = BuildRemoteSandboxRoot(remoteStagingRoot, vmName);
        var actual = NormalizeRemotePath(remoteSandboxRoot);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Remote sandbox staging path '{remoteSandboxRoot}' does not match expected managed path '{expected}'.");
    }

    public static void ValidateRemoteStagingRoot(string remoteStagingRoot)
    {
        _ = NormalizeRemoteStagingRoot(remoteStagingRoot);
    }

    private static bool IsValidVmNamePrefix(string? prefix) =>
        !string.IsNullOrWhiteSpace(prefix)
        && prefix.Length < ProviderVmNameMaxLength
        && IsValidVmName(prefix, allowTrailingHyphen: true);

    private static bool IsValidVmName(string name, bool allowTrailingHyphen = false)
    {
        if (name.Length == 0 || name.Length > MultipassVmNameMaxLength)
            return false;
        if (!IsLowerAsciiLetter(name[0]))
            return false;
        if (!allowTrailingHyphen && name[^1] == '-')
            return false;

        foreach (var ch in name)
        {
            if (!IsLowerAsciiLetter(ch) && !IsAsciiDigit(ch) && ch != '-')
                return false;
        }

        return true;
    }

    private static string NormalizeRemoteStagingRoot(string remoteStagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteStagingRoot);
        var normalized = NormalizeRemotePath(remoteStagingRoot);
        if (normalized == "/")
            throw new InvalidOperationException("RemoteStagingRoot must not be the remote filesystem root.");
        return normalized;
    }

    private static string NormalizeRemotePath(string remotePath)
    {
        if (!remotePath.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Remote path '{remotePath}' must be absolute.");
        if (ContainsInvalidPathCharacter(remotePath))
            throw new InvalidOperationException("Remote path contains an invalid control character.");

        var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part is "." or "..")
                throw new InvalidOperationException($"Remote path '{remotePath}' must not contain traversal segments.");
        }

        return "/" + string.Join("/", parts);
    }

    private static bool ContainsInvalidPathCharacter(string value) =>
        value.IndexOfAny(['\0', '\r', '\n']) >= 0;

    private static bool IsLowerAsciiLetter(char ch) => ch is >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char ch) => ch is >= '0' and <= '9';
}
