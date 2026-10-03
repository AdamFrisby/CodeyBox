using System.Collections.Concurrent;
using System.Globalization;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Bakes OpenStack baseline Glance images from the provider-neutral toolchain
/// inputs (provisioning commands, staged executables, verification probes).
/// Flow: boot a builder server from the configured base cloud image, stage and
/// install executables plus run provisioning over SSH, run the verification
/// probes, power the builder off, and snapshot it to a Glance image named and
/// tagged with the shared toolchain hash. Single-flight per hash inside the
/// process; a bounded bake timeout aborts stuck builds; a failed build leaves
/// no half image behind and always deletes the builder server.
/// </summary>
internal sealed class OpenStackBaselineBuilder
{
    internal const string BuilderNameInfix = "bake-";
    internal const string StageDirectory = "/tmp/codeybox-baseline-stage";
    internal const string BaselineMetadataKey = "codeybox.baseline";

    private readonly OpenStackSandboxOptions _options;
    private readonly OpenStackCredentials _credentials;
    private readonly OpenStackApiClient _api;
    private readonly IOpenStackKeyGenerator _keys;
    private readonly IOpenStackTransportFactory _transports;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    internal OpenStackBaselineBuilder(
        OpenStackSandboxOptions options,
        OpenStackCredentials credentials,
        OpenStackApiClient api,
        IOpenStackKeyGenerator keys,
        IOpenStackTransportFactory transports,
        Func<string, string?> environment,
        TimeProvider clock,
        ILogger log)
    {
        _options = options;
        _credentials = credentials;
        _api = api;
        _keys = keys;
        _transports = transports;
        _environment = environment;
        _clock = clock;
        _log = log;
    }

    /// <summary>Computes the live scoped pin without building anything.</summary>
    internal string ResolveBaselineRef()
    {
        var plan = Plan();
        return OpenStackBaselineNaming.FormatScopedPin(plan.ShortHash, plan.ImageName);
    }

    /// <summary>
    /// Ensures the baked image for <paramref name="pinnedRef"/> (or the live
    /// toolchain when unpinned) exists and returns its scoped pin. A scoped pin
    /// whose toolchain hash differs from live is stale and refused; a legacy
    /// bare image ref resolves directly by id or exact name.
    /// </summary>
    internal async Task<string?> EnsureBaselineImageAsync(
        string? pinnedRef,
        ConcurrentDictionary<string, SemaphoreSlim> buildLocks,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buildLocks);
        if (!_options.UseBaselineImages)
            return null;
        var baselineErrors = _options.ValidateBaseline();
        if (baselineErrors.Count > 0)
            throw new InvalidOperationException(
                "OpenStack baseline options are invalid: " + string.Join("; ", baselineErrors));

        if (!string.IsNullOrWhiteSpace(pinnedRef)
            && BaselinePin.TryParseScopedPin(pinnedRef, out _, out var pinHash, out _))
        {
            var live = Plan();
            if (!string.Equals(pinHash, live.ShortHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pinned OpenStack baseline '{pinnedRef}' names toolchain tc-{pinHash}, " +
                    $"but the live configuration resolves tc-{live.ShortHash}; " +
                    "refusing to bake current configuration under a stale ref.");
            }
            var image = await EnsureImageForPlanAsync(live, buildLocks, ct).ConfigureAwait(false);
            return OpenStackBaselineNaming.FormatScopedPin(live.ShortHash, image.Name!);
        }

        if (!string.IsNullOrWhiteSpace(pinnedRef))
        {
            var image = await ResolveLegacyImageAsync(pinnedRef.Trim(), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Pinned OpenStack baseline '{pinnedRef}' was not found by id or exact image name.");
            var hash = OpenStackBaselineNaming.TryExtractHashFromName(image.Name, _options.BaselineImagePrefix)
                ?? OpenStackBaselineNaming.TryExtractHashFromTags(image.Tags)
                ?? throw new InvalidOperationException(
                    $"Pinned OpenStack baseline '{pinnedRef}' is not a codeybox baseline image.");
            return OpenStackBaselineNaming.FormatScopedPin(hash, image.Name!);
        }

        var livePlan = Plan();
        var ensured = await EnsureImageForPlanAsync(livePlan, buildLocks, ct).ConfigureAwait(false);
        return OpenStackBaselineNaming.FormatScopedPin(livePlan.ShortHash, ensured.Name!);
    }

