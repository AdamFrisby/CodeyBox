using System.Text;

namespace CodeyBox.AzureSandboxPlugin;

/// <summary>A RAM-backed mount the guest creates at first boot.</summary>
public sealed record AzureTmpfsMount(string Path, long SizeBytes);

/// <summary>Inputs for one sandbox's cloud-init userData.</summary>
public sealed record AzureCloudInitSpec(
    string Hostname,
    string AdminUsername,
    string ClientPublicKey,
    string HostPrivateKeyPem,
    string HostPublicKey,
    IReadOnlyList<AzureTmpfsMount> TmpfsMounts);

/// <summary>
/// Builds the cloud-init userData for one Azure VM: hostname, admin user with
/// the per-sandbox client key, orchestrator-generated ed25519 host keys, and
/// genuine RAM-backed tmpfs mounts. Carries no cloud credentials — the ARM
/// bearer token stays on the host and never enters guest metadata.
/// </summary>
public static class AzureCloudInit
{
    /// <summary>Validates a guest mount path: absolute, no traversal, no whitespace.</summary>
    public static void ValidateMountPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/'))
            throw new ArgumentException($"Guest mount path must be absolute: '{path}'.", nameof(path));
        if (path.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Guest mount path must not contain whitespace: '{path}'.", nameof(path));
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "." || segment == "..")
                throw new ArgumentException($"Guest mount path must not traverse: '{path}'.", nameof(path));
        }
    }

    public static string Build(AzureCloudInitSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.AdminUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ClientPublicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.HostPrivateKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.HostPublicKey);
        ArgumentNullException.ThrowIfNull(spec.TmpfsMounts);
        foreach (var mount in spec.TmpfsMounts)
        {
            ArgumentNullException.ThrowIfNull(mount);
            ValidateMountPath(mount.Path);
            if (mount.SizeBytes <= 0)
                throw new ArgumentException($"Tmpfs mount '{mount.Path}' needs a positive size.", nameof(spec));
        }

        var script = new StringBuilder();
        script.Append("#cloud-config\n");
        script.Append("hostname: ").Append(QuoteYamlScalar(spec.Hostname)).Append('\n');
        script.Append("users:\n");
        script.Append("  - name: ").Append(QuoteYamlScalar(spec.AdminUsername)).Append('\n');
        script.Append("    sudo: ALL=(ALL) NOPASSWD:ALL\n");
        script.Append("    shell: /bin/bash\n");
        script.Append("    ssh_authorized_keys:\n");
        script.Append("      - ").Append(QuoteYamlScalar(spec.ClientPublicKey)).Append('\n');
        script.Append("ssh_keys:\n");
        script.Append("  ed25519_private: |\n");
        AppendIndentedBlock(script, spec.HostPrivateKeyPem, "    ");
        script.Append("  ed25519_public: ").Append(QuoteYamlScalar(spec.HostPublicKey)).Append('\n');
        if (spec.TmpfsMounts.Count > 0)
        {
            script.Append("mounts:\n");
            foreach (var mount in spec.TmpfsMounts)
            {
                script.Append("  - [tmpfs, ")
                    .Append(QuoteYamlScalar(mount.Path))
                    .Append(", tmpfs, \"defaults,size=")
                    .Append(mount.SizeBytes)
                    .Append("\", \"0\", \"0\"]\n");
            }
        }
        script.Append("runcmd:\n");
        script.Append("  - chmod 600 /etc/ssh/ssh_host_ed25519_key\n");
        script.Append("  - systemctl restart ssh || systemctl restart sshd || true\n");
        return script.ToString();
    }

    private static void AppendIndentedBlock(StringBuilder script, string block, string indent)
    {
        foreach (var line in block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
                continue;
            script.Append(indent).Append(line).Append('\n');
        }
    }

    private static string QuoteYamlScalar(string value)
    {
        if (value.Length == 0)
            return "''";
        var safe = true;
        foreach (var ch in value)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '/' || ch == '@' || ch == '+'))
            {
                safe = false;
                break;
            }
        }
        if (safe)
            return value;
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
