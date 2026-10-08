using System.Net;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.GceSandboxPlugin;

/// <summary>Ephemeral per-sandbox client key material.</summary>
public sealed record GceClientKeyMaterial(string PrivateKeyPath, string PublicKeyText);

/// <summary>Guest host key pair generated on the orchestrator host and pinned via known_hosts.</summary>
public sealed record GceHostKeyMaterial(string PrivateKeyPem, string PublicKeyText);

/// <summary>Test seam for per-sandbox SSH key generation (ssh-keygen in production).</summary>
public interface IGceKeyGenerator
{
    Task<GceClientKeyMaterial> GenerateClientKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct);

    Task<GceHostKeyMaterial> GenerateHostKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct);
}

/// <summary>
/// <see cref="IGceKeyGenerator"/> backed by <c>ssh-keygen</c>. No key material is logged;
/// paths stay inside the per-sandbox temp directory the sandbox handle wipes on disposal.
/// </summary>
public sealed class SshKeygenGceGenerator(IProcessRunner runner) : IGceKeyGenerator
{
    private readonly IProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<GceClientKeyMaterial> GenerateClientKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keygenBinary);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var privatePath = UniqueKeyPath(directory, "id");
        await RunKeygenAsync(keygenBinary, privatePath, comment, ct).ConfigureAwait(false);
        var publicText = await ReadPublicKeyAsync(privatePath + ".pub", ct).ConfigureAwait(false);
        return new GceClientKeyMaterial(privatePath, publicText);
    }

    public async Task<GceHostKeyMaterial> GenerateHostKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keygenBinary);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var privatePath = UniqueKeyPath(directory, "host");
        await RunKeygenAsync(keygenBinary, privatePath, comment, ct).ConfigureAwait(false);
        var publicText = await ReadPublicKeyAsync(privatePath + ".pub", ct).ConfigureAwait(false);
        var privatePem = await File.ReadAllTextAsync(privatePath, ct).ConfigureAwait(false);
        return new GceHostKeyMaterial(privatePem, publicText);
    }

    private async Task RunKeygenAsync(string keygenBinary, string privatePath, string comment, CancellationToken ct)
    {
        IReadOnlyList<string> argv = [keygenBinary, "-t", "ed25519", "-f", privatePath, "-N", "", "-C", comment ?? string.Empty];
        CodeyBox.HostProcess.ProcessRunResult result;
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
            throw new InvalidOperationException($"'{keygenBinary}' exited {result.ExitCode} generating an ephemeral sandbox key.");
    }

    private static string UniqueKeyPath(string directory, string prefix)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, prefix + "-ed25519-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
    }

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
}

/// <summary>Test seam for resolving orchestrator-declared hostnames to egress IPs.</summary>
public interface IGceDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Production <see cref="IGceDnsResolver"/> over the platform DNS stack.</summary>
public sealed class SystemGceDnsResolver : IGceDnsResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
    }
}

/// <summary>Test seam for building one sandbox's SSH transport.</summary>
public interface IGceTransportFactory
{
    IRemoteHostTransport Create(GceSshTransportSpec spec);
}

/// <summary>Parameters for one sandbox's SSH transport.</summary>
public sealed record GceSshTransportSpec(
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
/// pollute host trust state). Unknown keys are never accepted.
/// </summary>
public sealed class OpenSshGceTransportFactory(
    IProcessRunner runner,
    ILoggerFactory? loggerFactory = null) : IGceTransportFactory
{
    private readonly IProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public IRemoteHostTransport Create(GceSshTransportSpec spec)
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
