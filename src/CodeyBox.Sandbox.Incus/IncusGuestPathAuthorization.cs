namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Single live-guest authorization check shared by initial provisioning and
/// interrupted-exec recovery. Host devices must be absent while this check
/// runs so a guest alias cannot redirect root operations into host storage.
/// </summary>
internal static class IncusGuestPathAuthorization
{
    internal static async Task ValidateCanonicalProvisioningPathsAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<string> mountGuestPaths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mountGuestPaths);
        var canonicalMounts = await ResolveCanonicalMountPathsAsync(
            cli,
            options,
            name,
            mountGuestPaths,
            ct).ConfigureAwait(false);

        foreach (var target in SnapshotProvisioningTargets(options))
        {
            EnsureProvisioningDestinationAllowed(target.Path);
            var canonical = await ResolveCanonicalGuestPathAsync(
                cli,
                options,
                name,
                target.Path,
                ct).ConfigureAwait(false);
            if (!string.Equals(canonical, target.Path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{target.Name} resolves through a guest filesystem alias; " +
                    "provisioning destinations must be canonical paths.");
            }
            EnsureProvisioningDestinationAllowed(canonical);
            if (canonicalMounts.Any(mount => IncusGuestPaths.Overlap(canonical, mount)))
            {
                throw new InvalidOperationException(
                    $"{target.Name} resolves into a sandbox mount; refusing root provisioning writes.");
            }
        }
    }

    /// <summary>
    /// Reauthorizes only mount/device targets after provisioning. Executable
    /// symlink destinations are intentional aliases by this point and are
    /// verified separately as exact link/target pairs.
    /// </summary>
    internal static async Task ValidateCanonicalMountPathsAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<string> mountGuestPaths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mountGuestPaths);
        _ = await ResolveCanonicalMountPathsAsync(
            cli,
            options,
            name,
            mountGuestPaths,
            ct).ConfigureAwait(false);
    }

    internal static async Task ValidateCanonicalParentAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        string guestPath,
        CancellationToken ct)
    {
        IncusInputValidation.ValidateAbsoluteGuestPath(guestPath, nameof(guestPath));
        var separator = guestPath.LastIndexOf('/');
        var parent = separator <= 0 ? "/" : guestPath[..separator];
        var canonical = await ResolveCanonicalGuestPathAsync(
            cli,
            options,
            name,
            parent,
            ct).ConfigureAwait(false);
        if (!string.Equals(canonical, parent, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Guest path parent '{parent}' resolves through an unauthorized filesystem alias.");
        }
    }

    internal static IReadOnlyList<(string Path, string Name)> SnapshotProvisioningTargets(
        IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targets = new List<(string Path, string Name)>(
            options.PackageCacheSeeds.Count
            + options.ExecutableProvisions.Count * 2);
        for (var i = 0; i < options.PackageCacheSeeds.Count; i++)
        {
            targets.Add((
                options.PackageCacheSeeds[i].VmDestPath,
                $"PackageCacheSeeds[{i}].VmDestPath"));
        }
        for (var i = 0; i < options.ExecutableProvisions.Count; i++)
        {
            var provision = options.ExecutableProvisions[i];
            targets.Add((
                provision.VmDestPath,
                $"ExecutableProvisions[{i}].VmDestPath"));
            for (var linkIndex = 0; linkIndex < provision.VmSymlinks.Count; linkIndex++)
            {
                targets.Add((
                    provision.VmSymlinks[linkIndex],
                    $"ExecutableProvisions[{i}].VmSymlinks[{linkIndex}]"));
            }
        }
        return targets;
    }

    internal static void EnsureProvisioningDestinationAllowed(string guestPath)
    {
        IncusInputValidation.ValidateAbsoluteGuestPath(guestPath, nameof(guestPath));
        if (guestPath == "/"
            || IncusCloudInit.OverlapsProviderOwnedPath(guestPath)
            || IncusGuestPaths.IsVolatileOrPseudoFilesystemPath(guestPath))
        {
            throw new InvalidOperationException(
                "Incus provisioning destinations cannot be root, volatile/pseudo filesystems, " +
                "or provider-owned guest control paths.");
        }
    }

    private static async Task<IReadOnlyList<string>> ResolveCanonicalMountPathsAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<string> mountGuestPaths,
        CancellationToken ct)
    {
        var canonicalMounts = new List<string>(mountGuestPaths.Count);
        foreach (var mountPath in mountGuestPaths)
        {
            IncusInputValidation.ValidateAbsoluteGuestPath(mountPath, nameof(mountGuestPaths));
            var canonical = await ResolveCanonicalGuestPathAsync(
                cli,
                options,
                name,
                mountPath,
                ct).ConfigureAwait(false);
            if (!string.Equals(canonical, mountPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Sandbox mount path '{mountPath}' resolves through a guest filesystem alias; " +
                    "mount destinations must be canonical paths.");
            }
            if (canonicalMounts.Any(existing => IncusGuestPaths.Overlap(canonical, existing)))
            {
                throw new InvalidOperationException(
                    "Canonical sandbox mount paths must be distinct and non-overlapping.");
            }
            canonicalMounts.Add(canonical);
        }
        return canonicalMounts;
    }

    private static async Task<string> ResolveCanonicalGuestPathAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        string guestPath,
        CancellationToken ct)
    {
        var result = await cli.RunCheckedAsync(
            "resolve guest provisioning path",
            options,
            IncusCommandBuilder.BuildRootExec(
                options,
                name,
                ["/usr/bin/realpath", "-m", "--", guestPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 8192,
            maxStderrBytes: 4096).ConfigureAwait(false);
        var canonical = result.Stdout.TrimEnd('\r', '\n');
        if (canonical.Length == 0
            || canonical.Contains('\r')
            || canonical.Contains('\n'))
        {
            throw new InvalidOperationException(
                "Guest path canonicalization returned an invalid response.");
        }
        IncusInputValidation.ValidateAbsoluteGuestPath(canonical, nameof(guestPath));
        return canonical;
    }
}
