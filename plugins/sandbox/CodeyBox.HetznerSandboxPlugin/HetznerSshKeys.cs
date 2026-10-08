using System.Net;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.HetznerSandboxPlugin;

/// <summary>Client-identity keypair: the private half lives at a 0600 file, the public half is registered as a Hetzner SSH key.</summary>
public sealed record HetznerClientKeyMaterial(string PrivateKeyPath, string PublicKeyText);

/// <summary>Guest host-identity keypair: the private half is injected via cloud-init, the public half is pinned in known_hosts.</summary>
public sealed record HetznerHostKeyMaterial(string PrivateKeyPem, string PublicKeyText);

/// <summary>Generates ephemeral per-sandbox ed25519 keypairs through the OpenSSH CLI (argv array, never a shell string).</summary>
public interface IHetznerKeyGenerator
{
    Task<HetznerClientKeyMaterial> GenerateClientKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct);

    Task<HetznerHostKeyMaterial> GenerateHostKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct);
}

/// <summary>
/// <see cref="IHetznerKeyGenerator"/> backed by <c>ssh-keygen</c>. No
/// hand-rolled crypto: key generation and file permissions come from the
/// platform OpenSSH binary (<c>ssh-keygen</c> creates the private file 0600).
/// </summary>
public sealed class SshKeygenGenerator(IProcessRunner runner) : IHetznerKeyGenerator
{
    private readonly IProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<HetznerClientKeyMaterial> GenerateClientKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keygenBinary);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var privatePath = UniqueKeyPath(directory, "id");
        await RunKeygenAsync(keygenBinary, privatePath, comment, ct).ConfigureAwait(false);
        var publicText = await ReadPublicKeyAsync(privatePath + ".pub", ct).ConfigureAwait(false);
        return new HetznerClientKeyMaterial(privatePath, publicText);
    }

    public async Task<HetznerHostKeyMaterial> GenerateHostKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keygenBinary);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var privatePath = UniqueKeyPath(directory, "host");
        await RunKeygenAsync(keygenBinary, privatePath, comment, ct).ConfigureAwait(false);
        var publicText = await ReadPublicKeyAsync(privatePath + ".pub", ct).ConfigureAwait(false);
        var privatePem = await File.ReadAllTextAsync(privatePath, ct).ConfigureAwait(false);
        return new HetznerHostKeyMaterial(privatePem, publicText);
    }

    private async Task RunKeygenAsync(string keygenBinary, string privatePath, string comment, CancellationToken ct)
    {
        IReadOnlyList<string> argv = [keygenBinary, "-t", "ed25519", "-f", privatePath, "-N", "", "-C", comment ?? string.Empty];
        ProcessRunResult result;
        try
        {
            result = await _runner.RunAsync(argv, stdin: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to start '{keygenBinary}' for ephemeral sandbox key generation: {ex.Message}", ex);
        }
        if (result.StartFailed)
        {
            throw new InvalidOperationException(
                $"Failed to start '{keygenBinary}' for ephemeral sandbox key generation.");
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{keygenBinary}' exited {result.ExitCode} generating an ephemeral sandbox key: {Tail(result.Stderr)}");
        }
    }

    private static string UniqueKeyPath(string directory, string prefix) =>
        Path.Combine(directory, prefix + "-ed25519-" + Guid.NewGuid().ToString("N"));

    private static async Task<string> ReadPublicKeyAsync(string path, CancellationToken ct)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Generated public key at '{path}' could not be read.", ex);
        }
        var key = text.Trim();
        if (!key.StartsWith("ssh-ed25519 ", StringComparison.Ordinal))
            throw new InvalidOperationException($"Generated public key at '{path}' is not an OpenSSH ed25519 key.");
        return key;
    }

    private static string Tail(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "(no stderr)";
        var trimmed = text.Trim();
        return trimmed.Length <= 240 ? trimmed : trimmed[^240..];
    }
}

/// <summary>Resolves hostnames to IPs for best-effort egress rules (test seam over <see cref="Dns"/>).</summary>
public interface IHetznerDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Production <see cref="IHetznerDnsResolver"/> over the platform DNS stack.</summary>
public sealed class SystemHetznerDnsResolver : IHetznerDnsResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
}

/// <summary>Builds the per-sandbox SSH data-plane transport (test seam).</summary>
public interface IHetznerTransportFactory
{
    IRemoteHostTransport Create(HetznerSshTransportSpec spec);
}

/// <summary>Parameters for one sandbox's SSH transport.</summary>
public sealed record HetznerSshTransportSpec(
    string SshTarget,
    int SshPort,
    string PrivateKeyPath,
    string KnownHostsPath,
    string SshBinary,
    int ConnectTimeoutSeconds);

/// <summary>
/// Production transport factory reusing the shared <see cref="OpenSshCliTransport"/>
/// seam (no copy): strict host keys (<c>StrictHostKeyChecking=yes</c> against a
/// per-sandbox pinned known_hosts file; the global known_hosts is ignored via
/// <c>GlobalKnownHostsFile=/dev/null</c> so a sandbox can neither read nor
/// pollute host trust state). <c>accept-new</c> never appears: a host-key
/// mismatch fails the connection instead of trusting on sight.
/// </summary>
public sealed class OpenSshHetznerTransportFactory(
    IProcessRunner runner,
    ILoggerFactory? loggerFactory = null) : IHetznerTransportFactory
{
    private readonly IProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public IRemoteHostTransport Create(HetznerSshTransportSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.SshTarget))
            throw new ArgumentException("SSH target must not be blank.", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.PrivateKeyPath))
            throw new ArgumentException("Private key path must not be blank.", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.KnownHostsPath))
            throw new ArgumentException("Known-hosts path must not be blank.", nameof(spec));

        var options = new MultipassRemoteSandboxOptions
        {
            SshTarget = spec.SshTarget,
            SshBinary = string.IsNullOrWhiteSpace(spec.SshBinary) ? "ssh" : spec.SshBinary,
            SshKeyPath = spec.PrivateKeyPath,
            SshPort = spec.SshPort == 22 ? null : spec.SshPort,
            ConnectTimeoutSeconds = Math.Max(1, spec.ConnectTimeoutSeconds),
            AcceptUnknownHostKeys = false,
            // The Validation below requires the pool fields even though the
            // SSH data path never touches them; "unused" says so honestly.
            RemoteMultipassPath = "unused",
            RemoteStagingRoot = "unused",
            ExtraSshOptions =
            [
                $"UserKnownHostsFile={spec.KnownHostsPath}",
                "GlobalKnownHostsFile=/dev/null",
            ],
        };
        return new OpenSshCliTransport(
            () => options,
            _runner,
            _loggerFactory.CreateLogger<OpenSshCliTransport>());
    }
}
