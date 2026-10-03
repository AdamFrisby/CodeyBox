using System.Net;
using System.Text;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// A RAM-backed filesystem the provider mounts inside the guest for one
/// sandbox. Real VMs have a dedicated kernel, so these are genuine tmpfs
/// mounts (unlike hosted container backends, which must refuse or downgrade
/// them). Credential mounts must always resolve to one of these.
/// </summary>
public sealed record OpenStackTmpfsMount(string Path, long SizeBytes);

/// <summary>Inputs to the cloud-config user_data rendered for one sandbox.</summary>
public sealed record OpenStackCloudInitSpec(
    string Hostname,
    string SshUser,
    string ClientPublicKey,
    string HostPrivateKey,
    string HostPublicKey,
    IReadOnlyList<OpenStackTmpfsMount> TmpfsMounts);

/// <summary>
/// Pure renderer for the cloud-config user_data attached to each sandbox
/// server. Kept pure (string in, string out) so the exact bytes Nova receives
/// are unit-testable without a cloud.
///
/// <para>The rendered config pins the guest's SSH host identity: the
/// orchestrator generates an ephemeral ed25519 host keypair, embeds the
/// private half here via <c>ssh_keys</c>, and pre-pins the public half in the
/// per-sandbox known_hosts file — first contact is verified, never trusted
/// on sight. Only ed25519 host keys are generated
/// (<c>ssh_genkeytypes: ['ed25519']</c>) so no unpinned key type can be
/// negotiated.</para>
/// </summary>
public static class OpenStackCloudInit
{
    /// <summary>Maximum hostname characters accepted (Nova server names allow more).</summary>
    public const int MaxHostnameChars = 64;

    /// <summary>Renders cloud-config user_data YAML for the given spec.</summary>
    /// <exception cref="ArgumentException">Any field is missing or malformed.</exception>
    public static string Build(OpenStackCloudInitSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var hostname = ValidateHostname(spec.Hostname);
        var user = ValidateUser(spec.SshUser);
        var clientKey = ValidatePublicKey(spec.ClientPublicKey, nameof(spec.ClientPublicKey));
        var hostPublic = ValidatePublicKey(spec.HostPublicKey, nameof(spec.HostPublicKey));
        var hostPrivate = ValidatePrivateKey(spec.HostPrivateKey);
        var mounts = ValidateMounts(spec.TmpfsMounts);

        var yaml = new StringBuilder();
        yaml.Append("#cloud-config\n");
        yaml.Append("hostname: ").Append(hostname).Append('\n');
        yaml.Append("manage_etc_hosts: true\n");
        yaml.Append("ssh_genkeytypes: ['ed25519']\n");
        yaml.Append("ssh_keys:\n");
        yaml.Append("  ed25519_private: |\n");
        foreach (var line in hostPrivate.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
                continue;
            yaml.Append("    ").Append(trimmed).Append('\n');
        }
        yaml.Append("  ed25519_public: ").Append(hostPublic).Append('\n');
        yaml.Append("users:\n");
        yaml.Append("  - name: ").Append(user).Append('\n');
        yaml.Append("    sudo: ALL=(ALL) NOPASSWD:ALL\n");
        yaml.Append("    shell: /bin/bash\n");
        yaml.Append("    ssh_authorized_keys:\n");
        yaml.Append("      - ").Append(clientKey).Append('\n');
        if (mounts.Count > 0)
        {
            yaml.Append("mounts:\n");
            foreach (var mount in mounts)
            {
                yaml.Append("  - [tmpfs, ")
                    .Append(mount.Path)
                    .Append(", tmpfs, \"defaults,size=")
                    .Append(mount.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",mode=0700\", \"0\", \"0\"]\n");
            }
        }
        yaml.Append("runcmd:\n");
        yaml.Append("  - [mkdir, -p, /work]\n");
        return yaml.ToString();
    }

    private static string ValidateHostname(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxHostnameChars)
            throw new ArgumentException($"Hostname must be 1-{MaxHostnameChars} characters.", nameof(value));
        var hostname = value.Trim().ToLowerInvariant();
        foreach (var ch in hostname)
        {
            if ((ch < 'a' || ch > 'z') && (ch < '0' || ch > '9') && ch != '-')
                throw new ArgumentException("Hostname must contain only lowercase letters, digits, and hyphens.", nameof(value));
        }
        if (hostname.StartsWith('-') || hostname.EndsWith('-'))
            throw new ArgumentException("Hostname must not start or end with a hyphen.", nameof(value));
        return hostname;
    }

    private static string ValidateUser(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 32)
            throw new ArgumentException("SSH user must be 1-32 characters.", nameof(value));
        var user = value.Trim();
        if ((!char.IsAsciiLetter(user[0]) && user[0] != '_')
            || user.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-'))
            throw new ArgumentException("SSH user must be a POSIX account name.", nameof(value));
        return user;
    }

    private static string ValidatePublicKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("SSH public key must not be blank.", parameterName);
        var key = value.Trim();
        if (key.Any(ch => ch == '\n' || ch == '\r' || ch == '\0'))
            throw new ArgumentException("SSH public key must be a single line.", parameterName);
        var parts = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !string.Equals(parts[0], "ssh-ed25519", StringComparison.Ordinal)
            || parts[1].Length < 40)
            throw new ArgumentException("SSH public key must be an OpenSSH ed25519 key.", parameterName);
        return key;
    }

    private static string ValidatePrivateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("SSH host private key must not be blank.", nameof(value));
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
        if (!normalized.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new ArgumentException("SSH host private key must be PEM-encoded.", nameof(value));
        if (normalized.Any(ch => ch != '\n' && char.IsControl(ch)))
            throw new ArgumentException("SSH host private key contains control characters.", nameof(value));
        return normalized.TrimEnd('\n');
    }

    private static IReadOnlyList<OpenStackTmpfsMount> ValidateMounts(IReadOnlyList<OpenStackTmpfsMount> mounts)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            if (mount is null)
                throw new ArgumentException("Tmpfs mount must not be null.", nameof(mounts));
            ValidateMountPath(mount.Path);
            if (mount.SizeBytes <= 0)
                throw new ArgumentException($"Tmpfs mount '{mount.Path}' must have a positive size.", nameof(mounts));
            if (!seen.Add(mount.Path))
                throw new ArgumentException($"Duplicate tmpfs mount path '{mount.Path}'.", nameof(mounts));
        }
        return mounts;
    }

    internal static void ValidateMountPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            throw new ArgumentException($"Tmpfs mount path must be absolute: '{path}'.", nameof(path));
        if (path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"Tmpfs mount path must not contain '..': '{path}'.", nameof(path));
        foreach (var ch in path)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '"' or '\'' or '`' or '$' or '\\')
                throw new ArgumentException($"Tmpfs mount path contains an illegal character: '{path}'.", nameof(path));
        }
    }
}