    // ------------------------------------------------------------------
    // Plan (pure-ish: reads bounded host files for fingerprints)
    // ------------------------------------------------------------------

    internal sealed record BaselinePlan(
        BaselineToolchainInputs Inputs,
        string FullHash,
        string ShortHash,
        string ImageName,
        IReadOnlyList<string> Fingerprints);

    internal BaselinePlan Plan()
    {
        var fingerprints = FingerprintExecutables();
        var executables = new BaselineToolchainExecutable[_options.ExecutableProvisions.Count];
        for (var i = 0; i < _options.ExecutableProvisions.Count; i++)
        {
            var provision = _options.ExecutableProvisions[i];
            executables[i] = new BaselineToolchainExecutable(
                provision.VmDestPath,
                provision.VmSymlinks,
                provision.Label,
                fingerprints[i]);
        }
        var inputs = new BaselineToolchainInputs(
            _options.ExtraRuncmd,
            executables,
            _options.BaselineVerificationCommands);
        var fullHash = BaselineContentHash.ComputeToolchainHash(inputs);
        var shortHash = BaselineContentHash.ToShortHash(fullHash);
        return new BaselinePlan(
            inputs,
            fullHash,
            shortHash,
            OpenStackBaselineNaming.DeriveImageName(_options.BaselineImagePrefix, shortHash),
            fingerprints);
    }

