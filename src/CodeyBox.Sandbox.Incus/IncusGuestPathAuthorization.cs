namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Single live-guest authorization check shared by initial provisioning and
/// interrupted-exec recovery. Host devices must be absent while this check
/// runs so a guest alias cannot redirect root operations into host storage.
/// </summary>
internal static class IncusGuestPathAuthorization
{
    /// <summary>
    /// Distinguishes a provisioning target written directly from a symlink that
    /// must resolve to an executable destination.
    /// </summary>
    internal enum ProvisioningTargetKind
    {
        Destination,
        Symlink,
    }

    /// <param name="Path">The literal guest path this target occupies.</param>
    /// <param name="Name">Human-readable configuration name for error messages.</param>
    /// <param name="Kind">Whether the path is written directly or is a symlink.</param>
    /// <param name="SymlinkTarget">
    /// For <see cref="ProvisioningTargetKind.Symlink"/> targets, the canonical executable
    /// destination the symlink must point at; <c>null</c> for direct destinations.
    /// </param>
    internal readonly record struct ProvisioningTarget(
        string Path,
        string Name,
        ProvisioningTargetKind Kind,
        string? SymlinkTarget);

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
            if (!string.Equals(canonical, target.Path, StringComparison.Ordinal)
                && !await IsBenignProvisionedSymlinkAsync(
                    cli,
                    options,
                    name,
                    target,
                    canonical,
                    ct).ConfigureAwait(false))
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

    /// <summary>
    /// A configured symlink is already satisfied when it resolves exactly to the
    /// executable destination the provider would point it at. Direct destinations
    /// reached through aliases and links resolving elsewhere remain unauthorized.
    /// Direct destinations are emitted before their links, so each intended target
    /// is independently required to be canonical.
    /// </summary>
    private static async Task<bool> IsBenignProvisionedSymlinkAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        ProvisioningTarget target,
        string canonical,
        CancellationToken ct)
    {
        if (target.Kind != ProvisioningTargetKind.Symlink
            || target.SymlinkTarget is not { } intendedTarget
            || !string.Equals(canonical, intendedTarget, StringComparison.Ordinal))
        {
            return false;
        }

        // realpath alone cannot prove that the configured path is the exact link
        // CodeyBox created: a parent-directory alias or a chain of links can resolve
        // to the same final path. Inspect the link itself and accept only the absolute
        // target that provisioning passes to ln -sfnT.
        var result = await cli.RunAllowFailureAsync(
            options,
            IncusCommandBuilder.BuildRootExec(
                options,
                name,
                ["/usr/bin/readlink", "--", target.Path]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 8192,
            maxStderrBytes: 4096).ConfigureAwait(false);
        if (result.ExitCode != 0)
            return false;

        var linkTarget = result.Stdout.TrimEnd('\r', '\n');
        return !linkTarget.Contains('\r')
            && !linkTarget.Contains('\n')
            && string.Equals(linkTarget, intendedTarget, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<ProvisioningTarget> SnapshotProvisioningTargets(
        IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targets = new List<ProvisioningTarget>(
            options.PackageCacheSeeds.Count
            + options.ExecutableProvisions.Count * 2);
        for (var i = 0; i < options.PackageCacheSeeds.Count; i++)
        {
            targets.Add(new ProvisioningTarget(
                options.PackageCacheSeeds[i].VmDestPath,
                $"PackageCacheSeeds[{i}].VmDestPath",
                ProvisioningTargetKind.Destination,
                SymlinkTarget: null));
        }
        for (var i = 0; i < options.ExecutableProvisions.Count; i++)
        {
            var provision = options.ExecutableProvisions[i];
            targets.Add(new ProvisioningTarget(
                provision.VmDestPath,
                $"ExecutableProvisions[{i}].VmDestPath",
                ProvisioningTargetKind.Destination,
                SymlinkTarget: null));
            for (var linkIndex = 0; linkIndex < provision.VmSymlinks.Count; linkIndex++)
            {
                targets.Add(new ProvisioningTarget(
                    provision.VmSymlinks[linkIndex],
                    $"ExecutableProvisions[{i}].VmSymlinks[{linkIndex}]",
                    ProvisioningTargetKind.Symlink,
                    SymlinkTarget: provision.VmDestPath));
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
