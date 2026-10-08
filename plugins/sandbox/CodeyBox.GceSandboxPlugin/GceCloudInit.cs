using System.Text;

namespace CodeyBox.GceSandboxPlugin;

/// <summary>A RAM-backed filesystem the provider mounts inside the guest for one sandbox.</summary>
public sealed record GceTmpfsMount(string Path, long SizeBytes);

/// <summary>Inputs to the GCE startup-script builder.</summary>
public sealed record GceStartupScriptSpec(
    string InstanceName,
    string SshUser,
    string HostPublicKeyText,
    string HostPrivateKeyPem,
    IReadOnlyList<GceTmpfsMount> TmpfsMounts);

/// <summary>
/// Builds the GCE <c>startup-script</c> metadata bootstrap: writes the pinned host key
/// pair, creates tmpfs mounts (credential mounts must resolve under one), and reloads
/// sshd. Carries no cloud API credentials — only the host public key and the guest host
/// key pair, delivered over the TLS API channel and never logged.
/// </summary>
public static class GceCloudInit
{
    /// <summary>Builds the startup script. Throws when a tmpfs mount is invalid.</summary>
    public static string Build(GceStartupScriptSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.InstanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.SshUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.HostPublicKeyText);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.HostPrivateKeyPem);
        foreach (var mount in spec.TmpfsMounts)
            ValidateTmpfsMount(mount);

        var script = new StringBuilder();
        script.AppendLine("#!/bin/bash");
        script.AppendLine("set -euo pipefail");
        script.AppendLine($"INSTANCE_NAME={QuoteShellWord(spec.InstanceName)}");
        script.AppendLine($"SSH_USER={QuoteShellWord(spec.SshUser)}");
        script.AppendLine("install -d -m 700 -o root -g root /etc/ssh/codeybox");
        script.AppendLine("cat > /etc/ssh/codeybox/ssh_host_ed25519_key <<'CODEYBOX_HOST_KEY_EOF'");
        script.Append(spec.HostPrivateKeyPem);
        if (!spec.HostPrivateKeyPem.EndsWith('\n'))
            script.Append('\n');
        script.AppendLine("CODEYBOX_HOST_KEY_EOF");
        script.AppendLine("chmod 600 /etc/ssh/codeybox/ssh_host_ed25519_key");
        script.AppendLine("cat > /etc/ssh/codeybox/ssh_host_ed25519_key.pub <<'CODEYBOX_HOST_PUB_EOF'");
        script.Append(spec.HostPublicKeyText.Trim());
        script.Append('\n');
        script.AppendLine("CODEYBOX_HOST_PUB_EOF");
        script.AppendLine("chmod 644 /etc/ssh/codeybox/ssh_host_ed25519_key.pub");
        script.AppendLine("if grep -q '^HostKey ' /etc/ssh/sshd_config 2>/dev/null; then");
        script.AppendLine("  sed -i 's|^HostKey .*|HostKey /etc/ssh/codeybox/ssh_host_ed25519_key|' /etc/ssh/sshd_config");
        script.AppendLine("else");
        script.AppendLine("  echo 'HostKey /etc/ssh/codeybox/ssh_host_ed25519_key' >> /etc/ssh/sshd_config");
        script.AppendLine("fi");
        foreach (var mount in spec.TmpfsMounts)
        {
            script.AppendLine($"install -d -m 755 {QuoteShellWord(mount.Path)}");
            script.AppendLine(
                $"mount -t tmpfs -o size={mount.SizeBytes},mode=755 tmpfs {QuoteShellWord(mount.Path)} || true");
        }
        script.AppendLine("systemctl reload sshd || systemctl restart sshd || service ssh reload || service ssh restart || true");
        return script.ToString();
    }

    internal static void ValidateTmpfsMount(GceTmpfsMount mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        if (string.IsNullOrWhiteSpace(mount.Path) || !mount.Path.StartsWith('/'))
            throw new ArgumentException($"Tmpfs mount path '{mount.Path}' must be an absolute guest path.", nameof(mount));
        if (mount.SizeBytes < 1024 * 1024 || mount.SizeBytes > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(mount), "Tmpfs mount size must be 1 MiB–1 GiB.");
    }

    internal static string QuoteShellWord(string value)
    {
        if (value.Length == 0)
            return "''";
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