    private IReadOnlyList<string> FingerprintExecutables()
    {
        var result = new List<string>(_options.ExecutableProvisions.Count);
        var aggregateBytes = 0L;
        for (var i = 0; i < _options.ExecutableProvisions.Count; i++)
        {
            var provision = _options.ExecutableProvisions[i]
                ?? throw new InvalidOperationException(
                    $"OpenStack baseline ExecutableProvisions[{i}] cannot be null.");
            if (string.IsNullOrWhiteSpace(provision.HostSourcePath))
                throw new InvalidOperationException(
                    $"OpenStack baseline ExecutableProvisions[{i}].HostSourcePath cannot be blank.");
            if (string.IsNullOrWhiteSpace(provision.VmDestPath))
                throw new InvalidOperationException(
                    $"OpenStack baseline ExecutableProvisions[{i}].VmDestPath cannot be blank.");
            var hostPath = ExpandHostSourcePath(provision.HostSourcePath);
            string fingerprint;
            try
            {
                fingerprint = BaselineContentHash.HashHostFile(
                    hostPath,
                    _options.MaxExecutableProvisionBytes,
                    _options.MaxAggregateExecutableProvisionBytes,
                    ref aggregateBytes);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"OpenStack baseline ExecutableProvisions[{i}]: cannot read host file '{hostPath}'.", ex);
            }
            result.Add(fingerprint);
        }
        return result;
    }

    private string ExpandHostSourcePath(string configuredPath)
    {
        if (string.Equals(configuredPath, "~", StringComparison.Ordinal)
            || configuredPath.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = _environment("HOME");
            if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home) || home.Length > 4096)
                throw new InvalidOperationException(
                    "HOME must be a bounded absolute path to expand an OpenStack baseline provision source.");
            return configuredPath.Length == 1
                ? home
                : Path.Combine(home, configuredPath[2..]);
        }
        if (configuredPath.StartsWith('~'))
            throw new InvalidOperationException(
                "OpenStack baseline provisioning supports only '~' and '~/' HOME expansion.");
        return configuredPath;
    }

    // ------------------------------------------------------------------
    // Ensure (single-flight, bounded, self-cleaning)
    // ------------------------------------------------------------------

    private async Task<OpenStackImage> EnsureImageForPlanAsync(
        BaselinePlan plan,
        ConcurrentDictionary<string, SemaphoreSlim> buildLocks,
        CancellationToken ct)
    {
        var gate = buildLocks.GetOrAdd(plan.ShortHash, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await FindImageByHashAsync(plan.ShortHash, plan.ImageName, ct).ConfigureAwait(false);
            if (existing?.Id is not null && string.Equals(existing.Status, "active", StringComparison.OrdinalIgnoreCase))
                return existing;
            if (existing?.Id is not null && IsSettledImageStatus(existing.Status))
            {
                // Terminal garbage under our content-addressed name (a killed
                // bake): remove it so the rebuild below starts clean.
                await DeleteImageQuietlyAsync(existing.Id, ct).ConfigureAwait(false);
            }
            else if (existing?.Id is not null)
            {
                // Another builder (possibly another host) is mid-bake under the
                // same content address: wait for it rather than racing it.
                var settled = await WaitForImageActiveAsync(existing.Id, ct).ConfigureAwait(false);
                if (settled is not null)
                    return settled;
                throw new SandboxProvisioningDeferredException(
                    OpenStackSandboxOptions.ProviderKind, "baseline-bake", "bake-in-progress",
                    $"OpenStack baseline image '{plan.ImageName}' never settled while another builder baked it",
                    TimeSpan.FromSeconds(_options.ProvisioningRecheckSeconds));
            }
            return await BakeAsync(plan, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsSettledImageStatus(string? status) =>
        status is not null
        && (status.Equals("killed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("deleted", StringComparison.OrdinalIgnoreCase)
            || status.Equals("pending_delete", StringComparison.OrdinalIgnoreCase)
            || status.Equals("deactivated", StringComparison.OrdinalIgnoreCase));

    internal async Task<OpenStackImage?> FindImageByHashAsync(
        string shortHash, string imageName, CancellationToken ct)
    {
        var byName = await _api.ListImagesAsync(_credentials, imageName, tag: null, ct).ConfigureAwait(false);
        var exact = byName
            .Where(image => string.Equals(image.Name, imageName, StringComparison.Ordinal) && image.Id is not null)
            .ToList();
        if (exact.Count == 1)
            return exact[0];
        if (exact.Count > 1)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "find baseline image",
                $"baseline image name '{imageName}' is ambiguous ({exact.Count} matches)");
        }
        var tagged = await _api.ListImagesAsync(
            _credentials, name: null, OpenStackBaselineNaming.ToolchainTag(shortHash), ct).ConfigureAwait(false);
        return tagged.FirstOrDefault(image =>
            image.Id is not null
            && string.Equals(
                OpenStackBaselineNaming.TryExtractHashFromTags(image.Tags), shortHash, StringComparison.Ordinal));
    }

    private async Task<OpenStackImage?> ResolveLegacyImageAsync(string reference, CancellationToken ct)
    {
        var byId = await _api.GetImageAsync(_credentials, reference, ct).ConfigureAwait(false);
        if (byId?.Id is not null)
            return byId;
        var matches = await _api.ListImagesAsync(_credentials, reference, tag: null, ct).ConfigureAwait(false);
        return matches.FirstOrDefault(image =>
            image.Id is not null && string.Equals(image.Name, reference, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Bake
    // ------------------------------------------------------------------

    private async Task<OpenStackImage> BakeAsync(BaselinePlan plan, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.BaselineBakeTimeoutSeconds));
        var bakeCt = timeoutCts.Token;

        var flavor = await _api.GetFlavorByNameAsync(_credentials, _options.FlavorName, bakeCt).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"OpenStack flavor '{_options.FlavorName}' not found for baseline bake.");
        var baseImageRef = string.IsNullOrWhiteSpace(_options.BaselineBaseImageName)
            ? _options.ImageName
            : _options.BaselineBaseImageName.Trim();
        var baseImageId = await ResolveBaseImageIdAsync(baseImageRef, bakeCt).ConfigureAwait(false);

        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var serverName = _options.ServerNamePrefix + BuilderNameInfix + suffix;
        var keypairName = _options.KeypairNamePrefix + BuilderNameInfix + suffix;
        var securityGroupName = _options.SecurityGroupNamePrefix + BuilderNameInfix + suffix;
        var ownerId = SlugifyOwner(string.IsNullOrWhiteSpace(_options.OwnerId) ? Environment.MachineName : _options.OwnerId);
        var sshTempDirectory = Path.Combine(Path.GetTempPath(), "codeybox-openstack-" + BuilderNameInfix + suffix);
        Directory.CreateDirectory(sshTempDirectory);

        string? serverId = null;
        string? securityGroupId = null;
        string? imageId = null;
        try
        {
            var clientKey = await _keys.GenerateClientKeyAsync(
                _options.SshKeygenBinary, sshTempDirectory, serverName, bakeCt).ConfigureAwait(false);
            var hostKey = await _keys.GenerateHostKeyAsync(
                _options.SshKeygenBinary, sshTempDirectory, serverName + "-host", bakeCt).ConfigureAwait(false);
            await _api.CreateKeypairAsync(_credentials, keypairName, clientKey.PublicKeyText, bakeCt)
                .ConfigureAwait(false);

            await _api.CreateSecurityGroupAsync(
                _credentials, securityGroupName, $"codeybox baseline builder {serverName}", bakeCt)
                .ConfigureAwait(false);
            var createdGroup = (await _api.ListSecurityGroupsAsync(
                _credentials, securityGroupName, bakeCt).ConfigureAwait(false))
                .FirstOrDefault(g => string.Equals(g.Name, securityGroupName, StringComparison.Ordinal));
            securityGroupId = createdGroup?.Id
                ?? throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "create builder security group", "empty id");
            foreach (var rule in BuildBuilderRules(securityGroupId))
                await _api.CreateSecurityGroupRuleAsync(_credentials, rule, bakeCt).ConfigureAwait(false);

            var userData = OpenStackCloudInit.Build(new OpenStackCloudInitSpec(
                serverName, _options.SshUser, clientKey.PublicKeyText,
                hostKey.PrivateKeyPem, hostKey.PublicKeyText, []));
            var created = await _api.CreateServerAsync(_credentials, new OpenStackServerSpec(
                serverName, flavor.Id!, baseImageId, [_options.NetworkId],
                KeyName: keypairName, UserData: userData,
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [OpenStackSandboxProvider.MetadataManagedKey] = "true",
                    [OpenStackSandboxProvider.MetadataOwnerKey] = ownerId,
                    [OpenStackSandboxProvider.MetadataCreatedKey] = _clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture),
                    [BaselineMetadataKey] = plan.ShortHash,
                },
                Tags: [OpenStackSandboxProvider.ManagedTag],
                SecurityGroupNames: [securityGroupName]), bakeCt).ConfigureAwait(false);
            serverId = created.Id
                ?? throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "create builder server", "empty id");

            await _api.WaitForServerStatusAsync(
                _credentials, serverId, ["ACTIVE"], bakeCt,
                TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds)).ConfigureAwait(false);
            var address = await WaitForFixedAddressAsync(serverId, bakeCt).ConfigureAwait(false);

            var knownHostsPath = Path.Combine(sshTempDirectory, "known_hosts");
            await WriteKnownHostsAsync(knownHostsPath, address, hostKey.PublicKeyText).ConfigureAwait(false);
            var transport = _transports.Create(new OpenStackSshTransportSpec(
                $"{_options.SshUser}@{address}", _options.SshPort, clientKey.PrivateKeyPath,
                knownHostsPath, _options.SshBinary, _options.SshConnectTimeoutSeconds));
            await WaitForSshReadyAsync(transport, bakeCt).ConfigureAwait(false);

            await ProvisionAsync(transport, plan, bakeCt).ConfigureAwait(false);

            await _api.StopServerAsync(_credentials, serverId, bakeCt).ConfigureAwait(false);
            await _api.WaitForServerStatusAsync(
                _credentials, serverId, ["SHUTOFF"], bakeCt,
                TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds)).ConfigureAwait(false);

            imageId = await _api.SnapshotServerAsync(
                _credentials, serverId, plan.ImageName,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["codeybox_tc_hash"] = plan.ShortHash,
                },
                bakeCt).ConfigureAwait(false);
            var settled = await WaitForImageActiveAsync(imageId, bakeCt).ConfigureAwait(false)
                ?? throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "snapshot builder",
                    $"baseline image '{plan.ImageName}' never reached active");
            _log.LogInformation(
                "Baked OpenStack baseline image {Image} (tc-{Hash}) from builder {Server}",
                plan.ImageName, plan.ShortHash, serverName);
            return settled;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new SandboxProvisioningDeferredException(
                OpenStackSandboxOptions.ProviderKind, "baseline-bake", "bake-timeout",
                $"OpenStack baseline bake for '{plan.ImageName}' exceeded {_options.BaselineBakeTimeoutSeconds}s",
                TimeSpan.FromSeconds(_options.ProvisioningRecheckSeconds));
        }
        finally
        {
            // A failed build leaves no half image and deletes the builder.
            if (imageId is not null && !await ImageIsActiveAsync(imageId).ConfigureAwait(false))
                await DeleteImageQuietlyAsync(imageId, CancellationToken.None).ConfigureAwait(false);
            await DeleteBuilderResourcesAsync(serverId, keypairName, securityGroupId).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(sshTempDirectory))
                    Directory.Delete(sshTempDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Baseline bake {Name}: failed to remove SSH key directory", serverName);
            }
        }
    }

    private IReadOnlyList<OpenStackSecurityGroupRuleSpec> BuildBuilderRules(string securityGroupId)
    {
        var rules = new List<OpenStackSecurityGroupRuleSpec>();
        foreach (var cidr in _options.OrchestratorSshCidrs)
        {
            var prefix = OpenStackSecurityGroupPolicy.NormalizeCidr(cidr, nameof(_options.OrchestratorSshCidrs));
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "ingress", prefix.Contains(':') ? "IPv6" : "IPv4",
                "tcp", OpenStackSecurityGroupPolicy.SshPort, OpenStackSecurityGroupPolicy.SshPort, prefix));
        }
        if (_options.BaselineBuilderOpenEgress)
        {
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "egress", "IPv4", null, null, null, "0.0.0.0/0"));
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "egress", "IPv6", null, null, null, "::/0"));
        }
        else
        {
            rules.AddRange(OpenStackSecurityGroupPolicy.BuildRules(
                securityGroupId, [], [], _options.DnsServerIps, _options.NtpServerIps, _options.MaxEgressRules));
        }
        foreach (var rule in rules)
            rule.Validate();
        return rules;
    }

    private async Task<string> ResolveBaseImageIdAsync(string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidOperationException(
                "No OpenStack baseline base image configured: set " +
                $"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:BaselineBaseImageName or ImageName.");
        }
        var byId = await _api.GetImageAsync(_credentials, reference.Trim(), ct).ConfigureAwait(false);
        if (byId?.Id is not null)
            return byId.Id;
        var matches = await _api.ListImagesAsync(_credentials, reference.Trim(), tag: null, ct).ConfigureAwait(false);
        var exact = matches
            .Where(image => string.Equals(image.Name, reference.Trim(), StringComparison.Ordinal) && image.Id is not null)
            .ToList();
        return exact.Count switch
        {
            1 => exact[0].Id!,
            0 => throw new InvalidOperationException(
                $"OpenStack baseline base image '{reference}' not found by id or exact name."),
            _ => throw new InvalidOperationException(
                $"OpenStack baseline base image name '{reference}' is ambiguous ({exact.Count} matches); use an image id."),
        };
    }

    // ------------------------------------------------------------------
    // Provisioning over SSH
    // ------------------------------------------------------------------

    private async Task ProvisionAsync(
        IRemoteHostTransport transport, BaselinePlan plan, CancellationToken ct)
    {
        for (var i = 0; i < plan.Fingerprints.Count; i++)
        {
            var provision = _options.ExecutableProvisions[i];
            var hostPath = ExpandHostSourcePath(provision.HostSourcePath);
            var remoteStaged = $"{StageDirectory}/{i}";
            try
            {
                await transport.StageInAsync(hostPath, remoteStaged, ct).ConfigureAwait(false);
            }
            catch (RemoteSshTransportException ex)
            {
                throw new InvalidOperationException(
                    $"Baseline bake: cannot stage host file '{hostPath}' for '{provision.VmDestPath}'.", ex);
            }
        }

        var install = new System.Text.StringBuilder("set -euo pipefail\n");
        for (var i = 0; i < _options.ExecutableProvisions.Count; i++)
        {
            var provision = _options.ExecutableProvisions[i];
            var dest = ValidateGuestPath(provision.VmDestPath, $"ExecutableProvisions[{i}].VmDestPath");
            var parent = ParentOf(dest);
            install.Append("mkdir -p -- ").Append(Quote(parent)).Append('\n');
            install.Append("cp -- ").Append(Quote($"{StageDirectory}/{i}")).Append(' ').Append(Quote(dest)).Append('\n');
            install.Append("chmod 0755 -- ").Append(Quote(dest)).Append('\n');
            foreach (var link in provision.VmSymlinks)
            {
                var validated = ValidateGuestPath(link, $"ExecutableProvisions[{i}].VmSymlinks");
                install.Append("mkdir -p -- ").Append(Quote(ParentOf(validated))).Append('\n');
                install.Append("ln -sfn -- ").Append(Quote(dest)).Append(' ').Append(Quote(validated)).Append('\n');
            }
        }
        var installRun = await transport.RunAsync(
            ["sudo", "bash", "-c", install.ToString()], stdin: null, ct,
            maxStdoutBytes: 1_048_576, maxStderrBytes: 1_048_576, killOnOutputLimit: false)
            .ConfigureAwait(false);
        if (installRun.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Baseline bake: executable install failed " +
                $"(exit {installRun.ExitCode}): {Tail(installRun.Stderr)}{Tail(installRun.Stdout)}");
        }

        for (var i = 0; i < _options.ExtraRuncmd.Count; i++)
        {
            var command = _options.ExtraRuncmd[i];
            ProcessRunResult run;
            try
            {
                run = await transport.RunAsync(
                    ["sudo", "bash", "-c", command], stdin: null, ct,
                    maxStdoutBytes: 1_048_576, maxStderrBytes: 1_048_576, killOnOutputLimit: false)
                    .ConfigureAwait(false);
            }
            catch (RemoteSshTransportException ex)
            {
                throw new InvalidOperationException(
                    $"Baseline bake: provisioning command {i} failed to execute.", ex);
            }
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Baseline bake: provisioning command {i} failed (exit {run.ExitCode}): {Tail(run.Stderr)}{Tail(run.Stdout)}");
            }
        }

        for (var i = 0; i < _options.BaselineVerificationCommands.Count; i++)
        {
            var command = _options.BaselineVerificationCommands[i];
            ProcessRunResult run;
            try
            {
                run = await transport.RunAsync(
                    command.Argv, stdin: null, ct,
                    maxStdoutBytes: 1_048_576, maxStderrBytes: 1_048_576, killOnOutputLimit: false)
                    .ConfigureAwait(false);
            }
            catch (RemoteSshTransportException ex)
            {
                throw new InvalidOperationException(
                    $"Baseline bake: verification '{command.Label}' failed to execute.", ex);
            }
            if (run.ExitCode != 0)
            {
                var hint = string.IsNullOrWhiteSpace(command.FailureHint) ? string.Empty : $" ({command.FailureHint})";
                throw new InvalidOperationException(
                    $"Baseline bake: verification '{command.Label}' failed (exit {run.ExitCode}){hint}: " +
                    $"{Tail(run.Stderr)}{Tail(run.Stdout)}");
            }
        }

        var cleanup = await transport.RunAsync(
            ["sudo", "rm", "-rf", "--", StageDirectory], stdin: null, ct).ConfigureAwait(false);
        if (cleanup.ExitCode != 0)
            _log.LogWarning("Baseline bake: failed to remove stage directory {Dir}", StageDirectory);
    }

    internal static string ValidateGuestPath(string path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            throw new InvalidOperationException($"{fieldName} must be an absolute guest path.");
        if (path.Length > 4096)
            throw new InvalidOperationException($"{fieldName} exceeds the 4096-character guest path bound.");
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "." || segment == "..")
                throw new InvalidOperationException($"{fieldName} must not contain '.' or '..' segments.");
        }
        if (path.Any(static c => char.IsControl(c)))
            throw new InvalidOperationException($"{fieldName} must not contain control characters.");
        return path;
    }

    private static string ParentOf(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "''";
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string Tail(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return string.Empty;
        var trimmed = output.Trim();
        return trimmed.Length <= 4000 ? trimmed : trimmed[^4000..];
    }

    private static string SlugifyOwner(string raw)
    {
        var slug = new string(raw.Trim().ToLowerInvariant().Select(ch =>
            (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length > 32)
            slug = slug[..32].TrimEnd('-');
        return string.IsNullOrEmpty(slug) ? "host" : slug;
    }

    // ------------------------------------------------------------------
    // Waits
    // ------------------------------------------------------------------

    private async Task<string> WaitForFixedAddressAsync(string serverId, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var ports = await _api.ListPortsByDeviceAsync(_credentials, serverId, ct).ConfigureAwait(false);
            foreach (var port in ports)
            {
                var fixedIp = port.FixedIps?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.IpAddress));
                if (fixedIp?.IpAddress is not null)
                    return fixedIp.IpAddress.Trim();
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "resolve builder address",
                    $"builder server '{serverId}' exposed no fixed IP in time");
            }
            await Task.Delay(PollDelay(attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForSshReadyAsync(IRemoteHostTransport transport, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(_options.SshReadyTimeoutSeconds);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.SshConnectTimeoutSeconds * 2)));
                var run = await transport.RunAsync(["true"], stdin: null, attemptCts.Token).ConfigureAwait(false);
                if (run.ExitCode == 0)
                    return;
            }
            catch (RemoteSshTransportException)
            {
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "builder ssh-ready",
                    "builder server did not accept SSH before the readiness deadline");
            }
            await Task.Delay(PollDelay(attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private async Task<OpenStackImage?> WaitForImageActiveAsync(string imageId, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var image = await _api.GetImageAsync(_credentials, imageId, ct).ConfigureAwait(false);
            if (image is null)
                return null;
            if (string.Equals(image.Status, "active", StringComparison.OrdinalIgnoreCase))
                return image;
            if (IsSettledImageStatus(image.Status)
                || string.Equals(image.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "wait baseline image",
                    $"baseline image '{imageId}' settled failed with status '{image.Status}'");
            }
            if (_clock.GetUtcNow() >= deadline)
                return null;
            await Task.Delay(PollDelay(attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ImageIsActiveAsync(string imageId)
    {
        try
        {
            var image = await _api.GetImageAsync(
                _credentials, imageId, CancellationToken.None).ConfigureAwait(false);
            return image is not null
                && string.Equals(image.Status, "active", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private TimeSpan PollDelay(int attempt)
    {
        var baseMs = (double)Math.Max(200, _options.PollIntervalMilliseconds);
        var doubled = baseMs * (1L << Math.Min(attempt, 10));
        var capped = Math.Min(doubled, _options.MaxPollIntervalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(capped, 1));
    }

    private static async Task WriteKnownHostsAsync(string knownHostsPath, string address, string hostPublicKey)
    {
        var line = address.Trim() + " " + hostPublicKey.Trim() + "\n";
        await File.WriteAllTextAsync(knownHostsPath, line, CancellationToken.None).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(knownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task DeleteImageQuietlyAsync(string imageId, CancellationToken ct)
    {
        try
        {
            await _api.DeleteImageAsync(_credentials, imageId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Baseline bake: failed to delete half image {ImageId}", imageId);
        }
    }

    private async Task DeleteBuilderResourcesAsync(
        string? serverId, string keypairName, string? securityGroupId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(_options.ReadyTimeoutSeconds, 30, 3600)));
        var ct = cts.Token;
        if (serverId is not null)
        {
            try { await _api.DeleteServerAsync(_credentials, serverId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Baseline bake: failed to delete builder server {ServerId}", serverId);
            }
            try
            {
                await _api.WaitForServerDeletedAsync(
                    _credentials, serverId, ct,
                    TimeSpan.FromSeconds(Math.Min(120, _options.ReadyTimeoutSeconds))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Baseline bake: builder server {ServerId} deletion unconfirmed", serverId);
            }
        }
        if (securityGroupId is not null)
        {
            try { await _api.DeleteSecurityGroupAsync(_credentials, securityGroupId, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Baseline bake: failed to delete builder security group {SecurityGroupId}", securityGroupId);
            }
        }
        if (!string.IsNullOrWhiteSpace(keypairName))
        {
            try { await _api.DeleteKeypairAsync(_credentials, keypairName, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Baseline bake: failed to delete builder keypair {Keypair}", keypairName);
            }
        }
    }
}
